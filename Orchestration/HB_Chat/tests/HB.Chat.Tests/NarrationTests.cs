using HB.Chat;
using HB.Orchestration.Shared;

namespace HB.Chat.Tests;

/// <summary>
/// 서버가 요약한 도구 호출 사이 글 (thinking.display: updates — 생각 블록 + 서명 block_kind "narration"). 서명은 합성 값.
/// </summary>
public sealed class NarrationTests
{
    private const string Narration = "CAQSDQoLQgluYXJyYXRpb24=";
    private const string Thinking = "CAQSDAoKQgh0aGlua2luZw==";

    [Theory]
    [InlineData(Narration, true)]
    [InlineData("CAQSDQoLQgluYXJyYXRpb24", true)] // 끝 = 없는 base64
    [InlineData(Thinking, false)]
    [InlineData("CAQ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not base64 !!", false)]
    public void IsNarration_ReadsBlockKind(string? signature, bool want) =>
        Assert.Equal(want, ClaudeThinking.IsNarration(signature));

    private static string Assistant(string content, string extra = "") =>
        "{\"type\":\"assistant\"" + extra + ",\"message\":{\"usage\":{\"input_tokens\":2},\"content\":[" + content + "]}}";

    [Fact]
    public void Parser_NarrationOnly()
    {
        var usage = Assert.IsType<UsageEvent>(StreamParser.Parse(Assistant(
            $$"""{"type":"thinking","thinking":"요약 글\n\n","signature":"{{Narration}}"},{"type":"thinking","thinking":"진짜 생각","signature":"{{Thinking}}"},{"type":"thinking","thinking":"","signature":"{{Narration}}"}""")));
        Assert.Equal(["요약 글"], usage.Narrations);
    }

    [Fact]
    public void Parser_TextBlocks()
    {
        var usage = Assert.IsType<UsageEvent>(StreamParser.Parse(Assistant("""{"type":"text","text":"중간 글\n"},{"type":"text","text":"  "}""")));
        Assert.Equal(["중간 글"], usage.Texts);
        Assert.Empty(usage.Narrations);
    }

    [Fact]
    public void Parser_TextBlockStart() =>
        Assert.IsType<TextBlockStartEvent>(StreamParser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}}"""));

    /// <summary>내보내기: 서버 요약임을 제목에 밝힌다.</summary>
    [Fact]
    public void Export_MarksNarration()
    {
        var room = new RoomStore(Path.Combine(Path.GetTempPath(), "hbchat-narr-" + Guid.NewGuid().ToString("N") + ".json")).Create("ws", PermissionLevel.Read, "opus", "제목");
        var md = ChatExport.Markdown(room, new ChatTranscript
        {
            FilePath = "x.jsonl",
            Entries = [new(1, DateTimeOffset.Now, ChatSpeaker.Conductor, "요약 글", 0) { Narration = true }],
        }, DateTimeOffset.Now);
        Assert.Contains("### 지휘자 (요약) · ", md);
    }

    [Fact]
    public void Parser_SubagentNarrationIgnored() =>
        Assert.IsNotType<UsageEvent>(StreamParser.Parse(Assistant(
            $$"""{"type":"thinking","thinking":"서브에이전트 글","signature":"{{Narration}}"}""", ",\"parent_tool_use_id\":\"toolu_1\"")));

    /// <summary>안 읽은 수 · 미리보기는 일반 글만 (결정). 마지막 말이 요약이어도 미리보기는 그 앞 답.</summary>
    [Fact]
    public void Summary_SkipsNarration()
    {
        var room = new RoomStore(Path.Combine(Path.GetTempPath(), "hbchat-narr-" + Guid.NewGuid().ToString("N") + ".json"))
            .Create("ws", PermissionLevel.Read, "opus", "제목") with { ReadUpTo = 0 };
        var chat = new ChatTranscript
        {
            FilePath = "x.jsonl",
            Entries =
            [
                new(1, null, ChatSpeaker.Conductor, "답", 0),
                new(2, null, ChatSpeaker.Conductor, "요약 글", 0) { Narration = true },
            ],
        };
        var summary = RoomList.Build(room, chat, RoomActivity.Idle);
        Assert.Equal(1, summary.Unread);
        Assert.Equal("답", summary.Preview);
    }
}
