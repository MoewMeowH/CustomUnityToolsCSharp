using System.Text.Json;

namespace HB.Chat;

/// <summary>
/// 한도 알림 하나. Id는 (출처, 창, 주기, 단계)로 정해져 화면 닫기 · 데스크톱 "본 것" 기억에 쓴다.
/// Title · Text는 서버가 만든 글 (데스크톱은 그대로 띄움).
/// </summary>
public sealed record LimitAlert(string Id, string Source, string Window, int Level, double Percent, DateTimeOffset? ResetsAt, DateTimeOffset At,
    string Title, string Text);

/// <summary>
/// 구독 한도 문턱 알림 (결정: 80 · 95%, 7일 창만, 창마다 단계를 처음 넘을 때 한 번).
/// 창(출처/이름)마다 이번 주기(초기화 시각)와 이미 알린 가장 높은 단계를 데이터 폴더 limit-alerts.json에 남겨,
/// 앱을 다시 켜도 같은 주기 안에서는 다시 알리지 않는다. 초기화 시각이 1시간 넘게 바뀌면 새 주기로 본다.
/// 지난 값 · 초기화가 지난 창 · 사용률을 모르는 창은 알리지 않는다.
/// </summary>
public sealed class LimitAlerts(Func<string> dataFolder)
{
    public static readonly IReadOnlyList<int> Levels = [80, 95];

    /// <summary>남겨 두는 최근 알림 수 (화면 띠 · 데스크톱이 읽음).</summary>
    public const int Keep = 20;

    private static readonly TimeSpan SamePeriod = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    /// <summary>창 하나의 이번 주기: 초기화 시각(모르면 null) · 알린 단계 · 주기를 시작한 시각(초기화 시각을 모를 때 7일로 끊음).</summary>
    private sealed record Seen(DateTimeOffset? ResetsAt, int Level, DateTimeOffset Since);

    /// <summary>초기화 시각을 모르는 창의 주기 길이 (7일 창).</summary>
    private static readonly TimeSpan UnknownPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// 이번 값으로 본 주기. 새 주기면 단계 0으로 (/self-review sub C4):
    /// 처음 봄 · 초기화 시각이 1시간 넘게 바뀜 · 알던 초기화 시각이 지났는데 지금은 모름 · 계속 모르는 채 7일이 지남.
    /// 모르다가 시각이 생기면 같은 주기로 보고 시각만 채운다.
    /// </summary>
    private static Seen Period(Seen? seen, DateTimeOffset? reset, DateTimeOffset at) => (seen, reset) switch
    {
        (null, _) => new Seen(reset, 0, at),
        ({ ResetsAt: null } s, { } now) => s with { ResetsAt = now },
        ({ ResetsAt: { } old }, { } now) when (now - old).Duration() >= SamePeriod => new Seen(now, 0, at),
        ({ ResetsAt: { } old }, null) when old <= at => new Seen(null, 0, at),
        ({ ResetsAt: null, Since: var since }, null) when at - since >= UnknownPeriod => new Seen(null, 0, at),
        ({ } s, _) => s,
    };

    private sealed record FileData(Dictionary<string, Seen> Windows, List<LimitAlert> Recent);

    private string FilePath() => Path.Combine(dataFolder(), "limit-alerts.json");

    /// <summary>출처 화면 이름.</summary>
    public static string SourceName(string source) => source switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "agy" => "AGY",
        _ => source,
    };

    /// <summary>
    /// 한도 값들을 보고 새로 넘은 문턱마다 알림을 만들어 남기고 돌려준다 (없으면 빈 목록). 저장에 실패하면 알림도 없다 (같은 알림이 되풀이되지 않게).
    /// </summary>
    public IReadOnlyList<LimitAlert> Check(IEnumerable<LimitSnapshot?> snapshots, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        lock (_gate)
        {
            var data = Load();
            var fresh = new List<LimitAlert>();
            var changed = false;
            foreach (var snap in snapshots)
            {
                if (snap is null || snap.IsStale(at)) continue;
                foreach (var w in snap.Windows)
                {
                    if (!w.IsWeekly || w.Percent is not { } raw) continue;
                    if (w.ResetsAt is { } passed && passed <= at) continue;
                    var key = snap.Source + "/" + w.Name;
                    data.Windows.TryGetValue(key, out var was);
                    var seen = Period(was, w.ResetsAt, at);
                    if (seen != was)
                    {
                        data.Windows[key] = seen;
                        changed = true;
                    }
                    var shown = Math.Round(raw, MidpointRounding.AwayFromZero); // 사이드바 표시 · 색과 같은 값
                    var level = Levels.Where(l => shown >= l).DefaultIfEmpty(0).Max();
                    if (level <= seen.Level) continue;
                    data.Windows[key] = seen with { Level = level };
                    changed = true;
                    fresh.Add(Make(snap.Source, w, level, shown, seen.ResetsAt, at));
                }
            }
            if (!changed) return [];
            data.Recent.AddRange(fresh);
            if (data.Recent.Count > Keep) data.Recent.RemoveRange(0, data.Recent.Count - Keep);
            try
            {
                AtomicFile.WriteAllText(FilePath(), JsonSerializer.Serialize(data, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            return fresh;
        }
    }

    /// <summary>남긴 최근 알림 (오래된 것부터). 없거나 못 읽으면 빈 목록.</summary>
    public IReadOnlyList<LimitAlert> Recent()
    {
        lock (_gate) return Load().Recent;
    }

    /// <summary>
    /// 화면 띠에 보일 알림: 초기화가 아직 안 지난(모르면 받은 지 7일 안) 것 중 창마다 가장 높은 단계 하나, 새것부터.
    /// </summary>
    public IReadOnlyList<LimitAlert> Active(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        return [.. Recent().Where(a => a.ResetsAt is { } r ? r > at : at - a.At < TimeSpan.FromDays(7))
            .GroupBy(a => (a.Source, a.Window)).Select(g => g.OrderByDescending(a => a.Level).ThenByDescending(a => a.At).First())
            .OrderByDescending(a => a.At)];
    }

    private static LimitAlert Make(string source, LimitWindow w, int level, double shown, DateTimeOffset? reset, DateTimeOffset at)
    {
        var name = $"{SourceName(source)} {w.Label}";
        var id = $"{source}/{w.Name}/{reset?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}/{level}";
        var text = $"{shown:0}% 사용" + (reset is { } r ? $" — 초기화 {r.ToLocalTime():MM-dd HH:mm}" : "");
        return new LimitAlert(id, source, w.Name, level, shown, reset, at, $"한도 {level}% 넘음 · {name}", text);
    }

    /// <summary>파일을 읽는다. 없거나 깨졌으면 빈 기록 (깨진 파일은 다음 저장 때 덮인다 — 최악은 알림 한 번 더).</summary>
    private FileData Load()
    {
        try
        {
            var path = FilePath();
            if (File.Exists(path) && JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json) is { } data)
                return new FileData(data.Windows ?? [], data.Recent ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return new FileData([], []);
    }
}
