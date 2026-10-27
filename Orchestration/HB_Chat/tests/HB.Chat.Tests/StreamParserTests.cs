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
