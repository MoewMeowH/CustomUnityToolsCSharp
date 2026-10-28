using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>stream-json 줄 읽기. 줄 모양은 0번 실측(Claude Code 2.1.311) 출력에서 경로 · 긴 값만 줄인 것.</summary>
public sealed class StreamParserTests
{
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
    [InlineData("""{"type":"system","subtype":"post_turn_summary"}""", "system/post_turn_summary")]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""", "assistant")]
    [InlineData("not json", "invalid")]
    [InlineData("[1,2]", "invalid")]
    public void OtherLines(string line, string type) =>
        Assert.Equal(new OtherEvent(type), StreamParser.Parse(line));

    [Fact]
    public void BlankIsNull() => Assert.Null(StreamParser.Parse("  "));
}
