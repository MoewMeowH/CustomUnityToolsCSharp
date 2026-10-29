using System.Collections.Concurrent;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Chat;

/// <summary>
/// 방에 보일 실행 말풍선의 자리: 말(ChatEntry) → 그 말 앞에 보일 말풍선들, 그리고 마지막 말 뒤의 말풍선(Trailing).
/// 말은 참조로 찾는다 — 지휘자 글 한 줄에서 말이 여럿 나와 줄 번호가 같아도 Place가 고른 그 말에만 붙는다 (/self-review a9e6066519 C2).
/// </summary>
public sealed record RunPlacement(IReadOnlyDictionary<ChatEntry, IReadOnlyList<RunBubble>> Before, IReadOnlyList<RunBubble> Trailing)
{
    public static readonly RunPlacement Empty = new(new Dictionary<ChatEntry, IReadOnlyList<RunBubble>>(ReferenceEqualityComparer.Instance), []);

    public IReadOnlyList<RunBubble> For(ChatEntry e) => Before.TryGetValue(e, out var list) ? list : [];

    /// <summary>시각 순 전체.</summary>
    public IEnumerable<RunBubble> All => Before.Values.SelectMany(b => b).Concat(Trailing).OrderBy(b => b.At);
}

/// <summary>
/// 시각으로 끼우기 — 실행 말풍선 · 방 기록 줄이 같은 규칙을 쓴다 (/self-review e464ec29ba C8):
/// 항목 시각보다 늦은 첫 말 앞, 그런 말이 없으면 맨 뒤(Trailing). 말은 참조로 찾는다.
/// </summary>
public static class TimePlacement
{
    public static (IReadOnlyDictionary<ChatEntry, IReadOnlyList<T>> Before, IReadOnlyList<T> Trailing) Place<T>(
        IReadOnlyList<ChatEntry> entries, IEnumerable<T> items, Func<T, DateTimeOffset> at)
    {
        var before = new Dictionary<ChatEntry, List<T>>(ReferenceEqualityComparer.Instance);
        var trailing = new List<T>();
        foreach (var item in items.OrderBy(at))
        {
            var t = at(item);
            var next = entries.FirstOrDefault(e => e.Timestamp is { } ts && ts > t);
            if (next is null) { trailing.Add(item); continue; }
            if (!before.TryGetValue(next, out var list)) before[next] = list = [];
            list.Add(item);
        }
        var placed = new Dictionary<ChatEntry, IReadOnlyList<T>>(ReferenceEqualityComparer.Instance);
        foreach (var (entry, list) in before) placed[entry] = list;
        return (placed, trailing);
    }
}

/// <summary>
/// 외부 에이전트 말풍선: 지휘자가 이 방에서 부른 run_mode 실행의 응답을 그 방에 보인다.
/// 이 방 세션 기록의 run_mode 결과 머리말에 있는 runId(확정 연결)만 쓴다 — 추정 연결은 쓰지 않는다
/// (앱 세션은 결과 머리말이 늘 있어 확정으로 잡힘, 1차 확인 ebf595880b). 판정 · 서브에이전트 말풍선은 대시보드 몫.
/// 세션 요약은 (크기 · 시각)이 같으면 다시 읽지 않고, 호출 기록(calls.jsonl, 모든 실행이 덧붙임)은 덧붙은 줄만 읽는다
/// (줄었으면 처음부터 — a9e6066519 C3). 대시보드 서버를 부르지 않고 같은 파일을 읽는다.
/// </summary>
public sealed class RoomRuns(IOptions<ChatOptions> options)
{
    private readonly Lock _gate = new();
    private string? _callsPath;
    private long _callsEnd;
    private readonly Dictionary<string, List<CallRecord>> _byRun = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Stamp, IReadOnlyList<string> RunIds)> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>그 방 세션 기록 · 본문에 맞춘 말풍선 자리. 기록이 없거나 읽지 못하면 비어 있다 (실패해도 화면은 그린다).</summary>
    public RunPlacement For(ChatTranscript? transcript)
    {
        if (transcript is not { FormatError: null } t || t.Entries.Count == 0) return RunPlacement.Empty;
        try
        {
            var runIds = SessionRunIds(t.FilePath);
            if (runIds.Count == 0) return RunPlacement.Empty;
            // 대시보드(CallLogCache)처럼 UsageEstimator.Apply — 기록을 남긴 뒤 남은 Lost 표시를 뺀다 (a9e6066519 자료 부족 1)
            return Place(t.Entries, CallsFor(runIds).SelectMany(run => RunBubbles.ForRun(run.Key, UsageEstimator.Apply(run.Value))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return RunPlacement.Empty; // 잘못된 CallLogPath 등 — 방은 말풍선 없이 그린다 (a9e6066519 C4)
        }
    }

    /// <summary>
    /// 말풍선을 시각으로 끼운다: 말풍선 시각(호출 끝)보다 늦은 첫 말 앞. 그런 말이 없으면 맨 뒤(Trailing).
    /// 시각으로 끼우므로 같은 실행의 followUp 단계도 그때 자리에 들어간다.
    /// </summary>
    public static RunPlacement Place(IReadOnlyList<ChatEntry> entries, IEnumerable<RunBubble> bubbles)
    {
        var (before, trailing) = TimePlacement.Place(entries, bubbles, b => b.At);
        return new RunPlacement(before, trailing);
    }

    /// <summary>세션 기록의 run_mode 호출 중 결과 머리말에 runId가 있는 것 (차례대로, 중복 없이). 읽기 실패는 기억하지 않는다.</summary>
    private IReadOnlyList<string> SessionRunIds(string path)
    {
        var info = new FileInfo(path);
        var stamp = info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
        if (_sessions.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.RunIds;
        var summary = ClaudeTranscriptReader.Read(path);
        IReadOnlyList<string> ids = [.. summary.RunCalls.Where(c => c.RunId is not null).Select(c => c.RunId!).Distinct(StringComparer.Ordinal)];
        if (summary.FormatError is null) _sessions[path] = (stamp, ids);
        return ids;
    }

    /// <summary>
    /// 호출 기록에 나온 외부 에이전트 이름들 (말풍선 이름과 같은 값 — Agent, 없으면 Model). 설정 화면 프로필 목록 (3차 e). 읽지 못하면 빈 목록.
    /// </summary>
    public IReadOnlyList<string> KnownAgents()
    {
        try
        {
            CallsFor([]); // 덧붙은 줄을 읽어 캐시를 채운다
            lock (_gate)
                return [.. _byRun.Values.SelectMany(l => l).Select(r => r.Agent ?? r.Model).Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>그 runId들의 호출 (기록에 있는 것만, 세션 차례). 덧붙은 줄을 먼저 읽고, 잠금 안에서 목록을 복사해 돌려준다.</summary>
    private List<KeyValuePair<string, List<CallRecord>>> CallsFor(IReadOnlyList<string> runIds)
    {
        var path = options.Value.CallLogFile;
        lock (_gate)
        {
            if (!string.Equals(path, _callsPath, StringComparison.OrdinalIgnoreCase))
            {
                _byRun.Clear();
                _callsEnd = 0;
                _callsPath = path;
            }
            var (records, end, reset) = CallLog.ReadFrom(path, _callsEnd);
            if (reset) _byRun.Clear();
            foreach (var r in records)
            {
                if (r.RunId is not { } run) continue;
                if (!_byRun.TryGetValue(run, out var list)) _byRun[run] = list = [];
                list.Add(r with { Prompt = "" }); // 말풍선에 안 쓰는 프롬프트는 들고 있지 않는다
            }
            _callsEnd = end;
            return [.. runIds.Where(_byRun.ContainsKey).Select(id => KeyValuePair.Create(id, _byRun[id].ToList()))];
        }
    }
}
