using System.Text;
using HB.Chat;
using HB.Orchestration.Shared;

namespace HB.Chat.Tests;

/// <summary>방 기록 줄 (계획서 2026-10-29_HB_Chat-진행줄-자리-정리표시): 승인 기록에서 그 방 줄만 · 덧붙은 줄 · 시각으로 끼우기 · 정리 표시 글.</summary>
public sealed class RoomNotesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-notes-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 1, 0, 0, TimeSpan.Zero);

    public RoomNotesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_dir, "approvals.jsonl");

    private static ChatEntry E(int line, ChatSpeaker who, string text, int seconds) => new(line, T0.AddSeconds(seconds), who, text, 0);

    private void Append(string text) => File.AppendAllText(LogPath, text, new UTF8Encoding(false));

    private static string Line(string room, string tool, string summary, string decision, int seconds) =>
        System.Text.Json.JsonSerializer.Serialize(new { at = T0.AddSeconds(seconds), room, tool, summary, decision }) + "\n";

    [Fact]
    public void Notes_OnlyThisRoom_SkipQuestionAnswers_LabelsAndDenied()
    {
        Append(Line("r1", "Bash", "git status\nsecond line", "허용", 10));
        Append(Line("r2", "Bash", "ls", "허용", 11)); // 다른 방
        Append(Line("r1", "AskUserQuestion", "q", "답: x", 12)); // 질문 답은 말풍선으로 이미 있음
        Append(Line("r1", "PowerShell", "rm x", "시간 초과", 13));
        Append(Line("r1", "ExitPlanMode", "plan", "허용", 14));
        Append(Line("r1", RoomNotes.BackgroundTool, "", "백그라운드 작업 1개를 '지금 끝내기'로 정리했습니다", 15));
        Append("not json\n");
        var notes = new RoomNotes(() => _dir).Notes("r1");
        Assert.Equal(["승인 허용 — Bash: git status …", "승인 시간 초과 — PowerShell: rm x", "계획 승인", "백그라운드 작업 1개를 '지금 끝내기'로 정리했습니다"],
            notes.Select(n => n.Text));
        Assert.Equal([false, true, false, false], notes.Select(n => n.Denied));
    }

    [Fact]
    public void Notes_ReadsOnlyAppendedLines_AndWaitsForUnfinishedLine()
    {
        var notes = new RoomNotes(() => _dir);
        Append(Line("r1", "Bash", "a", "허용", 1));
        Assert.Single(notes.Notes("r1"));
        // 줄바꿈이 아직 없는 줄(쓰는 중)은 다음에 읽는다
        var half = Line("r1", "Bash", "b", "허용", 2);
        Append(half[..^1]);
        Assert.Single(notes.Notes("r1"));
        Append("\n");
        Assert.Equal(2, notes.Notes("r1").Count);
        // 파일이 줄면(지움) 처음부터
        File.WriteAllText(LogPath, Line("r1", "Bash", "c", "거절", 3));
        Assert.Equal(["승인 거절 — Bash: c"], notes.Notes("r1").Select(n => n.Text));
    }

    [Fact]
    public void Place_BetweenFirstAndSecondAnswer()
    {
        // 사용자 확인: 두 번째 답 아래로 몰리던 승인 · 정리 줄이 첫 답과 두 번째 답 사이에 와야 한다
        var entries = new[] { E(1, ChatSpeaker.User, "돌려", 0), E(3, ChatSpeaker.Conductor, "시작했어", 30), E(9, ChatSpeaker.Conductor, "끝났어", 90) };
        var p = NotePlacement.Place(entries, [new RoomNote(T0.AddSeconds(10), "승인 허용 — Bash: job", false), new RoomNote(T0.AddSeconds(60), "정리", false),
            new RoomNote(T0.AddSeconds(200), "늦음", false)]);
        Assert.Equal(["승인 허용 — Bash: job"], p.For(entries[1]).Select(n => n.Text));
        Assert.Equal(["정리"], p.For(entries[2]).Select(n => n.Text));
        Assert.Equal(["늦음"], p.Trailing.Select(n => n.Text));
    }

    [Theory]
    [InlineData("wrapup", 1, "백그라운드 작업 1개를 '지금 끝내기'로 정리했습니다")]
    [InlineData("wrapup", 0, "지휘자가 이어 답하던 중 '지금 끝내기'로 정리했습니다")]
    [InlineData("cap", 2, "기다림 상한이 지나 백그라운드 작업 2개를 정리했습니다")]
    [InlineData("stop", 1, "기다리는 중 멈춰 백그라운드 작업 1개를 정리했습니다")]
    // 남은 작업 0개 = 작업이 다 끝나고 지휘자가 이어 답하던 중 (/self-review e464ec29ba C1)
    [InlineData("cap", 0, "지휘자가 이어 답하던 중 기다림 상한이 지나 정리했습니다")]
    [InlineData("stop", 0, "지휘자가 이어 답하던 중 멈췄습니다")]
    public void BackgroundNote_Texts(string by, int left, string text) =>
        Assert.Equal(text, RoomHub.BackgroundNote(new RunResult(true, false, null, null, null, null, 0, 0, null) { BackgroundClosedBy = by, BackgroundLeft = left }));

    [Fact]
    public void BackgroundNote_NoneWhenNotClosed() =>
        Assert.Null(RoomHub.BackgroundNote(new RunResult(true, false, null, null, null, null, 0, 0, null)));

    [Fact]
    public void Export_IncludesNotesInPlace()
    {
        var room = new RoomStore(Path.Combine(_dir, "rooms.json")).Create("ws", PermissionLevel.Command, "opus", null);
        var entries = new[] { E(1, ChatSpeaker.User, "돌려", 0), E(3, ChatSpeaker.Conductor, "시작했어", 30) };
        var transcript = new ChatTranscript { FilePath = "x.jsonl", Entries = entries };
        var notes = NotePlacement.Place(entries, [new RoomNote(T0.AddSeconds(10), "승인 허용 — Bash: job", false)]);
        var md = ChatExport.Markdown(room, transcript, T0, notes: notes);
        Assert.True(md.IndexOf("승인 허용 — Bash: job", StringComparison.Ordinal) < md.IndexOf("시작했어", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_MergesNotesWithRunsByTime()
    {
        // 같은 말 앞의 실행 말풍선(5초)과 승인 줄(10초): 화면처럼 실행 → 승인 차례 (/self-review e464ec29ba C4)
        var room = new RoomStore(Path.Combine(_dir, "rooms.json")).Create("ws", PermissionLevel.Command, "opus", null);
        var entries = new[] { E(1, ChatSpeaker.User, "돌려", 0), E(3, ChatSpeaker.Conductor, "시작했어", 30) };
        var transcript = new ChatTranscript { FilePath = "x.jsonl", Entries = entries };
        var run = new RunBubble("k", T0.AddSeconds(5), RunBubbleKind.Start, "gpt", null, "실행 시작", null, false, "r1", "c1");
        var md = ChatExport.Markdown(room, transcript, T0, runs: RoomRuns.Place(entries, [run]),
            notes: NotePlacement.Place(entries, [new RoomNote(T0.AddSeconds(10), "승인 허용 — Bash: job", false)]));
        Assert.True(md.IndexOf("실행 시작", StringComparison.Ordinal) < md.IndexOf("승인 허용 — Bash: job", StringComparison.Ordinal));
        Assert.True(md.IndexOf("승인 허용 — Bash: job", StringComparison.Ordinal) < md.IndexOf("시작했어", StringComparison.Ordinal));
    }

    [Fact]
    public void Notes_HostDenialWithoutSummary_NoTrailingColon()
    {
        Append(Line("r1", "Bash", "", "거절 (승인할 곳 없음)", 1));
        Append(Line("r1", "ExitPlanMode", "", "거절 (계획 모드로 시작한 실행이 아님)", 2));
        var notes = new RoomNotes(() => _dir).Notes("r1");
        Assert.Equal(["승인 거절 (승인할 곳 없음) — Bash", "계획 거절 (계획 모드로 시작한 실행이 아님)"], notes.Select(n => n.Text));
        Assert.All(notes, n => Assert.True(n.Denied));
    }
}
