using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HB.Chat.Desktop;

/// <summary>
/// 앱 탭: 사이트 줄(Claude · ChatGPT · Gemini · 뒤로 · 새로 고침 · 주소) + 사이트마다 WebView2 하나 (처음 고를 때 만들고 그 뒤로는 숨겼다 보이기만).
/// 채팅 · 대시보드와 따로 된 환경(사용자 데이터 폴더 <c>webapps</c>, 브라우저 인자 없음)에서 돈다 — 규칙은 <see cref="WebAppPolicy"/>.
/// 환경은 앱 탭을 처음 열 때 만든다 (채팅만 쓰면 비용 없음). 마지막에 본 사이트는 <c>desktop-apps.json</c>에 남긴다.
/// </summary>
public sealed class AppsPane : UserControl
{
    private readonly string _folder, _statePath;
    private readonly Action<string> _trace;
    private readonly FlowLayoutPanel _bar = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 3, 6, 3), WrapContents = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _address = new() { AutoSize = false, Width = 520, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(10, 1, 3, 1), AutoEllipsis = true };
    private readonly Button _back, _reload;
    private readonly Dictionary<string, Button> _siteButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WebView2> _views = new(StringComparer.Ordinal);
    private readonly List<Form> _popups = [];
    private Task<CoreWebView2Environment>? _env;
    private string? _shown;
    private FrameColors _colors = Appearance.Colors(true);
    private CoreWebView2PreferredColorScheme _scheme = CoreWebView2PreferredColorScheme.Auto;
    private bool _opening;

    public AppsPane(string dataFolder, Action<string> trace)
    {
        _folder = Path.Combine(dataFolder, "webapps");
        _statePath = Path.Combine(dataFolder, "desktop-apps.json");
        _trace = trace;
        foreach (var site in WebAppPolicy.Sites)
        {
            var b = MakeButton(site.Name);
            b.Click += async (_, _) => await ShowSiteAsync(site);
            _siteButtons[site.Name] = b;
            _bar.Controls.Add(b);
        }
        _back = MakeButton("← 뒤로");
        _back.Margin = new Padding(16, 1, 3, 1);
        _back.Click += (_, _) => { if (Current?.CoreWebView2 is { CanGoBack: true } c) c.GoBack(); };
        _reload = MakeButton("새로 고침");
        _reload.Click += (_, _) => Current?.CoreWebView2?.Reload();
        _bar.Controls.AddRange([_back, _reload, _address]);
        Controls.Add(_content);
        Controls.Add(_bar);
        ApplyColors();
    }

    private WebView2? Current => _shown is not null && _views.TryGetValue(_shown, out var v) ? v : null;

    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Height = 26, Margin = new Padding(3, 1, 3, 1), Padding = new Padding(6, 0, 6, 0),
        UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
    };

    /// <summary>앱 탭이 보일 때: 고른 사이트가 없으면 마지막에 본 사이트(없으면 첫 사이트)를 연다.</summary>
    public async void Open()
    {
        if (_shown is not null)
        {
            Current?.Focus();
            return;
        }
        var last = LoadLast();
        await ShowSiteAsync(WebAppPolicy.Sites.FirstOrDefault(s => s.Name == last) ?? WebAppPolicy.Sites[0]);
    }

    private async Task ShowSiteAsync(WebApp site)
    {
        if (_opening) return; // 만드는 중에 다른 단추를 연달아 누름
        var previous = _shown;
        _shown = site.Name;
        UpdateBar();
        if (!_views.TryGetValue(site.Name, out var view))
        {
            _opening = true;
            try
            {
                view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = _colors.Page, Visible = false };
                _content.Controls.Add(view);
                await view.EnsureCoreWebView2Async(await EnvAsync());
                Configure(view.CoreWebView2, site.Name);
                view.CoreWebView2.SourceChanged += (_, _) => { if (_shown == site.Name) UpdateBar(); };
                view.CoreWebView2.HistoryChanged += (_, _) => { if (_shown == site.Name) UpdateBar(); };
                _views[site.Name] = view;
                view.CoreWebView2.Navigate(site.Home.AbsoluteUri);
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or Win32Exception or IOException
                or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                _trace($"앱 탭 {site.Name} 열기 실패: {ex.GetType().Name} {ex.Message}");
                if (view is not null && !_views.ContainsKey(site.Name)) { _content.Controls.Remove(view); view.Dispose(); }
                _shown = previous; // 단추 · 주소 줄을 아직 보이는 화면에 맞춘다
                UpdateBar();
                _address.Text = $"{site.Name}을(를) 열지 못했습니다: " + ex.Message;
                return;
            }
            finally
            {
                _opening = false;
            }
        }
        if (_shown != site.Name) return; // 만드는 사이 다른 사이트를 고름 (막혀 있지만 안전하게)
        SaveLast(site.Name);
        foreach (var (name, v) in _views) v.Visible = name == site.Name;
        UpdateBar();
        view.Focus();
    }

    /// <summary>웹 환경 (하나를 같이 씀). 만들기가 실패하면 잊는다 — 실패한 Task를 붙잡으면 앱을 다시 켤 때까지 앱 탭이 계속 실패했다.</summary>
    private async Task<CoreWebView2Environment> EnvAsync()
    {
        var env = _env ??= CoreWebView2Environment.CreateAsync(null, _folder, new CoreWebView2EnvironmentOptions());
        try
        {
            return await env;
        }
        catch
        {
            if (_env == env) _env = null;
            throw;
        }
    }

    /// <summary>
    /// 한 화면(사이트 · 팝업)의 규칙: 앱에 말 걸 길 없음, https만(팝업은 로그인 흐름 주소만), 로그인 새 창은 앱 안 팝업, localhost 요청 끊기, 알림 거부.
    /// </summary>
    private void Configure(CoreWebView2 core, string site, bool popup = false)
    {
        var s = core.Settings;
        s.IsWebMessageEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.AreDevToolsEnabled = true;
        core.Profile.PreferredColorScheme = _scheme;

        core.NavigationStarting += (_, e) =>
        {
            var user = e.IsUserInitiated && !e.IsRedirected;
            var nav = popup ? WebAppPolicy.PopupNavigate(e.Uri, user) : WebAppPolicy.Navigate(e.Uri, user);
            if (nav == WebNav.Allow) return;
            e.Cancel = true;
            _trace($"앱 탭 {site} 이동 {nav} {WebAppPolicy.Short(e.Uri)} (사용자 {e.IsUserInitiated}, 리다이렉트 {e.IsRedirected})");
            if (nav == WebNav.External) OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            var nav = WebAppPolicy.NewWindow(e.Uri, e.IsUserInitiated);
            _trace($"앱 탭 {site} 새 창 {nav} {WebAppPolicy.Short(e.Uri)} (사용자 {e.IsUserInitiated})");
            if (nav == WebNav.Popup)
            {
                OpenPopup(e, site);
                return;
            }
            e.Handled = true;
            if (nav == WebNav.External) OpenExternal(e.Uri);
        };
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            _trace($"앱 탭 {site} 외부 프로토콜 실행 막음 {WebAppPolicy.Short(e.Uri)}");
        };
        // 알림만 거부 (사용자 결정) — 마이크 · 클립보드 등은 WebView2 기본 묻기 창
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications) e.State = CoreWebView2PermissionState.Deny;
        };
        // 바깥 페이지가 채팅 · 대시보드 서버(localhost)를 부르지 못하게 — 모든 출처(틀 · 워커 포함)의 요청
        foreach (var filter in WebAppPolicy.LocalFilters)
            core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!WebAppPolicy.IsLocal(e.Request.Uri)) return;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            _trace($"앱 탭 {site} localhost 요청 끊음 {WebAppPolicy.Short(e.Request.Uri)}");
        };
        core.ProcessFailed += (_, e) => BeginInvoke(() =>
            _address.Text = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited
                ? "웹 엔진이 끝났습니다 — 앱을 다시 켜세요"
                : $"화면 오류({e.ProcessFailedKind}) — 새로 고침을 누르세요");
    }

    /// <summary>로그인 새 창: 같은 환경의 작은 창 (opener가 이어져야 결과가 돌아온다). 창이 닫기를 요청하면 닫는다.</summary>
    private async void OpenPopup(CoreWebView2NewWindowRequestedEventArgs e, string site)
    {
        var deferral = e.GetDeferral();
        var popup = new Form
        {
            Text = site + " 로그인", Width = 560, Height = 720, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            Icon = FindForm()?.Icon,
        };
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = _colors.Page };
        popup.Controls.Add(view);
        _popups.Add(popup);
        popup.FormClosed += (_, _) => { _popups.Remove(popup); popup.Dispose(); };
        try
        {
            popup.Show(FindForm());
            await view.EnsureCoreWebView2Async(await EnvAsync());
            Configure(view.CoreWebView2, site + " 팝업", popup: true);
            view.CoreWebView2.WindowCloseRequested += (_, _) => popup.Close();
            // 주소 줄이 없는 창이라 제목 앞에 지금 호스트를 붙인다 (제목은 페이지가 정하므로 그것만으로는 어느 사이트인지 모른다)
            void Title()
            {
                var core = view.CoreWebView2;
                var host = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) && u.Scheme == "https" ? u.Host : site;
                popup.Text = host + " — " + (core.DocumentTitle is { Length: > 0 } t ? t : site + " 로그인");
            }
            view.CoreWebView2.SourceChanged += (_, _) => Title();
            view.CoreWebView2.DocumentTitleChanged += (_, _) => Title();
            e.NewWindow = view.CoreWebView2;
            e.Handled = true;
        }
        catch (Exception ex) when (ex is COMException or Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _trace($"앱 탭 {site} 팝업 실패: {ex.GetType().Name} {ex.Message}");
            e.Handled = true; // 열지 못한 창은 띄우지 않는다
            popup.Close();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _trace("앱 탭 브라우저로 열지 못함: " + ex.Message);
        }
    }

    private void UpdateBar()
    {
        foreach (var (name, b) in _siteButtons)
        {
            var on = name == _shown;
            b.BackColor = on ? _colors.Selected : _colors.Back;
            b.ForeColor = on ? _colors.Text : _colors.Muted;
        }
        var core = Current?.CoreWebView2;
        _back.Enabled = core?.CanGoBack == true;
        _reload.Enabled = core is not null;
        _address.Text = core is null ? "" : WebAppPolicy.Short(core.Source);
    }

    /// <summary>맨 위 줄과 같은 색 · 사이트 화면 밝기 (MainForm.ApplyTheme이 부른다).</summary>
    public void ApplyTheme(FrameColors colors, CoreWebView2PreferredColorScheme scheme)
    {
        _colors = colors;
        _scheme = scheme;
        ApplyColors();
        foreach (var v in _views.Values)
        {
            v.DefaultBackgroundColor = colors.Page;
            if (v.CoreWebView2 is { } core) core.Profile.PreferredColorScheme = scheme; // 같은 프로필 — 팝업도 따라간다
        }
    }

    private void ApplyColors()
    {
        BackColor = _content.BackColor = _colors.Page;
        _bar.BackColor = _colors.Back;
        _address.ForeColor = _colors.Muted;
        foreach (var b in new[] { _back, _reload })
        {
            b.BackColor = _colors.Back;
            b.ForeColor = _colors.Text;
        }
        foreach (Control c in _bar.Controls)
            if (c is Button b)
            {
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = _colors.Hover;
            }
        UpdateBar();
    }

    private string? LoadLast()
    {
        try
        {
            return File.Exists(_statePath) ? JsonDocument.Parse(File.ReadAllText(_statePath)).RootElement.GetProperty("site").GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private void SaveLast(string site)
    {
        try
        {
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new { site }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 기억 못 해도 다음에 첫 사이트로 열 뿐
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var p in _popups.ToArray()) p.Dispose();
            foreach (var v in _views.Values) v.Dispose();
        }
        base.Dispose(disposing);
    }
}
