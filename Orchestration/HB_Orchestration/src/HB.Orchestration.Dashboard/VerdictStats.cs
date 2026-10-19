using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>판정 기록 (RunId·FindingKey마다 마지막 판정). 파일이 바뀌었을 때만 다시 읽는다 (<see cref="CallLogCache"/>와 같은 방식).</summary>
public sealed class VerdictLogCache(IOptions<DashboardOptions> options)
{
    private readonly Lock _gate = new();
    private string? _stamp;
    private IReadOnlyList<VerdictRecord> _cached = [];

    public string Path => VerdictLog.PathFor(options.Value.ResolvedLogPath);

    public IReadOnlyList<VerdictRecord> Latest
    {
        get
        {
            var path = Path;
            var info = new FileInfo(path);
            var stamp = info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";

            lock (_gate)
            {
                if (stamp == _stamp) return _cached;
                _cached = VerdictLog.Latest(VerdictLog.ReadAll(path));
                _stamp = stamp;
                return _cached;
            }
        }
    }
}

/// <summary>에이전트별·모드 역할별 판정 집계 한 줄.</summary>
public sealed class VerdictRow(string key)
{
    public string Key { get; } = key;
    public int Adopted { get; set; }
    public int Partial { get; set; }
    public int Rejected { get; set; }
    public int Deferred { get; set; }
    public int Duplicate { get; set; }

    /// <summary>채택·부분 채택 중 ownReview에 없던 것 (에이전트만의 기여).</summary>
    public int AgentOnlyAdopted { get; set; }

    /// <summary>Claude만 지적해 채택·부분 채택된 것 중, 이 에이전트가 그 실행에 있었는데 놓친 수.</summary>
    public int Missed { get; set; }

    /// <summary>판정이 있는 실행에서 이 줄에 속한 호출(재시도 포함)의 비용 합계. 단가가 하나도 없으면 null.</summary>
    public decimal? Cost { get; set; }

    public int Findings => Adopted + Partial + Rejected + Deferred + Duplicate;

    /// <summary>(채택 + 부분 채택 × 0.5) / (채택 + 부분 채택 + 기각). 분모가 0이면 null.</summary>
    public double? Rate => Adopted + Partial + Rejected is var d and > 0 ? (Adopted + Partial * Verdicts.PartialWeight) / d : null;

    /// <summary>채택 1건당 비용 (부분 채택은 0.5건). 채택이 없거나 비용을 모르면 null.</summary>
    public decimal? CostPerAdopted => Cost is { } c && Adopted + Partial * Verdicts.PartialWeight is var n and > 0 ? c / (decimal)n : null;

    public void Add(VerdictRecord v)
    {
        switch (v.Verdict)
        {
            case Verdicts.Adopted: Adopted++; break;
            case Verdicts.Partial: Partial++; break;
            case Verdicts.Rejected: Rejected++; break;
            case Verdicts.Deferred: Deferred++; break;
            case Verdicts.Duplicate: Duplicate++; break;
        }
        if (v.Verdict is Verdicts.Adopted or Verdicts.Partial && !v.ClaudeAlsoFound) AgentOnlyAdopted++;
    }
}

/// <summary>판정 하나 + 그 지적을 낸 에이전트 이름들 (화면 표시용).</summary>
public sealed record VerdictView(VerdictRecord Verdict, string Mode, IReadOnlyList<string> Agents, string? FirstCallId);

/// <summary>판정 없는 실행.</summary>
public sealed record UnjudgedRun(string RunId, string Mode, DateTimeOffset Start, string FirstCallId);

public sealed record VerdictSummary(
    IReadOnlyList<VerdictRow> ByAgent,
    IReadOnlyList<VerdictRow> ByModeRole,
    IReadOnlyList<VerdictRow> ClaudeOnlyByMode,
    IReadOnlyList<VerdictView> Recent,
    IReadOnlyList<UnjudgedRun> Unjudged,
    int JudgedRuns,
    RequestSummary? Requests = null);

/// <summary>처리 안 한 자료 요청 하나 (판정 화면 목록).</summary>
public sealed record OpenRequest(string RunId, string Id, string What, string Mode, string? CallId);

/// <summary>
/// 자료 요청(1-N1) 처리 집계 — 채택률과 따로 (계획서 2026-10-19_자료요청-왕복, 사용자 결정 "응 그러자").
/// Total = 기간 안 실행의 구조화 결과에 나온 요청 수, Open = 처리 판정(supplied·declined·deferred)이 없는 요청.
/// </summary>
public sealed record RequestSummary(int Total, int Supplied, int Declined, int Deferred, IReadOnlyList<OpenRequest> Open)
{
    /// <summary>보충 단계(followUp)가 새로 낸 요청 — 실행마다 1회라 다시 보충할 수 없어 Open에 넣지 않는다.</summary>
    public int FromFollowUps { get; init; }
}

/// <summary>
/// 판정 집계. 에이전트·역할은 판정에 저장하지 않으므로 RunId + 단계 번호로 호출 기록에서 찾는다
/// (에이전트 교체 실행도 실제로 응답한 에이전트로 묶인다). 기간은 판정 시각이 아니라 실행 시작 시각 기준 (소급 판정도 그 실행의 기간에 들어감).
/// </summary>
public static class VerdictStats
{
    public static VerdictSummary Build(
        IReadOnlyList<VerdictRecord> latest, IReadOnlyList<CallRecord> calls, Func<CallRecord, decimal?> costOf,
        DateTimeOffset? since = null, int recent = 30)
    {
        var runs = calls.Where(c => c.RunId is not null).GroupBy(c => c.RunId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        // 실행마다 첫 호출을 한 번만 구한다 (판정마다 다시 훑지 않도록 — perf_argon 094ba91515 1-F1).
        var firsts = runs.ToDictionary(kv => kv.Key, kv => kv.Value.MinBy(c => c.Timestamp)!);
        bool InPeriod(string runId) => since is null || firsts[runId].Timestamp >= since;

        var all = latest.Where(v => runs.ContainsKey(v.RunId) && InPeriod(v.RunId)).ToList();
        var verdicts = all.Where(v => !v.IsRequest).ToList(); // 자료 요청 처리는 채택률·비용·판정 여부에 넣지 않는다
        var judged = verdicts.Select(v => v.RunId).ToHashSet(); // 요청 처리만 있는 실행은 "판정 없는 실행"에 남는다
        var requestSummary = Requests(all.Where(v => v.IsRequest).ToList(), runs, firsts, InPeriod);

        var byAgent = new Dictionary<string, VerdictRow>();
        var byModeRole = new Dictionary<string, VerdictRow>();
        var claudeOnly = new Dictionary<string, VerdictRow>();
        VerdictRow Row(Dictionary<string, VerdictRow> d, string key) =>
            d.TryGetValue(key, out var r) ? r : d[key] = new VerdictRow(key);

        // 놓침은 지적을 내는 단계에만 매긴다 (판정자·검증 역할 제외 — review 453bc0e4fe 1-F1·2-F3):
        // 구조화 출력을 낸 단계, 또는 그 실행에서 지적을 하나라도 낸 단계. 단계마다 대표 호출 하나만 본다.
        // self-review 실행의 단계(self-reviewer)는 지적이 0개여도 리뷰어다 — 안 그러면 다 놓친 리뷰일수록 놓침에서 빠진다.
        var findingSteps = verdicts.GroupBy(v => v.RunId).ToDictionary(g => g.Key, g => g.SelectMany(v => v.Steps).ToHashSet());
        List<CallRecord> Reviewers(string runId, List<CallRecord> run) =>
            run.Where(c => c.Step is not null).Select(c => c.Step!.Value).Distinct()
                .Select(s => StepCall(run, s)).OfType<CallRecord>()
                .Where(c => c.Success && (c.Structured is not null || c.Tool == Agents.VerdictRecorder.SelfReviewTool || findingSteps[runId].Contains(c.Step!.Value)))
                .ToList();

        foreach (var v in verdicts)
        {
            var run = runs[v.RunId];
            if (v.IsClaudeOnly)
            {
                Row(claudeOnly, ModeOf(run)).Add(v);
                if (v.Verdict is not (Verdicts.Adopted or Verdicts.Partial)) continue;
                var reviewers = Reviewers(v.RunId, run);
                foreach (var agent in reviewers.Select(AgentOf).Distinct()) Row(byAgent, agent).Missed++;
                foreach (var mr in reviewers.Select(ModeRoleOf).Distinct()) Row(byModeRole, mr).Missed++;
                continue;
            }

            var stepCalls = v.Steps.Select(s => StepCall(run, s)).OfType<CallRecord>().ToList();
            foreach (var agent in stepCalls.Select(AgentOf).Distinct()) Row(byAgent, agent).Add(v);
            foreach (var mr in stepCalls.Select(ModeRoleOf).Distinct()) Row(byModeRole, mr).Add(v);
        }

        // 비용: 판정이 있는 실행의 호출만 (판정하지 않은 실행의 비용으로 채택 1건당 비용이 부풀지 않도록).
        // 지적이 없던 에이전트·역할(실패만 한 단계, 판정자)도 줄을 만들어 비용을 보인다 — 채택이 없으니 건당 비용은 "-" (review 7c780ad3cd).
        foreach (var c in judged.SelectMany(id => runs[id]))
        {
            if (costOf(c) is not { } cost) continue;
            var a = Row(byAgent, AgentOf(c));
            a.Cost = (a.Cost ?? 0) + cost;
            var m = Row(byModeRole, ModeRoleOf(c));
            m.Cost = (m.Cost ?? 0) + cost;
        }

        var views = verdicts.OrderByDescending(v => v.Timestamp).Take(recent).Select(v => View(v, runs[v.RunId])).ToList();

        var unjudged = runs.Where(kv => !judged.Contains(kv.Key) && InPeriod(kv.Key))
            .Select(kv => new UnjudgedRun(kv.Key, ModeOf(kv.Value), firsts[kv.Key].Timestamp, firsts[kv.Key].Id))
            .OrderByDescending(u => u.Start).ToList();

        return new VerdictSummary(
            byAgent.Values.OrderByDescending(r => r.Findings + r.Missed).ThenBy(r => r.Key).ToList(),
            byModeRole.Values.OrderBy(r => r.Key).ToList(),
            claudeOnly.Values.OrderBy(r => r.Key).ToList(),
            views, unjudged, judged.Count, requestSummary);
    }

    private static RequestSummary Requests(List<VerdictRecord> handled, Dictionary<string, List<CallRecord>> runs,
        Dictionary<string, CallRecord> firsts, Func<string, bool> inPeriod)
    {
        var done = handled.SelectMany(v => (v.FindingIds ?? []).Select(id => (v.RunId, Id: id, v.Verdict)))
            .GroupBy(x => (x.RunId, x.Id.ToUpperInvariant())).ToDictionary(g => g.Key, g => g.Last().Verdict);
        int total = 0, supplied = 0, declined = 0, deferred = 0, fromFollowUps = 0;
        var open = new List<OpenRequest>();
        foreach (var (runId, run) in runs)
        {
            if (!inPeriod(runId)) continue;
            // followUp으로 보낸 요청은 지휘자가 따로 기록하지 않아도 보충한 것이다 (서버가 이미 안다).
            var sentByServer = run.Where(c => c.Success && c.FollowUpRequests is not null).SelectMany(c => c.FollowUpRequests!)
                .Select(id => id.ToUpperInvariant()).ToHashSet();
            foreach (var step in run.Where(c => c.Step is not null).Select(c => c.Step!.Value).Distinct())
            {
                if (StepCall(run, step) is not { Success: true, Structured.Requests: { Count: > 0 } reqs } call) continue;
                foreach (var q in reqs)
                {
                    total++;
                    var key = (runId, q.Id.ToUpperInvariant());
                    var verdict = done.TryGetValue(key, out var v) ? v : sentByServer.Contains(key.Item2) ? Verdicts.Supplied : null;
                    switch (verdict)
                    {
                        case Verdicts.Supplied: supplied++; break;
                        case Verdicts.Declined: declined++; break;
                        case Verdicts.Deferred: deferred++; break;
                        default:
                            if (call.FollowUpOf is not null) fromFollowUps++;
                            else open.Add(new OpenRequest(runId, q.Id, q.What, ModeOf(run), call.Id));
                            break;
                    }
                }
            }
        }
        return new RequestSummary(total, supplied, declined, deferred, [.. open.OrderByDescending(o => firsts[o.RunId].Timestamp)])
        {
            FromFollowUps = fromFollowUps,
        };
    }

    /// <summary>한 실행의 판정 (호출 상세 화면용). 단계 순서, Claude만 지적은 맨 뒤.</summary>
    public static List<VerdictView> ForRun(IReadOnlyList<VerdictRecord> latest, IReadOnlyList<CallRecord> runCalls, string runId)
    {
        if (runCalls.Count == 0) return []; // 기록 없는 실행 (review 453bc0e4fe 2-F2)
        var run = runCalls.ToList();
        return latest.Where(v => v.RunId == runId)
            .OrderBy(v => v.IsClaudeOnly).ThenBy(v => v.Steps.Count > 0 ? v.Steps[0] : int.MaxValue)
            .Select(v => View(v, run))
            .ToList();
    }

    private static VerdictView View(VerdictRecord v, List<CallRecord> run)
    {
        var stepCalls = v.Steps.Select(s => StepCall(run, s)).OfType<CallRecord>().ToList();
        return new VerdictView(v, ModeOf(run), stepCalls.Select(c => $"{c.Step}. {c.Role} · {AgentOf(c)}").ToList(),
            stepCalls.FirstOrDefault()?.Id ?? run.MinBy(c => c.Timestamp)?.Id);
    }

    /// <summary>단계의 대표 호출: 성공한 마지막 시도, 없으면 마지막 시도.</summary>
    private static CallRecord? StepCall(List<CallRecord> run, int step)
    {
        var attempts = run.Where(c => c.Step == step).ToList();
        return attempts.Where(c => c.Success).MaxBy(c => c.Attempt ?? 1) ?? attempts.MaxBy(c => c.Attempt ?? 1);
    }

    private static string AgentOf(CallRecord c) => c.Agent ?? c.Model;
    private static string ModeOf(List<CallRecord> run) => run[0].Mode ?? run[0].Tool;
    private static string ModeRoleOf(CallRecord c) => $"{c.Mode ?? c.Tool} · {c.Role ?? "-"}";
}
