using System.Diagnostics;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// 앱에서 더하는 작업 폴더 (3차 d): 더하기 검사 · 저장 · 다시 읽기(읽을 때도 검사) · 지우기 · 설정 값 붙이기 · 목록 묶음 ·
/// /self-review security(eee1adb777 · 460430d647) 반영 — 다른 표기 · 보호 폴더 · 방이 쓰던 이름 · MCP 명령 · 훅.
/// </summary>
public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly string _app;
    private readonly string _claude;
    private readonly Dictionary<string, string> _configured;

    public WorkspaceStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "projA"));
        Directory.CreateDirectory(Path.Combine(_dir, "projB"));
        _app = Path.Combine(_dir, "apps", "hbchat");
        Directory.CreateDirectory(_app);
        _claude = Path.Combine(_dir, "dotclaude"); // Claude 설정 폴더 자리
        Directory.CreateDirectory(Path.Combine(_claude, "projects"));
        _file = Path.Combine(_dir, "data", "workspaces.json");
        _configured = new(StringComparer.OrdinalIgnoreCase) { ["ToolWithCS"] = Path.Combine(_dir, "projA") };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WorkspaceStore New() => new(_file, _configured, [_app, Path.Combine(_dir, "data"), _claude]);

    private static Room RoomIn(string workspace) => new()
    {
        Id = "0123456789ab", SessionId = Guid.NewGuid().ToString("D"), Workspace = workspace, Level = PermissionLevel.Read, Model = "opus",
        CreatedAt = DateTimeOffset.Now, DeletedAt = DateTimeOffset.Now, // 휴지통에 있어도 쓰는 방으로 센다
    };

    [Fact]
    public void Add_SavesAndReloads_AfterConfigured()
    {
        var store = New();
        Assert.Null(store.Add(" 프로젝트 B ", Path.Combine(_dir, "projB") + Path.DirectorySeparatorChar, []));
        Assert.Equal(Path.Combine(_dir, "projB"), store.PathOf("프로젝트 B"), ignoreCase: true); // 끝 구분자 걷은 최종 경로
        var again = New(); // 켤 때 다시 읽음
        Assert.Equal(["ToolWithCS", "프로젝트 B"], again.All().Select(w => w.Name));
        Assert.Equal([true, false], again.All().Select(w => w.FromConfig));
        Assert.Null(again.LoadError);
        Assert.Empty(again.LoadWarnings);
    }

    [Theory]
    [InlineData("", "projB", "이름")]
    [InlineData("-x", "projB", "이름")]
    [InlineData("a/b", "projB", "이름")]
    [InlineData("toolwithcs", "projB", "이미 있는 이름")] // 설정 파일 이름과 대소문자만 다름
    [InlineData("new", "relative/path", "드라이브 문자")]
    [InlineData("new", "nope", "없는 폴더")]
    [InlineData("new", "projA", "이미 다른 이름으로")] // 설정 파일 것과 같은 폴더
    [InlineData("new", "apps", "품거나")]             // 앱 폴더를 품음
    [InlineData("new", "apps/hbchat", "품거나")]      // 앱 폴더 그 자체
    [InlineData("new", "data/..", "품거나")]          // _dir — 앱 · 데이터를 품음
    [InlineData("new", "dotclaude", "품거나")]        // Claude 설정 폴더 그 자체 (eee1adb777 C2)
    [InlineData("new", "dotclaude/projects", "품거나")] // 그 안
    public void Add_RejectsUnsafe(string name, string relative, string expected)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        var path = relative.StartsWith("relative", StringComparison.Ordinal) ? relative : Path.Combine(_dir, relative);
        var error = New().Add(name, path, []);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Add_RejectsOtherSpellings()
    {
        // \\?\ · \\.\ · UNC는 받지 않는다 — 글자 비교로 보호 폴더 검사를 지나쳤다 (eee1adb777 C1 · 460430d647 C3)
        var store = New();
        var b = Path.Combine(_dir, "projB");
        Assert.Contains("드라이브 문자", store.Add("q", @"\\?\" + b, []));
        Assert.Contains("드라이브 문자", store.Add("d", @"\\.\" + b, []));
        Assert.Contains("드라이브 문자", store.Add("u", @"\\localhost\" + b[0] + "$" + b[2..], []));
        // 8.3 짧은 이름은 최종 경로로 풀어 비교한다 — 짧은 이름이 있는 볼륨에서만
        if (ShortName(_app) is { } shortApp && !string.Equals(shortApp, _app, StringComparison.OrdinalIgnoreCase))
            Assert.Contains("품거나", store.Add("s", shortApp, []));
    }

    [Fact]
    public void Add_RejectsRootSystemAndHome()
    {
        var store = New();
        Assert.Contains("드라이브 루트", store.Add("root", Path.GetPathRoot(_dir), []));
        Assert.Contains("시스템 폴더", store.Add("win", Environment.GetFolderPath(Environment.SpecialFolder.Windows), []));
        Assert.Contains("홈", store.Add("home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), []));
    }

    [Fact]
    public void Add_RejectsNameUsedByRooms()
    {
        // 방은 작업 폴더를 이름으로 가리킨다 — 주인 없는 이름을 다른 폴더로 다시 쓰면 예전 방이 새 폴더에서 돈다 (eee1adb777 C4)
        Assert.Contains("방이 남아", New().Add("game", Path.Combine(_dir, "projB"), [RoomIn("Game")]));
    }

    [Fact]
    public void Add_RejectsPathThroughJunction()
    {
        if (!OperatingSystem.IsWindows()) return;
        var link = Path.Combine(_dir, "link");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, Path.Combine(_dir, "projB")]) { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        if (!Directory.Exists(link)) return; // 정션을 못 만드는 환경
        Assert.Contains("링크", New().Add("linked", link, []));
        Directory.CreateDirectory(Path.Combine(link, "inner"));
        Assert.Contains("링크", New().Add("inner", Path.Combine(link, "inner"), [])); // 링크 아래 폴더도
        Assert.NotNull(New().WhyUnsafe(Path.Combine(link, "inner"))); // 실행 전 다시 보는 검사도
    }

    [Fact]
    public void Remove_OnlyAppAdded_AndNotWhileUsed()
    {
        var store = New();
        Assert.Null(store.Add("B", Path.Combine(_dir, "projB"), []));
        Assert.Contains("설정 파일", store.Remove("ToolWithCS", []));
        Assert.Contains("1개", store.Remove("b", [RoomIn("B")]));
        Assert.Null(store.Remove("B", [RoomIn("ToolWithCS")]));
        Assert.Null(store.PathOf("B"));
        Assert.Single(New().All()); // 지운 것도 저장됨
    }

    [Fact]
    public void Load_ChecksEntriesAgain_AndKeepsThemInFile()
    {
        // 파일을 앱 밖에서 고쳐도 위험한 폴더 · 모양이 틀린 값은 쓰지 않는다 — 파일에는 남겨 둔다 (eee1adb777 C5 · 460430d647 C4 · C5)
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var b = Path.Combine(_dir, "projB").Replace("\\", "\\\\");
        var app = _app.Replace("\\", "\\\\");
        File.WriteAllText(_file, $$$"""{"version":1,"workspaces":{"ok":"{{{b}}}","nul":null,"rel":"x/y","toolwithcs":"{{{b}}}","bad":"{{{app}}}"}}""");
        var store = New();
        Assert.Null(store.LoadError);
        Assert.Equal(["ToolWithCS", "ok"], store.All().Select(w => w.Name)); // 설정 이름과 겹친 앱 항목은 쓰지 않음
        Assert.Equal(4, store.LoadWarnings.Count);
        Assert.Contains(store.LoadWarnings, w => w.StartsWith("toolwithcs:", StringComparison.Ordinal) && w.Contains("설정 파일"));
        Directory.CreateDirectory(Path.Combine(_dir, "projC"));
        Assert.Null(store.Add("C", Path.Combine(_dir, "projC"), [])); // 파일에 null 경로가 있어도 더하기가 던지지 않음 (460430d647 C4)
        Assert.Contains("\"toolwithcs\"", File.ReadAllText(_file)); // 가려진 항목도 파일에서 지우지 않음
    }

    [Fact]
    public void BrokenFile_IsNotOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ broken");
        var store = New();
        Assert.NotNull(store.LoadError);
        Assert.Contains("읽지 못해", store.Add("B", Path.Combine(_dir, "projB"), []));
        Assert.Equal("{ broken", File.ReadAllText(_file));
        Assert.Equal(["ToolWithCS"], store.All().Select(w => w.Name)); // 설정 파일 것은 그대로
    }

    [Fact]
    public void Options_LookUpConfiguredFirstThenAdded()
    {
        var store = New();
        Assert.Null(store.Add("B", Path.Combine(_dir, "projB"), []));
        var o = new ChatOptions { Workspaces = new(_configured, StringComparer.OrdinalIgnoreCase) };
        Assert.Null(o.WorkspacePath("B")); // 붙이기 전(테스트 · 설정 파일만)
        o.AppWorkspaces = store;
        Assert.Equal(Path.Combine(_dir, "projB"), o.WorkspacePath("b"), ignoreCase: true);
        Assert.Equal(Path.Combine(_dir, "projA"), o.WorkspacePath("ToolWithCS"));
        Assert.Equal(["ToolWithCS", "B"], o.AllWorkspaces.Select(w => w.Name));
    }

    [Fact]
    public void McpCommandAndHooks_AreShown()
    {
        // 읽기 이상 방은 .mcp.json의 MCP 명령을 승인 없이 띄우고 프로젝트 훅도 돈다 — 설정 화면에 그대로 보인다 (eee1adb777 C3)
        var ws = Path.Combine(_dir, "projB");
        Assert.Null(WorkspaceStore.McpCommand(ws, "hb-orchestration"));
        Assert.False(WorkspaceStore.HasProjectHooks(ws));
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"evil.exe","args":["--x","1"]}}}""");
        Assert.Equal("evil.exe --x 1", WorkspaceStore.McpCommand(ws, "hb-orchestration"));
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), "{ broken");
        Assert.Null(WorkspaceStore.McpCommand(ws, "hb-orchestration"));
        Directory.CreateDirectory(Path.Combine(ws, ".claude"));
        File.WriteAllText(Path.Combine(ws, ".claude", "settings.json"), """{"hooks":{"PreToolUse":[]}}""");
        Assert.True(WorkspaceStore.HasProjectHooks(ws));
        File.WriteAllText(Path.Combine(ws, ".claude", "settings.json"), """{"permissions":{}}""");
        Assert.False(WorkspaceStore.HasProjectHooks(ws));
    }

    [Fact]
    public void Groups_OnlyWhenMoreThanOneWorkspace()
    {
        static RoomSummary S(string id, string ws) =>
            new(id, id, null, DateTimeOffset.Now, 0, RoomActivity.Idle, false, PermissionLevel.Read, "opus") { Workspace = ws };
        var one = RoomList.Groups([S("a", "X"), S("b", "x")]);
        Assert.Null(Assert.Single(one).Workspace); // 하나뿐이면 묶지 않음 (대소문자 무시)
        var two = RoomList.Groups([S("a", "X"), S("b", "Y"), S("c", "X")]);
        Assert.Equal(["X", "Y"], two.Select(g => g.Workspace)); // 가장 최근 방(받은 순) 기준
        Assert.Equal(["a", "c"], two[0].Rooms.Select(r => r.Id));
    }

    /// <summary>폴더의 8.3 짧은 이름 (cmd for %~s). 못 얻으면 null.</summary>
    private static string? ShortName(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var psi = new ProcessStartInfo("cmd.exe", ["/c", "for %I in (\"" + folder + "\") do @echo %~sI"])
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var line = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return line.Length > 0 && Directory.Exists(line) ? line : null;
    }
}
