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
        "seven_day_overage_included" => "Fable 7일",
        _ => Name,
    };
}

/// <summary>한 곳(Claude · Codex)의 마지막 한도: 받은 시각 · 창들 · 요금제 · 상태.</summary>
public sealed record LimitSnapshot(string Source, DateTimeOffset At, IReadOnlyList<LimitWindow> Windows)
{
    public string? Plan { get; init; }
    public string? Status { get; init; }
    public bool? UsingOverage { get; init; }

    /// <summary>Codex 한도 초기화 크레딧 개수 (계정 조회에서만 온다).</summary>
    public int? ResetCredits { get; init; }

    /// <summary>받은 곳: <see cref="FromAccount"/>(계정 조회) · <see cref="FromRollout"/>(세션 기록). Claude는 null.</summary>
    public string? From { get; init; }

    public const string FromAccount = "account";
    public const string FromRollout = "rollout";

    /// <summary>지난 값: 받은 지 6시간이 넘었거나, 모든 창의 초기화 시각이 지났다 (그 뒤 사용률은 모름).</summary>
    public bool IsStale(DateTimeOffset now) =>
        now - At > TimeSpan.FromHours(6) || (Windows.Count > 0 && Windows.All(w => w.ResetsAt is { } r && r <= now));
}

/// <summary>
/// 구독 사용량 한도 (결정: 추가 호출 없이 실행 때 받은 마지막 값).
/// Claude: 실행마다 오는 rate_limit_event를 데이터 폴더 limits.json에 남긴다 (마지막 값만).
/// Codex: 계정 조회(<see cref="CodexLimits"/>, <see cref="AccountInterval"/>마다 한 번 — 사용자 결정으로 Codex만 추가 호출)와
/// 세션 기록(CODEX_HOME/sessions/…/rollout-*.jsonl)의 token_count 이벤트 rate_limits 중 더 새 것. 조회가 실패하면 기록 값만 남는다.
/// </summary>
public sealed class UsageLimits(Func<string> dataFolder, Func<string?>? codexHome = null, Func<CancellationToken, Task<LimitSnapshot?>>? codexQuery = null)
{
    private static readonly Lock _gate = new();

    /// <summary>Codex 기록에서 볼 최근 파일 수 · 파일마다 읽는 끝부분 (큰 기록 대비).</summary>
    public const int CodexFiles = 5;
    public const int TailBytes = 512 * 1024;

    /// <summary>Codex 값을 다시 읽지 않는 시간 (목록을 다시 그릴 때마다 디스크를 훑지 않게).</summary>
    public static readonly TimeSpan CodexCache = TimeSpan.FromSeconds(5);

    private (DateTimeOffset At, LimitSnapshot? Value)? _codex;

    /// <summary>계정 조회 간격 — 성공 · 실패 모두 이만큼은 다시 부르지 않는다 (결정 "5분").</summary>
    public static readonly TimeSpan AccountInterval = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _accountTried;
    private LimitSnapshot? _account;
    private Task? _refresh;

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

    /// <summary>
    /// 가장 최근 Codex 한도: 마지막 계정 조회 값과 세션 기록 값(몇 초 캐시) 중 받은 시각이 새 것. 둘 다 없으면 null.
    /// 조회를 기다리지 않는다 — 새로 받으려면 <see cref="RefreshCodexAsync"/>.
    /// </summary>
    public LimitSnapshot? Codex(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        LimitSnapshot? account;
        lock (_gate)
        {
            account = _account;
            if (_codex is { } cached && at - cached.At < CodexCache) return Newer(account, cached.Value);
        }
        LimitSnapshot? rollout;
        lock (_scan) rollout = ReadCodex(SessionsDir(), _files);
        lock (_gate) _codex = (at, rollout);
        return Newer(account, rollout);
    }

    /// <summary>
    /// 계정 값과 기록 값 중 받은 시각이 새 것. 기록 값이 이겨도 크레딧 개수 · 요금제는 기록에 없으니 마지막 계정 값에서 가져온다
    /// (Codex를 쓰는 동안 크레딧 줄이 사라지지 않게 — /self-review sub C1).
    /// </summary>
    private static LimitSnapshot? Newer(LimitSnapshot? account, LimitSnapshot? rollout) =>
        account is null ? rollout
        : rollout is null || account.At >= rollout.At ? account
        : rollout with { ResetCredits = rollout.ResetCredits ?? account.ResetCredits, Plan = rollout.Plan ?? account.Plan };

    /// <summary>기록 파일별 마지막 결과 (수정 시각 · 크기가 같으면 다시 읽지 않음 — C2). <see cref="_scan"/> 안에서만 쓴다.</summary>
    private readonly Dictionary<string, (DateTime Write, long Length, LimitSnapshot? Value)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _scan = new();

    /// <summary>
    /// 계정 조회를 한 번 돌린다 — 마지막 시도가 <see cref="AccountInterval"/> 안이면 하지 않고, 돌고 있으면 그 작업을 돌려준다 (한 번에 하나).
    /// 실패(null · 예외)면 전 값을 지우지 않는다. 돌려준 작업은 예외로 끝나지 않는다.
    /// </summary>
    public Task RefreshCodexAsync(DateTimeOffset? now = null)
    {
        if (codexQuery is null) return Task.CompletedTask;
        var at = now ?? DateTimeOffset.Now;
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false } running) return running;
            if (_accountTried is { } tried && at - tried < AccountInterval) return Task.CompletedTask;
            _accountTried = at;
            return _refresh = Task.Run(async () =>
            {
                LimitSnapshot? got = null;
                try { got = await codexQuery(CancellationToken.None); }
                catch (Exception) { } // 조회기는 null로 실패를 알리지만, 백그라운드 작업이 예외로 끝나지 않게
                if (got is not null) lock (_gate) _account = got;
            });
        }
    }

    private string SessionsDir() =>
        Path.Combine(codexHome?.Invoke() ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");

    /// <summary>값을 찾을 때까지 훑는 rollout 상한 — 값이 null인 기록이 몰려 있어도(Codex Desktop이 한꺼번에 21개를 만든 적 있음) 그 뒤를 보게.</summary>
    public const int ScanFiles = 60;

    /// <summary>
    /// 최근 rollout의 끝부분에서 rate_limits가 든 마지막 token_count 줄. 값이 든 파일 <see cref="CodexFiles"/>개를 찾을 때까지(최대 <see cref="ScanFiles"/>개) 본다.
    /// 날짜 폴더(YYYY/MM/DD)를 새것부터 봐서 기록이 쌓여도 전체를 훑지 않는다 (지휘자 K1). 파일 · 줄 하나가 깨져도 그것만 건너뛴다.
    /// </summary>
    public static LimitSnapshot? ReadCodex(string sessionsDir) => ReadCodex(sessionsDir, null);

    /// <summary>
    /// <paramref name="cache"/>가 있으면 파일별 결과를 (수정 시각, 크기)로 기억해 바뀌지 않은 파일은 다시 읽지 않는다 — 값이 없는 기록이 몰려 있어도
    /// 5초마다 수십 개를 다시 파싱하지 않게. 이번에 본 파일만 남긴다. 부르는 쪽이 한 번에 하나만 부른다.
    /// </summary>
    internal static LimitSnapshot? ReadCodex(string sessionsDir, Dictionary<string, (DateTime Write, long Length, LimitSnapshot? Value)>? cache)
    {
        LimitSnapshot? best = null;
        var withValue = 0;
        var seen = new Dictionary<string, (DateTime Write, long Length, LimitSnapshot? Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in RecentRollouts(sessionsDir).Take(ScanFiles))
        {
            LimitSnapshot? found;
            try
            {
                var (write, length) = (file.LastWriteTimeUtc, file.Length);
                found = cache is not null && cache.TryGetValue(file.FullName, out var hit) && hit.Write == write && hit.Length == length
                    ? hit.Value : LastLimits(file.FullName);
                seen[file.FullName] = (write, length, found);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; } // 그 사이 지워짐 등
            if (found is null) continue;
            withValue++;
            if (best is null || found.At > best.At) best = found;
            if (withValue >= CodexFiles) break;
        }
        if (cache is not null)
        {
            cache.Clear();
            foreach (var (path, entry) in seen) cache[path] = entry;
        }
        return best;
    }

    /// <summary>최근 날짜 폴더 수 — 이어 가는 스레드는 시작한 날 폴더에 계속 쓰므로 하루만 보지 않는다.</summary>
    public const int RecentDays = 14;

    /// <summary>
    /// 날짜 폴더(이름 내림차순 — 0을 채운 숫자) 최근 <see cref="RecentDays"/>개 안의 파일을 수정 시각 내림차순으로.
    /// </summary>
    private static IEnumerable<FileInfo> RecentRollouts(string sessionsDir)
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
        return files.OrderByDescending(f => f.LastWriteTimeUtc);
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

    /// <summary>token_count 줄 하나 → 한도. 다른 묶음(limit_id가 codex가 아님)이면 null.</summary>
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
        return new LimitSnapshot("codex", at, windows) { Plan = Text(limits["plan_type"]), From = LimitSnapshot.FromRollout };
    }

    internal static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    internal static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>창 길이(분) → 이름: 300 → five_hour, 10080 → seven_day, 그 밖은 "N분" (모르면 primary · secondary).</summary>
    internal static string MinutesName(int minutes, string fallback) => minutes switch
    {
        300 => "five_hour",
        10080 => "seven_day",
        > 0 when minutes % 1440 == 0 => $"{minutes / 1440}일",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}시간",
        > 0 => $"{minutes}분",
        _ => fallback,
    };
}
