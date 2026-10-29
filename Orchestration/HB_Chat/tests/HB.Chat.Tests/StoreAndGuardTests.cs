using System.Text.Json.Nodes;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>방 색인 · MCP 설정 파일 · 같은 출처 검사.</summary>
public sealed class StoreAndGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-test-" + Guid.NewGuid().ToString("N"));

    public StoreAndGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RoomStore_CreateUpdate_SurvivesReload()
    {
        var path = Path.Combine(_dir, "rooms.json");
        var store = new RoomStore(path);
        var room = store.Create("ToolWithCS", PermissionLevel.Edit, "opus", "  제목  ");
        Assert.True(RoomStore.IsRoomId(room.Id));
        Assert.True(Guid.TryParseExact(room.SessionId, "D", out _));
        Assert.Equal("제목", room.Title);

        store.Update(room.Id, r => r with { KnownSize = 42, TotalCostUsd = 0.5, Id = "ffffffffffff" });
        Assert.Null(store.Update("aaaaaaaaaaaa", r => r));

        var again = new RoomStore(path).Get(room.Id);
        Assert.NotNull(again);
        Assert.Equal(42, again.KnownSize);
        Assert.Equal(PermissionLevel.Edit, again.Level);
        Assert.Contains("\"Edit\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase); // 사람이 읽는 값
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RoomStore_EffortAndLevelChange_SurviveReload()
    {
        // Claude 앱 기능 B: 만들 때 강도, 곁칸에서 모델 · 강도 · 단계 바꾸기 — 다시 읽어도 그대로, 옛 방(필드 없음)은 null
        var path = Path.Combine(_dir, "rooms-effort.json");
        var store = new RoomStore(path);
        var room = store.Create("w", PermissionLevel.Read, "opus", null, effort: "high");
        Assert.Equal("high", new RoomStore(path).Get(room.Id)!.Effort);
        store.Update(room.Id, r => r with { Model = "haiku", Effort = null, Level = PermissionLevel.Command });
        var again = new RoomStore(path).Get(room.Id)!;
        Assert.Equal(("haiku", (string?)null, PermissionLevel.Command), (again.Model, again.Effort, again.Level));
        Assert.Null(store.Create("w", PermissionLevel.Chat, "opus", null).Effort);
    }

    [Fact]
    public void RoomStore_BrokenFile_IsNotOverwritten()
    {
        var path = Path.Combine(_dir, "rooms.json");
        File.WriteAllText(path, "{ broken");
        var store = new RoomStore(path);
        Assert.NotNull(store.LoadError);
        Assert.Empty(store.All());
        Assert.Throws<InvalidOperationException>(() => store.Create("w", PermissionLevel.Chat, "opus", null));
        Assert.Equal("{ broken", File.ReadAllText(path));
    }

    [Fact]
    public void RoomStore_SaveFailure_LeavesMemoryUnchanged()
    {
        // rooms.json 자리에 폴더가 있으면 바꾸기(File.Move)가 실패한다 → 만들기 · 고치기가 메모리에도 남지 않아야 한다 (0a3b1d5155 C6).
        var good = new RoomStore(Path.Combine(_dir, "ok.json"));
        var room = good.Create("w", PermissionLevel.Chat, "opus", null);

        var path = Path.Combine(_dir, "blocked.json");
        File.Copy(Path.Combine(_dir, "ok.json"), path);
        var store = new RoomStore(path);
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<Exception>(() => store.Create("w", PermissionLevel.Chat, "opus", null));
        Assert.Single(store.All());
        Assert.ThrowsAny<Exception>(() => store.Update(room.Id, r => r with { KnownSize = 7 }));
        Assert.Null(store.Get(room.Id)!.KnownSize);
    }

    [Theory]
    [InlineData("0123456789ab", true)]
    [InlineData("0123456789AB", false)]
    [InlineData("0123456789a", false)]
    [InlineData("../../x", false)]
    [InlineData(null, false)]
    public void RoomIdShape(string? id, bool ok) => Assert.Equal(ok, RoomStore.IsRoomId(id));

    [Fact]
    public void McpConfig_TakesOnlyTheNamedServer()
    {
        var ws = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), """
            {"mcpServers":{
              "hb-orchestration":{"command":"dotnet","args":["x.dll"],"env":{"KEY":"${KEY:-}"}},
              "other":{"command":"evil"}}}
            """);
        var path = McpConfigFile.Write(ws, "hb-orchestration", Path.Combine(_dir, "data"));
        var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();
        Assert.Equal(["hb-orchestration"], servers.Select(kv => kv.Key));
        Assert.Equal("${KEY:-}", (string?)servers["hb-orchestration"]!["env"]!["KEY"]);

        Assert.Throws<InvalidOperationException>(() => McpConfigFile.Write(ws, "missing", Path.Combine(_dir, "data")));
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.Write(Path.Combine(_dir, "none"), "hb-orchestration", Path.Combine(_dir, "data")));

        // 이름은 파일 이름에 쓰기 전에 검사한다 (b19234c862 C4).
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.Write(ws, @"..\..\escape", Path.Combine(_dir, "data")));
        Assert.False(File.Exists(Path.Combine(_dir, "escape.json")));
    }

    [Fact]
    public async Task McpConfig_ConcurrentWrites_SameFileNoLeftovers()
    {
        // 같은 작업 폴더의 방들이 동시에 써도 실패하지 않고 임시 파일이 남지 않는다 (0a3b1d5155 C5).
        var ws = Path.Combine(_dir, "ws2");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        var data = Path.Combine(_dir, "data2");
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => McpConfigFile.Write(ws, "hb-orchestration", data))));
        Assert.Single(paths.Distinct());
        Assert.Equal([Path.GetFileName(paths[0])], Directory.GetFiles(data).Select(Path.GetFileName));
    }

    [Fact]
    public void StoppedTurnPlaceholder_IsOnlyTheExactConductorText()
    {
        Assert.True(HB.Chat.Pages.RoomModel.IsStoppedTurn(new(1, null, HB.Orchestration.Shared.ChatSpeaker.Conductor, "No response requested.\n", 0)));
        Assert.False(HB.Chat.Pages.RoomModel.IsStoppedTurn(new(1, null, HB.Orchestration.Shared.ChatSpeaker.User, "No response requested.", 0)));
        Assert.False(HB.Chat.Pages.RoomModel.IsStoppedTurn(new(1, null, HB.Orchestration.Shared.ChatSpeaker.Conductor, "No response requested. 하지만", 0)));
    }

    [Fact]
    public void SplitGuide_SeparatesLeadingReminderBlocks()   // 2026-10-29_단톡방-보류-나머지 D11
    {
        var (guide, text) = HB.Chat.Pages.RoomModel.SplitGuide("<system-reminder>\n규칙 바뀜\n</system-reminder>\n<system-reminder>\n단톡방 안내\n</system-reminder>\n@X 안녕");
        Assert.Equal("@X 안녕", text);
        Assert.StartsWith("<system-reminder>", guide);
        Assert.Contains("단톡방 안내", guide);
        Assert.Equal(("", "그냥 말 <system-reminder>x</system-reminder>"), HB.Chat.Pages.RoomModel.SplitGuide("그냥 말 <system-reminder>x</system-reminder>")); // 앞에 있을 때만
    }

    [Theory]
    [InlineData("GET", null, null, true)]
    [InlineData("POST", "http://localhost:5290", null, true)]
    [InlineData("POST", "http://evil.example", null, false)]
    [InlineData("POST", "http://localhost:5291", null, false)]
    [InlineData("POST", null, "http://localhost:5290/Room/abc", true)]
    [InlineData("POST", null, "http://evil.example/x", false)]
    [InlineData("POST", null, null, false)]
    [InlineData("POST", "null", null, false)]
    public void SameOrigin(string method, string? origin, string? referer, bool ok) =>
        Assert.Equal(ok, SameOriginGuard.IsAllowed(method, origin, referer, "http", "localhost:5290"));
}
