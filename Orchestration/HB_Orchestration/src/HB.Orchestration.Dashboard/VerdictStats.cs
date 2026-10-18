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
    int JudgedRuns);

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
        bool InPeriod(List<CallRecord> run) => since is null || run.Min(c => c.Timestamp) >= since;

        var verdicts = latest.Where(v => runs.TryGetValue(v.RunId, out var run) && InPeriod(run)).ToList();
        var judged = verdicts.Select(v => v.RunId).ToHashSet();

        var byAgent = new Dictionary<string, VerdictRow>();
        var byModeRole = new Dictionary<string, VerdictRow>();
        var claudeOnly = new Dictionary<string, VerdictRow>();
        VerdictRow Row(Dictionary<string, VerdictRow> d, string key) =>
            d.TryGetValue(key, out var r) ? r : d[key] = new VerdictRow(key);

        foreach (var v in verdicts)
        {
            var run = runs[v.RunId];
            if (v.IsClaudeOnly)
            {
                Row(claudeOnly, ModeOf(run)).Add(v);
                if (v.Verdict is not (Verdicts.Adopted or Verdicts.Partial)) continue;
                foreach (var agent in run.Where(c => c.Success).Select(AgentOf).Distinct()) Row(byAgent, agent).Missed++;
                foreach (var mr in run.Where(c => c.Success).Select(ModeRoleOf).Distinct()) Row(byModeRole, mr).Missed++;
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

        var unjudged = runs.Where(kv => !judged.Contains(kv.Key) && InPeriod(kv.Value))
            .Select(kv =>
            {
                var first = kv.Value.MinBy(c => c.Timestamp)!;
                return new UnjudgedRun(kv.Key, ModeOf(kv.Value), first.Timestamp, first.Id);
            })
            .OrderByDescending(u => u.Start).ToList();

        return new VerdictSummary(
            byAgent.Values.OrderByDescending(r => r.Findings + r.Missed).ThenBy(r => r.Key).ToList(),
            byModeRole.Values.OrderBy(r => r.Key).ToList(),
            claudeOnly.Values.OrderBy(r => r.Key).ToList(),
            views, unjudged, judged.Count);
    }

    /// <summary>한 실행의 판정 (호출 상세 화면용). 단계 순서, Claude만 지적은 맨 뒤.</summary>
    public static List<VerdictView> ForRun(IReadOnlyList<VerdictRecord> latest, IReadOnlyList<CallRecord> runCalls, string runId) =>
        latest.Where(v => v.RunId == runId)
            .OrderBy(v => v.IsClaudeOnly).ThenBy(v => v.Steps.Count > 0 ? v.Steps[0] : int.MaxValue)
            .Select(v => View(v, runCalls.ToList()))
            .ToList();

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
