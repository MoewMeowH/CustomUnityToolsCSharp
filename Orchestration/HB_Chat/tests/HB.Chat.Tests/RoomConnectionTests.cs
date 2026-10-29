using System.Text.Json.Nodes;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// 방별 연결: 방 설정 파일 · 묻지 않을 서버 인자 · tool_result 그림 · Unity 프로젝트 목록 ·
/// 기록 그림 파일 · 자리표시 풀기 · 앱 도구 목록 · 방 규칙.
/// </summary>
public sealed class RoomConnectionTests : IDisposable
{
    private const string Room = "0123456789ab";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-conn-" + Guid.NewGuid().ToString("N"));

    public RoomConnectionTests() => Directory.CreateDirectory(Path.Combine(_dir, "ws"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Ws => Path.Combine(_dir, "ws");
    private string Data => Path.Combine(_dir, "data");

    [Fact]
    public void WriteRoom_CopiesPickedServers_AddsUnity_ReportsMissing()
    {
        File.WriteAllText(Path.Combine(Ws, ".mcp.json"),
            """{"mcpServers":{"hb-orchestration":{"command":"dotnet","env":{"K":"${K:-}"}},"notion":{"type":"http","url":"https://x/mcp"},"other":{"command":"evil"}}}""");
        var (path, missing) = McpConfigFile.WriteRoom(Ws, ["hb-orchestration", "notion", "gone"], ("C:/u/unity.exe", "F:/Game"), Data, Room);
        Assert.Equal(McpConfigFile.RoomPath(Data, Room), path);
        Assert.Equal(["gone"], missing);
        var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject();
        Assert.Equal(["hb-orchestration", "notion", "unity"], servers.Select(p => p.Key));
        Assert.Equal("${K:-}", servers["hb-orchestration"]!["env"]!["K"]!.GetValue<string>()); // 자리표시는 그대로 (CLI가 푼다)
        Assert.Equal(["mcp", "--project-path", "F:/Game"], servers["unity"]!["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        McpConfigFile.DeleteRoom(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteRoom_EmptyWhenNothingPicked_RejectsBadNames()
    {
        var (path, missing) = McpConfigFile.WriteRoom(Ws, [], null, Data, Room); // .mcp.json이 없어도 고른 것이 없으면 된다
        Assert.Empty(missing);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject());
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.WriteRoom(Ws, [@"..\x"], null, Data, Room));
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.WriteRoom(Ws, ["unity"], null, Data, Room)); // 이름 겹침 — unity는 앱이 만든다
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.WriteRoom(Ws, ["hb-chat"], null, Data, Room));
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.WriteRoom(Ws, [], null, Data, "../escape"));
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.WriteRoom(Ws, ["notion"], null, Data, Room)); // .mcp.json 없음
    }

    [Fact]
    public void ServerNames_AndEntry_FromMcpJson()
    {
        Assert.Empty(McpConfigFile.ServerNames(Ws));
        File.WriteAllText(Path.Combine(Ws, ".mcp.json"), """{"mcpServers":{"a":{"command":"x"},"bad name!":{"command":"y"},"my.server":{"command":"z"}}}""");
        Assert.Equal(["a"], McpConfigFile.ServerNames(Ws)); // 인자 · 파일 이름에 못 쓰는 이름은 뺀다
        Assert.Equal("x", McpConfigFile.Entry(Ws, "a")!["command"]!.GetValue<string>());
        Assert.Null(McpConfigFile.Entry(Ws, "b"));
        File.WriteAllText(Path.Combine(Ws, ".mcp.json"), "{ broken");
        Assert.Empty(McpConfigFile.ServerNames(Ws));
    }

    [Fact]
    public void ExtraServers_AreNotPreAllowed_NamesChecked()
    {
        // 결정 4: 방별 서버는 --allowedTools에 넣지 않고 호출마다 앱이 지금 방을 보고 허용한다 (실행 중 내리기가 바로 막히게)
        var spec = new RunSpec(PermissionLevel.Read, "opus", "15eb061e-7153-4c14-83ba-083385ed6945", false, "C:/data/mcp.json", "hb-orchestration", "규칙")
        {
            ExtraMcpServers = ["notion", "unity"],
        };
        var args = ClaudeArgs.Build(spec, ["opus"]);
        Assert.Contains("mcp__hb-orchestration", args);
        Assert.DoesNotContain(args, a => a.StartsWith("mcp__notion", StringComparison.Ordinal) || a.StartsWith("mcp__unity", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(spec with { ExtraMcpServers = ["a b"] }, ["opus"]));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(spec with { ExtraMcpServers = ["my.server"] }, ["opus"])); // . : / 는 받지 않음
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(spec with { ExtraMcpServers = [TerminalMcp.Server] }, ["opus"])); // 앱 MCP는 따로 (시작은 카드)
    }

    [Theory]
    [InlineData("mcp__unity__eval", "unity")]
    [InlineData("mcp__hb-chat__terminal_start", "hb-chat")]
    [InlineData("mcp__x__", "x")]
    [InlineData("Bash", null)]
    [InlineData("mcp__", null)]
    [InlineData("mcp____eval", null)]
    public void McpServerOf_ReadsServerName(string tool, string? server) => Assert.Equal(server, RoomHub.McpServerOf(tool));

    [Fact]
    public void ServerName_RejectsPathAndDots()
    {
        Assert.True(ClaudeArgs.IsServerName("hb-orchestration"));
        Assert.True(ClaudeArgs.IsServerName("notion_2"));
        Assert.False(ClaudeArgs.IsServerName("a/../../x"));
        Assert.False(ClaudeArgs.IsServerName("my.server"));
        Assert.False(ClaudeArgs.IsServerName("-x"));
        Assert.Throws<InvalidOperationException>(() => McpConfigFile.Write(Ws, "a/../../x", Data));
    }

    [Fact]
    public void Parser_ReadsToolUseIds_AndToolResultImages()
    {
        var usage = Assert.IsType<UsageEvent>(StreamParser.Parse(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"mcp__unity__capture_game_view","input":{}}]}}"""));
        Assert.Equal("toolu_1", Assert.Single(usage.Tools).Id);

        var images = Assert.IsType<ToolImagesEvent>(StreamParser.Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"ok"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}},{"type":"image","source":{"type":"base64","media_type":"image/gif","data":"AAAA"}}]}]}}"""));
        var image = Assert.Single(images.Images); // gif는 뺀다
        Assert.Equal(("toolu_1", "image/png", "AAAA"), (image.ToolUseId, image.MediaType, image.Base64));

        // 서브에이전트 · 그림 없는 user 줄 · 글 결과는 그림 이벤트가 아니다
        Assert.IsType<OtherEvent>(StreamParser.Parse(
            """{"type":"user","parent_tool_use_id":"toolu_0","message":{"content":[{"type":"tool_result","tool_use_id":"x","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}}"""));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"x","content":"text only"}]}}"""));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"user","message":{"content":"plain"}}"""));
    }

    [Fact]
    public void UnityProjectStore_UsesUnityProjectColumn_AndProjectCheck()
    {
        var project = Path.Combine(_dir, "Game");
        Directory.CreateDirectory(project);
        WorkspaceStore New() => new(Path.Combine(Data, "unity-projects.json"), new Dictionary<string, string>(), [Data],
            usedBy: r => r.UnityProject,
            extraCheck: p => File.Exists(Path.Combine(p, "ProjectSettings", "ProjectVersion.txt")) ? null : "Unity 프로젝트 폴더가 아닙니다");
        var store = New();
        Assert.Contains("Unity 프로젝트", store.Add("Game", project, []));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1");
        // 작업 폴더 이름이 같은 방은 상관없다 — Unity 칸만 본다
        var room = new Room
        {
            Id = Room, SessionId = Guid.NewGuid().ToString("D"), Workspace = "Game", Level = PermissionLevel.Command, Model = "opus", CreatedAt = DateTimeOffset.Now,
        };
        Assert.Null(store.Add("Game", project, [room]));
        Assert.Contains("방이", store.Remove("Game", [room with { UnityProject = "game" }]));
        Assert.Null(store.Remove("Game", [room]));

        // 다시 읽을 때도 프로젝트 검사
        Assert.Null(store.Add("Game", project, []));
        File.Delete(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"));
        var again = New();
        Assert.Null(again.PathOf("Game"));
        Assert.Contains(again.LoadWarnings, w => w.Contains("Unity 프로젝트"));
    }

    [Fact]
    public void ChatOptions_UnityProjectPath_ConfigFirst_AndCliPath()
    {
        var cli = Path.Combine(_dir, "unity.exe");
        var options = new ChatOptions { UnityCliPath = cli };
        options.UnityProjects["A"] = " F:/A ";
        Assert.Equal("F:/A", options.UnityProjectPath("a"));
        Assert.Null(options.UnityProjectPath("B"));
        Assert.Null(options.UnityProjectPath(null));
        Assert.Null(options.ResolveUnityCli()); // 정한 파일이 없으면 null (PATH로 넘어가지 않는다)
        File.WriteAllText(cli, "");
        Assert.Equal(cli, options.ResolveUnityCli());
    }

    [Fact]
    public void RoomShots_SavesPngAndJpg_PathOfChecksName()
    {
        var png = RoomShots.Save(Data, Room, [1, 2, 3]);
        var jpg = RoomShots.Save(Data, Room, [4, 5], "jpg");
        Assert.EndsWith(".png", png);
        Assert.EndsWith(".jpg", jpg);
        Assert.Equal("image/jpeg", RoomShots.ContentType(RoomShots.PathOf(Data, Room, jpg)!));
        Assert.Equal("image/png", RoomShots.ContentType(RoomShots.PathOf(Data, Room, png)!));
        Assert.Null(RoomShots.PathOf(Data, Room, "x.gif"));
        Assert.Null(RoomShots.PathOf(Data, "zzzzzz999999", png));
        Assert.Throws<ArgumentException>(() => RoomShots.Save(Data, Room, [1], "gif"));
        // 같은 밀리초에 여러 장이어도 덮지 않는다 (/self-review 0bcf01451b C4)
        var names = Enumerable.Range(0, 20).AsParallel().Select(i => RoomShots.Save(Data, Room, [(byte)i])).ToList();
        Assert.Equal(20, names.Distinct().Count());
        Assert.All(names, n => Assert.Single(File.ReadAllBytes(RoomShots.PathOf(Data, Room, n)!)));
        Assert.Equal("jpg", RoomShots.Ext("image/jpeg"));
        Assert.Null(RoomShots.Ext("image/svg+xml"));
    }

    [Fact]
    public void Probe_ExpandsPlaceholders_LikeCli()
    {
        var name = "HBCHAT_TEST_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal("a--b", McpProbe.Expand($"a-${{{name}}}-b"));
        Assert.Equal("a-def-b", McpProbe.Expand($"a-${{{name}:-def}}-b"));
        Environment.SetEnvironmentVariable(name, "v");
        try
        {
            Assert.Equal("a-v-b", McpProbe.Expand($"a-${{{name}:-def}}-b"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        Assert.Equal("plain $HOME", McpProbe.Expand("plain $HOME"));
    }

    [Fact]
    public void Probe_AppTools_ListTerminalAndBrowserTools()
    {
        var tools = McpProbe.AppTools();
        Assert.Contains(tools, t => t.Name == "terminal_start" && t.Description is { Length: > 0 });
        Assert.Contains(tools, t => t.Name == "browser_screenshot");
    }

    [Fact]
    public async Task Probe_FailingCommand_IsRecordedAsFailure()
    {
        var probe = new McpProbe();
        var entry = new JsonObject { ["command"] = Path.Combine(_dir, "no-such-server.exe") };
        var result = await probe.ProbeAsync(Room, "x", entry, Ws, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Same(result, probe.Last(Room, "x"));
        Assert.False(probe.IsRunning(Room, "x"));
        probe.Forget(Room);
        Assert.Null(probe.Last(Room, "x"));
    }

    [Fact]
    public async Task Probe_Unauthorized_IsLoginNotFailure()
    {
        // OAuth http 서버(Notion 등)는 앱이 토큰 없이 붙으면 401 — 실패가 아니라 "로그인 서버"로 (사용자 발견 2026-10-29)
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = socket.GetStream();
                var buffer = new byte[8192];
                await stream.ReadAsync(buffer, stop.Token);
                var body = "{\"error\":\"invalid_token\"}";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), stop.Token);
            }
        });
        try
        {
            var probe = new McpProbe();
            var entry = new JsonObject { ["type"] = "http", ["url"] = $"http://127.0.0.1:{port}/mcp" };
            var result = await probe.ProbeAsync(Room, "notion", entry, Ws, CancellationToken.None);
            Assert.NotNull(result);
            Assert.False(result.Ok);
            Assert.True(result.NeedsLogin, result.Error);
            Assert.Contains("OAuth", result.Error);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; } catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException or IOException) { }
        }
    }

    [Fact]
    public void RoomStore_KeepsMcpStatus_AcrossReload()
    {
        var path = Path.Combine(Data, "rooms.json");
        var store = new RoomStore(path);
        var room = store.Create("ws", PermissionLevel.Read, "opus", null);
        store.Update(room.Id, r => r with
        {
            McpStatus = [new McpServerState("notion", "connected")], McpTools = ["mcp__notion__search"], McpStatusAt = DateTimeOffset.Now,
        });
        var again = new RoomStore(path).Get(room.Id)!; // 앱을 다시 켬
        Assert.Equal([new McpServerState("notion", "connected")], again.McpStatus);
        Assert.Equal(["mcp__notion__search"], again.McpTools);
        Assert.NotNull(again.McpStatusAt);
    }

    [Fact]
    public void Parser_InitKeepsToolNames()
    {
        var init = Assert.IsType<InitEvent>(StreamParser.Parse(
            """{"type":"system","subtype":"init","session_id":"s","tools":["Read","mcp__notion__search","mcp__notion__fetch"],"mcp_servers":[{"name":"notion","status":"connected"}]}"""));
        Assert.Equal(["Read", "mcp__notion__search", "mcp__notion__fetch"], init.Tools);
    }

    [Fact]
    public void Rules_MentionUnity_OnlyWhenAttached()
    {
        Assert.DoesNotContain("Unity", RoomRules.For(PermissionLevel.Command, terminal: true));
        var rules = RoomRules.For(PermissionLevel.Command, terminal: true, unity: "ToolCreate");
        Assert.Contains("ToolCreate", rules);
        Assert.Contains("capture_game_view", rules);
        Assert.Contains("에디터를 켜 달라고", rules);
        Assert.Contains("eval", rules);
        Assert.Contains("notion", RoomRules.For(PermissionLevel.Read, extras: ["notion"]));
        Assert.Contains("단계와 상관없이", RoomRules.For(PermissionLevel.Read, extras: ["notion"]));
        Assert.DoesNotContain("단계와 상관없이", RoomRules.For(PermissionLevel.Read));
    }
}
