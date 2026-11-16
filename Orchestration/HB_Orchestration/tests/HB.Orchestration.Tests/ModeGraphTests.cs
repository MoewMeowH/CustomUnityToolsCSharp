using System.ClientModel;
using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>Graph 흐름: 단계 연결 풀이(ModeGraph)와 그 실행 · 반환 범위 · 표기 · 대시보드 검증.</summary>
public sealed class ModeGraphTests
{
    private const string A = """{ "Id": "a", "Role": "R", "Agent": "a1" }""";
    private const string B = """{ "Id": "b", "Role": "R", "Agent": "a1" }""";

    private const string Modes = $$"""
        {
          "Roles": { "R": { "Prompt": "역할 R" }, "Rev": { "Prompt": "역할", "Output": "findings" } },
          "Modes": {
            "merge":    { "Flow": "Graph", "Steps": [ {{A}}, {{B}}, { "Role": "R", "Agent": "a1", "From": ["b", "input", "A"] } ] },
            "mergeAll": { "Flow": "Graph", "Return": "All", "Steps": [ {{A}}, {{B}}, { "Role": "R", "Agent": "a1", "From": ["input", "a", "b"] } ] },
            "blind":    { "Flow": "Graph", "Steps": [ {{A}}, { "Role": "R", "Agent": "a1", "From": ["a"] } ] },
            "ends":     { "Flow": "Graph", "Steps": [ {{A}}, { "Role": "R", "Agent": "a1", "From": ["a"] }, { "Role": "R", "Agent": "a1", "From": ["a"] } ] },
            "diamond":  { "Flow": "Graph", "Steps": [ {{A}}, { "Id": "b", "Role": "R", "Agent": "a2", "From": ["a"] }, { "Id": "c", "Role": "R", "Agent": "a1", "From": ["a"] },
                                                    { "Role": "R", "Agent": "a1", "From": ["b", "c"] } ] },
            "twofail":  { "Flow": "Graph", "Steps": [ {{A}}, { "Id": "b", "Role": "R", "Agent": "a2", "From": ["a"] }, { "Id": "c", "Role": "R", "Agent": "a2", "From": ["a"] },
                                                    { "Role": "R", "Agent": "a1", "From": ["b"] } ] },
            "gfu":      { "Flow": "Graph", "Steps": [ { "Id": "a", "Role": "Rev", "Agent": "a1" }, { "Role": "R", "Agent": "a1", "From": ["a"] } ] },
            "gfu3":     { "Flow": "Graph", "Steps": [ { "Id": "a", "Role": "Rev", "Agent": "a1" }, { "Id": "b", "Role": "R", "Agent": "a2", "From": ["a"] },
                                                    { "Role": "R", "Agent": "a1", "From": ["b"] } ] },
            "imgOk":    { "Flow": "Graph", "Steps": [ {{A}}, { "Role": "R", "Agent": "a2", "From": ["a"] } ] },
            "imgNo":    { "Flow": "Graph", "Steps": [ {{A}}, { "Role": "R", "Agent": "a2", "From": ["input", "a"] } ] },
            "gfb":      { "Flow": "Graph", "Steps": [ { "Id": "a", "Role": "R", "Agent": "a1", "FallbackAgent": "a2" }, { "Role": "R", "Agent": "a1", "From": ["a"] } ] },
            "gone":     { "Flow": "Graph", "Steps": [ { "Role": "Rev", "Agent": "a1" } ] },
            "gbad":     { "Flow": "Graph", "Steps": [ { "Role": "Rev", "Agent": "a1", "From": ["x"] } ] }
          }
        }
        """;

    /// <summary>모드 하나("g")만 든 modes.json.</summary>
    private static string Json(string mode) => $$"""{ "Roles": { "R": { "Prompt": "역할 R" } }, "Modes": { "g": {{mode}} } }""";

    private static StepDefinition S(string? id = null, string[]? from = null, StepSees? sees = null) =>
        new() { Id = id, Role = "R", Agent = "a1", From = from?.ToList(), Sees = sees };

    private static ModeDefinition Mode(ModeFlow flow, params StepDefinition[] steps) => new() { Flow = flow, Steps = [.. steps] };

    private static void AssertNode(GraphNode node, int[] wait, int[] sees, bool input)
    {
        Assert.Equal(wait, node.WaitFor);
        Assert.Equal(sees, node.Sees);
        Assert.Equal(input, node.SeesInput);
    }

    private static List<string> Prompts(ModeRunnerHarness h) =>
        [.. h.Records().OrderBy(r => r.Step).Select(r => r.Prompt.ReplaceLineEndings("\n"))];

    // ── 연결 풀이 ────────────────────────────────────────────────────────────

    [Fact]
    public void Sequential_waits_for_the_previous_step_and_sees_by_setting()
    {
        var (nodes, error) = ModeGraph.Resolve(Mode(ModeFlow.Sequential,
            S(sees: StepSees.Previous), S(sees: StepSees.Input), S(sees: StepSees.Previous), S(), S(sees: StepSees.All)));

        Assert.Null(error);
        AssertNode(nodes![0], wait: [], sees: [], input: true);                     // 첫 단계는 Sees와 상관없이 요청만
        AssertNode(nodes[1], wait: [1], sees: [], input: true);                     // Input: 기다리기만 하고 보지 않는다
        AssertNode(nodes[2], wait: [2], sees: [2], input: false);
        AssertNode(nodes[3], wait: [3], sees: [3], input: true);                    // 기본값 = InputAndPrevious
        AssertNode(nodes[4], wait: [1, 2, 3, 4], sees: [1, 2, 3, 4], input: true);
        Assert.Equal([5], ModeGraph.Terminals(nodes).Order());
    }

    [Fact]
    public void Concurrent_steps_wait_for_nothing_and_are_all_terminal()
    {
        var (nodes, error) = ModeGraph.Resolve(Mode(ModeFlow.Concurrent, S(), S(), S()));

        Assert.Null(error);
        Assert.All(nodes!, n => AssertNode(n, wait: [], sees: [], input: true));
        Assert.Equal([1, 2, 3], ModeGraph.Terminals(nodes!).Order());
    }

    [Fact]
    public void Graph_from_names_earlier_steps_and_defaults_to_the_request()
    {
        var (nodes, error) = ModeGraph.Resolve(Mode(ModeFlow.Graph,
            S("a"), S("b", from: ["a"]), S("c", from: ["INPUT", "B", "a"]), S(from: ["a"])));

        Assert.Null(error);
        AssertNode(nodes![0], wait: [], sees: [], input: true);         // From 없음 = 요청
        AssertNode(nodes[1], wait: [1], sees: [1], input: false);
        AssertNode(nodes[2], wait: [1, 2], sees: [1, 2], input: true);  // 이름은 대소문자 무시, 단계 번호 차례로
        AssertNode(nodes[3], wait: [1], sees: [1], input: false);
        Assert.Equal([3, 4], ModeGraph.Terminals(nodes).Order());
    }

    [Fact]
    public void Debate_is_only_checked_not_resolved()
    {
        var (nodes, error) = ModeGraph.Resolve(Mode(ModeFlow.Debate, S("x"), S("y")));

        Assert.Null(error);
        Assert.Empty(nodes!);
        Assert.Contains("겹칩니다", ModeGraph.Resolve(Mode(ModeFlow.Debate, S("x"), S("X"))).Error);
    }

    [Theory]
    [InlineData("""{ "Id": "a b", "Role": "R", "Agent": "a1" }""", "1단계의 Id 'a b'은(는) 영문")]
    [InlineData("""{ "Id": "", "Role": "R", "Agent": "a1" }""", "1단계의 Id ''은(는) 영문")]
    [InlineData("""{ "Id": "a\n", "Role": "R", "Agent": "a1" }""", "은(는) 영문")] // 끝에 줄바꿈이 붙은 값
    [InlineData("""{ "Id": "Input", "Role": "R", "Agent": "a1" }""", "\"input\"은 쓸 수 없습니다")]
    [InlineData("""{ "Id": "a", "Role": "R", "Agent": "a1" }, { "Id": "A", "Role": "R", "Agent": "a1" }""", "2단계의 Id 'A'이(가) 1단계와 겹칩니다")]
    [InlineData("""{ "Role": "R", "Agent": "a1", "From": [] }""", "1단계의 From이 비어 있습니다")]
    [InlineData("""{ "Role": "R", "Agent": "a1", "From": ["x"] }""", "1단계의 From 'x'은(는) 이 모드에 없는 Id")]
    [InlineData("""{ "Role": "R", "Agent": "a1", "From": [null] }""", "이 모드에 없는 Id")]
    [InlineData("""{ "Id": "a", "Role": "R", "Agent": "a1", "From": ["a"] }""", "1단계의 From 'a'은(는) 자기 자신입니다")]
    [InlineData("""{ "Role": "R", "Agent": "a1", "From": ["b"] }, { "Id": "b", "Role": "R", "Agent": "a1" }""", "1단계의 From 'b'은(는) 뒤에 있는 2단계입니다")]
    [InlineData("""{ "Id": "a", "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1", "From": ["a", "A"] }""", "2단계의 From에 'A'이(가) 두 번")]
    [InlineData("""{ "Role": "R", "Agent": "a1", "From": ["input", "INPUT"] }""", "\"input\"이 두 번")]
    [InlineData("""{ "Role": "R", "Agent": "a1", "Sees": "Input" }""", "1단계에 Sees가 있습니다")]
    public async Task Graph_with_wrong_wiring_is_rejected_before_any_call(string steps, string expected)
    {
        using var h = new ModeRunnerHarness(Json($$"""{ "Flow": "Graph", "Steps": [ {{steps}} ] }"""));

        var result = await h.Run("g");

        Assert.StartsWith("[모드 'g'] ", result);
        Assert.Contains(expected, result);
        Assert.Equal(0, h.Client.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""" "Flow": "Concurrent", """)]
    [InlineData(""" "Flow": "Debate", """)]
    public async Task From_outside_a_graph_mode_is_rejected(string flow)
    {
        using var h = new ModeRunnerHarness(Json($$"""{ {{flow}} "Steps": [ {{A}}, { "Role": "R", "Agent": "a1", "From": ["a"] } ] }"""));

        var result = await h.Run("g");

        Assert.StartsWith("[모드 'g'] 2단계에 From이 있습니다. From은 Flow가 Graph인 모드에서만 씁니다", result);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Undefined_flow_value_is_rejected_before_any_call()
    {
        var json = Json($$"""{ "Flow": 7, "Steps": [ {{A}} ] }"""); // 열거형 변환기는 정의되지 않은 숫자도 받는다
        using var h = new ModeRunnerHarness(json);

        var result = await h.Run("g");

        Assert.StartsWith("[모드 'g'] Flow 값 '7'", result);
        Assert.Equal(0, h.Client.Calls);
        Assert.Contains("(설정 오류: Flow 값 '7'", h.Runner.Describe());
        Assert.Contains(ModesConfigService.Validate(json, [new AgentInfo("a1", "p", "m", null, true)]).Errors,
            e => e.StartsWith("[g] Flow 값 '7'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Graph_step_count_is_capped()
    {
        var one = """{ "Role": "R", "Agent": "a1" }""";
        using var ok = new ModeRunnerHarness(Json($$"""{ "Flow": "Graph", "Steps": [ {{string.Join(", ", Enumerable.Repeat(one, ModeDefinition.MaxGraphStepsHardCap))}} ] }"""));
        using var over = new ModeRunnerHarness(Json($$"""{ "Flow": "Graph", "Steps": [ {{string.Join(", ", Enumerable.Repeat(one, ModeDefinition.MaxGraphStepsHardCap + 1))}} ] }"""));

        Assert.StartsWith("runId: ", await ok.Run("g"));
        Assert.Equal(ModeDefinition.MaxGraphStepsHardCap, ok.Client.Calls);
        Assert.Contains("Graph 모드는 단계가 8개까지입니다 (지금 9개)", await over.Run("g"));
        Assert.Equal(0, over.Client.Calls);
    }

    // ── 실행 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merging_step_sees_the_request_and_both_earlier_results_in_step_order()
    {
        using var h = new ModeRunnerHarness(Modes);

        var result = await h.Run("merge");

        Assert.Equal([
            "## 요청\n요청",
            "## 요청\n요청",
            "## 요청\n요청\n\n## 앞 단계 결과\n### 1. R\n응답\n\n### 2. R\n응답",
        ], Prompts(h));
        Assert.Equal([1, 2, 3], h.Records().Select(r => r.Step!.Value).Order());
        Assert.Contains("### 3. R", result);        // 반환 기본값 = 끝 단계만
        Assert.DoesNotContain("### 1. R", result);
        Assert.DoesNotContain("### 2. R", result);
    }

    [Fact]
    public async Task Return_all_shows_every_step_in_step_order()
    {
        using var h = new ModeRunnerHarness(Modes);

        var result = await h.Run("mergeAll");

        var at = new[] { "### 1. R", "### 2. R", "### 3. R" }.Select(t => result.IndexOf(t, StringComparison.Ordinal)).ToList();
        Assert.True(at[0] >= 0 && at[0] < at[1] && at[1] < at[2]);
    }

    [Fact]
    public async Task Step_that_names_only_an_earlier_step_does_not_get_the_request()
    {
        using var h = new ModeRunnerHarness(Modes);

        await h.Run("blind");

        Assert.Equal("## 앞 단계 결과\n### 1. R\n응답", Prompts(h)[1]);
    }

    [Fact]
    public async Task Every_terminal_step_is_returned()
    {
        using var h = new ModeRunnerHarness(Modes);

        var result = await h.Run("ends");

        Assert.Contains("### 2. R", result);
        Assert.Contains("### 3. R", result);
        Assert.DoesNotContain("### 1. R", result);
    }

    [Fact]
    public async Task Failed_branch_skips_only_the_steps_that_wait_for_it()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);
        h.FallbackClient.Script = [ScriptedChatClient.Throw(FakeResponse.Error(500))]; // 2단계(a2)만 실패

        var result = await h.Run("diamond");

        Assert.Equal(2, h.Client.Calls);            // 1단계와 3단계 — 4단계는 부르지 않는다
        Assert.Equal(1, h.FallbackClient.Calls);
        Assert.Equal([1, 2, 3], h.Records().Select(r => r.Step!.Value).Order());
        Assert.Contains("(실패: 단계 2 · 실행하지 않음: 단계 4)", result);
        Assert.Contains("### 2. R", result);        // 실패한 단계는 보인다
        Assert.DoesNotContain("### 4. R", result);
        Assert.DoesNotContain("### 3. R", result);  // 끝 단계가 아닌 성공 단계는 Last에서 빠진다
    }

    [Fact]
    public async Task Skip_note_lists_failures_and_skipped_steps_separately()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);
        h.FallbackClient.Script = [ScriptedChatClient.Throw(FakeResponse.Error(500))]; // 2 · 3단계(a2) 실패, 4단계는 2단계만 기다린다

        var result = await h.Run("twofail");

        Assert.Contains("(실패: 단계 2, 3 · 실행하지 않음: 단계 4)", result);
    }

    [Fact]
    public async Task Cancel_during_a_step_stops_the_run_before_later_steps()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var cts = new CancellationTokenSource();
        h.Client.Script = [_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Runner.RunAsync("blind", "요청", null, null, null, null, ModeRunnerHarness.DefaultReason, null, cts.Token));

        Assert.Equal(1, h.Client.Calls); // 1단계를 기다리던 2단계는 부르지 않는다
    }

    [Fact]
    public async Task Images_need_vision_only_on_graph_steps_that_see_the_request()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);
        h.Options.Context.ExtraRoots["tc"] = h.Root;
        File.WriteAllBytes(Path.Combine(h.Root, "a.png"), PngHeader(512, 288));
        h.Options.Agents[1].Vision = false; // a2

        Task<string> Run(string mode) =>
            h.Runner.RunAsync(mode, "질문", null, null, null, ["@tc/a.png"], ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

        Assert.StartsWith("runId: ", await Run("imgOk"));   // a2 단계는 앞 단계 결과만 본다 — 이미지가 가지 않는다
        Assert.StartsWith("[모드 'imgNo'] 이미지를 받지 않는 에이전트(Vision=false): a2", await Run("imgNo"));
    }

    /// <summary>머리만 있는 png (서버는 머리만 읽는다).</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    [Fact]
    public async Task Follow_up_works_on_a_graph_step()
    {
        const string first = """
            {"findings":[],"needsInfo":[{"what":"Calls 수집부","path":"src/Reader.cs","symbol":null,"why":"중복 처리"}]}
            """;
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply(first), ScriptedChatClient.Reply("둘"), ScriptedChatClient.Reply("""{"findings":[],"needsInfo":[]}""")];
        var run = await h.Run("gfu");
        var runId = run.Split('\n')[0]["runId: ".Length..].Trim();

        var result = await h.Runner.RunFollowUpAsync("gfu", new FollowUpRequest(runId, 1, ["1-N1"]), "찾은 것", null, null, "자료 보충", CancellationToken.None);

        Assert.StartsWith($"runId: {runId}", result);
        Assert.Contains("(보충 재질문: 1단계 → 3단계 · 보충한 요청 1-N1)", result);
        Assert.Equal(3, h.Client.Calls);
    }

    [Fact]
    public async Task Follow_up_step_number_does_not_reuse_a_skipped_step_number()
    {
        const string first = """
            {"findings":[],"needsInfo":[{"what":"Calls 수집부","path":"src/Reader.cs","symbol":null,"why":"중복 처리"}]}
            """;
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);
        h.Client.Script = [ScriptedChatClient.Reply(first), ScriptedChatClient.Reply("""{"findings":[],"needsInfo":[]}""")];
        h.FallbackClient.Script = [ScriptedChatClient.Throw(FakeResponse.Error(500))]; // 2단계 실패 → 3단계는 실행되지 않는다
        var run = await h.Run("gfu3");
        var runId = run.Split('\n')[0]["runId: ".Length..].Trim();

        var result = await h.Runner.RunFollowUpAsync("gfu3", new FollowUpRequest(runId, 1, ["1-N1"]), "찾은 것", null, null, "자료 보충", CancellationToken.None);

        Assert.Contains("(보충 재질문: 1단계 → 4단계 · 보충한 요청 1-N1)", result); // 실행되지 않은 3단계 번호를 쓰지 않는다
    }

    [Fact]
    public async Task Quota_failure_reruns_the_graph_mode_on_the_fallback_agent()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);
        h.Client.Script = [ScriptedChatClient.Throw(new ClientResultException("HTTP 429 (insufficient_quota: credit_balance_exhausted)", new FakeResponse(429)))];

        var result = await h.Run("gfb");

        Assert.Contains("한도 소진으로 실패 → 대체 에이전트로 다시 실행", result);
        Assert.Contains("a1→a2", result);
        Assert.Equal(1, h.Client.Calls);            // 1단계 실패 뒤 2단계는 부르지 않았다
        Assert.Equal(2, h.FallbackClient.Calls);    // 다시 실행은 두 단계 모두 대체 에이전트로
        Assert.Contains("### 2. R", result);
    }

    [Fact]
    public void Golden_single_step_accepts_a_graph_mode_and_multi_step_rejects_it()
    {
        using var h = new ModeRunnerHarness(Modes);

        Assert.Null(h.Runner.PreviewFixedPrompt("gone", null, 10).Error);
        Assert.Contains("1단계의 From 'x'은(는) 이 모드에 없는 Id", h.Runner.PreviewFixedPrompt("gbad", null, 10).Error);
        Assert.Contains("골든 다단계 실행은 Sequential 모드만 됩니다", h.Runner.PreviewFixedMode("merge", null, 10).Error);
    }

    // ── 표기 ────────────────────────────────────────────────────────────────

    [Fact]
    public void List_modes_shows_what_each_graph_step_sees()
    {
        using var h = new ModeRunnerHarness(Modes);

        var lines = h.Runner.Describe().Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Contains("- merge:  [R@a1, R@a1, R@a1←요청·1·2]", lines);
        Assert.Contains("- blind:  [R@a1, R@a1←1]", lines);
        Assert.Contains(lines, l => l.StartsWith("- gbad:  [Rev@a1] (설정 오류: 1단계의 From 'x'", StringComparison.Ordinal));
    }

    // ── 대시보드 ────────────────────────────────────────────────────────────

    [Fact]
    public void Dashboard_validation_reports_wrong_wiring()
    {
        AgentInfo[] agents = [new("a1", "p", "m", null, true)];

        var bad = ModesConfigService.Validate(Json("""{ "Flow": "Graph", "Steps": [ { "Role": "R", "Agent": "a1", "From": ["x"] } ] }"""), agents);
        var good = ModesConfigService.Validate(Json($$"""{ "Flow": "Graph", "Steps": [ {{A}}, { "Role": "R", "Agent": "a1", "From": ["input", "a"] } ] }"""), agents);

        Assert.Contains(bad.Errors, e => e.StartsWith("[g] 1단계의 From 'x'", StringComparison.Ordinal));
        Assert.True(good.Ok);
    }

    [Fact]
    public void Dashboard_agent_replace_keeps_id_and_from()
    {
        var dir = Directory.CreateTempSubdirectory("hb-modes-graph-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "modes.json"),
                Json($$"""{ "Flow": "Graph", "Steps": [ {{A}}, {{B}}, { "Role": "R", "Agent": "a1", "From": ["input", "a", "b"] } ] }"""));
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
                {"Orchestration":{"Providers":{"p":{"Type":"openai-compatible"}},"Agents":[{"Name":"a1","Provider":"p","Model":"m"},{"Name":"a2","Provider":"p","Model":"m"}]}}
                """);
            var service = new ModesConfigService(Options.Create(new DashboardOptions { McpConfigDir = dir }), _ => null);

            var saved = service.ReplaceAgent(service.Load().Stamp, "a1", "a2"); // 파일 전체를 다시 쓰는 저장

            Assert.True(saved.Ok, string.Join(" / ", saved.Errors));
            var mode = service.Load().Typed.Modes["g"];
            Assert.Equal(ModeFlow.Graph, mode.Flow);
            Assert.Equal(["a2", "a2", "a2"], mode.Steps.Select(s => s.Agent));
            Assert.Equal(["a", "b", null], mode.Steps.Select(s => s.Id));
            Assert.Equal(["input", "a", "b"], mode.Steps[2].From);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
