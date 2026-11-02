using HB.Chat;
using HB.Chat.Pages;
using HB.Orchestration.Shared;

namespace HB.Chat.Tests;

/// <summary>외부 에이전트 말풍선을 턴 도중에 보이기: run_mode 결과 신호 · 새 줄 뒤 말풍선 조각.</summary>
public sealed class RoomLiveRunsTests(ChatApp app) : IClassFixture<ChatApp>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 1, 0, 0, TimeSpan.Zero);

    private static ChatEntry E(int line, ChatSpeaker who, int seconds) => new(line, T0.AddSeconds(seconds), who, "글", 0);

    private static RunBubble Bubble(string key, int seconds) =>
        new(key, T0.AddSeconds(seconds), RunBubbleKind.External, "codex_cli_sol", "review_main · 1단계", "응답", null, false, "0123456789", "c1");

    [Fact]
    public void RunModeTool_AndResultArrival()
    {
        Assert.True(RoomHub.IsRunModeTool("mcp__hb-orchestration__run_mode"));
        Assert.True(RoomHub.IsRunModeTool("mcp__other-name__run_mode"));       // 서버 이름은 설정마다
        Assert.False(RoomHub.IsRunModeTool("mcp__hb-orchestration__list_modes"));
        Assert.False(RoomHub.IsRunModeTool("run_mode"));

        var waiting = new HashSet<string>(StringComparer.Ordinal) { "toolu_run", "toolu_bg" };
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        Assert.False(RoomHub.RunResultArrived(new ToolImagesEvent([]) { ToolUseIds = ["toolu_other"] }, waiting, tasks));
        Assert.True(RoomHub.RunResultArrived(new ToolImagesEvent([]) { ToolUseIds = ["toolu_other", "toolu_run"] }, waiting, tasks));
        Assert.False(RoomHub.RunResultArrived(new ToolImagesEvent([]) { ToolUseIds = ["toolu_run"] }, waiting, tasks)); // 받은 id는 지운다

        // 백그라운드로 넘어간 run_mode: 안내가 온 때는 알리지 않고, 바로 그 작업이 끝날 때만 (다른 백그라운드 작업 끝은 아님 — 6b6443f26e C2)
        var moved = new ToolImagesEvent([]) { ToolUseIds = ["toolu_bg"], BackgroundTasks = new Dictionary<string, string> { ["toolu_bg"] = "task7" } };
        Assert.False(RoomHub.RunResultArrived(moved, waiting, tasks));
        Assert.False(RoomHub.RunResultArrived(new TaskEvent("done", "bash1", null, null, "completed", null), waiting, tasks));
        Assert.False(RoomHub.RunResultArrived(new TaskEvent("progress", "task7", null, null, null, null), waiting, tasks));
        Assert.True(RoomHub.RunResultArrived(new TaskEvent("done", "task7", null, null, "completed", null), waiting, tasks));
        Assert.Empty(tasks);
    }

    [Fact]
    public void Parser_ReadsBackgroundMoveNotice_OnlyAtResultStart()
    {
        var moved = Assert.IsType<ToolImagesEvent>(StreamParser.Parse(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_bg","content":"MCP tool \"mcp__hb-orchestration__run_mode\" is still running after 120s. It was moved to the background as task task7."}]}}"""));
        Assert.Equal("task7", moved.BackgroundTasks["toolu_bg"]);
        var arrayForm = Assert.IsType<ToolImagesEvent>(StreamParser.Parse(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_b2","content":[{"type":"text","text":"MCP tool \"x\" was moved to the background as task t9 so that you can continue"}]}]}}"""));
        Assert.Equal("t9", arrayForm.BackgroundTasks["toolu_b2"]);
        // 결과 본문에 인용된 문구는 안내가 아니다
        var quoted = Assert.IsType<ToolImagesEvent>(StreamParser.Parse(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_r","content":"runId: 0123456789\nMCP tool \"x\" was moved to the background as task t1"}]}}"""));
        Assert.Empty(quoted.BackgroundTasks);
    }

    [Fact]
    public void LiveRunsAfter_OnlyBubblesPlacedAfterTheShownLine()
    {
        var entries = new[] { E(1, ChatSpeaker.User, 0), E(4, ChatSpeaker.Conductor, 60) };
        var placement = RoomRuns.Place(entries, [Bubble("before-4", 40), Bubble("late", 90)]);
        Assert.Equal(["before-4", "late"], RoomModel.LiveRunsAfter(entries, placement, 1).Select(b => b.Key));
        Assert.Equal(["late"], RoomModel.LiveRunsAfter(entries, placement, 4).Select(b => b.Key)); // 4번 말과 그 앞 말풍선은 이미 화면에
    }

    [Fact]
    public async Task RunsHandler_ReturnsThisRoomsBubblesAfterLine()
    {
        var room = app.Room("live runs");
        string L(string json) => json.Replace("SID", room.SessionId);
        File.WriteAllText(app.TranscriptPath(room.SessionId), string.Join("\n",
            L("""{"type":"user","sessionId":"SID","timestamp":"2026-10-28T01:00:00Z","message":{"role":"user","content":"리뷰 받아 줘"}}"""),
            L("""{"type":"assistant","sessionId":"SID","timestamp":"2026-10-28T01:00:05Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"mcp__hb-orchestration__run_mode","input":{"mode":"review_main"}}]}}"""),
            L("""{"type":"user","sessionId":"SID","timestamp":"2026-10-28T01:00:40Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"runId: 0123456789\n### 1. Reviewer"}]}}"""),
            L("""{"type":"assistant","sessionId":"SID","timestamp":"2026-10-28T01:00:50Z","message":{"role":"assistant","content":[{"type":"text","text":"리뷰를 정리하면"}]}}""")) + "\n");
        await CallLog.AppendAsync(new CallRecord
        {
            Id = "c1", Timestamp = T0.AddSeconds(30), Tool = "run_mode", Mode = "review_main", Model = "gpt-6.1-sol", Agent = "codex_cli_sol",
            Prompt = "p", RunId = "0123456789", Step = 1, Success = true, Response = "외부 리뷰 응답 LIVE", Reason = "r", ElapsedMs = 5_000, // 끝 35초 — 지휘자 글(50초) 앞
        }, Path.Combine(app.Dir, "calls.jsonl"));
        var other = app.Room("other room", ChatApp.User("다른 방", 0));

        using var client = new HttpClient { BaseAddress = app.BaseAddress };
        var fresh = await client.GetStringAsync($"/Room/{room.Id}?handler=Runs&after=1");
        Assert.Contains("class=\"live-runs\"", fresh);
        Assert.Contains("외부 리뷰 응답 LIVE", fresh);
        Assert.Contains("data-run-key=", fresh);
        Assert.DoesNotContain("id=\"chat\"", fresh);                            // 말풍선만 — 대화 쪽은 아님

        // 그 실행 뒤 지휘자 말(4번 줄)까지 이미 그렸으면 새로 붙일 것이 없다
        Assert.DoesNotContain("외부 리뷰 응답 LIVE", await client.GetStringAsync($"/Room/{room.Id}?handler=Runs&after=4"));
        // 다른 방에서는 이 방 실행이 나오지 않는다
        Assert.DoesNotContain("외부 리뷰 응답 LIVE", await client.GetStringAsync($"/Room/{other.Id}?handler=Runs&after=0"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/Room/000000000000?handler=Runs&after=0")).StatusCode);
    }
}
