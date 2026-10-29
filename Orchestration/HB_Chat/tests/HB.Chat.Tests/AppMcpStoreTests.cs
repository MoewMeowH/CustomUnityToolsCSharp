using System.Text.Json.Nodes;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// 앱에서 더한 MCP 서버: 저장소 검사 · 저장 · 다시 읽기, JSON 붙여넣기 세 모양, 칸 채우기,
/// ~/.claude.json 읽기, 평문 비밀 값 경고, 방 설정 파일에 앱 서버가 들어가는 순서(작업 폴더 먼저).
/// </summary>
public sealed class AppMcpStoreTests : IDisposable
{
    private const string Room = "0123456789ab";
    private static readonly string[] Reserved = ["hb-orchestration", "hb-chat", "unity"];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-appmcp-" + Guid.NewGuid().ToString("N"));

    public AppMcpStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string File_ => Path.Combine(_dir, "mcp-servers.json");
    private AppMcpStore New() => new(File_, Reserved);
    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Add_SavesReloads_AndRemoves()
    {
        var store = New();
        Assert.Null(store.Add("memory", J("""{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"]}""")));
        Assert.Null(store.Add("notion", J("""{"type":"http","url":"https://mcp.notion.com/mcp"}""")));
        Assert.Equal("이미 있는 이름입니다.", store.Add("Memory", J("""{"command":"x"}""")));
        var again = New();
        Assert.Equal(["memory", "notion"], again.All().Select(s => s.Name));
        Assert.Equal("npx", again.Get("memory")!["command"]!.GetValue<string>());
        Assert.Null(again.Remove("memory"));
        Assert.Equal("없는 서버입니다.", again.Remove("memory"));
        Assert.Equal(["notion"], New().All().Select(s => s.Name));
    }

    [Theory]
    [InlineData("bad name", """{"command":"x"}""", "이름")]
    [InlineData("my.server", """{"command":"x"}""", "이름")]
    [InlineData("hb-chat", """{"command":"x"}""", "앱이 쓰는 이름")]
    [InlineData("Unity", """{"command":"x"}""", "앱이 쓰는 이름")]
    [InlineData("a", """{"command":"x","cwd":"C:/"}""", "모르는 칸")]
    [InlineData("a", """{}""", "command")]
    [InlineData("a", """{"command":"x","args":"y"}""", "args")]
    [InlineData("a", """{"command":"x","env":{"K":1}}""", "env")]
    [InlineData("a", """{"url":"file:///c:/x"}""", "http")]
    [InlineData("a", """{"url":"https://x","command":"y"}""", "같이 쓰지 않습니다")]
    [InlineData("a", """{"type":"http"}""", "url")]
    [InlineData("a", """{"command":"x","headers":{"A":"b"}}""", "headers")]
    [InlineData("a", """{"type":"ws","url":"https://x"}""", "type")]
    public void Validate_RejectsBadShapes(string name, string json, string expected) =>
        Assert.Contains(expected, AppMcpStore.Validate(name, J(json), Reserved));

    [Fact]
    public void BrokenFile_RefusesWrites_AndBadEntriesBecomeWarnings()
    {
        File.WriteAllText(File_, "{ broken");
        var broken = New();
        Assert.NotNull(broken.LoadError);
        Assert.Contains("읽지 못해", broken.Add("a", J("""{"command":"x"}""")));
        File.WriteAllText(File_, """{"version":1,"servers":{"ok":{"command":"x"},"bad":{"cwd":"y"},"hb-chat":{"command":"z"}}}""");
        var store = New();
        Assert.Equal(["ok"], store.All().Select(s => s.Name));
        Assert.Equal(2, store.LoadWarnings.Count);
    }

    [Fact]
    public void ParsePaste_ThreeShapes()
    {
        var (wrapped, e1) = AppMcpStore.ParsePaste("""{"mcpServers":{"a":{"command":"x"},"b":{"url":"https://y"}}}""", null);
        Assert.Null(e1);
        Assert.Equal(["a", "b"], wrapped.Select(i => i.Name));
        var (named, _) = AppMcpStore.ParsePaste("""{"c":{"command":"x"}}""", null);
        Assert.Equal("c", Assert.Single(named).Name);
        var (bare, _) = AppMcpStore.ParsePaste("""{"type":"stdio","command":"x"}""", " d ");
        Assert.Equal("d", Assert.Single(bare).Name);
        Assert.Contains("이름 칸", AppMcpStore.ParsePaste("""{"command":"x"}""", null).Error);
        Assert.Contains("JSON", AppMcpStore.ParsePaste("{ nope", null).Error);
        Assert.NotNull(AppMcpStore.ParsePaste("""{"a":"x"}""", null).Error);
    }

    [Fact]
    public void FromFields_SplitsCommandLine_AndUrl()
    {
        var stdio = AppMcpStore.FromFields("stdio", """npx -y "C:\My Tools\server.js" --flag""", null);
        Assert.Equal("npx", stdio["command"]!.GetValue<string>());
        Assert.Equal(["-y", @"C:\My Tools\server.js", "--flag"], stdio["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        var http = AppMcpStore.FromFields("http", "ignored", " https://mcp.notion.com/mcp ");
        Assert.Equal("https://mcp.notion.com/mcp", http["url"]!.GetValue<string>());
        Assert.Null(AppMcpStore.Validate("n", http, Reserved));
    }

    [Fact]
    public void ReadClaudeJson_UserAndLocalScopes()
    {
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, """
            {"mcpServers":{"rider":{"type":"http","url":"http://localhost:64342/mcp"}},
             "projects":{"C:/proj":{"mcpServers":{"notion-todo":{"command":"npx","env":{"TOKEN":"value-only-here"}}}},"D:/other":{"allowedTools":[]}}}
            """);
        var items = AppMcpStore.ReadClaudeJson(path);
        Assert.Equal([("user", "rider"), ("local: C:/proj", "notion-todo")], items.Select(i => (i.Scope, i.Name)));
        Assert.Empty(AppMcpStore.ReadClaudeJson(Path.Combine(_dir, "none.json")));
        // 화면 설명에는 env 이름만, 값은 없다
        Assert.Contains("env: TOKEN", AppMcpStore.Describe(items[1].Entry));
        Assert.DoesNotContain("value-only-here", AppMcpStore.Describe(items[1].Entry));
    }

    [Theory]
    [InlineData("sk-abc123", true)]
    [InlineData("Bearer ghp_abcdef", true)]
    [InlineData("ntn_12345", true)]
    [InlineData("a1b2c3d4e5f6g7h8i9j0k", true)]
    [InlineData("${NOTION_TOKEN}", false)]
    [InlineData("Bearer ${TOKEN}", false)]
    [InlineData("production", false)]
    [InlineData("C:/Program Files/x", false)]
    public void LooksSecret(string value, bool expected) => Assert.Equal(expected, AppMcpStore.LooksSecret(value));

    [Fact]
    public void SecretWarnings_NameTheFields()
    {
        var entry = J("""{"url":"https://x","headers":{"Authorization":"Bearer sk-live-abc","X-Mode":"fast"}}""");
        Assert.Equal(["headers.Authorization"], AppMcpStore.SecretWarnings(entry));
    }

    // ---- 리뷰 반영 (/self-review security ae543471a8 · /self-review 779716239c) ----

    [Fact]
    public void Warnings_CoverArgsQueryRemoteHttpAndPlaceholders()
    {
        var args = J("""{"command":"server","args":["--api-key=sk-abcdef","--port","8080","ghp_tokenvalue"]}""");
        Assert.Equal(["args[0]", "args[3]"], AppMcpStore.SecretWarnings(args));
        var query = J("""{"url":"https://h.example/sse?token=a1b2c3d4e5f6g7h8i9j0k&mode=x"}""");
        Assert.Equal(["url 쿼리 token"], AppMcpStore.SecretWarnings(query));
        var remoteHttp = J("""{"url":"http://mcp.example.com/mcp"}""");
        Assert.Contains(AppMcpStore.SecretWarnings(remoteHttp), w => w.Contains("원격 http://"));
        var sendsEnv = J("""{"url":"https://third.example/mcp","headers":{"X-K":"${ANTHROPIC_API_KEY}"}}""");
        Assert.Contains(AppMcpStore.SecretWarnings(sendsEnv), w => w.Contains("${ANTHROPIC_API_KEY}") && w.Contains("third.example"));
        // 로컬 주소는 http · 환경 변수 모두 괜찮다
        Assert.Empty(AppMcpStore.SecretWarnings(J("""{"url":"http://127.0.0.1:64342/mcp","headers":{"X":"${K}"}}""")));
    }

    [Fact]
    public void Describe_MasksSecrets_AndShowsVarNamesOnly()
    {
        var d = AppMcpStore.Describe(J("""{"command":"server","args":["--api-key=sk-abcdef","--port","8080"],"env":{"TOKEN":"ntn_123","MODE":"${MODE}"}}"""));
        Assert.Equal("server --api-key=••• --port 8080 · env: TOKEN, MODE=${MODE}", d);
        Assert.DoesNotContain("sk-abcdef", d);
        Assert.DoesNotContain("ntn_123", d);
        var u = AppMcpStore.Describe(J("""{"url":"https://h.example/sse?token=a1b2c3d4e5f6g7h8i9j0k&mode=x","headers":{"Authorization":"Bearer ${TOKEN}"}}"""));
        Assert.Equal("https://h.example/sse?token=•••&mode=x · headers: Authorization=${TOKEN}", u);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"servers":[]}""")]
    [InlineData("""{"servers":"x"}""")]
    public void OddShapedFile_IsBroken_NotOverwritten(string content)
    {
        File.WriteAllText(File_, content);
        var store = New();
        Assert.NotNull(store.LoadError);
        Assert.Contains("읽지 못해", store.Add("a", J("""{"command":"x"}""")));
        Assert.Equal(content, File.ReadAllText(File_)); // 덮지 않았다
    }

    [Fact]
    public void Add_FillsHttpType_RejectsControlCharsAndDoubleUnderscore()
    {
        var store = New();
        Assert.Null(store.Add("remote", J("""{"url":"https://x/mcp"}""")));
        Assert.Equal("http", store.Get("remote")!["type"]!.GetValue<string>());
        Assert.Contains("제어 문자", store.Add("h", J("""{"url":"https://x","headers":{"A":"b\r\nX-Evil: 1"}}""")));
        Assert.Contains("제어 문자", store.Add("e", J("""{"command":"x","env":{"K\n":"v"}}""")));
        Assert.Contains("이름", store.Add("a__b", J("""{"command":"x"}""")));
        Assert.False(ClaudeArgs.IsServerName("unity__x"));
        Assert.Equal("remote", store.NameOf("REMOTE"));
        Assert.Null(store.NameOf("none"));
    }

    [Fact]
    public void ReadClaudeJson_SkipsNonObjectProjects_AndCaches()
    {
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, """{"mcpServers":{"a":{"command":"x"}},"projects":{"C:/p":"not-an-object","D:/q":{"mcpServers":{"b":{"command":"y"}}}}}""");
        var first = AppMcpStore.ReadClaudeJson(path);
        Assert.Equal(["a", "b"], first.Select(i => i.Name));
        first[0].Entry["command"] = "changed"; // 사본이라 캐시는 그대로
        Assert.Equal("x", AppMcpStore.ReadClaudeJson(path)[0].Entry["command"]!.GetValue<string>());
    }

    [Fact]
    public void WriteRoom_UsesAppServers_WorkspaceFirst()
    {
        var ws = Path.Combine(_dir, "ws");
        Directory.CreateDirectory(ws);
        var store = New();
        store.Add("notion", J("""{"type":"http","url":"https://app/mcp"}"""));
        store.Add("memory", J("""{"command":"app-memory"}"""));
        // .mcp.json이 없어도 앱 서버만이면 된다
        var (path, missing) = McpConfigFile.WriteRoom(ws, ["notion", "gone"], null, Path.Combine(_dir, "data"), Room, store.Get);
        Assert.Equal(["gone"], missing);
        Assert.Equal("https://app/mcp", JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["notion"]!["url"]!.GetValue<string>());
        // 작업 폴더에 같은 이름이 있으면 그쪽
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), """{"mcpServers":{"memory":{"command":"ws-memory"}}}""");
        var (path2, _) = McpConfigFile.WriteRoom(ws, ["memory", "notion"], null, Path.Combine(_dir, "data"), Room, store.Get);
        var servers = JsonNode.Parse(File.ReadAllText(path2))!["mcpServers"]!;
        Assert.Equal("ws-memory", servers["memory"]!["command"]!.GetValue<string>());
        Assert.Equal("https://app/mcp", servers["notion"]!["url"]!.GetValue<string>());
    }
}
