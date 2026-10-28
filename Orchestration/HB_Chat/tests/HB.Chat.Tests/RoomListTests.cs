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
    public void Search_TitleAndBody_CaseInsensitive_NewestFirst()
    {
        var a = NewRoom(title: "Unity 빌드") with { Id = "aaaaaaaaaaaa" };
        var b = NewRoom() with { Id = "bbbbbbbbbbbb" };
        var ta = Transcript(E(1, ChatSpeaker.User, "빌드 로그 봐 줘", 1), E(2, ChatSpeaker.Conductor, "UNITY 에러가 있습니다", 2));
        var tb = Transcript(E(1, ChatSpeaker.User, "unity 테스트 돌려", 10), E(2, ChatSpeaker.Conductor, "No response requested.", 11));

        var hits = RoomList.Search([(a, ta), (b, tb)], "  unity ");
        // a 제목 · a 지휘자 말 · b 사용자 말 (중지된 턴 자리표시는 빼고, 이름 없는 b의 제목(= 첫 말)은 제목으로 따로 안 셈)
        Assert.Equal(3, hits.Count);
        Assert.Equal(("bbbbbbbbbbbb", 1), (hits[0].RoomId, hits[0].Line!.Value)); // 최근 것부터
        Assert.Contains(hits, h => h.RoomId == "aaaaaaaaaaaa" && h.Line is null && h.Snippet == "Unity 빌드");
        Assert.Contains(hits, h => h.RoomId == "aaaaaaaaaaaa" && h.Line == 2 && h.Speaker == ChatSpeaker.Conductor);

        Assert.Empty(RoomList.Search([(a, ta)], "   "));
        Assert.Empty(RoomList.Search([(a, ta)], new string('x', RoomList.QueryMax + 1)));
        var many = Transcript([.. Enumerable.Range(1, 80).Select(i => E(i, ChatSpeaker.User, "찾는 말 " + i, i))]);
        Assert.Equal(RoomList.SearchMax, RoomList.Search([(b, many)], "찾는").Count);
    }

    [Fact]
    public void Snippet_CutsAroundMatch_OnOneLine()
    {
        var text = new string('가', 100) + "\n찾는 말\n" + new string('나', 100);
        var s = RoomList.Snippet(text, text.IndexOf("찾는", StringComparison.Ordinal), 2, around: 5);
        Assert.Equal("…가가가가 찾는 말 나나…", s); // 앞 5자(가 넷 + 줄바꿈) · 뒤 5자(공백 · 말 · 줄바꿈 · 나 둘)
        Assert.Equal("찾는 말", RoomList.Snippet("찾는 말", 0, 2));
    }

    [Theory]
    [InlineData("  새 이름  ", "새 이름", null)]
    [InlineData("   ", null, null)]
    [InlineData(null, null, null)]
    [InlineData("줄\n바꿈", null, "줄바꿈")]
    public void CleanTitle_TrimsAndRejects(string? input, string? title, string? errorPart)
    {
        var (t, error) = RoomList.CleanTitle(input);
        Assert.Equal(title, t);
        if (errorPart is null) Assert.Null(error); else Assert.Contains(errorPart, error);
        Assert.NotNull(RoomList.CleanTitle(new string('가', 201)).Error);
    }

    [Fact]
    public void Trash_PurgesOnlyExpired_AndTheirAttachments()
    {
        var data = Path.Combine(_dir, "data");
        var store = new RoomStore(Path.Combine(data, "rooms.json"));
        var now = DateTimeOffset.Now;
        var old = store.Create("ws", PermissionLevel.Chat, "haiku", "오래됨");
        var recent = store.Create("ws", PermissionLevel.Chat, "haiku", "최근");
        var alive = store.Create("ws", PermissionLevel.Chat, "haiku", "살아 있음");
        store.Update(old.Id, r => r with { DeletedAt = now.AddDays(-31) });
        store.Update(recent.Id, r => r with { DeletedAt = now.AddDays(-29) });
        Directory.CreateDirectory(Path.Combine(data, "attachments", old.Id));
        File.WriteAllText(Path.Combine(data, "attachments", old.Id, "a.txt"), "x");
        Directory.CreateDirectory(Path.Combine(data, "attachments", recent.Id));

        Assert.Equal([old.Id], Trash.Purge(store, data, now));
        Assert.Null(store.Get(old.Id));
        Assert.False(Directory.Exists(Path.Combine(data, "attachments", old.Id)));
        Assert.True(Directory.Exists(Path.Combine(data, "attachments", recent.Id)));
        Assert.NotNull(new RoomStore(Path.Combine(data, "rooms.json")).Get(alive.Id)); // 저장도 됨
        Assert.Equal(1, Trash.DaysLeft(store.Get(recent.Id)!, now)); // 29일 전에 넣음 → 하루 남음
        Assert.Empty(Trash.Purge(store, data, now));
    }

    [Fact]
    public void Trash_DeleteNow_OnlyTrashRooms()
    {
        // 4차 C1: 휴지통 즉시 비우기 — 방 하나 · 전체, 휴지통 밖 방은 id를 줘도 지우지 않는다
        var data = Path.Combine(_dir, "data");
        var store = new RoomStore(Path.Combine(data, "rooms.json"));
        var now = DateTimeOffset.Now;
        var a = store.Create("ws", PermissionLevel.Chat, "haiku", "a");
        var b = store.Create("ws", PermissionLevel.Chat, "haiku", "b");
        var alive = store.Create("ws", PermissionLevel.Chat, "haiku", "살아 있음");
        store.Update(a.Id, r => r with { DeletedAt = now });
        store.Update(b.Id, r => r with { DeletedAt = now });
        Directory.CreateDirectory(Path.Combine(data, "attachments", a.Id));

        Assert.Empty(Trash.DeleteNow(store, data, [alive.Id]));
        Assert.NotNull(store.Get(alive.Id));
        Assert.Equal([a.Id], Trash.DeleteNow(store, data, [a.Id]));
        Assert.False(Directory.Exists(Path.Combine(data, "attachments", a.Id)));

        // 비우기는 화면에 보인 방만 — 그사이 휴지통에 들어온 방은 남는다 (bd52c6f0d1 C3)
        var late = store.Create("ws", PermissionLevel.Chat, "haiku", "늦게 넣음");
        store.Update(late.Id, r => r with { DeletedAt = now });
        Assert.Equal([b.Id], Trash.DeleteNow(store, data, [b.Id, alive.Id]));
        Assert.NotNull(store.Get(late.Id));
        Assert.Equal([alive.Id, late.Id], new RoomStore(Path.Combine(data, "rooms.json")).All().Select(r => r.Id));
    }

    [Fact]
    public void Export_Markdown_AndFileName()
    {
        var room = NewRoom(title: "빌드 확인");
        var t = Transcript(
            E(1, ChatSpeaker.User, "로그 봐 줘", 0),
            new ChatEntry(3, T0.AddMinutes(1), ChatSpeaker.Conductor, "에러 없음\n\n[[icon:완료]]", 2) { Tools = ["Read", "Read"] },
            E(4, ChatSpeaker.Conductor, "No response requested.", 2),
            E(5, ChatSpeaker.System, "대화가 압축됐습니다", 3));
        var md = ChatExport.Markdown(room, t, T0);
        Assert.StartsWith("# 빌드 확인\n", md);
        Assert.Contains("### 나 · ", md);
        Assert.Contains("_도구: Read ×2_", md);
        Assert.Contains("에러 없음\n\n[[icon:완료]]\n", md); // 글 그대로 (스티커 태그 포함)
        Assert.Contains("> 중지된 턴", md);
        Assert.Contains("> 안내 · ", md);
        Assert.True(md.IndexOf("로그 봐 줘", StringComparison.Ordinal) < md.IndexOf("에러 없음", StringComparison.Ordinal));

        Assert.Equal("a_b_c_20261028-1000.md", ChatExport.FileName("a/b:c", T0.ToOffset(TimeSpan.Zero)));
        Assert.StartsWith("대화_", ChatExport.FileName("...", T0));
    }

    [Fact]
    public void RoomStore_RemoveWhere_SavesAndChecksUnderLock()
    {
        var store = new RoomStore(Path.Combine(_dir, "rm", "rooms.json"));
        var a = store.Create("ws", PermissionLevel.Chat, "haiku", null);
        var b = store.Create("ws", PermissionLevel.Chat, "haiku", null);
        Assert.Equal([a.Id], store.RemoveWhere(r => r.Id == a.Id));
        Assert.Empty(store.RemoveWhere(r => r.Id == "ffffffffffff"));
        Assert.Equal([b.Id], new RoomStore(Path.Combine(_dir, "rm", "rooms.json")).All().Select(r => r.Id));
    }

    [Fact]
    public void Trash_RestoredRoomIsNotPurged()
    {
        // 지울 목록을 잠금 안에서 정한다 — 되살린 방(DeletedAt null)은 30일 기준에 안 걸린다 (95e8b1c242 C7)
        var data = Path.Combine(_dir, "data2");
        var store = new RoomStore(Path.Combine(data, "rooms.json"));
        var now = DateTimeOffset.Now;
        var room = store.Create("ws", PermissionLevel.Chat, "haiku", null);
        store.Update(room.Id, r => r with { DeletedAt = now.AddDays(-40) });
        store.Update(room.Id, r => r with { DeletedAt = null }); // 되살림
        Assert.Empty(Trash.Purge(store, data, now));
        Assert.NotNull(store.Get(room.Id));
    }

    [Fact]
    public void Page_After_TooMany_ResetsToLastPage()
    {
        var entries = Enumerable.Range(1, 120).Select(i => E(i, ChatSpeaker.User, "말 " + i)).ToList();
        var fresh = RoomList.Page(entries, null, after: 0); // 갈래 첫 턴 뒤 같은 경우
        Assert.True(fresh.Reset);
        Assert.Equal(50, fresh.Items.Count);
        Assert.Equal(71, fresh.Items[0].Line);
        Assert.True(fresh.HasMore);
        Assert.False(RoomList.Page(entries, null, after: 70).Reset); // 50개까지는 붙인다
    }

    [Fact]
    public void Snippet_StripsStickerTags_AndCutKeepsPairs()
    {
        Assert.Equal("끝났습니다", RoomList.Snippet("끝났습니다 [[icon:완료]]", 0, 3));
        Assert.Equal("가가", RoomList.Cut("가가😀", 3)); // 이모지 앞에서 자름
        Assert.Equal("가가😀", RoomList.Cut("가가😀", 4));
    }

    [Fact]
    public void ReadUpToBeforeLastUser_InheritedReadThisTurnUnread()
    {
        var t = Transcript(
            E(1, ChatSpeaker.User, "이어받은 질문"), E(2, ChatSpeaker.Conductor, "이어받은 답"),
            E(5, ChatSpeaker.User, "이번 질문"), E(6, ChatSpeaker.Conductor, "이번 답"));
        Assert.Equal(4, RoomList.ReadUpToBeforeLastUser(t));
        Assert.Equal(1, RoomList.Build(NewRoom(readUpTo: 4), t, RoomActivity.Idle).Unread); // 이번 답만
        Assert.Equal(2, RoomList.ReadUpToBeforeLastUser(Transcript(E(2, ChatSpeaker.Conductor, "답만"))));
        Assert.Null(RoomList.ReadUpToBeforeLastUser(new ChatTranscript { FilePath = "x", FormatError = "잠김" }));
        Assert.Null(RoomList.ReadUpToBeforeLastUser(null));
    }

    [Fact]
    public void MarkLegacyRead_SkipsUnforkedForkRoom()
    {
        var store = new RoomStore(Path.Combine(_dir, "fk", "rooms.json"));
        var fork = store.Create("ws", PermissionLevel.Chat, "haiku", null, forkOf: Guid.NewGuid().ToString("D"), forkSourceSize: 10);
        Assert.Equal(10, fork.ForkSourceSize);
        Assert.Equal(0, RoomList.MarkLegacyRead(store, new ChatTranscripts(() => Path.Combine(_dir, "none"))));
        Assert.Null(store.Get(fork.Id)!.ReadUpTo); // 첫 턴이 끝날 때 정한다 (95e8b1c242 C1)
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
