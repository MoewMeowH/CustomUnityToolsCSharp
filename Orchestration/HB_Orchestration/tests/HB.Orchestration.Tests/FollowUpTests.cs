using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Tests;

/// <summary>자료 요청 왕복 (계획서 2026-10-19_자료요청-왕복): followUp · 요청 처리 판정 · 대시보드 집계.</summary>
public sealed class FollowUpTests
{
    private const string Modes = """
        {
          "Roles": { "Rev": { "Prompt": "역할", "Output": "findings" } },
          "Modes": {
            "r":   { "MaxRunsPerSession": 1, "Steps": [ { "Role": "Rev", "Agent": "a1" } ] },
            "two": { "Steps": [ { "Role": "Rev", "Agent": "a1" }, { "Role": "Rev", "Agent": "a1" } ] },
            "deb": { "Flow": "Debate", "MaxRounds": 1, "Steps": [ { "Role": "Rev", "Agent": "a1" }, { "Role": "Rev", "Agent": "a1" } ] }
          }
        }
        """;

    private const string FirstAnswer = """
        {"findings":[{"severity":"minor","file":"A.cs","line":3,"problem":"추측","detail":null,"suggestion":null,"confidence":"low"}],
         "needsInfo":[{"what":"Calls 수집부","path":"src/Reader.cs","symbol":null,"why":"중복 처리"},
                      {"what":"사내 설정","path":null,"symbol":"Secret","why":"값 확인"}]}
        """;

    private const string SecondAnswer = """{"findings":[{"severity":"major","file":"Reader.cs","line":1,"problem":"중복 id를 둘로 셈","detail":null,"suggestion":null,"confidence":"high"}],"needsInfo":[]}""";

    private static async Task<(ModeRunnerHarness H, string RunId)> FirstRun(string mode = "r")
    {
        var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply(FirstAnswer), ScriptedChatClient.Reply(SecondAnswer), ScriptedChatClient.Reply(SecondAnswer)];
        var result = await h.Run(mode);
        Assert.Contains("자료 요청 1-N1: Calls 수집부 (path src/Reader.cs) — 중복 처리", result);
        Assert.Matches(@"^첨부 0자 \+ input [\d,]+자 = [\d,]+자 / 상한 350,000자 \(0%\)$", result.Split('\n')[1]);   // 첨부 여유 머리말 (기본 상한)
        return (h, result.Split('\n')[0]["runId: ".Length..].Trim());
    }

    [Fact]
    public async Task FollowUp_sends_full_prompt_from_blob_when_original_was_split()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply(FirstAnswer), ScriptedChatClient.Reply(SecondAnswer)];
        var big = "큰 요청 " + new string('y', CallLog.PromptBlobMinBytes);
        var first = await h.Run("r", big);
        var runId = first.Split('\n')[0]["runId: ".Length..].Trim();
        var original = h.Records()[0];
        Assert.NotNull(original.PromptSha256); // 기록에는 앞부분만

        var result = await FollowUp(h, runId, ["1-N1"]);

        Assert.StartsWith($"runId: {runId}", result);
        var sent = h.Client.Received[1].Messages.Where(m => m.Role != ChatRole.System).ToList();
        Assert.Equal(CallLog.FullPrompt(original, h.LogPath).Text, sent[0].Text);
        Assert.Contains(big, sent[0].Text);
    }

    private static Task<string> FollowUp(ModeRunnerHarness h, string runId, string[] ids, IReadOnlyList<ContextRange>? context = null,
        string input = "1-N2는 사내 설정이라 보내지 않음", string mode = "r", int step = 1, string? reason = "자료 보충") =>
        h.Runner.RunFollowUpAsync(mode, new FollowUpRequest(runId, step, ids), input, null, context, reason, CancellationToken.None);

    [Fact]
    public async Task FollowUp_sends_previous_turn_and_supplement_as_new_step_without_using_run_limit()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fu_reader_tmp.cs"), "class Reader { }\n");
        try
        {
            var result = await FollowUp(h, runId, ["1-N1"], [new ContextRange("fu_reader_tmp.cs", 1, 1)]);

            Assert.StartsWith($"runId: {runId}", result);
            Assert.StartsWith("### 2.", result.Split('\n')[1]);                                  // followUp 결과에는 첨부 여유 줄 없음
            Assert.Contains("### 2. Rev · a1", result);
            Assert.Contains("- 2-F1 [major] Reader.cs:1 — 중복 id를 둘로 셈", result);
            Assert.Contains("보충한 요청 1-N1 · 보내지 않은 요청 1-N2", result);

            // 앞 대화: [원래 요청, 원래 응답, 보충]
            var sent = h.Client.Received[1].Messages.Where(m => m.Role != ChatRole.System).ToList();
            Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], sent.Select(m => m.Role));
            Assert.Equal(h.Records()[0].Prompt, sent[0].Text);
            Assert.Equal(FirstAnswer, sent[1].Text);
            Assert.Contains("class Reader { }", sent[2].Text);
            Assert.Contains("1-N1(Calls 수집부)", sent[2].Text);
            Assert.Contains("보내지 않은 요청: 1-N2", sent[2].Text);
            Assert.Contains("## 보충 지시", sent[2].Text);

            var rec = h.Records().Last();
            Assert.Equal((2, 1), (rec.Step, rec.FollowUpOf));
            Assert.Equal(["1-N1"], rec.FollowUpRequests);
            Assert.Equal(["1-N2"], rec.FollowUpSkipped);
            Assert.Equal(runId, rec.RunId);

            // 실행 한도 1회 모드: followUp은 세지 않았으므로 다음 실행이 한도로 막히는 것은 첫 실행 때문이다
            Assert.Equal(1, h.Sessions.Get("r"));
        }
        finally
        {
            File.Delete(Path.Combine(Environment.CurrentDirectory, "fu_reader_tmp.cs"));
        }
    }

    [Fact]
    public async Task Result_tells_how_to_judge_and_shows_overlap_with_original_step()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        // 가짜 클라이언트는 호출 순번으로 응답을 고른다 (0번 = 첫 실행)
        h.Client.Script = [ScriptedChatClient.Reply(FirstAnswer), ScriptedChatClient.Reply("""{"findings":[{"severity":"minor","file":"A.cs","line":4,"problem":"추측","detail":null,"suggestion":null,"confidence":"low"}],"needsInfo":[]}""")];
        var file = Path.Combine(Environment.CurrentDirectory, "fu_overlap_tmp.cs");
        File.WriteAllText(file, "x\n");
        string result;
        try { result = await FollowUp(h, runId, ["1-N1"], [new ContextRange("fu_overlap_tmp.cs", 1, 1)], input: ""); }
        finally { File.Delete(file); }
        Assert.Contains("겹침 후보: A.cs:3-4 — 1-F1, 2-F1", result);
        Assert.Contains("판정은 2단계 지적 기준 — 1단계의 같은 지적은 duplicate로", result);
        Assert.Contains("보내지 않은 요청: 1-N2 (지휘자 판단).", ScriptedChatClient.UserText(h.Client.Received[1])); // 메모가 없으면 '메모 참고'를 쓰지 않음
    }

    [Fact]
    public async Task Failed_followup_can_be_retried()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        h.Client.Script = [ScriptedChatClient.Reply(FirstAnswer), ScriptedChatClient.Throw(new HttpRequestException("끊김")), ScriptedChatClient.Reply(SecondAnswer)];
        var failed = await FollowUp(h, runId, ["1-N1"]);
        Assert.Contains("[실패]", failed);

        var retried = await FollowUp(h, runId, ["1-N1"]);
        Assert.Contains("### 3. Rev · a1", retried); // 실패한 2단계 다음 번호
        Assert.Contains("중복 id를 둘로 셈", retried);
        Assert.StartsWith("[followUp 불가]", await FollowUp(h, runId, ["1-N1"])); // 성공한 뒤에는 1회 소진
    }

    [Fact]
    public async Task Concurrent_followups_on_same_run_send_only_one()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        using var gate = new ManualResetEventSlim(false);
        h.Client.Script = [_ => { gate.Wait(TimeSpan.FromSeconds(10)); return ScriptedChatClient.Reply(SecondAnswer)(CancellationToken.None); }];

        var first = Task.Run(() => FollowUp(h, runId, ["1-N1"]));
        await Task.Delay(300);
        var second = await FollowUp(h, runId, ["1-N1"]);
        gate.Set();
        await first;

        Assert.Contains("진행 중", second);
        Assert.Equal(2, h.Client.Calls); // 첫 실행 + 보충 1회
    }

    [Fact]
    public async Task Blank_request_ids_debate_and_golden_runs_are_refused()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        Assert.Contains("하나 이상", await FollowUp(h, runId, [" ", ""]));

        // 순번: 0 첫 실행 · 1·2 토론 두 턴 · 3 골든
        h.Client.Script = [ScriptedChatClient.Reply(FirstAnswer), ScriptedChatClient.Reply(FirstAnswer), ScriptedChatClient.Reply(SecondAnswer), ScriptedChatClient.Reply(FirstAnswer)];
        var deb = await h.Run("deb");
        var debId = deb.Split('\n')[0]["runId: ".Length..].Trim();
        Assert.Contains("Debate", await FollowUp(h, debId, ["1-N1"], mode: "deb"));

        var (goldenId, _, _) = await h.Runner.RunFixedPromptAsync("r", "## 요청\nx", null, GoldenCli.Tool, "golden t", CancellationToken.None);
        Assert.Contains("골든", await FollowUp(h, goldenId!, ["1-N1"]));
    }

    [Fact]
    public async Task Second_followup_on_same_run_is_refused()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        await FollowUp(h, runId, ["1-N1"]);
        var again = await FollowUp(h, runId, ["1-N2"]);
        Assert.StartsWith("[followUp 불가]", again);
        Assert.Contains("1회", again);
        Assert.Equal(2, h.Client.Calls);
    }

    [Theory]
    [InlineData(new[] { "1-N9" }, "r", 1, "없는 요청 ID '1-N9'")]
    [InlineData(new[] { "1-N1" }, "two", 1, "모드는 'r'")]
    [InlineData(new[] { "1-N1" }, "r", 3, "성공한 3단계가 없습니다")]
    public async Task Bad_followup_is_refused_before_any_call(string[] ids, string mode, int step, string message)
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        var result = await FollowUp(h, runId, ids, mode: mode, step: step);
        Assert.Contains(message, result);
        Assert.Equal(1, h.Client.Calls);
    }

    [Fact]
    public async Task FollowUp_needs_reason_and_material()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        Assert.StartsWith("[reason 필요]", await FollowUp(h, runId, ["1-N1"], reason: null));
        Assert.Contains("보충할 자료가 없습니다", await FollowUp(h, runId, ["1-N1"], input: ""));
        Assert.Contains("runId 'nope'의 기록이 없습니다", await FollowUp(h, "nope", ["1-N1"]));
        Assert.Equal(1, h.Client.Calls);
    }

    // ── 요청 처리 판정 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Request_ids_take_request_verdicts_and_are_kept_apart()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;

        var ok = VerdictRecorder.Record(runId, [
            new VerdictItem("1-N1", null, "supplied", FindingIds: ["1-N1"]),
            new VerdictItem("1-N2", null, "declined", Reason: "사내 설정", FindingIds: ["1-N2"]),
            new VerdictItem("1-F1", null, "rejected", Reason: "추측", FindingIds: ["1-F1"])], 500, h.LogPath);
        Assert.Contains("자료 요청 처리 2", ok);

        var saved = VerdictLog.ReadAll(VerdictLog.PathFor(h.LogPath));
        var n1 = saved.Single(v => v.FindingKey == "1-N1");
        Assert.Equal(("request", "Calls 수집부", "src/Reader.cs"), (n1.Kind, n1.Summary, n1.File));
        Assert.Equal([1], n1.Steps);
        Assert.Null(saved.Single(v => v.FindingKey == "1-F1").Kind);

        Assert.Contains("reason이 필수", VerdictRecorder.Record(runId, [new VerdictItem("1-N1", null, "declined", FindingIds: ["1-N1"])], 500, h.LogPath));
        Assert.Contains("자료 요청의 verdict 'adopted'", VerdictRecorder.Record(runId, [new VerdictItem("1-N1", null, "adopted", FindingIds: ["1-N1"])], 500, h.LogPath));
        Assert.Contains("섞을 수 없습니다", VerdictRecorder.Record(runId, [new VerdictItem("k", null, "supplied", FindingIds: ["1-N1", "1-F1"])], 500, h.LogPath));
        Assert.Contains("verdict 'supplied'", VerdictRecorder.Record(runId, [new VerdictItem("1-F1", null, "supplied", FindingIds: ["1-F1"])], 500, h.LogPath));
        Assert.DoesNotContain("findingIds에 요청 ID", VerdictRecorder.Record(runId, [new VerdictItem("1-F1", null, "supplied", FindingIds: ["1-F1"])], 500, h.LogPath));
    }

    [Fact]
    public async Task Request_verdict_without_finding_ids_points_to_finding_ids()
    {
        // 2026-10-22 · 10-23 두 번: findingKey에만 요청 ID를 넣어 지적으로 검사돼 거절됨 → 안내를 붙인다 (동작은 그대로)
        var (h, runId) = await FirstRun();
        using var _ = h;
        const string hint = "자료 요청 처리는 findingIds에 요청 ID를 넣으세요";

        Assert.Contains(hint, VerdictRecorder.Record(runId, [new VerdictItem("1-N1", "요청", "declined", Reason: "사내")], 500, h.LogPath));
        Assert.Contains(hint, VerdictRecorder.Record(runId, [new VerdictItem("my-key", "요청", "supplied")], 500, h.LogPath));
        Assert.Contains(hint, VerdictRecorder.Record(runId, [new VerdictItem("1-N2", "요청", "bogus", Reason: "x")], 500, h.LogPath));
        Assert.DoesNotContain(hint, VerdictRecorder.Record(runId, [new VerdictItem("my-key", "지적", "bogus", Reason: "x")], 500, h.LogPath));
        Assert.Empty(VerdictLog.ReadAll(VerdictLog.PathFor(h.LogPath)));
    }

    [Fact]
    public async Task Stats_count_requests_apart_from_adoption_and_list_open_ones()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        VerdictRecorder.Record(runId, [
            new VerdictItem("1-N1", null, "supplied", FindingIds: ["1-N1"]),
            new VerdictItem("1-F1", null, "adopted", FindingIds: ["1-F1"])], 500, h.LogPath);

        var s = VerdictStats.Build(VerdictLog.Latest(VerdictLog.ReadAll(VerdictLog.PathFor(h.LogPath))), h.Records(), _ => null);

        var row = s.ByAgent.Single(r => r.Key == "a1");
        Assert.Equal((1, 1.0), (row.Findings, row.Rate)); // 요청 판정(supplied)은 지적 수·채택률에 안 들어감
        Assert.Equal((2, 1, 0, 0), (s.Requests!.Total, s.Requests.Supplied, s.Requests.Declined, s.Requests.Deferred));
        Assert.Equal(["1-N2"], s.Requests.Open.Select(o => o.Id));

        Assert.Empty(s.Requests.Weeks);   // 주별 표는 review_* 모드만 ('r'은 아님) — 아래 Weekly_request_rate_counts_review_runs_only
    }

    [Fact]
    public void Weekly_request_rate_counts_review_runs_only()   // 계획서 2026-10-27_외부-첨부-범위-규칙, sub_self 11b9c52eba C1 · C2 · C5 · C6
    {
        var sunday = new DateTimeOffset(new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Local));
        var monday = sunday.AddDays(1);
        StructuredOutputRecord Out(int requests, string state = OutputStates.Complete) => new()
        {
            Format = "review", State = state, RequestedBy = "role",
            Requests = [.. Enumerable.Range(1, requests).Select(i => new InfoRequest { Id = $"1-N{i}", What = "정의" })],
        };
        CallRecord Call(string run, DateTimeOffset at, string mode, int step, StructuredOutputRecord? output, string tool = "run_mode", int? followUpOf = null) => new()
        {
            Id = $"{run}-{step}", Timestamp = at.AddMinutes(step), Tool = tool, Mode = mode, RunId = run, Step = step,
            Model = "m", Prompt = "p", Success = true, Structured = output, FollowUpOf = followUpOf,
        };
        CallRecord[] calls =
        [
            Call("r1", sunday, "review_main", 1, Out(2)),                       // 일요일 → 그 전 주 월요일(10-19)
            Call("r2", monday, "review_main", 1, Out(0)),
            Call("r2", monday, "review_main", 2, Out(1), followUpOf: 1),         // 보충 단계의 요청은 세지 않음
            Call("r3", monday, "review_high", 1, Out(1)),
            Call("r3", monday, "review_high", 2, Out(1)),                       // 두 단계 요청은 실행 하나로 합산
            Call("r4", monday, "review_main", 1, Out(3), tool: GoldenCli.Tool),  // 골든 평가 제외
            Call("r5", monday, "review_main", 1, Out(0, OutputStates.Failed)),  // 해석 실패 제외 ('요청 없음'으로 세지 않음)
            Call("r6", monday, "perf_main", 1, Out(2)),                         // review_* 아님
        ];

        var weeks = VerdictStats.Build([], calls, _ => null).Requests!.Weeks;

        Assert.Equal(
            [(new DateOnly(2026, 10, 26), 2, 1, 2), (new DateOnly(2026, 10, 19), 1, 1, 2)],
            weeks.Select(w => (w.WeekStart, w.Runs, w.RunsWithRequests, w.Requests)));
        Assert.Equal((0.5, 1.0), (weeks[0].RunRate, weeks[0].PerRun));
    }

    [Fact]
    public async Task Request_only_verdicts_do_not_make_a_run_judged_or_add_cost()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        VerdictRecorder.Record(runId, [new VerdictItem("1-N1", null, "deferred", Reason: "나중에", FindingIds: ["1-N1"])], 500, h.LogPath);

        var s = VerdictStats.Build(VerdictLog.Latest(VerdictLog.ReadAll(VerdictLog.PathFor(h.LogPath))), h.Records(), _ => 1m);

        Assert.Equal(0, s.JudgedRuns);
        Assert.Contains(s.Unjudged, u => u.RunId == runId);
        Assert.DoesNotContain(s.ByAgent, r => r.Cost is not null);
        Assert.Equal(1, s.Requests!.Deferred);
    }

    [Fact]
    public async Task Followup_sent_requests_count_as_supplied_and_its_own_requests_are_not_open()
    {
        var (h, runId) = await FirstRun();
        using var _ = h;
        h.Client.Script = [ScriptedChatClient.Reply("""{"findings":[],"needsInfo":[{"what":"또 필요","path":null,"symbol":null,"why":"x"}]}""")];
        await FollowUp(h, runId, ["1-N1"]);

        var s = VerdictStats.Build([], h.Records(), _ => null);

        Assert.Equal((3, 1, 1), (s.Requests!.Total, s.Requests.Supplied, s.Requests.FromFollowUps)); // 1-N1 자동 보충 · 2-N1은 보충 단계 요청
        Assert.Equal(["1-N2"], s.Requests.Open.Select(o => o.Id));
    }

    [Fact]
    public void Needs_info_object_without_what_fails_instead_of_vanishing()
    {
        var s = FindingParser.Parse(FindingFormat.Findings, 1, """{"findings":[],"needsInfo":[{"what":"","path":"X.cs","symbol":null,"why":"y"}]}""", ChatFinishReason.Stop, "prompt");
        Assert.Equal(OutputStates.Failed, s.State);
        Assert.Contains("what", s.Error);
    }
}
