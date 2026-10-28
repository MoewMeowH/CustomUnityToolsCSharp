using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HB.Chat;
using HB.Orchestration.Shared;

namespace HB.Chat.Tests;

/// <summary>
/// 지휘자 브라우저 (계획서 2026-10-29_HB_Chat-지휘자-브라우저): 주소 검사 · 사이트 허용 · 늘 막는 포트, 실제 Chrome(없으면 Edge)으로 열기 · 스크린샷 크기 ·
/// 글 읽기 · 클릭 · 밖 사이트 이동 · 새 탭 · iframe 막기 · 대화 상자 닫기 · 같은 문서 이동 · 미사용 닫기, 스크린샷 기록 파일, 휴지통 정리, 기록 줄의 그림 · 인자 · 규칙.
/// 브라우저가 없는 PC에서는 실제 띄우는 시험을 건너뛴다(xUnit 2라 통과로 보인다).
/// </summary>
public sealed class AgentBrowserTests : IDisposable
{
    private const string Room = "abcdef123456";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-browser-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _server = new(IPAddress.Loopback, 0);
    private readonly TcpListener _forbidden = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<AgentBrowser> _browsers = [];

    private const string MainPage =
        "<html><head><title>probe</title></head><body style='margin:0;font:40px sans-serif'><h1 id=t>HB42</h1>" +
        "<button id=b style='position:absolute;left:20px;top:200px;width:200px;height:80px' onclick=\"document.getElementById('t').textContent='CLICKED'\">press</button>" +
        "<a id=out style='position:absolute;left:20px;top:320px' href='https://example.com/'>outside</a>" +
        "<a id=tab style='position:absolute;left:300px;top:320px' href='/' target='_blank'>newtab</a>" +
        "<button id=d style='position:absolute;left:600px;top:200px;width:200px;height:80px' onclick=\"alert('hello dialog')\">alert</button></body></html>";

    private const string FramePage = "<html><head><title>frame</title></head><body><iframe src='https://example.com/'></iframe></body></html>";

    public AgentBrowserTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Start();
        _forbidden.Start();
        _ = Serve(_server);
        _ = Serve(_forbidden);
    }

    private int Port => ((IPEndPoint)_server.LocalEndpoint).Port;
    private int ForbiddenPort => ((IPEndPoint)_forbidden.LocalEndpoint).Port;

    private AgentBrowser NewBrowser(TimeSpan? idle = null)
    {
        var b = new AgentBrowser(() => _dir, blockedPorts: () => [ForbiddenPort]) { IdleClose = idle ?? TimeSpan.FromMinutes(30) };
        _browsers.Add(b);
        return b;
    }

    /// <summary>
    /// 아주 작은 HTTP 서버: /frame이면 다른 사이트 iframe 페이지, 그 밖은 시험 페이지. 연결마다 따로 — Chrome이 미리 열어 두고 요청을 안 보내는 연결이
    /// 있으면 하나씩 받는 서버는 다음 요청을 못 받았다(동시 시험에서 시간 초과).
    /// </summary>
    private async Task Serve(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Answer(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
    }

    private async Task Answer(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var n = await stream.ReadAsync(buffer, _stop.Token);
            if (n == 0) return;
            var request = Encoding.ASCII.GetString(buffer, 0, n);
            var body = Encoding.UTF8.GetBytes(request.StartsWith("GET /frame", StringComparison.Ordinal) ? FramePage : MainPage);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        foreach (var b in _browsers) b.Dispose();
        _stop.Cancel();
        _server.Stop();
        _forbidden.Stop();
        Thread.Sleep(300); // 브라우저가 프로필 파일을 놓도록
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("http://localhost:5173/", true)]
    [InlineData("http://127.0.0.7/", true)]
    [InlineData("http://[::1]:8080/", true)]
    [InlineData("http://app.localhost/", true)]
    [InlineData("http://localhost.:5290/", true)] // 끝의 점 (사용자 시험에서 찾음)
    [InlineData("http://app.localhost./", true)]
    [InlineData("http://LOCALHOST.:5290/", true)]
    [InlineData("http://10.0.0.5/", false)]
    [InlineData("https://example.com/", false)]
    public void LocalHosts(string url, bool local) => Assert.Equal(local, AgentBrowser.IsLocalHost(new Uri(url).Host));

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("chrome://settings")]
    [InlineData("not a url")]
    public void OnlyHttpUrls(string url) => Assert.Null(AgentBrowser.ParseUrl(url));

    [Fact]
    public void ApprovedHosts_PerRoom_ForbiddenPortsAlways()
    {
        var b = NewBrowser();
        Assert.False(b.IsAllowed(Room, new Uri("https://example.com/")));
        b.ApproveHost(Room, "Example.com");
        Assert.True(b.IsAllowed(Room, new Uri("https://example.com/other")));
        Assert.False(b.IsAllowed("zzzzzz999999", new Uri("https://example.com/")));
        // 이 앱 · 대시보드 포트는 로컬이어도 늘 막힘 — 카드로 허용해도 (8b11177bd5 C1)
        var app = new Uri($"http://127.0.0.1:{ForbiddenPort}/Room/x");
        Assert.True(b.IsForbidden(app));
        Assert.True(b.IsForbidden(new Uri($"http://localhost:{ForbiddenPort}/")));
        // 끝의 점 · 다른 loopback 모양도 같은 막힘 (사용자 시험 2026-10-29: localhost.가 카드 길로 갔다)
        foreach (var url in new[] { $"http://localhost.:{ForbiddenPort}/", $"http://x.localhost.:{ForbiddenPort}/", $"http://127.1:{ForbiddenPort}/",
                     $"http://2130706433:{ForbiddenPort}/", $"http://[::ffff:127.0.0.1]:{ForbiddenPort}/", $"http://LOCALHOST:{ForbiddenPort}/" })
            Assert.True(b.IsForbidden(new Uri(url)), url);
        b.ApproveHost(Room, "Example.com.");
        Assert.True(b.IsAllowed(Room, new Uri("https://example.com./x"))); // 끝의 점은 같은 사이트
        Assert.False(b.IsAllowed(Room, app));
        b.ApproveHost(Room, "127.0.0.1");
        Assert.False(b.IsAllowed(Room, app));
        Assert.False(b.IsForbidden(new Uri($"http://127.0.0.1:{Port}/")));
    }

    [Fact]
    public async Task OpenScreenshotReadClick_BlockOutside_NewTab_Dialog_IdleClose()
    {
        if (AgentBrowser.FindInstalled() is null) return; // 브라우저가 없는 PC
        var b = NewBrowser(TimeSpan.FromSeconds(3));
        var changes = 0;
        b.Changed += _ => Interlocked.Increment(ref changes);
        Assert.StartsWith("[거절]", await b.OpenAsync(Room, "https://example.com/", default)); // 허용 안 된 밖 사이트
        Assert.StartsWith("[거절]", await b.OpenAsync(Room, $"http://127.0.0.1:{ForbiddenPort}/", default)); // 이 앱 · 대시보드 자리
        var opened = await b.OpenAsync(Room, $"http://127.0.0.1:{Port}/", default);
        Assert.StartsWith("열림", opened);
        Assert.Contains("밖에서 온 글", opened); // 제목 표시 (8b11177bd5 C4)
        Assert.Equal("probe", b.View(Room).Title);

        var png = await b.ScreenshotAsync(Room, default);
        Assert.Equal((AgentBrowser.Width, AgentBrowser.Height), PngSize(png));

        var read = await b.ReadAsync(Room, default);
        Assert.Contains("HB42", read);
        Assert.Matches("button\\[submit\\] \"press\" \\(120,240\\)", read);
        await b.ClickAsync(Room, 120, 240, default);
        Assert.Contains("CLICKED", await b.ReadAsync(Room, default));
        Assert.StartsWith("[거절]", await b.ClickAsync(Room, 5000, 10, default));
        Assert.StartsWith("키: Enter", await b.KeyAsync(Room, "Enter", default));
        Assert.StartsWith("[거절]", await b.KeyAsync(Room, "F13", default));

        // alert이 떠도 멈추지 않고 닫은 뒤 알린다 (822a55c0cf C3)
        var dialog = await b.ClickAsync(Room, 700, 240, default);
        Assert.Contains("hello dialog", dialog);
        Assert.StartsWith("키: Tab", await b.KeyAsync(Room, "Tab", default)); // 페이지가 그대로 움직인다

        // 같은 문서 안 이동은 로드를 기다리지 않는다 (822a55c0cf C6)
        var sw = Stopwatch.StartNew();
        Assert.StartsWith("열림", await b.OpenAsync(Room, $"http://127.0.0.1:{Port}/#part", default));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.EndsWith("#part", b.View(Room).Url);

        // 새 탭(target=_blank)은 바로 닫힌다 (8b11177bd5 C2)
        var page = await b.ReadAsync(Room, default);
        var tab = Regex.Match(page, "a \"newtab\" \\((\\d+),(\\d+)\\)");
        Assert.True(tab.Success, page);
        await b.ClickAsync(Room, int.Parse(tab.Groups[1].Value), int.Parse(tab.Groups[2].Value), default);
        await Task.Delay(800);
        Assert.StartsWith("새 탭", b.View(Room).Blocked);

        // 페이지 안 링크로 밖 사이트에 가는 이동도 막힌다 (실측 ④)
        var link = Regex.Match(page, "a \"outside\" \\((\\d+),(\\d+)\\)");
        await b.ClickAsync(Room, int.Parse(link.Groups[1].Value), int.Parse(link.Groups[2].Value), default);
        await Task.Delay(800);
        Assert.Equal("https://example.com/", b.View(Room).Blocked);
        Assert.NotNull(b.LastFrame(Room)); // 곁칸 실시간 화면

        // 다른 사이트 iframe 문서도 막힌다 — 사이트 격리를 꺼 같은 가로채기 (822a55c0cf C2)
        Assert.StartsWith("열림", await b.OpenAsync(Room, $"http://127.0.0.1:{Port}/frame", default));
        await Task.Delay(500);
        Assert.Equal("https://example.com/", b.View(Room).Blocked);

        // 안 쓰면 닫힌다 (시험에서는 3초)
        await Task.Delay(4500);
        Assert.False(b.View(Room).Open);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task ConcurrentFirstCalls_LaunchOneBrowser()
    {
        if (AgentBrowser.FindInstalled() is null) return;
        // 같은 방 첫 호출 둘이 겹쳐도 Chrome은 하나 (822a55c0cf C4)
        var b = NewBrowser();
        var url = $"http://127.0.0.1:{Port}/";
        var results = await Task.WhenAll(b.OpenAsync(Room, url, default), b.OpenAsync(Room, url, default));
        Assert.All(results, r => Assert.StartsWith("열림", r));
        var profile = Path.Combine(_dir, "browser", Room);
        Assert.Equal(1, MainBrowserProcesses(profile));
        b.Close(Room, "시험");
        Assert.False(b.View(Room).Open);
        Assert.Equal(0, MainBrowserProcesses(profile)); // 트리째 끝남
    }

    /// <summary>그 프로필을 쓰는 브라우저 본 프로세스 수 (명령 줄은 CIM으로).</summary>
    private static int MainBrowserProcesses(string profile)
    {
        var exe = Path.GetFileName(AgentBrowser.FindInstalled()!);
        var script = $"@(Get-CimInstance Win32_Process -Filter \"Name='{exe}'\" | Where-Object {{ $_.CommandLine -like '*{profile}*' -and $_.CommandLine -notlike '*--type=*' }}).Count";
        var info = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(output.Trim());
    }

    [Fact]
    public void Shots_SavedAndServedOnlyByShape()
    {
        var b = NewBrowser();
        var name = b.SaveShot(Room, [1, 2, 3]);
        var second = b.SaveShot(Room, [4, 5, 6]);
        Assert.Matches(@"^\d{8}-\d{6}-\d{3}(-\d+)?\.png$", name);
        Assert.NotEqual(name, second); // 같은 밀리초여도 덮지 않음
        Assert.NotNull(b.ShotPath(Room, name));
        Assert.NotNull(b.ShotPath(Room, second));
        Assert.Null(b.ShotPath("zzzzzz999999", name)); // 다른 방
        Assert.Null(b.ShotPath(Room, "..\\..\\x.png"));
        Assert.Null(b.ShotPath(Room, "20261029-000000-000.png")); // 없는 파일
        Assert.Throws<InvalidOperationException>(() => b.SaveShot("../x", [1]));
    }

    [Fact]
    public void TrashPurge_RemovesBrowserProfileAndShots()
    {
        // 프로필(쿠키) · 스크린샷도 방과 함께 지운다 (8b11177bd5 C6)
        var store = new RoomStore(Path.Combine(_dir, "rooms.json"));
        var room = store.Create("ws", PermissionLevel.Command, "opus", null);
        store.Update(room.Id, r => r with { DeletedAt = DateTimeOffset.Now });
        var dirs = new[] { "attachments", "browser", "browser-shots" }.Select(d => Path.Combine(_dir, d, room.Id)).ToList();
        foreach (var d in dirs) Directory.CreateDirectory(d);
        Assert.Equal([room.Id], Trash.DeleteNow(store, _dir, [room.Id]));
        Assert.All(dirs, d => Assert.False(Directory.Exists(d)));
    }

    [Fact]
    public void ShotNote_HasImage_ArgsAndRules()
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now, room = Room, tool = RoomNotes.BrowserShotTool, summary = "20261029-010203-004.png", decision = "스크린샷 — http://localhost:3000/",
        });
        var (room, note) = RoomNotes.Parse(line)!.Value;
        Assert.Equal(Room, room);
        Assert.Equal("스크린샷 — http://localhost:3000/", note.Text);
        Assert.Equal("20261029-010203-004.png", note.Image);
        Assert.False(note.Denied);

        // 브라우저 도구: 열기만 호스트가 보고 나머지는 묻지 않는다
        Assert.Contains("mcp__hb-chat__browser_screenshot", TerminalMcp.QuietTools);
        Assert.DoesNotContain(TerminalMcp.BrowserOpenTool, TerminalMcp.QuietTools);
        Assert.Contains("browser_open", RoomRules.For(PermissionLevel.Command, terminal: true));
        Assert.Contains("로그인 · 결제 정보는 입력하지 않는다", RoomRules.For(PermissionLevel.Command, terminal: true));
        Assert.Contains("이 앱 · 대시보드 주소는 열 수 없음", RoomRules.For(PermissionLevel.Command, terminal: true));
        Assert.DoesNotContain("browser_open", RoomRules.For(PermissionLevel.Command));
        Assert.Equal("""{"url":"https://example.com/"}""", TerminalMcp.UrlInput("https://example.com/"));
        Assert.Equal("https://x.test/", TerminalMcp.ParseString("""{"url":"https://x.test/","n":1}""", "url"));
        Assert.Null(TerminalMcp.ParseString("""{"url":3}""", "url"));
        // 늘 막는 포트: 설정 목록 + 대시보드 주소 포트
        var server = new NoServer();
        server.Features.Set<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>(
            new Microsoft.AspNetCore.Hosting.Server.Features.ServerAddressesFeature { Addresses = { "http://localhost:5291" } });
        var ports = TerminalMcp.BlockedPorts(server, new ChatOptions { DashboardUrl = "http://localhost:5280" });
        Assert.Equal([5280, 5290, 5291], ports.Order());
    }

    /// <summary>주소 목록만 있는 서버 (늘 막는 포트 시험용).</summary>
    private sealed class NoServer : Microsoft.AspNetCore.Hosting.Server.IServer
    {
        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public Task StartAsync<TContext>(Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application, CancellationToken ct) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static (int W, int H) PngSize(byte[] png) =>
        (png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19], png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23]);
}
