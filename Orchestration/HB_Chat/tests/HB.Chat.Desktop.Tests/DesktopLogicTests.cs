using System.Drawing;
using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>데스크톱 앱 (계획서 2-3 a): 이동 규칙 · 서버 확인 · 저장소 경로 · 설정 · 창 위치.</summary>
public sealed class DesktopLogicTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hbchat-desktop-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly NavigationPolicy Policy = new(new Uri("http://localhost:5290"), new Uri("http://localhost:5280"));

    [Theory]
    [InlineData("http://localhost:5290/Settings", true)]
    [InlineData("http://localhost:5290/settings/", true)]
    [InlineData("http://localhost:5290/Settings?done=profile", true)]
    [InlineData("http://localhost:5290/", false)]
    [InlineData("http://localhost:5290/Room/0123456789ab", false)]
    [InlineData("http://localhost:5280/Settings", false)] // 다른 서버
    [InlineData(null, false)]
    public void SettingsPage_PressesSettingsButton(string? source, bool settings) =>
        Assert.Equal(settings, MainForm.IsSettingsPage(source, new Uri("http://localhost:5290")));

    [Theory]
    [InlineData("http://localhost:5290/Room/0123456789ab", AppTab.Chat, NavAction.Allow, AppTab.Chat)]
    [InlineData("http://localhost:5280/Call/c1", AppTab.Chat, NavAction.Switch, AppTab.Dashboard)]       // 호출 상세 → 대시보드 탭
    [InlineData("http://LOCALHOST:5290/Room/x", AppTab.Dashboard, NavAction.Switch, AppTab.Chat)]      // 앱에서 열기 → 채팅 탭 (호스트 대소문자)
    [InlineData("https://example.com/?q=1", AppTab.Chat, NavAction.External, AppTab.Chat)]
    [InlineData("http://localhost:5291/", AppTab.Chat, NavAction.External, AppTab.Chat)]               // 다른 포트는 앱 아님
    [InlineData("http://127.0.0.1:5290/", AppTab.Chat, NavAction.External, AppTab.Chat)]               // 다른 호스트 이름도 앱 아님
    [InlineData("file:///C:/Windows/win.ini", AppTab.Chat, NavAction.Block, AppTab.Chat)]
    [InlineData("ms-settings:privacy", AppTab.Chat, NavAction.Block, AppTab.Chat)]
    [InlineData("javascript:alert(1)", AppTab.Chat, NavAction.Block, AppTab.Chat)]
    [InlineData("about:blank", AppTab.Chat, NavAction.Block, AppTab.Chat)]
    [InlineData("not a url", AppTab.Chat, NavAction.Block, AppTab.Chat)]
    [InlineData(null, AppTab.Dashboard, NavAction.Block, AppTab.Dashboard)]
    public void Main_RoutesByOrigin(string? url, AppTab current, NavAction action, AppTab tab)
    {
        var d = Policy.Main(url, current);
        Assert.Equal((action, tab), (d.Action, d.Tab));
        if (action is NavAction.Block) Assert.Null(d.Url);
    }

    [Theory]
    [InlineData("about:srcdoc", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://example.com/?leak=1", false)]  // 위젯이 스스로 밖으로 (0번 실측)
    [InlineData("http://localhost:5290/", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData(null, false)]
    public void FrameAllowed_OnlySrcdocAndBlank(string? url, bool allowed) => Assert.Equal(allowed, NavigationPolicy.FrameAllowed(url));

    [Fact]
    public void Probe_RecognizesChatAndDashboard()
    {
        Assert.True(LocalServer.IsChatRooms("[]"));
        Assert.True(LocalServer.IsChatRooms("[{\"id\":\"0123456789ab\",\"activity\":\"Idle\"}]"));
        Assert.False(LocalServer.IsChatRooms("{\"rooms\":[]}"));
        Assert.False(LocalServer.IsChatRooms("<html>"));
        Assert.True(LocalServer.IsDashboard("<head><title>HB Orchestration · 호출 기록</title>"));
        Assert.False(LocalServer.IsDashboard("<title>HB Chat</title>"));
    }

    [Fact]
    public void ServerArgs_ContentRootUrlAndExtras()
    {
        var s = new LocalServer(ServerKind.Dashboard, new Uri("http://localhost:5280/"), "x.exe", @"C:\repo\dash", ["--Dashboard:McpConfigDir=C:\\pub"], "log");
        Assert.Equal(["--contentRoot", @"C:\repo\dash", "--urls=http://localhost:5280", "--Dashboard:McpConfigDir=C:\\pub"], s.Args);
    }

    [Fact]
    public void AnyRunning_ReadsActivity()
    {
        Assert.False(RoomStates.AnyRunning("[{\"activity\":\"Idle\"},{\"activity\":\"Idle\"}]"));
        Assert.True(RoomStates.AnyRunning("[{\"activity\":\"Idle\"},{\"activity\":\"Approval\"}]"));
        Assert.True(RoomStates.AnyRunning("[{\"activity\":\"Busy\"}]"));
        Assert.False(RoomStates.AnyRunning("not json")); // 읽지 못하면 끄기를 막지 않음
    }

    [Fact]
    public void RepoLayout_FindsChatRootAndPublishDir()
    {
        var chatRoot = Path.Combine(_dir, "Orchestration", "HB_Chat");
        Directory.CreateDirectory(Path.Combine(chatRoot, "src", "HB.Chat"));
        File.WriteAllText(Path.Combine(chatRoot, "src", "HB.Chat", "HB.Chat.csproj"), "<Project />");
        var exeDir = Path.Combine(chatRoot, "src", "HB.Chat.Desktop", "bin", "Release", "net10.0-windows10.0.17763.0");
        Directory.CreateDirectory(exeDir);
        var orch = Path.Combine(_dir, "Orchestration", "HB_Orchestration");
        Directory.CreateDirectory(orch);

        var layout = RepoLayout.Find(exeDir)!;
        Assert.Equal(chatRoot, layout.ChatRoot);
        Assert.Equal(Path.Combine(orch, "src", "HB.Orchestration.Dashboard"), layout.DashboardProjectDir);
        Assert.EndsWith(Path.Combine("src", "HB.Chat", "bin", RepoLayout.Configuration, "net10.0", "HB.Chat.exe"), layout.ChatServerExe);
        Assert.Equal(Path.Combine(_dir, "Orchestration", "Publish_Output"), layout.PublishDir()); // config 없으면 ..\Publish_Output

        File.WriteAllText(Path.Combine(orch, "HB_Orchestration.config"), "# PUBLISH_DIR=nope\nSOLUTION_DIR=.\nPUBLISH_DIR=out\\pub\\\n");
        Assert.Equal(Path.Combine(orch, "out", "pub"), layout.PublishDir()); // 배치처럼 그 폴더 기준, 끝 구분자 없음
        File.WriteAllText(Path.Combine(orch, "HB_Orchestration.config"), "PUBLISH_DIR=\n");
        Assert.Equal(Path.Combine(_dir, "Orchestration", "Publish_Output"), layout.PublishDir()); // 빈 값은 기본

        Assert.Null(RepoLayout.Find(_dir));
    }

    [Fact]
    public void Settings_FileThenArgs()
    {
        var (defaults, w0) = DesktopSettings.Load(["--data=" + _dir]);
        Assert.Null(w0);
        Assert.Equal(("http://localhost:5290", "http://localhost:5280", _dir), (defaults.ChatUrl, defaults.DashboardUrl, defaults.DataFolder));

        File.WriteAllText(Path.Combine(_dir, "desktop.json"), "{ // 주석 허용\n \"chatUrl\": \"http://localhost:5291\", \"DataFolder\": \"C:\\\\elsewhere\", }");
        var (s, w1) = DesktopSettings.Load(["--data=" + _dir, "--dashboard-url=http://localhost:5281"]);
        Assert.Null(w1);
        Assert.Equal(("http://localhost:5291", "http://localhost:5281", _dir), (s.ChatUrl, s.DashboardUrl, s.DataFolder)); // 데이터 폴더는 파일이 못 바꿈

        File.WriteAllText(Path.Combine(_dir, "desktop.json"), "{ broken");
        var (fallback, w2) = DesktopSettings.Load(["--data=" + _dir]);
        Assert.NotNull(w2);
        Assert.Equal("http://localhost:5290", fallback.ChatUrl);
    }

    [Fact]
    public void Placement_KeepsOnlyVisibleWindows()
    {
        Rectangle[] screens = [new(0, 0, 1920, 1040)];
        Assert.NotNull(new WindowPlacement(100, 100, 1200, 800, false).FitTo(screens));
        Assert.Null(new WindowPlacement(3000, 100, 1200, 800, false).FitTo(screens));   // 떼어 낸 모니터
        Assert.Null(new WindowPlacement(1850, 100, 1200, 800, false).FitTo(screens));   // 70px만 보임
        Assert.Null(new WindowPlacement(100, 100, 300, 200, false).FitTo(screens));     // 최소 크기보다 작음

        var path = Path.Combine(_dir, "w.json");
        new WindowPlacement(10, 20, 900, 700, true).Save(path);
        Assert.Equal(new WindowPlacement(10, 20, 900, 700, true), WindowPlacement.Load(path));
        File.WriteAllText(path, "{");
        Assert.Null(WindowPlacement.Load(path));
    }

    [Fact]
    public void WebRtcBlock_CoversConstructors() =>
        Assert.All(["RTCPeerConnection", "webkitRTCPeerConnection", "RTCDataChannel"], k => Assert.Contains("'" + k + "'", MainForm.WebRtcBlock));

    [Fact]
    public void BrowserArguments_ResolveOnlyLocalhost() =>
        Assert.Contains("--host-resolver-rules=\"MAP * ~NOTFOUND , EXCLUDE localhost\"", MainForm.BrowserArguments); // d459e3af9b C2

    [Theory]
    [InlineData("http://localhost:5290", true)]
    [InlineData("http://127.0.0.1:5290/", true)]
    [InlineData("http://[::1]:5290", true)]
    [InlineData("http://192.168.0.10:5290", false)]   // LAN에 열림 (d459e3af9b C3)
    [InlineData("http://mypc:5290", false)]
    [InlineData("https://localhost:5290", false)]     // 서버는 http로만 띄운다
    [InlineData("http://localhost:5290/Room/x", false)]
    [InlineData("http://user@localhost:5290", false)]
    [InlineData("abc", false)]
    [InlineData(null, false)]
    public void IsLoopbackHttp_OnlyThisPc(string? url, bool ok) => Assert.Equal(ok, DesktopSettings.IsLoopbackHttp(url));

    [Fact]
    public void Settings_RejectBadValuesWithWarning()
    {
        File.WriteAllText(Path.Combine(_dir, "desktop.json"),
            "{ \"chatUrl\": \"http://192.168.0.10:5290\", \"dashboardUrl\": null, \"chatServerExe\": \"C:\\\\evil\\\\calc.exe\", " +
            "\"dashboardServerExe\": \"C:\\\\x\\\\HB.Orchestration.Dashboard.exe\" }");
        var (s, warning) = DesktopSettings.Load(["--data=" + _dir + Path.DirectorySeparatorChar, "--debug-port=80"]);
        Assert.Equal((DesktopSettings.DefaultChatUrl, DesktopSettings.DefaultDashboardUrl), (s.ChatUrl, s.DashboardUrl));
        Assert.Null(s.ChatServerExe);                                            // 이름이 HB.Chat.exe가 아님 (d459e3af9b C4)
        Assert.Equal(@"C:\x\HB.Orchestration.Dashboard.exe", s.DashboardServerExe);
        Assert.Null(s.DebugPort);                                                // 1024 아래
        Assert.Equal(_dir, s.DataFolder);                                        // 끝 구분자를 뗀 같은 값 (26d478170f C16)
        Assert.NotNull(warning);
        Assert.Contains("calc.exe", warning);

        var (d, _) = DesktopSettings.Load(["--data=" + _dir, "--debug-port=9333"]);
        Assert.Equal(9333, d.DebugPort);
        File.WriteAllText(Path.Combine(_dir, "desktop.json"), "{ \"debugPort\": 9333 }");
        Assert.Null(DesktopSettings.Load(["--data=" + _dir]).Settings.DebugPort); // 파일로는 원격 디버깅을 못 연다
    }

    [Theory]
    [InlineData("https://example.com/a/b?secret=1#x", "https://example.com/a/b")]
    [InlineData("ms-settings:privacy?x=1", "ms-settings:privacy")]
    [InlineData(null, "(없음)")]
    public void Short_DropsQuery(string? url, string expected) => Assert.Equal(expected, MainForm.Short(url)); // d459e3af9b C6

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Res(System.Net.HttpStatusCode code, string body) => new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task Probe_TellsMissingBusyOldAndOther()
    {
        var listening = true;
        LocalServer Chat() => new(ServerKind.Chat, new Uri("http://localhost:5290"), "x.exe", ".", [], "log", _ => listening);
        async Task<ServerState> Probe(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            using var http = new HttpClient(new FakeHandler(respond));
            return await Chat().ProbeAsync(http, CancellationToken.None);
        }

        Assert.Equal(ServerState.Ours, await Probe(_ => Res(System.Net.HttpStatusCode.OK, "[]")));
        Assert.Equal(ServerState.Other, await Probe(_ => throw new HttpRequestException("reset"))); // 듣는데 HTTP가 아님
        listening = false;
        Assert.Equal(ServerState.Missing, await Probe(_ => throw new InvalidOperationException("듣는 것이 없으면 묻지도 않는다")));
        listening = true;
        Assert.Equal(ServerState.Busy, await Probe(_ => throw new TaskCanceledException("timeout"))); // 늦은 우리 서버일 수 있음 — 새로 띄우지 않음 (26d478170f C1)
        Assert.Equal(ServerState.OldChat, await Probe(r => r.RequestUri!.AbsolutePath == "/api/rooms"
            ? Res(System.Net.HttpStatusCode.NotFound, "") : Res(System.Net.HttpStatusCode.OK, "<title>HB Chat · 방</title>")));   // 26d478170f C8
        Assert.Equal(ServerState.Other, await Probe(r => r.RequestUri!.AbsolutePath == "/api/rooms"
            ? Res(System.Net.HttpStatusCode.NotFound, "") : Res(System.Net.HttpStatusCode.OK, "<title>Something</title>")));
        Assert.Equal(ServerState.Other, await Probe(_ => Res(System.Net.HttpStatusCode.OK, "<html>")));
    }
}
