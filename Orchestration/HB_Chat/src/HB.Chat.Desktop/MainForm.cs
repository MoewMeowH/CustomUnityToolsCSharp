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
    private readonly EventWaitHandle _show, _exitSignal;
    private RegisteredWaitHandle? _showWait, _exitWait;
    private readonly Uri _chatUrl, _dashboardUrl;
    private readonly NavigationPolicy _policy;
    private readonly ToolStripButton _chatTab = new("채팅"), _dashTab = new("대시보드");

    /// <summary>설정 (3차 d): 채팅 탭에서 /Settings를 연다 — 탭이 아니라 바로 가기.</summary>
    private readonly ToolStripButton _settingsButton = new("설정") { ToolTipText = "작업 폴더 · 방 이름 (채팅 탭에서 열림)" };
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

    // 트레이 · 알림 (2-3 b)
    private readonly TrayIcons _trayIcons = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private RoomWatcher? _watcher;
    private Dictionary<string, RoomState>? _rooms;
    private readonly FailureMarks _unseenFailures = new();
    private string? _balloonRoom;
    private bool _exiting, _hintShown, _confirming;

    // 미니 창 · 단축키 (2-3 c)
    private CoreWebView2Environment? _env;
    private MiniForm? _mini;
    private Task? _miniReady;
    private IReadOnlyList<RoomState> _roomList = [];
    private bool _hotkeyRegistered;
    private const int HotkeyId = 0x4842, WmHotkey = 0x0312;

    public MainForm(DesktopSettings settings, string? startWarning, EventWaitHandle show, EventWaitHandle exit)
    {
        _settings = settings;
        _startWarning = startWarning;
        _show = show;
        _exitSignal = exit;
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
        _settingsButton.Click += (_, _) => ShowTab(AppTab.Chat, new Uri(_chatUrl, "/Settings").AbsoluteUri);
        bar.Items.AddRange([_chatTab, _dashTab, new ToolStripSeparator(), _settingsButton, _status]);
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.AddRange([_chatView, _dashView, _message]);
        Controls.Add(body);
        Controls.Add(bar);
        UpdateTabs();

        // 트레이 (2-3 b): 닫기(X)는 여기로 숨김, 메뉴 열기 · 끝내기, 두 번 누르면 열기, 풍선을 누르면 그 방
        _trayMenu.Items.Add("열기", null, (_, _) => BringUp());
        _trayMenu.Items.Add("미니 창", null, (_, _) => ToggleMini());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("끝내기", null, (_, _) => Exit());
        _tray = new NotifyIcon { Icon = _trayIcons[TrayLevel.Idle], Text = "HB Chat", ContextMenuStrip = _trayMenu, Visible = true };
        _tray.DoubleClick += (_, _) => BringUp();
        _tray.BalloonTipClicked += (_, _) => OpenRoom(_balloonRoom);
        // 작업 표시줄 · Alt-Tab으로 앞에 오면 보고 있는 방의 안 본 실패를 지운다 (0f0b6007a4 C3)
        Activated += (_, _) => UpdateTray();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => OnShowSignal(BringUp), null, -1, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => OnShowSignal(Exit), null, -1, false);
        if (_startWarning is not null)
        {
            _warnings.Add(_startWarning); // 서버 상태가 써져도 남게 (C6)
            SetStatus("");
        }
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
        var env = _env = await CoreWebView2Environment.CreateAsync(null, _settings.WebViewFolder, new CoreWebView2EnvironmentOptions(arguments));
        await ConfigureAsync(_chatView, AppTab.Chat, env);
        await ConfigureAsync(_dashView, AppTab.Dashboard, env);

        // 서버마다 따로 기다린다 — 한쪽이 늦거나 실패해도 다른 탭은 준비되는 대로 연다 (26d478170f C3 · C4)
        var progress = new Progress<string>(SetStatus);
        await Task.WhenAll(BringUpAsync(_chatServer, progress), BringUpAsync(_dashServer, progress));
        if (!_closing) SetStatus(string.Join(" · ", Describe(_chatServer, _chatError), Describe(_dashServer, _dashError)));
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
            if (error is null)
            {
                _chatView.CoreWebView2.Navigate(_chatUrl.AbsoluteUri);
                _watcher = new RoomWatcher(_chatUrl, OnRooms, OnConnection, SynchronizationContext.Current!);
                _watcher.Start();
            }
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
        // 그 방을 열면(방 전환은 pushState라 SourceChanged로 온다) 안 본 실패 표시를 지운다
        if (tab == AppTab.Chat) core.SourceChanged += (_, _) => UpdateTray();
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
                // 미니 창에서 누른 링크도 큰 창에 — 숨김 · 최소화뿐 아니라 다른 창 뒤에 있어도 앞으로 (C3)
                if (!Visible || WindowState == FormWindowState.Minimized || ActiveForm != this) BringUp();
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
        UpdateTray(); // 채팅 탭으로 돌아오면 보고 있는 방의 안 본 실패를 지운다 (0f0b6007a4 C3)
    }

    private void UpdateTabs()
    {
        _chatTab.Checked = _tab == AppTab.Chat;
        _dashTab.Checked = _tab == AppTab.Dashboard;
        _settingsButton.Enabled = _chatError is null; // 채팅 화면이 준비되기 전에는 주소를 받을 곳이 없다 (/self-review 460430d647 C8)
        var error = _tab == AppTab.Chat ? _chatError : _dashError;
        _message.Text = error ?? "";
        _message.Visible = error is not null;
        _chatView.Visible = _tab == AppTab.Chat && error is null;
        _dashView.Visible = _tab == AppTab.Dashboard && error is null;
    }

    /// <summary>상태 줄: 이번 글 + 계속 남는 경고(시작 설정 · 단축키). 다른 글이 경고를 덮지 않게 한 곳에서 붙인다 (bde22e2224 C6).</summary>
    private void SetStatus(string text) =>
        _status.Text = string.Join(" · ", new[] { text }.Concat(_warnings).Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>두 번째 실행 신호(앞으로 · --exit) (스레드 풀). 창이 닫히는 중이거나 핸들이 없으면 버린다 (26d478170f C7).</summary>
    private void OnShowSignal(Action action)
    {
        if (_closing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 그 사이 핸들이 없어짐 (ObjectDisposedException 포함)
        }
    }

    private void BringUp()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        UpdateTray();
    }

    // ---- 알림 · 트레이 (2-3 b) ----

    /// <summary>`/events` 신호마다 받은 방 목록 (UI 스레드). 앞 목록과 비교해 알리고 트레이를 바꾼다.</summary>
    private void OnRooms(IReadOnlyList<RoomState> rooms)
    {
        if (_closing) return;
        var viewing = ViewingRoom();
        var notices = Notices.Diff(_rooms, rooms, viewing);
        _rooms = rooms.ToDictionary(r => r.Id);
        _roomList = rooms;
        _mini?.UpdateRooms(rooms);
        _unseenFailures.Update(notices, rooms, viewing);
        UpdateTray();
        if (notices.Count == 0) return;
        Trace("알림 " + string.Join(", ", notices.Select(n => $"{n.Kind} {n.RoomId}"))); // 방 ID만 (제목은 남기지 않음)
        var (title, text) = Notices.Balloon(notices);
        _balloonRoom = Notices.RoomToOpen(notices);
        var icon = notices.Any(n => n.Kind == NoticeKind.Approval) ? ToolTipIcon.Warning
            : notices.Any(n => n.Kind == NoticeKind.Failed) ? ToolTipIcon.Error : ToolTipIcon.Info;
        _tray.ShowBalloonTip(5000, title, text.Length > 0 ? text : "(제목 없음)", icon);
    }

    /// <summary>
    /// 연결이 끊기면(서버 종료 · 다시 켜는 중) 앞 목록을 버리고 트레이를 "연결 끊김"으로. 다시 붙은 뒤 첫 목록은 조용히 받는다 —
    /// 서버를 다시 켜서 끊긴 턴을 "답 끝"으로 알리지 않게 (0f0b6007a4 C4).
    /// </summary>
    private void OnConnection(bool connected)
    {
        if (_closing || connected || _rooms is null) return;
        _rooms = null;
        _trayLevel = TrayLevel.Idle;
        _tray.Icon = _trayIcons[TrayLevel.Idle];
        _tray.Text = TrayStatus.Disconnected;
        Trace("채팅 서버 연결 끊김");
    }

    /// <summary>트레이 아이콘 · 도움말. 지금 보고 있는 방의 실패는 본 것으로.</summary>
    private void UpdateTray()
    {
        if (_closing || _rooms is null) return;
        var rooms = _rooms.Values.ToList();
        _unseenFailures.Update([], rooms, ViewingRoom());
        var level = TrayStatus.Level(rooms, _unseenFailures.Ids);
        if (level != _trayLevel) Trace($"트레이 {_trayLevel} → {level}");
        _trayLevel = level;
        _tray.Icon = _trayIcons[level];
        _tray.Text = TrayStatus.Tooltip(rooms);
    }

    private TrayLevel _trayLevel = TrayLevel.Idle;

    /// <summary>
    /// 사용자가 지금 보고 있는 채팅 방: 미니 창이 앞이면 그 방, 큰 창이 앞이고 채팅 탭이면 그 방. 아니면 null — 그 방도 알린다.
    /// </summary>
    private string? ViewingRoom()
    {
        if (_mini is { Visible: true } mini && ActiveForm == mini && mini.WindowState != FormWindowState.Minimized) return mini.Current;
        return Visible && WindowState != FormWindowState.Minimized && ActiveForm == this && _tab == AppTab.Chat && _chatView.CoreWebView2 is { } core
            ? RoomIdOf(core.Source, _chatUrl)
            : null;
    }

    // ---- 미니 창 · 전역 단축키 (2-3 c) ----

    /// <summary>미니 창 보이기 · 숨기기 (트레이 메뉴 · 단축키). 처음 부를 때 만든다 — 채팅 서버가 준비된 뒤에만.</summary>
    private async void ToggleMini()
    {
        if (_closing) return;
        if (_chatError is not null || _env is null)
        {
            SetStatus("채팅 서버가 준비되면 미니 창을 열 수 있습니다");
            return;
        }
        // 만드는 중에 또 부르면(트레이 · 단축키가 겹침) 무시 — 둘 다 Toggle하면 보였다 바로 숨는다 (/self-review bde22e2224 C5)
        if (_miniReady is { IsCompleted: false }) return;
        try
        {
            await (_miniReady ??= CreateMiniAsync(_env));
        }
        catch (Exception ex)
        {
            // async void라 여기서 다 받는다 — 다음에 다시 만든다
            _miniReady = null;
            if (!_closing) SetStatus("미니 창을 만들지 못했습니다: " + ex.Message);
            return;
        }
        if (_closing || _mini is null) return;
        _mini.Toggle();
        Trace($"미니 창 {(_mini.Visible ? "보임" : "숨김")} {_mini.Current}");
    }

    private async Task CreateMiniAsync(CoreWebView2Environment env)
    {
        var mini = new MiniForm(_chatUrl, Path.Combine(_settings.DataFolder, "desktop-mini.json"));
        try
        {
            mini.OpenInMain += OpenRoom;
            mini.Activated += (_, _) => UpdateTray();
            mini.CreateControl();
            await ConfigureAsync(mini.View, AppTab.Chat, env); // 큰 창과 같은 규칙 (다른 탭 · 밖 주소는 큰 창 · 브라우저로)
            mini.Attach();
        }
        catch
        {
            mini.Dispose(); // 실패한 창은 남기지 않는다 (C5)
            throw;
        }
        if (_closing)
        {
            mini.Dispose(); // 만드는 사이 앱이 닫힘 — 끝날 때 정리에서 빠지지 않게
            return;
        }
        _mini = mini;
        mini.UpdateRooms(_roomList);
    }

    /// <summary>
    /// 설정 Hotkey를 등록한다. 형식이 틀리거나 다른 프로그램이 쓰고 있으면 상태 줄에 남기고(서버 상태 뒤에 계속) 기록한다 —
    /// 실제 확인에서 Ctrl+Alt+Space가 이미 다른 프로그램 것이라 등록이 조용히 실패했다.
    /// </summary>
    private void RegisterHotkey()
    {
        if (string.IsNullOrWhiteSpace(_settings.Hotkey)) return; // 비우면 단축키 없음 (C9)
        var text = _settings.Hotkey.Trim();
        string? problem = null;
        if (Hotkey.Parse(text) is not { } key) problem = $"단축키 형식이 맞지 않습니다: {text} (예: Ctrl+Alt+Space)";
        else if (!(_hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, key.Modifiers | Hotkey.NoRepeat, (uint)key.Key)))
            problem = Marshal.GetLastWin32Error() == ErrorHotkeyAlreadyRegistered
                ? $"단축키 {text}를 다른 프로그램이 쓰고 있습니다 — 트레이 메뉴의 미니 창을 쓰거나 desktop.json의 Hotkey를 바꾸세요"
                : $"단축키 {text}를 등록하지 못했습니다 (오류 {Marshal.GetLastWin32Error()})";
        Trace(problem ?? $"단축키 {text} 등록");
        if (problem is null) return;
        _warnings.Add(problem);
        SetStatus("");
    }

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>핸들이 (다시) 만들어질 때마다 단축키를 등록한다 (C10).</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey();
    }

    /// <summary>상태 줄에 서버 상태 뒤로 계속 보일 경고 (단축키 등).</summary>
    private readonly List<string> _warnings = [];

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == HotkeyId)
        {
            ToggleMini();
            return;
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>채팅 주소의 `/Room/{12자리 16진}`이면 방 ID.</summary>
    internal static string? RoomIdOf(string? url, Uri chatUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !NavigationPolicy.SameOrigin(u, chatUrl)) return null;
        var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["Room", var id] && IsRoomId(id) ? id : null;
    }

    internal static bool IsRoomId(string? id) => id is { Length: 12 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>풍선을 누르면 큰 창을 열고 그 방으로. 방 ID는 서버가 준 값이지만 모양을 한 번 더 본다.</summary>
    private void OpenRoom(string? roomId)
    {
        BringUp();
        if (IsRoomId(roomId)) ShowTab(AppTab.Chat, new Uri(_chatUrl, "/Room/" + roomId).AbsoluteUri);
    }

    /// <summary>트레이 메뉴 끝내기 — 진짜로 끈다 (도는 턴이 있으면 OnFormClosing이 묻는다).</summary>
    private void Exit()
    {
        if (_confirming) return; // 이미 묻는 중 (0f0b6007a4 C2)
        _exiting = true;
        Close();
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
        // 묻는 창이 떠 있는 동안 또 닫기가 오면(트레이 끝내기 · --exit · 닫기) 무시한다 — 확인 창이 겹치지 않게 (0f0b6007a4 C2)
        if (_confirming && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            return;
        }
        // 닫기(X)는 트레이로 숨긴다 (2-3 b). 끝내기는 트레이 메뉴 · Windows 종료.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _balloonRoom = null;
                _tray.ShowBalloonTip(4000, "HB Chat", "트레이에서 계속 돌아갑니다 — 끝내려면 트레이 메뉴의 끝내기", ToolTipIcon.None);
            }
            return;
        }
        // 이 앱이 띄운 채팅 서버에 도는 턴이 있으면 묻는다 — 끄면 서버와 claude가 같이 끝나 그 턴이 멈춘다. Windows 종료 때는 묻지 않는다.
        if (e.CloseReason != CloseReason.WindowsShutDown && _chatServer is { Owned: true } && AnyRunning())
        {
            if (!Visible) BringUp();
            _confirming = true;
            DialogResult answer;
            try
            {
                answer = MessageBox.Show(this, "실행 중인 턴이 있습니다. 끄면 그 턴이 멈춥니다. 끌까요?", "HB Chat",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            }
            finally
            {
                _confirming = false;
            }
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                _exiting = false;
                return;
            }
        }
        _closing = true;
        _mini?.CloseForExit(); // 미니 창 위치를 남기고 진짜 닫는다
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

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
        _hotkeyRegistered = false;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mini?.Dispose();
            _closing = true;
            _showWait?.Unregister(null);
            _exitWait?.Unregister(null);
            _cts.Cancel();
            _watcher?.Dispose();
            _tray.Visible = false; // 안 지우면 트레이에 빈 아이콘이 남는다
            _tray.Dispose();
            _trayMenu.Dispose(); // NotifyIcon.Dispose가 메뉴는 해제하지 않는다 (0f0b6007a4 C10)
            _trayIcons.Dispose();
            _chatView.Dispose();
            _dashView.Dispose();
            _http.Dispose();
            _job?.Dispose(); // 앱이 띄운 서버 · claude 트리를 끝낸다
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
