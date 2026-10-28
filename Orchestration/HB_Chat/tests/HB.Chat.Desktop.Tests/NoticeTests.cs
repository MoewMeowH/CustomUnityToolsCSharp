using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>2-3 b: 방 상태 읽기 · 알림 판정 · 트레이 상태.</summary>
public sealed class NoticeTests
{
    private static RoomState R(string id, string activity, bool failed = false, string? title = null) => new(id, title ?? "방 " + id, activity, failed, 0);

    private static Dictionary<string, RoomState> Map(params RoomState[] rooms) => rooms.ToDictionary(r => r.Id);

    [Fact]
    public void Parse_ReadsApiRoomsAndSkipsBadRows()
    {
        var rooms = RoomStates.Parse("[{\"id\":\"a\",\"title\":\"하나\",\"activity\":\"Approval\",\"failed\":false,\"unread\":2,\"face\":\"Approval\",\"lastAt\":\"2026-10-28T05:00:00+00:00\"}," +
                                     "{\"id\":\"\",\"activity\":\"Idle\"},{\"id\":\"b\",\"activity\":null},null,{\"id\":\"c\",\"activity\":\"Idle\"}]")!;
        Assert.Equal(["a", "c"], rooms.Select(r => r.Id));
        Assert.True(rooms[0].IsApproval);
        Assert.Equal(2, rooms[0].Unread);
        Assert.Equal("", rooms[1].Title);
        Assert.Null(RoomStates.Parse("{\"rooms\":[]}"));
        Assert.Null(RoomStates.Parse("oops"));
        Assert.True(RoomStates.AnyRunning("[{\"id\":\"a\",\"activity\":\"Busy\"}]"));
        Assert.False(RoomStates.AnyRunning("not json"));
    }

    [Fact]
    public void Question_IsApprovalWithQuestionWords()
    {
        // 4차 B4 (3차 a C7): 질문 카드도 서버는 Approval — 표정 Question으로 글만 '질문' · '답 기다림'
        var rooms = RoomStates.Parse("[{\"id\":\"q\",\"title\":\"질문 방\",\"activity\":\"Approval\",\"failed\":false,\"unread\":0,\"face\":\"Question\"}," +
                                     "{\"id\":\"a\",\"title\":\"승인 방\",\"activity\":\"Approval\",\"failed\":false,\"unread\":0,\"face\":\"Approval\"}]")!;
        Assert.True(rooms[0].IsApproval && rooms[0].IsQuestion);
        Assert.False(rooms[1].IsQuestion);
        var n = Notices.Diff(Map(R("q", "Busy"), R("a", "Busy")), rooms, null);
        Assert.Equal([true, false], n.Select(x => x.Question));
        Assert.Equal(("질문", "질문 방"), Notices.Balloon([n[0]]));
        Assert.Equal("HB Chat — 승인 기다림 1 · 답 기다림 1", TrayStatus.Tooltip(rooms));
        Assert.Equal(TrayLevel.Approval, TrayStatus.Level(rooms, new HashSet<string>()));
    }

    [Fact]
    public void Diff_FirstSnapshotIsSilent() => Assert.Empty(Notices.Diff(null, [R("a", "Approval")], null));

    [Fact]
    public void Diff_ApprovalDoneFailed()
    {
        var before = Map(R("a", "Busy"), R("b", "Approval"), R("c", "Busy"), R("d", "Idle"), R("e", "Approval"));
        var after = new[] { R("a", "Approval"), R("b", "Idle"), R("c", "Idle", failed: true), R("d", "Idle"), R("e", "Approval"), R("f", "Busy") };
        var n = Notices.Diff(before, after, null);
        Assert.Equal([(NoticeKind.Approval, "a"), (NoticeKind.Done, "b"), (NoticeKind.Failed, "c")], n.Select(x => (x.Kind, x.RoomId)));
        // e는 계속 승인 기다림(다시 알리지 않음), f는 새 방(앞 상태 없음), d는 그대로
    }

    [Fact]
    public void Diff_ApprovalToBusyIsSilent_ViewedFailureToo()
    {
        Assert.Empty(Notices.Diff(Map(R("a", "Approval")), [R("a", "Busy")], null)); // 승인 뒤 다시 실행 — 알림 없음 (0f0b6007a4 C8)
        Assert.Empty(Notices.Diff(Map(R("a", "Busy")), [R("a", "Idle", failed: true)], viewingRoom: "a"));
    }

    [Fact]
    public void Parse_DropsDuplicateIds() =>
        Assert.Equal(["a"], RoomStates.Parse("[{\"id\":\"a\",\"activity\":\"Busy\"},{\"id\":\"a\",\"activity\":\"Idle\"}]")!.Select(r => r.Activity == "Busy" ? r.Id : "x"));

    [Fact]
    public void RoomToOpen_PrefersApproval()
    {
        Assert.Equal("b", Notices.RoomToOpen([new Notice(NoticeKind.Done, "a", "A"), new Notice(NoticeKind.Approval, "b", "B")]));
        Assert.Equal("a", Notices.RoomToOpen([new Notice(NoticeKind.Done, "a", "A"), new Notice(NoticeKind.Failed, "c", "C")]));
        Assert.Null(Notices.RoomToOpen([]));
    }

    [Fact]
    public void FailureMarks_AddOnFailure_ClearOnViewSuccessOrGone()
    {
        var marks = new FailureMarks();
        marks.Update([new Notice(NoticeKind.Failed, "a", "A"), new Notice(NoticeKind.Failed, "b", "B"), new Notice(NoticeKind.Failed, "c", "C")],
            [R("a", "Idle", failed: true), R("b", "Idle", failed: true), R("c", "Idle", failed: true)], null);
        Assert.Equal(["a", "b", "c"], marks.Ids.Order());

        marks.Update([], [R("a", "Idle", failed: true), R("b", "Idle", failed: true), R("c", "Idle", failed: true)], viewingRoom: "a"); // 봄
        marks.Update([], [R("b", "Idle", failed: false), R("c", "Idle", failed: true)], null);                                       // b 성공 · a 없어짐
        Assert.Equal(["c"], marks.Ids);
        marks.Update([new Notice(NoticeKind.Done, "c", "C")], [R("c", "Idle", failed: true)], null);                                  // 답 끝 알림
        Assert.Empty(marks.Ids);
    }

    [Fact]
    public void Diff_SkipsRoomBeingViewed()
    {
        var n = Notices.Diff(Map(R("a", "Busy"), R("b", "Busy")), [R("a", "Idle"), R("b", "Approval")], viewingRoom: "a");
        Assert.Equal(["b"], n.Select(x => x.RoomId));
    }

    [Fact]
    public void Balloon_OneOrMany_TitlesOnly()
    {
        Assert.Equal(("승인 요청", "명령 방"), Notices.Balloon([new Notice(NoticeKind.Approval, "a", "명령 방")]));
        var many = Enumerable.Range(1, 7).Select(i => new Notice(i % 2 == 0 ? NoticeKind.Failed : NoticeKind.Done, "r" + i, "방 " + i)).ToList();
        var (title, text) = Notices.Balloon(many);
        Assert.Equal("알림 7개", title);
        Assert.StartsWith("답 끝 · 방 1\n실패 · 방 2", text);
        Assert.EndsWith("\n…", text);
        Assert.Equal(new string('가', 200) + "…", Notices.Balloon([new Notice(NoticeKind.Done, "a", new string('가', 300))]).Text);
    }

    [Theory]
    [InlineData("http://localhost:5290/Room/0123456789ab", "0123456789ab")]
    [InlineData("http://localhost:5290/Room/0123456789ab?x=1", "0123456789ab")]
    [InlineData("http://localhost:5290/Room/0123456789AB", null)]      // 방 ID는 소문자 16진
    [InlineData("http://localhost:5290/Room/0123456789ab/extra", null)]
    [InlineData("http://localhost:5290/", null)]
    [InlineData("http://localhost:5280/Room/0123456789ab", null)]      // 대시보드 주소
    [InlineData(null, null)]
    public void RoomIdOf_OnlyChatRoomPages(string? url, string? id) => Assert.Equal(id, MainForm.RoomIdOf(url, new Uri("http://localhost:5290")));

    [Fact]
    public void Tray_ApprovalBeatsBusyBeatsUnseenFailure()
    {
        var none = new HashSet<string>();
        Assert.Equal(TrayLevel.Idle, TrayStatus.Level([R("a", "Idle", failed: true)], none));          // 이미 본 실패는 회색
        Assert.Equal(TrayLevel.Failed, TrayStatus.Level([R("a", "Idle", failed: true)], new HashSet<string> { "a" }));
        Assert.Equal(TrayLevel.Busy, TrayStatus.Level([R("a", "Busy"), R("b", "Idle")], new HashSet<string> { "b" }));
        Assert.Equal(TrayLevel.Approval, TrayStatus.Level([R("a", "Busy"), R("b", "Approval")], none));
        Assert.Equal("HB Chat — 승인 기다림 1 · 실행 중 1", TrayStatus.Tooltip([R("a", "Busy"), R("b", "Approval")]));
        Assert.Equal("HB Chat — 쉬는 중", TrayStatus.Tooltip([]));
    }
}
