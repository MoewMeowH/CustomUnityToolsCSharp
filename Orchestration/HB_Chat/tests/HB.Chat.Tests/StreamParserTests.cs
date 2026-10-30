using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>stream-json 줄 읽기. 줄 모양은 0번 실측(Claude Code 2.1.311) 출력에서 경로 · 긴 값만 줄인 것.</summary>
public sealed class StreamParserTests
{
    /// <summary>서브에이전트가 부른 도구는 컨텍스트 · 칩에 넣지 않고 앱 단계 승인 기록용 이벤트로만 나온다.</summary>
    [Fact]
    public void Subagent_ToolUse_IsSeparateEvent()
    {
        var ev = Assert.IsType<SubagentToolsEvent>(StreamParser.Parse(
            """{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"echo sub"}}],"usage":{"input_tokens":9}}}"""));
        Assert.Equal("Bash", Assert.Single(ev.Tools).Name);
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"content":[{"type":"text","text":"hi"}]}}"""));
    }

    [Fact]
    public void Init_ReadsSessionVersionAndMcp()
    {
        var ev = Assert.IsType<InitEvent>(StreamParser.Parse(
            """{"type":"system","subtype":"init","session_id":"e76e7e6b-219d-41b5-a103-bd9513cfbdc2","claude_code_version":"2.1.311","permissionMode":"acceptEdits","tools":["Read"],"mcp_servers":[{"name":"hb-orchestration","status":"connected","source":"dynamic"}]}"""));
        Assert.Equal("e76e7e6b-219d-41b5-a103-bd9513cfbdc2", ev.SessionId);
        Assert.Equal("2.1.311", ev.Version);
        Assert.Equal([("hb-orchestration", "connected")], ev.McpServers);
    }

    [Fact]
    public void ControlRequest_KeepsIdSubtypeAndRawRequest()
    {
        // 3차 0번 실측 모양 (2.1.312, stdio 호스트)
        var ev = Assert.IsType<ControlRequestEvent>(StreamParser.Parse(
            """{"type":"control_request","request_id":"9f1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[]},"requires_user_interaction":true}}"""));
        Assert.Equal("9f1", ev.RequestId);
        Assert.Equal("can_use_tool", ev.Subtype);
        Assert.Contains("\"tool_name\":\"AskUserQuestion\"", ev.RequestJson);
        // request_id나 request가 없으면 다른 줄로 (답할 곳을 모름)
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"control_request","request":{"subtype":"can_use_tool"}}"""));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"control_request","request_id":"x"}"""));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"control_response","response":{"subtype":"success","request_id":"hbchat_init"}}"""));
        // 요청 거두기 (SDK 규약 모양 — 추정, 7d839f4aa5 C1)
        Assert.Equal("9f1", Assert.IsType<ControlCancelEvent>(StreamParser.Parse("""{"type":"control_cancel_request","request_id":"9f1"}""")).RequestId);
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"control_cancel_request"}"""));
    }

    [Fact]
    public void AssistantToolUse_GivesShortDetail()
    {
        // 3차 0번 실측 C: tool_use 입력 전체는 assistant 줄에 온다 (usage가 없는 줄도). 칩에는 짧은 대상만.
        var ev = Assert.IsType<UsageEvent>(StreamParser.Parse(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a","name":"Read","input":{"file_path":"C:\\ws\\src\\RoomHub.cs"}},{"type":"tool_use","id":"b","name":"Bash","input":{"command":"git status\ngit log"}},{"type":"text","text":"x"}]}}"""));
        Assert.Equal(0, ev.ContextTokens); // usage가 없는 줄 — 실행기는 컨텍스트로 쓰지 않는다
        Assert.Equal([new ToolUse("Read", "RoomHub.cs") { Id = "a" }, new ToolUse("Bash", "git status") { Id = "b" }], ev.Tools);

        using var doc = System.Text.Json.JsonDocument.Parse("""{"mode":"review_main","input":"x"}""");
        Assert.Equal("review_main", StreamParser.Detail("mcp__hb-orchestration__run_mode", doc.RootElement));
        using var upd = System.Text.Json.JsonDocument.Parse("""{"taskId":"3","status":"completed"}""");
        Assert.Equal("#3 → completed", StreamParser.Detail("TaskUpdate", upd.RootElement));
        using var longCmd = System.Text.Json.JsonDocument.Parse($$"""{"command":"{{new string('a', 100)}}"}""");
        Assert.Equal(StreamParser.DetailChars + 1, StreamParser.Detail("PowerShell", longCmd.RootElement)!.Length);
        using var other = System.Text.Json.JsonDocument.Parse("""{"x":1}""");
        Assert.Null(StreamParser.Detail("Mystery", other.RootElement));
        // 서브에이전트 줄의 도구는 칩 · 컨텍스트에 넣지 않는다
        Assert.IsType<SubagentToolsEvent>(StreamParser.Parse("""{"type":"assistant","parent_tool_use_id":"t","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}"""));
    }

    [Fact]
    public void PostTurnSummary_IsStatus()
    {
        // 3차 0번 실측 B 모양
        var ev = Assert.IsType<StatusEvent>(StreamParser.Parse(
            """{"type":"system","subtype":"post_turn_summary","summarizes_uuid":"u","status_category":"blocked","status_detail":"asked which color you prefer; awaiting Red or Blue","needs_action":"answer the color question: Red or Blue","uuid":"v","session_id":"s"}"""));
        Assert.Equal("blocked", ev.Category);
        Assert.Equal("answer the color question: Red or Blue", ev.NeedsAction);
        Assert.Null(Assert.IsType<StatusEvent>(StreamParser.Parse("""{"type":"system","subtype":"post_turn_summary","status_category":"completed","status_detail":"done","needs_action":""}""")).NeedsAction);
    }

    [Fact]
    public void Steps_ReadTasksFolderInOrder()
    {
        // 3차 0번 실측 B: ~/.claude/tasks/<세션>/<번호>.json
        var root = Path.Combine(Path.GetTempPath(), "hbchat-tasks-" + Guid.NewGuid().ToString("N"));
        const string sid = "5660e916-64bb-46d5-b611-afc7c781fd8b";
        var dir = Path.Combine(root, sid);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "10.json"), """{"id":"10","subject":"ten","status":"pending"}""");
            File.WriteAllText(Path.Combine(dir, "2.json"), """{"id":"2","subject":"two","status":"completed","blocks":[],"blockedBy":[]}""");
            File.WriteAllText(Path.Combine(dir, "3.json"), """{"id":"3","subject":"three","status":"in_progress"}""");
            File.WriteAllText(Path.Combine(dir, "4.json"), "{ broken");
            File.WriteAllText(Path.Combine(dir, "5.json"), """{"id":"5","subject":"gone","status":"deleted"}""");
            var steps = Steps.Read(root, sid);
            Assert.Equal(["2", "3", "10"], steps.Select(s => s.Id));
            Assert.Equal("1/3", Steps.Progress(steps));
            Assert.Equal("three", Steps.Current(steps)!.Subject);
            Assert.Empty(Steps.Read(root, "../escape")); // 세션 ID 모양이 아니면 읽지 않는다
            Assert.Empty(Steps.Read(root, Guid.NewGuid().ToString("D")));
            Assert.Empty(Steps.Read(null, sid)); // 위치를 모르면 빈 목록 (ea28ba9185 C4)
            Assert.Null(Steps.Progress([]));

            // 100개가 넘으면 번호 순 앞 100개 — 이름순(1, 10, 100, 101 …)으로 자르지 않는다 (ea28ba9185 C5)
            for (var i = 1; i <= Steps.MaxFiles + 5; i++)
                File.WriteAllText(Path.Combine(dir, $"{i}.json"), $$"""{"id":"{{i}}","subject":"s{{i}}","status":"pending"}""");
            var many = Steps.Read(root, sid);
            Assert.Equal(Steps.MaxFiles, many.Count);
            Assert.Equal(Enumerable.Range(1, Steps.MaxFiles).Select(i => i.ToString()), many.Select(s => s.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Question_ParsesValidatesAndAddsAnswers()
    {
        // 3차 0번 실측 A의 AskUserQuestion 입력
        const string input = """{"questions":[{"question":"Which color do you prefer?","header":"Color","options":[{"label":"Red","description":"Choose Red"},{"label":"Blue","description":"Choose Blue"}],"multiSelect":false}]}""";
        var q = QuestionRequest.Parse(input)!;
        var item = Assert.Single(q.Items);
        Assert.Equal("Color", item.Header);
        Assert.Equal(["Red", "Blue"], item.Options.Select(o => o.Label));

        Assert.NotNull(q.Validate(new Dictionary<string, string>()));
        Assert.NotNull(q.Validate(new Dictionary<string, string> { ["Which color do you prefer?"] = "  " }));
        Assert.NotNull(q.Validate(new Dictionary<string, string> { ["Which color do you prefer?"] = new string('a', QuestionRequest.MaxAnswerChars + 1) }));
        var answers = new Dictionary<string, string> { ["Which color do you prefer?"] = "Red " };
        Assert.Null(q.Validate(answers));

        // 허용 답의 updatedInput = 원래 입력 + answers (0번 실측 A에서 지휘자가 "You answered Red."로 받음)
        using var doc = System.Text.Json.JsonDocument.Parse(q.WithAnswers(answers));
        Assert.Equal("Red", doc.RootElement.GetProperty("answers").GetProperty("Which color do you prefer?").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("questions").GetArrayLength());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":[{"question":"","options":[]}]}""")]
    [InlineData("""{"questions":[{"question":"q","options":[{"description":"no label"}]}]}""")]
    [InlineData("""{"questions":[{"question":"q","options":[]},{"question":"q","options":[]}]}""")] // 같은 질문 글 둘 — 답의 키가 겹친다
    public void Question_RejectsWrongShape(string input) => Assert.Null(QuestionRequest.Parse(input));

    [Fact]
    public void AssistantUsage_IsContextSize_AndResultHasWindow()
    {
        // 2-2 0번 실측 모양 (haiku 5.5): 마지막 assistant usage = 그 요청의 컨텍스트, result modelUsage.contextWindow
        var usage = Assert.IsType<UsageEvent>(StreamParser.Parse(
            """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"input_tokens":2,"cache_creation_input_tokens":381,"cache_read_input_tokens":7607,"cache_creation":{"ephemeral_1h_input_tokens":381},"output_tokens":8}},"session_id":"s"}"""));
        Assert.Equal(7998, usage.ContextTokens);
        Assert.Equal("claude-opus-5-5", usage.Model);
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"usage":{"input_tokens":9}}}"""));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"assistant","message":{"content":[]}}"""));

        // 보조 모델(1M)이 섞여도 메시지 모델(200K)의 창 — 없으면 입력이 가장 많은 모델 (/self-review 83b453653b C2)
        var result = Assert.IsType<ResultEvent>(StreamParser.Parse(
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","total_cost_usd":0.001,"permission_denials":[],"modelUsage":{"claude-haiku-5-5":{"inputTokens":50,"contextWindow":1000000},"claude-opus-5-5":{"inputTokens":2,"cacheReadInputTokens":90000,"contextWindow":200000}}}"""));
        Assert.Equal(200000, result.WindowFor("claude-opus-5-5"));
        Assert.Equal(200000, result.WindowFor("unknown")); // 입력 많은 쪽
        Assert.Equal(200000, result.WindowFor(null));
        Assert.Equal(1000000, result.WindowFor("claude-haiku-5-5"));
        Assert.Null(Assert.IsType<ResultEvent>(StreamParser.Parse("""{"type":"result","subtype":"success","result":"ok"}""")).WindowFor("x"));
    }

    [Fact]
    public void TextDeltaAndToolStart()
    {
        Assert.Equal(new DeltaEvent("안녕"), StreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"안녕"}},"session_id":"x"}"""));
        Assert.Equal(new ToolEvent("mcp__hb-orchestration__list_modes"), StreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t","name":"mcp__hb-orchestration__list_modes","input":{}}}}"""));
        // thinking · input_json 조각은 화면에 쓰지 않는다.
        Assert.IsType<OtherEvent>(StreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{"}}}"""));
    }

    [Fact]
    public void PermissionDenied()
    {
        var ev = Assert.IsType<DeniedEvent>(StreamParser.Parse(
            """{"type":"system","subtype":"permission_denied","tool_name":"Write","tool_use_id":"toolu_1","decision_reason_type":"asyncAgent","decision_reason":"no approval surface in this session"}"""));
        Assert.Equal("Write", ev.Tool);
        Assert.StartsWith("no approval surface", ev.Reason);
    }

    [Fact]
    public void Result_SuccessAndError()
    {
        var ok = Assert.IsType<ResultEvent>(StreamParser.Parse(
            """{"type":"result","subtype":"success","is_error":false,"result":"MANGO","total_cost_usd":0.00103859,"session_id":"s","permission_denials":[{"tool_name":"Write"},{"tool_name":"mcp__x"}]}"""));
        Assert.True(ok.Ok);
        Assert.Equal("MANGO", ok.Text);
        Assert.Equal(0.00103859, ok.CostUsd);
        Assert.Equal(2, ok.Denials);

        var err = Assert.IsType<ResultEvent>(StreamParser.Parse("""{"type":"result","subtype":"error_max_turns","is_error":true}"""));
        Assert.False(err.Ok);
        Assert.Null(err.CostUsd);

        // subtype success인데 is_error면 실패로 본다.
        Assert.False(Assert.IsType<ResultEvent>(StreamParser.Parse("""{"type":"result","subtype":"success","is_error":true,"result":"x"}""")).Ok);
    }

    [Fact]
    public void Retry()
    {
        var ev = Assert.IsType<RetryEvent>(StreamParser.Parse("""{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"error":"overloaded"}"""));
        Assert.Equal(2, ev.Attempt);
        Assert.Equal("overloaded", ev.Error);
    }

    [Theory]
    [InlineData("""{"type":"rate_limit_event"}""", "rate_limit_event")]
    [InlineData("""{"type":"system","subtype":"task_summary","detail":"x"}""", "system/task_summary")] // post_turn_summary는 3차 b부터 StatusEvent
    [InlineData("""{"type":"assistant","message":{"content":[]}}""", "assistant")]
    [InlineData("not json", "invalid")]
    [InlineData("[1,2]", "invalid")]
    public void OtherLines(string line, string type) =>
        Assert.Equal(new OtherEvent(type), StreamParser.Parse(line));

    [Fact]
    public void BlankIsNull() => Assert.Null(StreamParser.Parse("  "));
}
