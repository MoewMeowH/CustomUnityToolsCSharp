using HB.Chat;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>외부 에이전트 말풍선: 이 방 세션의 확정 runId만 · 시각으로 끼우기 · 내보내기.</summary>
public sealed class RoomRunsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-runs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 1, 0, 0, TimeSpan.Zero);

    public RoomRunsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ChatEntry E(int line, ChatSpeaker who, string text, int seconds) => new(line, T0.AddSeconds(seconds), who, text, 0);

    private static RunBubble Bubble(string key, int seconds, RunBubbleKind kind = RunBubbleKind.External) =>
        new(key, T0.AddSeconds(seconds), kind, "gpt_main_sol", "design_lite · 1단계", "응답", null, false, "0123456789", "c1");

    [Fact]
    public void Place_BeforeFirstLaterEntry_ElseTrailing()
    {
        var entries = new[] { E(1, ChatSpeaker.User, "물어봐", 0), E(5, ChatSpeaker.Conductor, "정리", 60), E(7, ChatSpeaker.User, "다음", 120) };
        var p = RoomRuns.Place(entries, [Bubble("b", 50), Bubble("a", 10, RunBubbleKind.Start), Bubble("late", 130), Bubble("follow", 100)]);
        Assert.Equal(["a", "b"], p.For(entries[1]).Select(b => b.Key)); // 시각 순, 지휘자 정리 앞
        Assert.Equal(["follow"], p.For(entries[2]).Select(b => b.Key)); // followUp 단계는 그때 자리에
        Assert.Empty(p.For(entries[0]));
        Assert.Equal(["late"], p.Trailing.Select(b => b.Key)); // 마지막 말보다 늦음
    }

    [Fact]
    public void Place_SameLineEntries_AttachOnlyToFirst()
    {
        // 지휘자 글 한 줄에 text 블록이 둘이면 줄 번호가 같은 말이 둘 (a9e6066519 C2)
        var entries = new[] { E(1, ChatSpeaker.User, "물어봐", 0), E(5, ChatSpeaker.Conductor, "첫 블록", 60), E(5, ChatSpeaker.Conductor, "둘째 블록", 60) };
        var p = RoomRuns.Place(entries, [Bubble("b", 30)]);
        Assert.Equal(["b"], p.For(entries[1]).Select(b => b.Key));
        Assert.Empty(p.For(entries[2]));
        Assert.Single(p.All);
    }

    [Fact]
    public async Task For_UsesOnlyConfirmedRunIdsOfThisSession()
    {
        var projects = Path.Combine(_dir, "projects", "p");
        Directory.CreateDirectory(projects);
        var session = Path.Combine(projects, "6f4aef15-4925-474d-970d-d95c06b0cf88.jsonl");
        File.WriteAllText(session, string.Join("\n",
            """{"type":"user","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf88","timestamp":"2026-10-28T01:00:00Z","message":{"role":"user","content":"설계 물어봐 줘"}}""",
            """{"type":"assistant","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf88","timestamp":"2026-10-28T01:00:05Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"mcp__hb-orchestration__run_mode","input":{"mode":"design_lite"}}]}}""",
            """{"type":"user","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf88","timestamp":"2026-10-28T01:00:40Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"runId: 0123456789\n### 1. Critic"}]}}""",
            """{"type":"assistant","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf88","timestamp":"2026-10-28T01:00:50Z","message":{"role":"assistant","content":[{"type":"text","text":"외부 의견을 정리하면"}]}}""") + "\n");

        var log = Path.Combine(_dir, "calls.jsonl");
        CallRecord Call(string id, string runId, string response, int seconds) => new()
        {
            Id = id, Timestamp = T0.AddSeconds(seconds), Tool = "run_mode", Mode = "design_lite", Model = "gpt-6.1", Agent = "gpt_main_sol",
            Prompt = "p", RunId = runId, Step = 1, Success = true, Response = response, Reason = "설계 비평", ElapsedMs = 30_000,
        };
        await CallLog.AppendAsync(Call("c1", "0123456789", "이 설계는 괜찮습니다", 6), log);
        await CallLog.AppendAsync(Call("c2", "abcdefabcd", "다른 세션의 실행", 7), log); // 이 세션에 없는 runId

        var runs = new RoomRuns(Options.Create(new ChatOptions { CallLogPath = log }));
        var transcript = ClaudeChatReader.Read(session);
        var placement = runs.For(transcript);
        var reply = transcript.Entries.Single(e => e.Speaker == ChatSpeaker.Conductor);
        var bubbles = placement.For(reply);
        Assert.Equal([RunBubbleKind.Start, RunBubbleKind.External], bubbles.Select(b => b.Kind));
        Assert.Equal("이 설계는 괜찮습니다", bubbles[1].Markdown);
        Assert.DoesNotContain(placement.All, b => b.RunId == "abcdefabcd");

        // 내보내기에도 같은 자리에
        var room = new Room { Id = "0123456789ab", SessionId = "6f4aef15-4925-474d-970d-d95c06b0cf88", Workspace = "ws", Level = PermissionLevel.Read, Model = "haiku", CreatedAt = T0 };
        var md = ChatExport.Markdown(room, transcript, T0, placement);
        Assert.True(md.IndexOf("이 설계는 괜찮습니다", StringComparison.Ordinal) < md.IndexOf("외부 의견을 정리하면", StringComparison.Ordinal));
        Assert.Contains("### gpt_main_sol (design_lite", md);
        Assert.Contains("> 이 설계는 괜찮습니다", md); // 응답 본문은 인용으로 (C7)

        // 덧붙은 기록만 따라 읽는다: 같은 실행의 2단계(followUp)가 새로 보인다 (C3)
        await CallLog.AppendAsync(Call("c3", "0123456789", "보충 뒤 의견", 8) with { Step = 2 }, log);
        Assert.Contains(runs.For(transcript).All, b => b.Markdown == "보충 뒤 의견");

        // 기록이 다시 쓰여 짧아지면 처음부터 — 지운 실행은 사라진다
        File.WriteAllText(log, "");
        await CallLog.AppendAsync(Call("c9", "abcdefabcd", "다른 세션", 9), log);
        Assert.Empty(runs.For(transcript).All);

        // 호출 기록이 없으면 비어 있고 화면은 그대로 · 잘못된 경로도 (C4)
        Assert.Empty(new RoomRuns(Options.Create(new ChatOptions { CallLogPath = Path.Combine(_dir, "none.jsonl") })).For(transcript).All);
        Assert.Empty(new RoomRuns(Options.Create(new ChatOptions { CallLogPath = "bad\0path" })).For(transcript).All);
        Assert.Empty(runs.For(null).All);
    }

    [Fact]
    public async Task For_DropsLostMarkerOfWrittenCall()
    {
        var session = WriteSession();
        var log = Path.Combine(_dir, "calls.jsonl");
        var ok = new CallRecord
        {
            Id = "c1", Timestamp = T0.AddSeconds(6), Tool = "run_mode", Mode = "design_lite", Model = "gpt-6.1", Agent = "gpt_main_sol",
            Prompt = "p", RunId = "0123456789", Step = 1, Success = true, Response = "정상 응답", ElapsedMs = 1000,
        };
        await CallLog.AppendAsync(ok, log);
        // 기록을 남긴 직후 죽어 남은 Lost 표시 — 대시보드는 UsageEstimator.Apply로 뺀다 (자료 부족 1)
        await CallLog.AppendAsync(ok with { Success = false, Response = null, ErrorKind = InflightLog.LostKind, Error = "끊김", Timestamp = T0.AddSeconds(7) }, log);

        var bubbles = new RoomRuns(Options.Create(new ChatOptions { CallLogPath = log })).For(ClaudeChatReader.Read(session)).All.ToList();
        Assert.Equal("정상 응답", Assert.Single(bubbles, b => b.Kind == RunBubbleKind.External).Markdown);
    }

    private string WriteSession()
    {
        var session = Path.Combine(_dir, "6f4aef15-4925-474d-970d-d95c06b0cf89.jsonl");
        File.WriteAllText(session, string.Join("\n",
            """{"type":"user","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf89","timestamp":"2026-10-28T01:00:00Z","message":{"role":"user","content":"설계 물어봐 줘"}}""",
            """{"type":"assistant","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf89","timestamp":"2026-10-28T01:00:05Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"mcp__hb-orchestration__run_mode","input":{"mode":"design_lite"}}]}}""",
            """{"type":"user","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf89","timestamp":"2026-10-28T01:00:40Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"runId: 0123456789"}]}}""",
            """{"type":"assistant","sessionId":"6f4aef15-4925-474d-970d-d95c06b0cf89","timestamp":"2026-10-28T01:00:50Z","message":{"role":"assistant","content":[{"type":"text","text":"정리"}]}}""") + "\n");
        return session;
    }
}
