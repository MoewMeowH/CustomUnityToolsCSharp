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

    // ── /self-review (계획서 2026-10-20_리뷰조건-self-reviewer) ─────────────────

    private const string SelfReviewText = "## 지적\n- C1 [minor] a.cs:1 — x\n\n## 자료 부족\n- 없음\n\n## 읽은 파일\n- subagent_in/x/a.cs:1-10\n";

    private static SelfReviewInfo Info(string review = SelfReviewText) =>
        new(review, "변경 의도", ["a.cs"], SubagentTokens: 84514, ToolUses: 15, DurationMs: 113093, Reason: "50줄 이상");

    [Fact]
    public async Task SelfReview_creates_run_and_verdicts_counted_for_self_reviewer()
    {
        var result = await VerdictRecorder.RecordSelfReviewAsync(Info(), [
            new VerdictItem("C1", "x", "adopted", Steps: [1]),
            new VerdictItem("K1", "지휘자만", "adopted"),
        ], 500, Log);

        Assert.StartsWith("self-review 실행 기록 runId: ", result);
        var call = Assert.Single(CallLog.ReadAll(Log));
        Assert.Equal((VerdictRecorder.SelfReviewTool, VerdictRecorder.SelfReviewAgent, 1), (call.Mode, call.Agent, call.Step));
        Assert.Null(call.InputTokens); // 외부 에이전트 사용량에 섞이지 않게
        Assert.Null(call.OutputTokens);
        Assert.Contains("84514", call.UsageRaw);
        Assert.Equal(113093, call.ElapsedMs);
        Assert.Equal(SelfReviewText, call.Response);
        Assert.Contains(call.RunId!, result);

        var verdicts = VerdictLog.Latest(VerdictLog.ReadAll(VerdictPath));
        Assert.Equal(2, verdicts.Count);
        var row = VerdictStats.Build(verdicts, CallLog.ReadAll(Log), _ => null).ByAgent.Single(r => r.Key == VerdictRecorder.SelfReviewAgent);
        Assert.Equal((1, 1), (row.Adopted, row.Missed)); // 지휘자만 찾은 것 = self-reviewer가 놓친 것
    }

    [Theory]
    [InlineData("## 읽은 파일\n- a")] // 지적 절 없음
    [InlineData("## 지적\n- a")]      // 읽은 파일 절 없음
    [InlineData("")]
    public async Task SelfReview_bad_review_writes_nothing(string review)
    {
        var result = await VerdictRecorder.RecordSelfReviewAsync(Info(review), [new VerdictItem("C1", "x", "adopted", Steps: [1])], 500, Log);

        Assert.StartsWith("[판정 기록 불가]", result);
        Assert.Empty(CallLog.ReadAll(Log));
    }

    [Fact]
    public void SelfReview_format_accepts_note_section()
    {
        // 2026-10-22: 설명 줄은 ## 비고에 (H4가 ## 읽은 파일의 설명 줄을 막으므로)
        Assert.Null(VerdictRecorder.SelfReviewFormatProblem(SelfReviewText + "\n## 비고\n- 폴더 안에서 검색했지만 정의가 없었다\n"));
    }

    [Fact]
    public async Task SelfReview_invalid_verdict_leaves_no_empty_run()
    {
        var result = await VerdictRecorder.RecordSelfReviewAsync(Info(), [
            new VerdictItem("C1", "x", "adopted", Steps: [1]),
            new VerdictItem("C2", "y", "rejected", Steps: [2]), // 단계 2 없음 + reason 없음
        ], 500, Log);

        Assert.Contains("이 실행에 없는 단계 2", result);
        Assert.Empty(CallLog.ReadAll(Log));
        Assert.Empty(VerdictLog.ReadAll(VerdictPath));
    }

    [Fact]
    public async Task SelfReview_with_no_reviewer_findings_still_counts_missed()
    {
        // self-reviewer가 "문제 없음"이고 지휘자만 찾은 것이 채택되면 self-reviewer의 놓침이다.
        await VerdictRecorder.RecordSelfReviewAsync(Info("## 지적\n- 없음\n\n## 읽은 파일\n- subagent_in/x/a.cs\n"),
            [new VerdictItem("K1", "지휘자만", "adopted")], 500, Log);

        var row = VerdictStats.Build(VerdictLog.Latest(VerdictLog.ReadAll(VerdictPath)), CallLog.ReadAll(Log), _ => null)
            .ByAgent.Single(r => r.Key == VerdictRecorder.SelfReviewAgent);
        Assert.Equal((0, 1), (row.Findings, row.Missed));
    }

    [Fact]
    public async Task SelfReview_same_review_twice_is_rejected_with_existing_run_id()
    {
        await VerdictRecorder.RecordSelfReviewAsync(Info(), [new VerdictItem("C1", "x", "adopted", Steps: [1])], 500, Log);
        var runId = CallLog.ReadAll(Log).Single().RunId!;

        var again = await VerdictRecorder.RecordSelfReviewAsync(Info(), [new VerdictItem("C1", "x", "adopted", Steps: [1])], 500, Log);

        Assert.Contains($"runId {runId}", again);
        Assert.Single(CallLog.ReadAll(Log));
        Assert.Single(VerdictLog.ReadAll(VerdictPath));
    }

    [Fact]
    public async Task SelfReview_too_long_review_is_rejected()
    {
        var result = await VerdictRecorder.RecordSelfReviewAsync(Info(SelfReviewText + new string('x', 100)),
            [new VerdictItem("C1", "x", "adopted", Steps: [1])], 500, Log, maxReviewChars: 50);

        Assert.Contains("이내입니다", result);
        Assert.Empty(CallLog.ReadAll(Log));
    }

    [Fact]
    public async Task SelfReview_timestamp_is_start_of_subagent_run()
    {
        var before = DateTimeOffset.Now;
        await VerdictRecorder.RecordSelfReviewAsync(Info(), [new VerdictItem("C1", "x", "adopted", Steps: [1])], 500, Log);

        Assert.True(CallLog.ReadAll(Log).Single().Timestamp <= before.AddMilliseconds(-100_000)); // DurationMs 113,093
    }

    [Fact]
    public async Task SelfReview_run_can_be_rejudged_with_its_run_id()
    {
        Assert.StartsWith("self-review 실행", await VerdictRecorder.RecordSelfReviewAsync(Info(), [new VerdictItem("C1", "x", "deferred", Steps: [1], Reason: "다음 대조 때")], 500, Log));
        var runId = CallLog.ReadAll(Log).Single().RunId!;

        var again = Record(runId, new VerdictItem("C1", "x", "adopted", Steps: [1]));

        Assert.Contains("앞 판정 대체 1", again);
        Assert.Equal(Verdicts.Adopted, VerdictLog.Latest(VerdictLog.ReadAll(VerdictPath)).Single().Verdict);
    }
}
