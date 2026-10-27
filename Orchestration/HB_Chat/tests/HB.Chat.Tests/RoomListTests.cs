using HB.Chat;
using HB.Orchestration.Shared;

namespace HB.Chat.Tests;

/// <summary>방 목록 줄 (계획서 2-2 a): 제목 · 미리보기 · 안 읽은 수 · 상태, 새 방 색인 필드.</summary>
public sealed class RoomListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-list-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    public RoomListTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Room NewRoom(string? title = null, int? readUpTo = null, string? lastError = null) => new()
    {
        Id = "0123456789ab", SessionId = Guid.NewGuid().ToString("D"), Workspace = "ws", Level = PermissionLevel.Read,
        Model = "haiku", CreatedAt = T0, Title = title, ReadUpTo = readUpTo, LastError = lastError,
    };

    private static ChatTranscript Transcript(params ChatEntry[] entries) => new() { FilePath = "x.jsonl", Entries = entries };

    private static ChatEntry E(int line, ChatSpeaker who, string text, int minutes = 0) => new(line, T0.AddMinutes(minutes), who, text, 0);

    [Theory]
    [InlineData("## 제목\n- 하나\n- **둘**", "제목 하나 둘")]
    [InlineData("끝났습니다.\n\n[[icon:완료]]", "끝났습니다.")]
    [InlineData("[문서](https://example.com/a) 보세요 `code`", "문서 보세요 code")]
    [InlineData("```csharp\nvar x = 1;\n```", "var x = 1;")]
    [InlineData("> 인용\n답", "인용 답")]
    public void Preview_StripsMarkup(string text, string expected) => Assert.Equal(expected, RoomList.Preview(text));

    [Fact]
    public void Preview_CutsLongText()
    {
        var preview = RoomList.Preview(new string('가', 200));
        Assert.Equal(RoomList.PreviewChars + 1, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Build_UnreadCountsConductorAfterReadUpTo()
    {
        var t = Transcript(
            E(3, ChatSpeaker.User, "질문"),
            E(5, ChatSpeaker.Conductor, "답 1"),
            E(8, ChatSpeaker.Conductor, "No response requested."), // 중지된 턴은 세지 않는다
            E(9, ChatSpeaker.User, "다음"),
            E(12, ChatSpeaker.Conductor, "답 2", 5));

        Assert.Equal(2, RoomList.Build(NewRoom(readUpTo: 0), t, RoomActivity.Idle).Unread); // 새 방(0) = 모두 안 읽음
        Assert.Equal(1, RoomList.Build(NewRoom(readUpTo: 5), t, RoomActivity.Idle).Unread);
        Assert.Equal(0, RoomList.Build(NewRoom(readUpTo: 12), t, RoomActivity.Idle).Unread);
        Assert.Equal(0, RoomList.Build(NewRoom(), t, RoomActivity.Idle).Unread); // null(옛 방) = 모두 읽음 (5225b1ffaf C4)

        var s = RoomList.Build(NewRoom(), t, RoomActivity.Idle);
        Assert.Equal("답 2", s.Preview);
        Assert.Equal(T0.AddMinutes(5), s.LastAt);
        Assert.Equal("질문", s.Title); // 이름 없으면 첫 사용자 말
    }

    [Fact]
    public void Build_PreviewMarksUserAndStoppedTurn()
    {
        Assert.Equal("나: 안녕", RoomList.Build(NewRoom(), Transcript(E(1, ChatSpeaker.User, "안녕")), RoomActivity.Idle).Preview);
        Assert.Equal("중지된 턴", RoomList.Build(NewRoom(), Transcript(E(1, ChatSpeaker.Conductor, "No response requested.")), RoomActivity.Idle).Preview);
        var empty = RoomList.Build(NewRoom(), null, RoomActivity.Idle);
        Assert.Null(empty.Preview);
        Assert.Equal("새 방", empty.Title);
        Assert.Equal(T0, empty.LastAt);
    }

    [Fact]
    public void Build_FailedOnlyWhenIdle()
    {
        Assert.True(RoomList.Build(NewRoom(lastError: "boom"), null, RoomActivity.Idle).Failed);
        Assert.False(RoomList.Build(NewRoom(lastError: "boom"), null, RoomActivity.Busy).Failed);
        Assert.Equal("이름", RoomList.Build(NewRoom(title: "이름"), Transcript(E(1, ChatSpeaker.User, "첫 말")), RoomActivity.Idle).Title);
    }

    [Fact]
    public void Build_BrokenTranscriptShowsNoEntries()
    {
        var broken = new ChatTranscript { FilePath = "x.jsonl", FormatError = "IOException: 잠김" };
        var s = RoomList.Build(NewRoom(), broken, RoomActivity.Idle);
        Assert.Equal(0, s.Unread);
        Assert.Null(s.Preview);
    }

    [Fact]
    public void Preview_DoesNotSplitSurrogatePair()
    {
        var text = new string('가', RoomList.PreviewChars - 1) + "😀끝"; // 이모지가 80자 경계에 걸침
        var preview = RoomList.Preview(text);
        Assert.False(char.IsHighSurrogate(preview[^2]));
        Assert.EndsWith("…", preview);
    }

    [Theory]
    [InlineData(0, 12, 20, 12)]        // 앞으로
    [InlineData(12, 12, 20, null)]     // 같으면 저장 안 함
    [InlineData(12, 5, 20, null)]      // 뒤로 가지 않음
    [InlineData(0, 999, 20, 20)]       // 마지막 줄로 자름 (지휘자 검토 R3)
    [InlineData(null, 3, 20, 3)]
    [InlineData(20, 999, 20, null)]
    public void NextReadUpTo_ClampsAndOnlyMovesForward(int? current, int line, int lastLine, int? expected) =>
        Assert.Equal(expected, RoomList.NextReadUpTo(current, line, lastLine));

    [Fact]
    public void MarkLegacyRead_MovesOnlyRoomsWithoutReadUpTo()
    {
        var path = Path.Combine(_dir, "rooms.json");
        var store = new RoomStore(path);
        var fresh = store.Create("ws", PermissionLevel.Chat, "haiku", null);
        Assert.Equal(0, fresh.ReadUpTo); // 새 방은 0 = 아무것도 안 읽음

        var legacy = store.Create("ws", PermissionLevel.Chat, "haiku", null);
        store.Update(legacy.Id, r => r with { ReadUpTo = null });
        var projects = Path.Combine(_dir, "projects", "p");
        Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(projects, legacy.SessionId + ".jsonl"),
            """{"type":"user","sessionId":"s","timestamp":"2026-10-28T00:00:00Z","message":{"role":"user","content":"hi"}}""" + "\n" +
            """{"type":"assistant","sessionId":"s","timestamp":"2026-10-28T00:00:01Z","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}""" + "\n");

        var transcripts = new ChatTranscripts(() => Path.Combine(_dir, "projects"));
        Assert.Equal(1, RoomList.MarkLegacyRead(store, transcripts));
        Assert.Equal(2, store.Get(legacy.Id)!.ReadUpTo); // 마지막 말(2번째 줄)까지 읽음
        Assert.Equal(0, store.Get(fresh.Id)!.ReadUpTo);
        Assert.Equal(0, RoomList.MarkLegacyRead(store, transcripts)); // 두 번째는 옮길 것 없음
    }

    [Fact]
    public void RoomStore_ReadsOldFileWithoutNewFields_AndKeepsNewOnes()
    {
        var path = Path.Combine(_dir, "rooms.json");
        // 2-1까지의 rooms.json 모양 (ReadUpTo · DeletedAt 없음)
        File.WriteAllText(path, """
            {"version":1,"rooms":[{"id":"0123456789ab","sessionId":"6a1f2b3c-0000-4000-8000-000000000001","workspace":"ToolWithCS",
              "level":"Read","model":"haiku","createdAt":"2026-10-27T10:00:00+09:00","allowRules":[]}]}
            """);
        var store = new RoomStore(path);
        Assert.Null(store.LoadError);
        var room = store.Get("0123456789ab")!;
        Assert.Null(room.ReadUpTo);
        Assert.Null(room.DeletedAt);

        var deletedAt = DateTimeOffset.Now;
        store.Update(room.Id, r => r with { ReadUpTo = 42, DeletedAt = deletedAt });
        var again = new RoomStore(path).Get(room.Id)!;
        Assert.Equal(42, again.ReadUpTo);
        Assert.Equal(deletedAt, again.DeletedAt);
    }
}
