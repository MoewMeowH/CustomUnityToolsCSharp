using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>
/// agy(Antigravity CLI) 구독 한도: agy가 statusLine 명령에 stdin으로 넘기는 상태 JSON의 <c>quota</c>를 앱이 만든 스크립트가 파일로 남기고, 앱은 그 파일만 읽는다.
/// 앱은 agy · Google 서버에 묻지 않는다 (Antigravity 약관 6조 — 제3자 소프트웨어로 서비스에 접근 금지, 공식 한도 API 없음).
/// 2026-11-24 실측(agy 1.5.16): bucket gemini-5h · gemini-weekly · 3p-5h · 3p-weekly, 값은 remaining_fraction · reset_time ·
/// reset_in_seconds. 모델을 고르기 전에는 quota가 없고, 안 쓴 bucket의 reset_time은 부를 때마다 "지금 + 창 길이"로 움직인다.
/// command 안 큰따옴표는 글자 그대로 넘어가 실패했다 → 경로에 공백이 있으면 쓸 수 없다.
/// </summary>
public static class AgyLimits
{
    /// <summary>데이터 폴더 안 자리 (스크립트 · 한도 파일).</summary>
    public const string Folder = "agy-status", ScriptName = "statusline.ps1", LimitsName = "limits.json";

    public static string ScriptPath(string dataFolder) => Path.Combine(dataFolder, Folder, ScriptName);

    public static string LimitsPath(string dataFolder) => Path.Combine(dataFolder, Folder, LimitsName);

    /// <summary>agy settings.json statusLine.command 값 — 따옴표 없이 (agy가 따옴표를 글자 그대로 넘김).</summary>
    public static string Command(string scriptPath) => $"powershell -NoProfile -ExecutionPolicy Bypass -File {Path.GetFullPath(scriptPath)}";

    /// <summary>
    /// statusLine 스크립트 (Windows PowerShell 5.1). quota만 같은 폴더 limits.json에 남기고(임시 파일 → 바꾸기), 이메일 · 대화 경로 등은 남기지 않는다.
    /// quota가 없으면 쓰지 않는다 (앞 값 유지). 늘 0으로 끝나고 한 줄을 낸다 — agy는 30번 연속 실패하면 상태 줄을 끈다.
    /// 출력은 ASCII만 (agy가 받는 콘솔 코드 페이지를 모름).
    /// </summary>
    public const string Script = """
        # HB_Chat이 만든 파일 (켤 때마다 이 내용으로 맞춘다). agy statusLine 명령: stdin 상태 JSON의 quota만 이 폴더 limits.json에 남긴다.
        $ErrorActionPreference = 'Stop'
        try {
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            $j = [Console]::In.ReadToEnd() | ConvertFrom-Json
            if ($null -eq $j.quota) { 'HB'; exit 0 }
            $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
            $out = Join-Path $dir 'limits.json'
            $tmp = "$out.$PID.tmp"
            $row = [ordered]@{ at = (Get-Date).ToString('o'); version = $j.version; quota = $j.quota }
            [IO.File]::WriteAllText($tmp, ($row | ConvertTo-Json -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
            if (Test-Path -LiteralPath $out) { [IO.File]::Replace($tmp, $out, [NullString]::Value) } else { [IO.File]::Move($tmp, $out) }
            $used = @($j.quota.PSObject.Properties | Where-Object { $null -ne $_.Value.remaining_fraction -and $_.Value.remaining_fraction -lt 1 } |
                ForEach-Object { '{0} {1}%' -f $_.Name, [int][math]::Round((1 - $_.Value.remaining_fraction) * 100) })
            if ($used.Count -gt 0) { 'HB ' + ($used -join ' | ') } else { 'HB 0%' }
        }
        catch {
            if ($tmp) { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
            'HB limits error'; exit 0
        }
        """;

    /// <summary>스크립트를 만든다 — 내용이 다를 때만 다시 쓴다 (누가 고쳐도 켤 때 되돌림). 실패는 무시한다 (앱 켜기를 막지 않게).</summary>
    public static void EnsureScript(string dataFolder)
    {
        var path = ScriptPath(dataFolder);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == Script) return;
            // BOM: Windows PowerShell 5.1은 BOM 없는 파일을 ANSI 코드 페이지로 읽는다 (주석의 한글)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Script, new UTF8Encoding(true));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>한도 파일을 읽는다. 없거나 못 읽으면 null.</summary>
    public static LimitSnapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// 한도 파일 글 → 한도. bucket마다 창 하나: 사용률 = (1 − remaining_fraction) × 100. remaining_amount만 있으면 사용률 없이 "남음 N",
    /// disabled면 "꺼짐"(<see cref="LimitWindow.Status"/>). 사용률 0인 창은 초기화 시각을 버린다 (안 쓴 창은 시각이 계속 움직임).
    /// 모양이 다르면 null.
    /// </summary>
    public static LimitSnapshot? Parse(string json, DateTimeOffset fallbackAt)
    {
        // 같은 키가 두 번 든 객체는 처음 접근할 때 ArgumentException — 파일 감시 콜백까지 올라가면 서버가 끝난다 (/self-review sub C2)
        try { return ParseCore(json, fallbackAt); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>화면이 ToLocalTime으로 그려도 넘치지 않는 시각만 (9999년 근처는 버림).</summary>
    private static DateTimeOffset? Sane(DateTimeOffset? at) =>
        at is { } t && t > DateTimeOffset.MinValue.AddDays(2) && t < DateTimeOffset.MaxValue.AddDays(-2) ? t : null;

    private static LimitSnapshot? ParseCore(string json, DateTimeOffset fallbackAt)
    {
        if (JsonNode.Parse(json.TrimStart((char)0xFEFF)) is not JsonObject root) return null;
        if (root["quota"] is not JsonObject quota) return null;
        var windows = new List<LimitWindow>();
        foreach (var (id, node) in quota)
        {
            if (node is not JsonObject bucket) continue;
            var fraction = UsageLimits.Num(bucket["remaining_fraction"]);
            var amount = UsageLimits.Num(bucket["remaining_amount"]);
            double? percent = fraction is { } f ? Math.Clamp((1 - f) * 100, 0, 100) : null;
            DateTimeOffset? reset = percent is 0 ? null
                : DateTimeOffset.TryParse(UsageLimits.Text(bucket["reset_time"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r) ? Sane(r) : null;
            var status = bucket["disabled"] is JsonValue d && d.GetValueKind() == JsonValueKind.True ? "꺼짐"
                : percent is null && amount is { } a ? $"남음 {a.ToString("0.##", CultureInfo.InvariantCulture)}" : null;
            windows.Add(new LimitWindow(id, percent, reset, status));
        }
        if (windows.Count == 0) return null;
        var at = DateTimeOffset.TryParse(UsageLimits.Text(root["at"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && Sane(t) is { } ok ? ok : fallbackAt;
        return new LimitSnapshot("agy", at, windows) { Plan = UsageLimits.Text(root["version"]) is { } v ? "agy " + v : null };
    }
}
