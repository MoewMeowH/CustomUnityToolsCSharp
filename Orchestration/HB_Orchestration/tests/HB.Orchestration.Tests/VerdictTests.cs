using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>record_verdicts 검사·기록과 판정 집계 (계획서 2026-10-19_지적-채택-기각-기록).</summary>
public sealed class VerdictTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-orch-verdict-").FullName;
    private string Log => Path.Combine(_dir, "calls.jsonl");
    private string VerdictPath => VerdictLog.PathFor(Log);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CallRecord Call(string run, int step, string agent, string role = "Reviewer", int attempt = 1, bool success = true,
        string mode = "review", long input = 1_000_000) => new()
    {
        Id = $"{run}-{step}-{attempt}",
        Timestamp = DateTimeOffset.Now.AddMinutes(-10 + step),
        Tool = mode, Mode = mode, RunId = run, Step = step, Role = role, Agent = agent, Model = agent + "-model",
        Prompt = "p", Response = success ? "r" : null, Success = success, Attempt = attempt, InputTokens = input, OutputTokens = 0,
    };

    private async Task Seed(params CallRecord[] calls)
    {
        foreach (var c in calls) await CallLog.AppendAsync(c, Log);
    }

    private string Record(string run, params VerdictItem[] items) => VerdictRecorder.Record(run, items, 500, Log);

    [Fact]
    public async Task Valid_items_are_written_and_claude_only_is_marked()
    {
        await Seed(Call("r1", 1, "sol"), Call("r1", 2, "argon"));

        var result = Record("r1",
            new VerdictItem("a", "OnDestroy 해제 누락", "Adopted", Steps: [1, 2]),
            new VerdictItem("b", "캐싱 제안", "rejected", Steps: [2], Reason: "Awake에서 이미 캐싱"),
            new VerdictItem("c", "코루틴 취소 누락", "adopted"));

        Assert.StartsWith("판정 3건 기록 — 채택 2 · 기각 1 (Claude만 지적 1)", result);
        var all = VerdictLog.ReadAll(VerdictPath);
        Assert.Equal(3, all.Count);
        Assert.Equal("adopted", all[0].Verdict);
        Assert.True(all[2].IsClaudeOnly);
        Assert.True(all[2].ClaudeAlsoFound);
    }

    [Fact]
    public async Task Unknown_run_is_rejected()
    {
        await Seed(Call("r1", 1, "sol"));
        Assert.StartsWith("[판정 기록 불가] runId 'nope'의 기록이 없습니다.", Record("nope", new VerdictItem("a", "x", "adopted")));
        Assert.False(File.Exists(VerdictPath));
    }

    [Fact]
    public async Task Any_invalid_item_writes_nothing()
    {
        await Seed(Call("r1", 1, "sol"));

        var result = Record("r1",
            new VerdictItem("ok", "정상", "adopted", Steps: [1]),
            new VerdictItem("bad-step", "없는 단계", "adopted", Steps: [3]),
            new VerdictItem("no-reason", "사유 없음", "partial", Steps: [1]),
            new VerdictItem("bad-verdict", "값 틀림", "maybe"),
            new VerdictItem("ok", "키 중복", "adopted"));

        Assert.StartsWith("[판정 기록 불가] 아무것도 기록하지 않았습니다.", result);
        Assert.Contains("없는 단계 3", result);
        Assert.Contains("부분 채택(partial)은 reason이 필수", result);
        Assert.Contains("verdict 'maybe'", result);
        Assert.Contains("findingKey가 겹칩니다", result);
        Assert.False(File.Exists(VerdictPath));
    }

    [Fact]
    public async Task Same_key_replaces_previous_verdict()
    {
        await Seed(Call("r1", 1, "sol"));
        Record("r1", new VerdictItem("a", "보류한 지적", "deferred", Steps: [1], Reason: "테스트 필요"));
        var second = Record("r1", new VerdictItem("a", "보류한 지적", "adopted", Steps: [1]));

        Assert.Contains("앞 판정 대체 1", second);
        var latest = VerdictLog.Latest(VerdictLog.ReadAll(VerdictPath));
        Assert.Equal("adopted", Assert.Single(latest).Verdict);
    }

    [Fact]
    public void Stats_group_by_responding_agent_and_count_retries_once()
    {
        // 단계 1은 재시도 후 성공 (시도 2줄), 단계 2는 에이전트 교체로 luna가 응답.
        CallRecord[] calls =
        [
            Call("r1", 1, "sol", attempt: 1, success: false), Call("r1", 1, "sol", attempt: 2),
            Call("r1", 2, "luna", role: "Checker"),
            Call("r2", 1, "sol"), // 판정 없는 실행
        ];
        var now = DateTimeOffset.Now;
        VerdictRecord V(string key, string verdict, int[] steps, bool claude = false) => new()
        {
            Id = key, Timestamp = now, RunId = "r1", FindingKey = key, Summary = key, Verdict = verdict, Steps = steps, ClaudeAlsoFound = claude || steps.Length == 0,
        };
        VerdictRecord[] verdicts =
        [
            V("common", Verdicts.Adopted, [1, 2]),
            V("sol-only", Verdicts.Partial, [1], claude: true),
            V("luna-wrong", Verdicts.Rejected, [2]),
            V("claude", Verdicts.Adopted, []),
        ];

        var s = VerdictStats.Build(verdicts, calls, c => c.InputTokens / 1_000_000m);

        var sol = s.ByAgent.Single(r => r.Key == "sol");
        Assert.Equal((1, 1, 0), (sol.Adopted, sol.Partial, sol.Rejected));
        Assert.Equal(0.75, sol.Rate);                // (채택 1 + 부분 1 × 0.5) / (1 + 1 + 0)
        Assert.Equal(1, sol.AgentOnlyAdopted);       // common만 (sol-only는 ownReview에도 있었음)
        Assert.Equal(1, sol.Missed);                 // claude 지적
        Assert.Equal(2m, sol.Cost);                  // r1 단계 1의 시도 2줄, r2는 판정 없는 실행이라 제외
        Assert.Equal(2m / 1.5m, sol.CostPerAdopted);

        var luna = s.ByAgent.Single(r => r.Key == "luna");
        Assert.Equal((1, 0, 1), (luna.Adopted, luna.Partial, luna.Rejected));
        Assert.Equal(0.5, luna.Rate);

        Assert.Equal(1, Assert.Single(s.ClaudeOnlyByMode).Adopted);
        Assert.Equal("r2", Assert.Single(s.Unjudged).RunId);
        Assert.Equal(1, s.JudgedRuns);
    }

    [Fact]
    public async Task Null_item_is_reported_not_thrown()
    {
        await Seed(Call("r1", 1, "sol"));
        var result = Record("r1", new VerdictItem("a", "정상", "adopted"), null!);
        Assert.Contains("2번: 항목이 비어 있습니다", result);
        Assert.False(File.Exists(VerdictPath));
    }

    [Fact]
    public void Line_cut_by_interrupted_write_does_not_swallow_next_record()
    {
        File.WriteAllText(VerdictPath, "{\"id\":\"broken\",\"runId\":");   // 개행 없이 끊긴 줄
        VerdictLog.Append([new VerdictRecord
        {
            Id = "ok", Timestamp = DateTimeOffset.Now, RunId = "r1", FindingKey = "a", Summary = "s", Verdict = Verdicts.Adopted,
        }], VerdictPath);

        Assert.Equal("ok", Assert.Single(VerdictLog.ReadAll(VerdictPath)).Id);
    }

    [Fact]
    public void Path_for_bare_file_name_has_a_folder()
    {
        Assert.False(string.IsNullOrEmpty(Path.GetDirectoryName(VerdictLog.PathFor("calls.jsonl"))));
    }

    [Fact]
    public void Agent_without_findings_still_shows_cost()
    {
        // 동시 실행: argon은 실패만 했고 지적이 없다. 판정이 있는 실행이므로 비용은 보인다.
        CallRecord[] calls = [Call("r1", 1, "sol"), Call("r1", 2, "argon", success: false)];
        VerdictRecord[] verdicts =
        [
            new() { Id = "v", Timestamp = DateTimeOffset.Now, RunId = "r1", FindingKey = "a", Summary = "s", Verdict = Verdicts.Adopted, Steps = [1] },
        ];

        var argon = VerdictStats.Build(verdicts, calls, c => c.InputTokens / 1_000_000m).ByAgent.Single(r => r.Key == "argon");
        Assert.Equal(0, argon.Findings);
        Assert.Equal(1m, argon.Cost);
        Assert.Null(argon.CostPerAdopted);
    }

    [Fact]
    public void Missed_counts_only_finding_steps_not_judge()
    {
        // 단계 1 리뷰어(sol, 지적 있음) · 단계 2 판정자(astra, 지적 없음). Claude만 지적이 채택되면 sol만 놓침.
        CallRecord[] calls = [Call("r1", 1, "sol"), Call("r1", 2, "astra", role: "Judge")];
        var now = DateTimeOffset.Now;
        VerdictRecord[] verdicts =
        [
            new() { Id = "a", Timestamp = now, RunId = "r1", FindingKey = "a", Summary = "s", Verdict = Verdicts.Rejected, Steps = [1], Reason = "x" },
            new() { Id = "c", Timestamp = now, RunId = "r1", FindingKey = "c", Summary = "s", Verdict = Verdicts.Adopted, ClaudeAlsoFound = true },
        ];

        var s = VerdictStats.Build(verdicts, calls, _ => null);
        Assert.Equal(1, s.ByAgent.Single(r => r.Key == "sol").Missed);
        Assert.DoesNotContain(s.ByAgent, r => r.Key == "astra");
    }

    [Fact]
    public void Missed_counts_structured_reviewer_with_zero_findings()
    {
        var structured = new StructuredOutputRecord { Format = "findings@1", State = OutputStates.Complete, RequestedBy = "schema" };
        CallRecord[] calls = [Call("r1", 1, "sol") with { Structured = structured }];
        VerdictRecord[] verdicts =
        [
            new() { Id = "c", Timestamp = DateTimeOffset.Now, RunId = "r1", FindingKey = "c", Summary = "s", Verdict = Verdicts.Adopted, ClaudeAlsoFound = true },
        ];

        Assert.Equal(1, VerdictStats.Build(verdicts, calls, _ => null).ByAgent.Single(r => r.Key == "sol").Missed);
    }

    [Fact]
    public void For_run_with_no_calls_is_empty()
    {
        VerdictRecord[] verdicts = [new() { Id = "c", Timestamp = DateTimeOffset.Now, RunId = "r1", FindingKey = "c", Summary = "s", Verdict = Verdicts.Adopted }];
        Assert.Empty(VerdictStats.ForRun(verdicts, [], "r1"));
    }
}
