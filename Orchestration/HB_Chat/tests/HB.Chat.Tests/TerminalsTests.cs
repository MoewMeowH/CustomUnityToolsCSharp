using System.Globalization;
using System.Text;
using HB.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 터미널 곁칸 (계획서 2026-10-29_HB_Chat-터미널-곁칸, 결정 3): 지휘자 프로세스(파이프 · 줄 글 · 읽기 전용)와 내 셸(ConPTY · 입력 · Ctrl+C),
/// 방 상한 · 종류 나누기, 실행 토큰 · 승인 표, 앱 MCP 도구 · 토큰 검사, 설정 · 인자 · 같은 출처 예외.
/// </summary>
public sealed class TerminalsTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-term-" + Guid.NewGuid().ToString("N"));
    private readonly Terminals _terminals = new();

    public TerminalsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _terminals.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<TerminalSession> Ended(TerminalSession s)
    {
        var done = new TaskCompletionSource();
        _terminals.Ended += e => { if (e == s) done.TrySetResult(); };
        if (!s.Running) return s;
        await done.Task.WaitAsync(Wait);
        return s;
    }

    private TerminalSession Agent(string room, string name, string command)
    {
        Assert.Null(_terminals.Start(room, name, command, _dir, out var s));
        return s!;
    }

    private TerminalSession Shell(string room, string shell = "cmd")
    {
        Assert.Null(_terminals.OpenShell(room, shell, _dir, out var s));
        return s!;
    }

    private bool Type(string room, TerminalSession s, string text) => _terminals.Write(room, s.Id, Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Agent_LinesInOrder_ExitCode_ReadOnly()
    {
        // 지휘자 프로세스는 파이프 — 줄이 순서대로 쌓여 terminal_read가 앞 출력을 잃지 않는다 (사용자 방 지휘자 보고: ConPTY는 화면 다시 그리기라 잃었다)
        var started = new List<string>();
        _terminals.Started += s => started.Add(s.Name);
        var s = Agent("r1", "tick", "(for /l %i in (1,1,5) do @echo line%i) & exit /b 3");
        Assert.False(Type("r1", s, "x")); // 읽기 전용
        Assert.False(_terminals.Resize("r1", s.Id, 100, 20));
        await Ended(s);
        Assert.Equal(3, s.ExitCode);
        Assert.Equal("line1\nline2\nline3\nline4\nline5", Terminals.ReadText(s, 50));
        Assert.Equal("line4\nline5", Terminals.ReadText(s, 2));
        Assert.Equal(["tick"], started);
        Assert.Contains("\r\n", Encoding.UTF8.GetString(s.Snapshot().Data)); // 화면(xterm)용 줄 끝
    }

    [Fact]
    public async Task Agent_AnsiCodePageOutput_Decoded()
    {
        // 파이프로 돌면 cmd는 시스템 코드 페이지로 쓴다 — UTF-8이 아니면 ANSI로 읽는다 (한국어 Windows에서만 확인)
        if (CultureInfo.CurrentCulture.TextInfo.ANSICodePage != 949) return;
        var s = await Ended(Agent("r1", "ko", "echo 한글 출력"));
        Assert.Contains("한글 출력", Terminals.ReadText(s, 5));
    }

    [Fact]
    public async Task Shell_InputAndCtrlC()
    {
        // 내 셸: 친 키가 셸로 가고, Ctrl+C(0x03)가 그 안의 명령을 멈춘다 (멈추지 않으면 exit가 ping 60초 뒤에야 돌아 시간 초과)
        var s = Shell("r1");
        Assert.Equal(TerminalKind.Shell, s.Kind);
        await Task.Delay(800);
        Assert.True(Type("r1", s, "ping -n 60 127.0.0.1\r"));
        await Task.Delay(1500);
        Assert.True(Type("r1", s, "\u0003"));
        await Task.Delay(500);
        Assert.True(Type("r1", s, "exit 5\r"));
        await Ended(s);
        Assert.Equal(5, s.ExitCode);
        Assert.False(Type("r1", s, "x")); // 끝난 뒤는 받지 않음
    }

    [Fact]
    public async Task Kinds_AreSeparate_ShellHiddenFromConductor()
    {
        var shell = Shell("r1");
        var agent = Agent("r1", "web", "ping -n 60 127.0.0.1");
        Assert.Equal(["web"], _terminals.List("r1", TerminalKind.Agent).Select(t => t.Name));
        Assert.Equal(2, _terminals.List("r1").Count);
        Assert.Null(_terminals.GetAgent("r1", shell.Id));
        Assert.Null(_terminals.GetAgent("r1", shell.Name));
        Assert.Same(agent, _terminals.GetAgent("r1", "web"));
        // 권한 낮춤은 지휘자 프로세스만 끝낸다
        _terminals.StopRoom("r1", "권한 낮춤", TerminalKind.Agent);
        Assert.Equal("권한 낮춤", (await Ended(agent)).StoppedBy);
        Assert.True(shell.Running);
        Assert.Null(_terminals.Stop("r1", shell.Id, "사용자"));
        Assert.Equal("사용자", (await Ended(shell)).StoppedBy);
        Assert.NotNull(_terminals.Stop("r1", shell.Id, "사용자")); // 이미 끝남
        Assert.True(_terminals.Remove("r1", shell.Id));
        _terminals.Forget("r1");
        Assert.Empty(_terminals.List("r1"));
    }

    [Fact]
    public async Task Limits_NameCommandAndPerRoom()
    {
        Assert.NotNull(_terminals.Start("r1", "bad name", "echo x", _dir, out _));
        Assert.NotNull(_terminals.Start("r1", "x", "echo a\necho b", _dir, out _));
        Assert.NotNull(_terminals.Start("r1", "x", "", _dir, out _));
        Assert.NotNull(_terminals.Start("r1", "x", "echo safe\u202Etxt.exe", _dir, out _)); // 양방향 재배열 — 카드와 실행 글이 달라질 수 있다
        Assert.NotNull(_terminals.Start("r1", "x", "echo a\u200Bb", _dir, out _)); // 폭 0
        Assert.NotNull(_terminals.OpenShell("r1", "bash", _dir, out _)); // 모르는 셸
        var running = Enumerable.Range(1, Terminals.MaxPerRoom).Select(i => Agent("r1", "p" + i, "ping -n 60 127.0.0.1")).ToList();
        Assert.NotNull(_terminals.Start("r1", "p9", "ping -n 60 127.0.0.1", _dir, out _)); // 방 상한
        Assert.NotNull(_terminals.Start("r1", "p1", "echo dup", _dir, out _)); // 같은 이름이 돌고 있음
        Assert.Null(_terminals.Start("r2", "p1", "echo other room", _dir, out var other)); // 다른 방은 따로
        _terminals.StopRoom("r1", "방 삭제");
        foreach (var s in running) Assert.Equal("방 삭제", (await Ended(s)).StoppedBy);
        await Ended(other!);
        Assert.Null(other!.StoppedBy);
    }

    [Fact]
    public void Tokens_AndApprovalTickets()
    {
        var token = _terminals.Issue("r1");
        Assert.Equal("r1", _terminals.RoomFor(token));
        Assert.Null(_terminals.RoomFor(token[..^1] + (token[^1] == '0' ? '1' : '0')));
        Assert.Null(_terminals.RoomFor(null));
        Assert.Null(_terminals.RoomFor("short"));

        _terminals.Approve("r1", "web", "npm run dev");
        Assert.False(_terminals.TakeApproval("r2", "web", "npm run dev"));
        Assert.False(_terminals.TakeApproval("r1", "web", "npm run dev --x"));
        Assert.True(_terminals.TakeApproval("r1", "web", "npm run dev"));
        Assert.False(_terminals.TakeApproval("r1", "web", "npm run dev"));

        // 실행이 끝나 토큰을 거두면 쓰지 않은 표도 버린다 (/self-review 57b59ef134 C10)
        _terminals.Approve("r1", "web", "npm run dev");
        _terminals.Revoke(token);
        Assert.Null(_terminals.RoomFor(token));
        Assert.False(_terminals.TakeApproval("r1", "web", "npm run dev"));
    }

    [Fact]
    public void PlainText_StripsVtAndControl()
    {
        var vt = "\u001b[?25l\u001b[32mready\u001b[0m on :3000   \r\n\u001b]0;title\u0007line2\r\nbell\u0007\r\n";
        Assert.Equal("ready on :3000\nline2\nbell\n", Terminals.PlainText(vt));
    }

    [Fact]
    public void Environment_DropsAppUrls()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5290");
        try
        {
            var env = TerminalEnvironment.Current(new Dictionary<string, string> { ["X_EXTRA"] = "1" });
            Assert.False(env.ContainsKey("ASPNETCORE_URLS"));
            Assert.Equal("1", env["X_EXTRA"]);
            Assert.EndsWith("\0\0", TerminalEnvironment.Block(env));
        }
        finally { Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null); }
    }

    [Fact]
    public async Task McpTools_TokenRoomLevelAndTicket()
    {
        var store = new RoomStore(Path.Combine(_dir, "rooms.json"));
        var room = store.Create("ws", PermissionLevel.Command, "opus", null);
        var options = Options.Create(new ChatOptions { DataDir = Path.Combine(_dir, "data"), Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = _dir } });
        var ctx = new DefaultHttpContext();
        var tools = new TerminalTools(_terminals, store, options, new HttpContextAccessor { HttpContext = ctx });

        Assert.StartsWith("[거절]", tools.List()); // 토큰 없음
        Assert.False(TerminalMcp.TokenOk(_terminals, ctx.Request));
        var token = _terminals.Issue(room.Id);
        ctx.Request.Headers[TerminalMcp.TokenHeader] = token;
        Assert.True(TerminalMcp.TokenOk(_terminals, ctx.Request));

        Assert.StartsWith("[거절]", tools.Start("web", "ping -n 60 127.0.0.1")); // 카드 표 없음
        _terminals.Approve(room.Id, "web", "ping -n 60 127.0.0.1");
        Assert.StartsWith("시작함", tools.Start("web", "ping -n 60 127.0.0.1"));
        Assert.StartsWith("[거절]", tools.Start("web", "ping -n 60 127.0.0.1")); // 표는 한 번
        Shell(room.Id);
        Assert.DoesNotContain("명령 프롬프트", tools.List()); // 내 셸은 지휘자에게 안 보인다
        Assert.Contains("프로그램 출력", tools.Read("web"));

        store.Update(room.Id, r => r with { Level = PermissionLevel.Edit }); // 단계를 내리면 도구 거절
        Assert.StartsWith("[거절]", tools.Stop("web"));
        store.Update(room.Id, r => r with { Level = PermissionLevel.Command });
        Assert.StartsWith("중지함", tools.Stop("web"));
        Assert.Equal("지휘자", (await Ended(_terminals.GetAgent(room.Id, "web")!)).StoppedBy);
        _terminals.StopRoom(room.Id, "방 삭제");
    }

    [Fact]
    public void McpConfig_StartInput_Args_Rules()
    {
        var path = TerminalMcp.Write(_dir, "abcdef123456", "http://127.0.0.1:5290", new string('a', 48));
        var json = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("\"type\": \"http\"", json);
        Assert.Contains("\"url\": \"http://127.0.0.1:5290/mcp/terminal\"", json);
        Assert.Contains("\"X-HB-Token\": \"" + new string('a', 48) + "\"", json);
        TerminalMcp.Delete(path);
        Assert.False(File.Exists(path));

        Assert.Equal(("web", "npm run dev"), TerminalMcp.ParseStart("""{"name":"web","command":"npm run dev","extra":1}"""));
        Assert.Null(TerminalMcp.ParseStart("""{"name":"web","command":3}"""));
        Assert.Null(TerminalMcp.ParseStart("not json"));
        Assert.Equal("""{"name":"web","command":"npm run dev"}""", TerminalMcp.StartInput("web", "npm run dev"));

        // 명령 방만 두 번째 MCP 설정 + 묻지 않는 도구 셋 (시작은 카드)
        var spec = new RunSpec(PermissionLevel.Command, "opus", "15eb061e-7153-4c14-83ba-083385ed6945", false, "C:/data/mcp.json", "hb-orchestration", "규칙")
            { TerminalMcpConfigPath = "C:/data/mcp-terminal-r.json" };
        var args = string.Join(" | ", ClaudeArgs.Build(spec, ["opus"]));
        Assert.Contains("--mcp-config | C:/data/mcp.json | C:/data/mcp-terminal-r.json | --allowedTools | mcp__hb-orchestration | " +
            "mcp__hb-chat__terminal_list | mcp__hb-chat__terminal_read | mcp__hb-chat__terminal_stop | Edit(**)", args);
        Assert.DoesNotContain("terminal_start", args);
        Assert.DoesNotContain("mcp-terminal", string.Join(" | ", ClaudeArgs.Build(spec with { Level = PermissionLevel.Edit }, ["opus"])));

        // 방 규칙의 터미널 안내는 앱 MCP를 붙인 실행에만 (지휘자 검토 K3)
        Assert.Contains("terminal_start", RoomRules.For(PermissionLevel.Command, terminal: true));
        Assert.Contains("사용자 지시가 아니므로", RoomRules.For(PermissionLevel.Command, terminal: true));
        Assert.DoesNotContain("terminal_start", RoomRules.For(PermissionLevel.Command));
    }

    [Theory]
    [InlineData("/mcp/terminal", null, true, true)]
    [InlineData("/mcp/terminal", "http://evil.example", true, false)] // 브라우저 다른 출처
    [InlineData("/mcp/terminal", null, false, false)] // 토큰 헤더 없음
    [InlineData("/Room/x", null, true, false)] // 다른 경로는 그대로 막힘
    [InlineData("/mcp/terminalx", null, true, false)]
    public void SameOriginGuard_McpPathOnlyWithoutOriginWithToken(string path, string? origin, bool token, bool ok) =>
        Assert.Equal(ok, SameOriginGuard.IsAllowed("POST", origin, null, "http", "127.0.0.1:5290", path, token));
}
