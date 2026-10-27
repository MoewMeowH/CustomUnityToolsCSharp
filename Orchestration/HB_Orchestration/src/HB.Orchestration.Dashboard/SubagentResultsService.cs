using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>서브에이전트 결과가 서버 기록에 들어갔는지.</summary>
public enum SubagentLinkKind
{
    /// <summary>원문이 self_review 기록의 응답이거나 run_mode ownReview 안에 그대로 있다.</summary>
    Exact,
    /// <summary>글자는 다르지만 줄 대부분(NearThreshold 이상)이 한 기록에 있다 — 지휘자가 형식을 맞추며 고쳐 넣은 경우.</summary>
    Near,
    /// <summary>어느 기록에도 없다. RunId · Similarity는 가장 비슷한 기록 (참고용).</summary>
    None,
}

/// <param name="Via">selfReview(record_verdicts) 또는 ownReview(run_mode).</param>
public sealed record SubagentLink(SubagentLinkKind Kind, string? RunId, string? Via, double Similarity);

public sealed record SubagentView(SubagentResult Result, SubagentLink Link);

/// <summary>
/// 서브에이전트 결과 ↔ 호출 기록 연결 (계획서 2026-10-27_서브에이전트-결과-보존).
/// 비교 전에 줄바꿈(CRLF)과 줄 끝 공백 · 앞뒤 빈 줄만 정리한다. 2026-10-27 실측: 검토형 101건 중 63건이 그대로 일치,
/// 15건가량은 지휘자가 고쳐 넣어 줄 81~97%만 일치 — 그래서 '거의 같음'을 따로 둔다.
/// </summary>
public static class SubagentLinker
{
    public const double NearThreshold = 0.8;

    /// <summary>비교에 쓰는 줄의 최소 길이 (빈 줄 · "## 지적" 같은 머리글은 모든 리뷰에 있어 일치율을 부풀린다).</summary>
    public const int MinLineLength = 20;

    /// <summary>이보다 짧은 원문은 '안에 들어 있음'으로 같음 판정하지 않고 통째로 같을 때만 (한 줄짜리 결과가 아무 기록에나 붙지 않게 — /self-review C1).</summary>
    public const int MinContainsLength = 200;

    /// <summary>
    /// 후보 기록의 시각 창 (/self-review C1 · 지휘자 K1): 기록은 결과를 넘긴 뒤에 생긴다 —
    /// run_mode는 서브에이전트 결과를 받은 뒤 부르고, self_review 기록은 판정 때 쓴다 (기록 시각 = Timestamp + ElapsedMs).
    /// 시계 차이만큼 앞을 조금 열고, 뒤는 같은 작업 안에서 기록할 만한 시간까지만 본다. 몇 주 전 · 다른 작업의 기록에 붙지 않게.
    /// </summary>
    public static readonly TimeSpan EarlySlack = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LateLimit = TimeSpan.FromDays(2);

    /// <summary>비교에서 빼는 절: 같은 실행의 리뷰어들 · 같은 리뷰어의 재검토는 읽은 파일 목록이 같아 일치율을 부풀린다 (/self-review C1).</summary>
    public const string ReadFilesHeading = "## 읽은 파일";

    private sealed record Candidate(string RunId, string Via, string? Agent, DateTimeOffset RecordedAt, string Text, HashSet<string> Lines);

    public static List<SubagentView> Link(IReadOnlyList<SubagentResult> results, IReadOnlyList<CallRecord> calls)
    {
        // ownReview 실행의 기록 시각 = 그 실행의 첫 호출 시각 (run_mode를 부른 때).
        var runStart = calls.Where(c => c.RunId is not null).GroupBy(c => c.RunId!)
            .ToDictionary(g => g.Key, g => g.Min(c => c.Timestamp), StringComparer.Ordinal);

        var candidates = new List<Candidate>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var c in calls)
        {
            if (c.RunId is not { } runId) continue;
            if (c.Tool == Agents.VerdictRecorder.SelfReviewTool && !string.IsNullOrWhiteSpace(c.Response))
                Add(runId, "selfReview", c.Agent, c.Timestamp.AddMilliseconds(Math.Max(0, c.ElapsedMs)), c.Response);
            if (!string.IsNullOrWhiteSpace(c.OwnReview))
                Add(runId, "ownReview", null, runStart[runId], c.OwnReview); // 여러 리뷰어 결과를 이어 붙이므로 에이전트로 거르지 않는다
        }

        void Add(string runId, string via, string? agent, DateTimeOffset at, string text)
        {
            // ownReview는 같은 실행의 모든 단계 · 시도에 같은 값으로 남는다 — 한 번만.
            if (!seen.Add((runId, via, text))) return;
            var normalized = Normalize(text);
            candidates.Add(new Candidate(runId, via, agent, at, normalized, Lines(normalized)));
        }

        return results.Select(r => new SubagentView(r, LinkOne(r, candidates))).ToList();
    }

    private static SubagentLink LinkOne(SubagentResult result, List<Candidate> all)
    {
        var message = Normalize(result.Message);
        if (message.Length == 0) return new SubagentLink(SubagentLinkKind.None, null, null, 0);

        var candidates = all.Where(c => Fits(result, c)).ToList();

        foreach (var c in candidates)
            if (c.Text == message || (message.Length >= MinContainsLength && c.Text.Contains(message, StringComparison.Ordinal)))
                return new SubagentLink(SubagentLinkKind.Exact, c.RunId, c.Via, 1);

        var lines = Lines(message);
        if (lines.Count == 0) return new SubagentLink(SubagentLinkKind.None, null, null, 0);

        Candidate? best = null;
        var bestRate = 0.0;
        foreach (var c in candidates)
        {
            var rate = (double)lines.Count(c.Lines.Contains) / lines.Count;
            if (rate > bestRate) (best, bestRate) = (c, rate);
        }
        return best is null
            ? new SubagentLink(SubagentLinkKind.None, null, null, 0)
            : new SubagentLink(bestRate >= NearThreshold ? SubagentLinkKind.Near : SubagentLinkKind.None, best.RunId, best.Via, bestRate);
    }

    /// <summary>selfReview 기록은 같은 에이전트만, 그리고 시각 창 안의 기록만 (넘긴 시각을 모르면 시각은 보지 않는다).</summary>
    private static bool Fits(SubagentResult result, Candidate c)
    {
        if (c.Agent is not null && !string.Equals(c.Agent, result.AgentType, StringComparison.OrdinalIgnoreCase)) return false;
        if (result.ReturnedAt is not { } returned) return true;
        var delta = c.RecordedAt - returned; // 차이로 비교 (극단 시각에서 덧셈이 범위를 넘지 않게)
        return delta >= -EarlySlack && delta <= LateLimit;
    }

    public static string Normalize(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();

    /// <summary>비교할 줄: 20자 이상, "## 읽은 파일" 절 밖.</summary>
    private static HashSet<string> Lines(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var inReadFiles = false;
        foreach (var raw in normalized.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal)) { inReadFiles = line == ReadFilesHeading; continue; }
            if (!inReadFiles && line.Length >= MinLineLength) set.Add(line);
        }
        return set;
    }
}

/// <summary>
/// '서브에이전트 결과' 화면: 읽을 때 보관 폴더를 Sync하고(간격 제한), 보관 파일을 읽어 호출 기록과 연결한다.
/// 보관 폴더 · 호출 기록이 그대로면 다시 읽거나 다시 연결하지 않는다.
/// </summary>
public sealed class SubagentResultsService(IOptions<DashboardOptions> options, ClaudeSessionsService sessions, ILogger<SubagentResultsService> logger)
{
    /// <summary>Sync 간격. 바뀐 세션 기록만 다시 읽지만 폴더를 훑는 비용은 있다.</summary>
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);

    /// <summary>첫 화면 안내에 세는 기간.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// 넘긴 뒤 이 시간 안의 기록 없음은 아직 진행 중일 수 있다 — 지휘자가 run_mode를 끝내야(단계가 끝나야) ownReview가,
    /// 판정을 남겨야 selfReview가 기록된다. 첫 화면 안내에서 빼고 화면에 따로 표시한다 (/self-review C3).
    /// </summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromHours(1);

    public static bool MaybePending(SubagentView v) =>
        v.Link.Kind == SubagentLinkKind.None && v.Result.ReturnedAt is { } at && DateTimeOffset.Now - at < PendingGrace;

    public sealed record Snapshot(IReadOnlyList<SubagentView> Views, int Unreadable, SubagentArchive.SyncReport? LastSync, DateTimeOffset? LastSyncAt, string Folder);

    private readonly Lock _gate = new();
    private readonly Lock _syncGate = new();
    private long _lastSyncTicks;
    private SubagentArchive.SyncReport? _lastSync;
    private DateTimeOffset? _lastSyncAt;
    private string? _archiveStamp;
    private (List<SubagentResult> Results, int Unreadable) _archive = ([], 0);
    private (object? Calls, object? Archive, List<SubagentView> Views) _links;

    public string Folder => SubagentArchive.FolderFor(options.Value.ResolvedLogPath);

    public Snapshot Load(IReadOnlyList<CallRecord> calls)
    {
        var folder = Folder;
        SyncIfDue(folder);

        lock (_gate)
        {
            var stamp = Stamp(folder);
            if (stamp != _archiveStamp)
            {
                _archive = SubagentArchive.ReadAll(folder);
                _archiveStamp = stamp;
            }
            if (!ReferenceEquals(_links.Calls, calls) || !ReferenceEquals(_links.Archive, _archive.Results))
                _links = (calls, _archive.Results, SubagentLinker.Link(_archive.Results, calls));
            return new Snapshot(_links.Views, _archive.Unreadable, _lastSync, _lastSyncAt, folder);
        }
    }

    /// <summary>최근 RecentWindow 안에 넘긴 결과 중 기록 없음 수 (첫 화면 안내, 진행 중일 수 있는 것은 뺌). 실패해도 0.</summary>
    public int UnrecordedRecent(IReadOnlyList<CallRecord> calls)
    {
        try
        {
            var since = DateTimeOffset.Now - RecentWindow;
            return Load(calls).Views.Count(v => v.Link.Kind == SubagentLinkKind.None && v.Result.ReturnedAt >= since && !MaybePending(v));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "서브에이전트 결과 읽기 실패");
            return 0;
        }
    }

    private void SyncIfDue(string folder)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSyncTicks);
        if (now - last < SyncInterval.Ticks || Interlocked.CompareExchange(ref _lastSyncTicks, now, last) != last) return;
        lock (_syncGate)
        {
            try
            {
                var report = SubagentArchive.Sync(sessions.Root, folder);
                _lastSync = report;
                _lastSyncAt = DateTimeOffset.Now;
                if (report.Errors.Count > 0) logger.LogWarning("서브에이전트 결과 보관 오류: {Errors}", string.Join(" | ", report.Errors));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "서브에이전트 결과 보관 실패");
            }
        }
    }

    /// <summary>보관 파일 수 + 폴더 수정 시각 (파일을 더하면 폴더 시각이 바뀐다).</summary>
    private static string Stamp(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "";
            return $"{Directory.EnumerateFiles(folder, "*.json").Count()}:{Directory.GetLastWriteTimeUtc(folder).Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
