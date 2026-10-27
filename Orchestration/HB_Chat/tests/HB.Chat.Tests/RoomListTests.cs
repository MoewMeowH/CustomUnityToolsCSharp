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
    public void Page_LastFifty_ThenOlder_KeepsSameLineTogether()
    {
        // 줄 1..120 (말 121개), 줄 71은 말 둘(assistant 메시지 하나에 text 블록 둘) — 끝 50개의 경계가 그 둘 사이에 온다
        var entries = new List<ChatEntry>();
        for (var line = 1; line <= 120; line++)
        {
            entries.Add(E(line, ChatSpeaker.Conductor, $"말 {line}"));
            if (line == 71) entries.Add(E(71, ChatSpeaker.Conductor, "말 71-2"));
        }

        var last = RoomList.Page(entries, null, null);
        Assert.Equal(51, last.Items.Count); // 끝 50개를 자르면 첫 말이 줄 71의 둘째 → 같은 줄 첫째도 함께
        Assert.Equal(71, last.Items[0].Line);
        Assert.Equal("말 71", last.Items[0].Text);
        Assert.True(last.HasMore);

        var older = RoomList.Page(entries, before: 71, after: null);
        Assert.Equal(50, older.Items.Count);
        Assert.Equal(21, older.Items[0].Line);
        Assert.Equal(70, older.Items[^1].Line);
        Assert.True(older.HasMore);

        var first = RoomList.Page(entries, before: 21, after: null);
        Assert.Equal(20, first.Items.Count);
        Assert.False(first.HasMore);

        var fresh = RoomList.Page(entries, before: null, after: 118);
        Assert.Equal([119, 120], fresh.Items.Select(e => e.Line));
        Assert.Empty(RoomList.Page(entries, null, after: 120).Items);
        Assert.Empty(RoomList.Page([], null, null).Items);
    }

    [Fact]
    public void ToolChips_ShortensMcpAndCounts()
    {
        Assert.Equal("run_mode", RoomList.ShortTool("mcp__hb-orchestration__run_mode"));
        Assert.Equal("Read", RoomList.ShortTool("Read"));
        Assert.Equal("mcp__x", RoomList.ShortTool("mcp__x"));
        // 이어진 것만 묶는다 — 진행 중 칩(chat.js addChip)과 같은 규칙 (83b453653b C9)
        Assert.Equal([("Read", 2), ("Grep", 1), ("Read", 1), ("run_mode", 1)],
            RoomList.ToolChips(["Read", "Read", "Grep", "Read", "mcp__hb-orchestration__run_mode"]));
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(7998, "8K")]
    [InlineData(999_499, "999K")]
    [InlineData(999_500, "1M")]  // "1000K"가 아니라 (83b453653b C10)
    [InlineData(1_000_000, "1M")]
    [InlineData(1_250_000, "1.3M")]
    public void Tokens_Short(long n, string expected) => Assert.Equal(expected, HB.Chat.Pages.RoomModel.Tokens(n));

    [Fact]
    public void ChatEntry_ComparesToolsByContent()
    {
        var a = new ChatEntry(1, T0, ChatSpeaker.Conductor, "x", 2) { Tools = ["Read", "Grep"] };
        Assert.Equal(a, new ChatEntry(1, T0, ChatSpeaker.Conductor, "x", 2) { Tools = new List<string> { "Read", "Grep" } });
        Assert.Equal(a.GetHashCode(), (a with { Tools = ["Read", "Grep"] }).GetHashCode());
        Assert.NotEqual(a, a with { Tools = ["Read"] });
        Assert.NotEqual(a, a with { Text = "y" });
    }

    [Fact]
    public void FaceCatalog_TagUrlAndJson()
    {
        var dir = Path.Combine(_dir, "stickers");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "idle.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(dir, "think.png"), [0x89, 0x50, 0x4E, 0x47]);
        var stickers = new HB.Orchestration.ChatUi.StickerCatalog(() => new HB.Orchestration.ChatUi.StickerOptions
        {
            Dir = dir,
            Files = new() { ["대기"] = "idle.png", ["골똘"] = "think.png" },
        });
        var options = new ChatOptions { Expressions = new(StringComparer.OrdinalIgnoreCase) { ["thinking"] = "골똘" } };
        var faces = new FaceCatalog(stickers, Microsoft.Extensions.Options.Options.Create(options));

        Assert.Equal("대기", faces.Tag(Face.Idle));          // 기본값 (사용자 결정)
        Assert.Equal("골똘", faces.Tag(Face.Thinking));      // 설정이 먼저 (대소문자 무시)
        Assert.Equal("/sticker/" + Uri.EscapeDataString("대기"), faces.Url(Face.Idle));
        Assert.Null(faces.Url(Face.Writing));                // 기본 태그 "설명"은 그림 없음 → 글자 아바타
        using var json = System.Text.Json.JsonDocument.Parse(faces.Json());
        Assert.Equal(Enum.GetValues<Face>().Length, json.RootElement.EnumerateObject().Count());
        Assert.Equal("생각 중…", json.RootElement.GetProperty("Thinking").GetProperty("label").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("Writing").GetProperty("url").ValueKind);
    }

    [Fact]
    public void ChatReader_KeepsToolNamesBeforeText()
    {
        var path = Path.Combine(_dir, "s.jsonl");
        File.WriteAllText(path,
            """{"type":"user","timestamp":"2026-10-28T00:00:00Z","message":{"role":"user","content":"hi"}}""" + "\n" +
            """{"type":"assistant","timestamp":"2026-10-28T00:00:01Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","id":"a","input":{}},{"type":"tool_use","name":"mcp__hb-orchestration__list_modes","id":"b","input":{}}]}}""" + "\n" +
            """{"type":"assistant","timestamp":"2026-10-28T00:00:02Z","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}""" + "\n");
        var chat = ClaudeChatReader.Read(path);
        var reply = chat.Entries.Single(e => e.Speaker == ChatSpeaker.Conductor);
        Assert.Equal(2, reply.ToolsBefore);
        Assert.Equal(["Read", "mcp__hb-orchestration__list_modes"], reply.Tools);
        Assert.Empty(chat.Entries.Single(e => e.Speaker == ChatSpeaker.User).Tools);
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
