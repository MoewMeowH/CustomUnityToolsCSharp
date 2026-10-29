using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>한도 창 하나: 이름(five_hour · seven_day · 300분 …) · 사용률(%) · 초기화 시각 · 상태.</summary>
public sealed record LimitWindow(string Name, double? Percent, DateTimeOffset? ResetsAt, string? Status)
{
    /// <summary>화면 이름: five_hour → 5시간, seven_day → 7일, 그 밖은 원래 이름.</summary>
    public string Label => Name switch
    {
        "five_hour" => "5시간",
        "seven_day" => "7일",
        _ => Name,
    };
}

/// <summary>한 곳(Claude · Codex)의 마지막 한도: 받은 시각 · 창들 · 요금제 · 상태.</summary>
public sealed record LimitSnapshot(string Source, DateTimeOffset At, IReadOnlyList<LimitWindow> Windows)
{
    public string? Plan { get; init; }
    public string? Status { get; init; }
    public bool? UsingOverage { get; init; }

    /// <summary>지난 값: 받은 지 6시간이 넘었거나, 모든 창의 초기화 시각이 지났다 (그 뒤 사용률은 모름).</summary>
    public bool IsStale(DateTimeOffset now) =>
        now - At > TimeSpan.FromHours(6) || (Windows.Count > 0 && Windows.All(w => w.ResetsAt is { } r && r <= now));
}

/// <summary>
/// 구독 사용량 한도 (결정: 추가 호출 없이 실행 때 받은 마지막 값).
/// Claude: 실행마다 오는 rate_limit_event를 데이터 폴더 limits.json에 남긴다 (마지막 값만).
/// Codex: 세션 기록(CODEX_HOME/sessions/…/rollout-*.jsonl)의 token_count 이벤트 rate_limits 중 가장 최근 것 — 대화형 Codex 사용도 반영된다.
/// </summary>
public sealed class UsageLimits(Func<string> dataFolder, Func<string?>? codexHome = null)
{
    private static readonly Lock _gate = new();

    /// <summary>Codex 기록에서 볼 최근 파일 수 · 파일마다 읽는 끝부분 (큰 기록 대비).</summary>
    public const int CodexFiles = 5;
    public const int TailBytes = 512 * 1024;

    /// <summary>Codex 값을 다시 읽지 않는 시간 (목록을 다시 그릴 때마다 디스크를 훑지 않게).</summary>
    public static readonly TimeSpan CodexCache = TimeSpan.FromSeconds(5);

    private (DateTimeOffset At, LimitSnapshot? Value)? _codex;

    private string FilePath() => Path.Combine(dataFolder(), "limits.json");

    /// <summary>Claude 실행이 알려 준 한도를 남긴다 (창이 없으면 남기지 않음). 실패는 무시한다 — 실행을 막지 않게.</summary>
    public void SaveClaude(RateLimitEvent ev, DateTimeOffset? now = null)
    {
        if (ev.Windows.Count == 0) return;
        var snapshot = new LimitSnapshot("claude", now ?? DateTimeOffset.Now, ev.Windows) { Status = ev.Status, UsingOverage = ev.UsingOverage };
        try
        {
            lock (_gate)
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new { claude = snapshot }), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>남겨 둔 Claude 한도. 없거나 못 읽으면 null.</summary>
    public LimitSnapshot? Claude()
    {
        try
        {
            lock (_gate)
            {
                var path = FilePath();
                if (!File.Exists(path)) return null;
                return JsonNode.Parse(File.ReadAllText(path))?["claude"]?.Deserialize<LimitSnapshot>();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>가장 최근 Codex 한도 (몇 초 캐시). 기록이 없거나 못 읽으면 null.</summary>
    public LimitSnapshot? Codex(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        lock (_gate)
            if (_codex is { } cached && at - cached.At < CodexCache) return cached.Value;
        var value = ReadCodex(SessionsDir());
        lock (_gate) _codex = (at, value);
        return value;
    }

    private string SessionsDir() =>
        Path.Combine(codexHome?.Invoke() ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");

    /// <summary>
    /// 최근 rollout 몇 개의 끝부분에서 rate_limits가 든 마지막 token_count 줄. 날짜 폴더(YYYY/MM/DD)를 새것부터 봐서
    /// 기록이 쌓여도 전체를 훑지 않는다 (지휘자 K1). 파일 · 줄 하나가 깨져도 그것만 건너뛴다 (/self-review b40d9efb00 C2 · C7).
    /// </summary>
    public static LimitSnapshot? ReadCodex(string sessionsDir)
    {
        LimitSnapshot? best = null;
        foreach (var file in RecentRollouts(sessionsDir).Take(CodexFiles))
        {
            try
            {
                if (LastLimits(file) is { } found && (best is null || found.At > best.At)) best = found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { } // 그 사이 지워짐 등
        }
        return best;
    }

    /// <summary>최근 날짜 폴더 수 — 이어 가는 스레드는 시작한 날 폴더에 계속 쓰므로 하루만 보지 않는다.</summary>
    public const int RecentDays = 14;

    /// <summary>
    /// 날짜 폴더(이름 내림차순 — 0을 채운 숫자) 최근 <see cref="RecentDays"/>개 안의 파일을 수정 시각 내림차순으로.
    /// </summary>
    private static IEnumerable<string> RecentRollouts(string sessionsDir)
    {
        static IEnumerable<string> Dirs(string dir)
        {
            try { return Directory.EnumerateDirectories(dir).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
        }
        if (!Directory.Exists(sessionsDir)) return [];
        var days = Dirs(sessionsDir).SelectMany(Dirs).SelectMany(Dirs).Take(RecentDays);
        var files = new List<FileInfo>();
        foreach (var day in days)
        {
            try { files.AddRange(new DirectoryInfo(day).EnumerateFiles("rollout-*.jsonl")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName);
    }

    private static LimitSnapshot? LastLimits(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var skip = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(skip, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
            try
            {
                if (ParseLimits(lines[i], path) is { } found) return found;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException) { } // 잘린 줄 · 모양이 다른 줄
        }
        return null;
    }

    /// <summary>token_count 줄 하나 → 한도. 다른 묶음(limit_id가 codex가 아님)이면 null (b40d9efb00 C5).</summary>
    private static LimitSnapshot? ParseLimits(string line, string path)
    {
        if (JsonNode.Parse(line) is not JsonObject ev || ev["payload"] is not JsonObject payload || payload["rate_limits"] is not JsonObject limits) return null;
        if (Text(limits["limit_id"]) is { } id && id != "codex") return null;
        var windows = new List<LimitWindow>();
        foreach (var key in new[] { "primary", "secondary" })
            if (limits[key] is JsonObject w && Num(w["used_percent"]) is not null) // 사용률이 숫자가 아닌 줄은 모양이 다른 것으로
                windows.Add(new LimitWindow(MinutesName((int)Math.Clamp(Num(w["window_minutes"]) ?? 0, 0, int.MaxValue), key), Num(w["used_percent"]),
                    Num(w["resets_at"]) is double r and > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)r) : null,
                    null));
        if (windows.Count == 0) return null;
        var at = DateTimeOffset.TryParse(Text(ev["timestamp"]), out var t) ? t : new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        return new LimitSnapshot("codex", at, windows) { Plan = Text(limits["plan_type"]) };
    }

    private static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>창 길이(분) → 이름: 300 → five_hour, 10080 → seven_day, 그 밖은 "N분" (모르면 primary · secondary).</summary>
    private static string MinutesName(int minutes, string fallback) => minutes switch
    {
        300 => "five_hour",
        10080 => "seven_day",
        > 0 when minutes % 1440 == 0 => $"{minutes / 1440}일",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}시간",
        > 0 => $"{minutes}분",
        _ => fallback,
    };
}
