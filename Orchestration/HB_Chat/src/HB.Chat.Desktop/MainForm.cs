using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HB.Chat.Desktop;

/// <summary>
/// 큰 창 (계획서 2-3 a): 위 탭 줄(채팅 · 대시보드) + WebView2 둘(같은 사용자 데이터 폴더). 켤 때 두 서버를 띄우거나 붙는다.
/// WebView2 규칙: 호스트 다리 없음(웹 메시지 · 호스트 객체 끔), 권한 요청 모두 거부, WebRTC를 문서 생성 스크립트로 지움(위젯 · 중첩 틀까지 — 0번 실측),
/// localhost 말고 이름 조회를 막음(DNS로 알리는 길 — dns-prefetch 등), 큰 틀 이동 · 새 창은 <see cref="NavigationPolicy"/>와 사용자 클릭,
/// 하위 틀은 about:srcdoc · about:blank만, 사용자 프로토콜 실행 거부. 화면은 HTTP 응답으로만 띄운다 (페이지 CSP가 붙게).
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>
    /// 위젯이 WebRTC(STUN · TURN 주소로 DNS · UDP)로 글을 내보내지 못하게 생성자를 지우고 다시 정의하지 못하게 한다.
    /// 문서 생성 스크립트는 모든 틀(위젯 srcdoc · 위젯이 만든 안쪽 iframe)에서 돈다 — 0번 실측. 브라우저 인자(--force-webrtc-ip-handling-policy)는 막지 못했다.
    /// 채팅 · 대시보드 화면은 WebRTC를 쓰지 않는다.
    /// </summary>
    internal const string WebRtcBlock =
        "(()=>{for(const k of ['RTCPeerConnection','webkitRTCPeerConnection','RTCDataChannel','RTCIceCandidate','RTCSessionDescription','RTCRtpSender','RTCRtpReceiver'])" +
        "{try{delete window[k];}catch(e){}try{Object.defineProperty(window,k,{value:undefined,configurable:false,writable:false});}catch(e){}}})();";

    /// <summary>
    /// 이 창의 WebView2는 localhost만 쓴다 (밖 주소는 기본 브라우저로). 그 밖의 이름 조회를 모두 실패시켜 dns-prefetch · preconnect ·
    /// STUN 이름처럼 CSP가 다루지 않는 DNS 길을 막는다 (/self-review security d459e3af9b C2).
    /// </summary>
    internal const string BrowserArguments = "--host-resolver-rules=\"MAP * ~NOTFOUND , EXCLUDE localhost\"";

    private readonly DesktopSettings _settings;
    private readonly string? _startWarning;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _showWait;
    private readonly Uri _chatUrl, _dashboardUrl;
    private readonly NavigationPolicy _policy;
    private readonly ToolStripButton _chatTab = new("채팅"), _dashTab = new("대시보드");
    private readonly ToolStripLabel _status = new() { Alignment = ToolStripItemAlignment.Right };
    private readonly WebView2 _chatView = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _dashView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _message = new() { Dock = DockStyle.Fill, Visible = false, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(24) };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _cts = new();
    private readonly string _placementPath;
    private readonly Lock _traceGate = new();
    private DesktopJob? _job;
    private LocalServer? _chatServer, _dashServer;
    private AppTab _tab = AppTab.Chat;
    private string? _chatError = "서버 준비 중…", _dashError = "서버 준비 중…";
    private string? _dashPending;
    private volatile bool _closing;

    public MainForm(DesktopSettings settings, string? startWarning, EventWaitHandle show)
    {
        _settings = settings;
        _startWarning = startWarning;
        _show = show;
        _chatUrl = new Uri(settings.ChatUrl); // DesktopSettings.Load가 루프백 http만 남긴다
        _dashboardUrl = new Uri(settings.DashboardUrl);
        _policy = new NavigationPolicy(_chatUrl, _dashboardUrl);
        _placementPath = Path.Combine(settings.DataFolder, "desktop-window.json");

        Text = "HB Chat";
        MinimumSize = WindowPlacement.MinSize;
        Size = new Size(1280, 860);
        StartPosition = FormStartPosition.CenterScreen;
        if (WindowPlacement.Load(_placementPath)?.FitTo(Screen.AllScreens.Select(s => s.WorkingArea)) is { } p)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = p.Bounds;
            if (p.Maximized) WindowState = FormWindowState.Maximized;
        }

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(6, 2, 6, 2) };
        _chatTab.Click += (_, _) => ShowTab(AppTab.Chat, null);
        _dashTab.Click += (_, _) => ShowTab(AppTab.Dashboard, null);
        bar.Items.AddRange([_chatTab, _dashTab, _status]);
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.AddRange([_chatView, _dashView, _message]);
        Controls.Add(body);
        Controls.Add(bar);
        UpdateTabs();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => OnShowSignal(), null, -1, false);
        if (_startWarning is not null) SetStatus(_startWarning);
        try
        {
            await StartAsync();
        }
        catch (OperationCanceledException)
        {
            // 켜는 중에 닫음 — 조용히 끝낸다 (지휘자 검토 R2)
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or Win32Exception or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (_closing) return;
            _chatError = _dashError = "켜지 못했습니다: " + ex.Message;
            UpdateTabs();
        }
    }

    private async Task StartAsync()
    {
        var layout = RepoLayout.Find(AppContext.BaseDirectory);
        if (layout is null && (_settings.ChatServerExe is null || _settings.DashboardServerExe is null))
        {
            _chatError = _dashError = "저장소의 HB_Chat 폴더를 찾지 못했습니다 — desktop.json에 ChatServerExe · DashboardServerExe를 적으세요.";
            UpdateTabs();
            return;
        }
        _job = new DesktopJob();
        var chatExe = _settings.ChatServerExe ?? layout!.ChatServerExe;
        var dashExe = _settings.DashboardServerExe ?? layout!.DashboardServerExe;
        _chatServer = new LocalServer(ServerKind.Chat, _chatUrl, chatExe, layout?.ChatProjectDir ?? Path.GetDirectoryName(chatExe)!, [],
            Path.Combine(_settings.LogFolder, "desktop-chat-server.log"));
        var publish = _settings.PublishDir ?? layout?.PublishDir();
        _dashServer = new LocalServer(ServerKind.Dashboard, _dashboardUrl, dashExe, layout?.DashboardProjectDir ?? Path.GetDirectoryName(dashExe)!,
            publish is null ? [] : ["--Dashboard:McpConfigDir=" + publish], Path.Combine(_settings.LogFolder, "desktop-dashboard-server.log"));

        var arguments = BrowserArguments + (_settings.DebugPort is { } port ? $" --remote-debugging-port={port}" : "");
        var env = await CoreWebView2Environment.CreateAsync(null, _settings.WebViewFolder, new CoreWebView2EnvironmentOptions(arguments));
        await ConfigureAsync(_chatView, AppTab.Chat, env);
        await ConfigureAsync(_dashView, AppTab.Dashboard, env);

        // 서버마다 따로 기다린다 — 한쪽이 늦거나 실패해도 다른 탭은 준비되는 대로 연다 (26d478170f C3 · C4)
        var progress = new Progress<string>(SetStatus);
        await Task.WhenAll(BringUpAsync(_chatServer, progress), BringUpAsync(_dashServer, progress));
        if (!_closing) SetStatus(string.Join(" · ", new[] { Describe(_chatServer, _chatError), Describe(_dashServer, _dashError) }));
    }

    private async Task BringUpAsync(LocalServer server, IProgress<string> progress)
    {
        string? error;
        try
        {
            error = await server.EnsureAsync(_job!, _http, progress, _cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            error = "서버를 확인하지 못했습니다: " + ex.Message;
        }
        if (_closing) return;
        if (server.Kind == ServerKind.Chat)
        {
            _chatError = error;
            if (error is null) _chatView.CoreWebView2.Navigate(_chatUrl.AbsoluteUri);
        }
        else
        {
            _dashError = error;
            if (error is null) _dashView.CoreWebView2.Navigate(_dashPending ?? _dashboardUrl.AbsoluteUri);
            _dashPending = null;
        }
        UpdateTabs();
    }

    /// <summary>상태 줄: "채팅 5290" (앱이 띄움) · "(붙음)" (이미 떠 있던 것) · "(안 됨)" (오류 — 그 탭에 이유).</summary>
    private static string Describe(LocalServer? s, string? error) =>
        s is null ? "" : $"{(s.Kind == ServerKind.Chat ? "채팅" : "대시보드")} {s.BaseUrl.Port}{(error is not null ? " (안 됨)" : s.Owned ? "" : " (붙음)")}";

    private async Task ConfigureAsync(WebView2 view, AppTab tab, CoreWebView2Environment env)
    {
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        var s = core.Settings;
        s.IsWebMessageEnabled = false;      // 페이지 · 위젯이 호스트에 말을 걸 길 없음 (알림은 호스트가 HTTP로 직접)
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreDevToolsEnabled = true;        // 개인 앱 — 문제 볼 때
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebRtcBlock);

        // 큰 틀: 이 탭 출처는 그대로. 다른 탭 · 기본 브라우저로 보내는 것은 사용자가 시작한 이동만 — 스크립트 · meta refresh · 리다이렉트로
        // 브라우저를 열거나 탭을 바꿔치지 못하게 (26d478170f C5 · d459e3af9b C1)
        core.NavigationStarting += (_, e) =>
        {
            var d = _policy.Main(e.Uri, tab);
            if (d.Action == NavAction.Allow) return;
            e.Cancel = true;
            var allowed = d.Action switch
            {
                NavAction.Switch => e.IsUserInitiated,
                NavAction.External => e.IsUserInitiated && !e.IsRedirected,
                _ => false,
            };
            Trace($"{tab} 큰 틀 이동 {d.Action}{(allowed ? "" : " 막음")} {Short(e.Uri)} (사용자 {e.IsUserInitiated}, 리다이렉트 {e.IsRedirected})");
            if (allowed) BeginInvoke(() => Route(d));
        };
        // 하위 틀(위젯 iframe · 그 안의 틀): srcdoc · 빈 틀 말고는 취소 (페이지 CSP frame-src 'none'과 이중)
        core.FrameNavigationStarting += (_, e) =>
        {
            if (NavigationPolicy.FrameAllowed(e.Uri)) return;
            e.Cancel = true;
            Trace($"{tab} 하위 틀 이동 막음 {Short(e.Uri)}");
        };
        // target=_blank 링크(호출 상세 · 대시보드에서 보기 · 앱에서 열기 등): 앱 출처면 그 탭, 밖이면 브라우저. 스크립트가 연 창은 무시.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Trace($"{tab} 새 창 요청 {Short(e.Uri)} (사용자 {e.IsUserInitiated})");
            if (!e.IsUserInitiated) return;
            var d = _policy.Main(e.Uri, tab);
            BeginInvoke(() => Route(d.Action == NavAction.Allow ? d with { Action = NavAction.Switch } : d));
        };
        // 사용자 프로토콜(ms-settings: 등) 실행은 이동 규칙과 별도로 한 번 더 막는다 (d459e3af9b C7)
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            Trace($"{tab} 외부 프로토콜 실행 막음 {Short(e.Uri)}");
        };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, e) => BeginInvoke(() => SetStatus(e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited
            ? "화면 엔진이 끝났습니다 — 앱을 다시 켜세요" // 브라우저 프로세스는 새로 고침으로 돌아오지 않는다 (26d478170f C6)
            : $"화면 프로세스 오류({e.ProcessFailedKind}) — F5로 새로 고침"));
    }

    private void Route(NavDecision d)
    {
        switch (d.Action)
        {
            case NavAction.Switch:
                ShowTab(d.Tab, d.Url);
                break;
            case NavAction.External when d.Url is { } url:
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Win32Exception ex)
                {
                    SetStatus("브라우저로 열지 못했습니다: " + ex.Message);
                }
                break;
        }
    }

    private void ShowTab(AppTab tab, string? url)
    {
        _tab = tab;
        if (url is not null)
        {
            var view = tab == AppTab.Chat ? _chatView : _dashView;
            var ready = (tab == AppTab.Chat ? _chatError : _dashError) is null && view.CoreWebView2 is not null;
            if (ready) view.CoreWebView2!.Navigate(url);
            else if (tab == AppTab.Dashboard) _dashPending = url; // 대시보드가 뜨면 그 주소로
        }
        UpdateTabs();
    }

    private void UpdateTabs()
    {
        _chatTab.Checked = _tab == AppTab.Chat;
        _dashTab.Checked = _tab == AppTab.Dashboard;
        var error = _tab == AppTab.Chat ? _chatError : _dashError;
        _message.Text = error ?? "";
        _message.Visible = error is not null;
        _chatView.Visible = _tab == AppTab.Chat && error is null;
        _dashView.Visible = _tab == AppTab.Dashboard && error is null;
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>두 번째 실행 신호 (스레드 풀). 창이 닫히는 중이거나 핸들이 없으면 버린다 (26d478170f C7).</summary>
    private void OnShowSignal()
    {
        if (_closing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(BringUp);
        }
        catch (InvalidOperationException)
        {
            // 그 사이 핸들이 없어짐 (ObjectDisposedException 포함)
        }
    }

    private void BringUp()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>이동 결정 기록 (logs/desktop.log) — 막거나 옮긴 것만.</summary>
    private void Trace(string line)
    {
        try
        {
            lock (_traceGate)
            {
                Directory.CreateDirectory(_settings.LogFolder);
                File.AppendAllText(Path.Combine(_settings.LogFolder, "desktop.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 기록을 못 해도 동작은 그대로
        }
    }

    /// <summary>기록용 주소: 쿼리 · 조각은 빼고(막힌 유출 시도의 글 · 링크의 토큰이 로그에 남지 않게 — d459e3af9b C6) 앞 160자.</summary>
    internal static string Short(string? url)
    {
        if (url is null) return "(없음)";
        var s = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? u.GetLeftPart(UriPartial.Path)
            : url.Split('?', '#')[0];
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 이 앱이 띄운 채팅 서버에 도는 턴이 있으면 묻는다 — 끄면 서버와 claude가 같이 끝나 그 턴이 멈춘다.
        if (_chatServer is { Owned: true } && AnyRunning()
            && MessageBox.Show(this, "실행 중인 턴이 있습니다. 끄면 그 턴이 멈춥니다. 끌까요?", "HB Chat",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _closing = true;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized).Save(_placementPath);
        base.OnFormClosing(e);
    }

    private bool AnyRunning()
    {
        try
        {
            var task = Task.Run(() => _http.GetStringAsync(new Uri(_chatUrl, "/api/rooms")));
            return task.Wait(TimeSpan.FromSeconds(2)) && RoomStates.AnyRunning(task.Result);
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _showWait?.Unregister(null);
            _cts.Cancel();
            _chatView.Dispose();
            _dashView.Dispose();
            _http.Dispose();
            _job?.Dispose(); // 앱이 띄운 서버 · claude 트리를 끝낸다
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
