using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HBLint.App.Desktop;

/// <summary>
/// 편집 앱 창 — WebView2 하나에 같은 프로세스의 서버 화면을 띄운다.
/// 페이지와는 WebView2 메시지로만 말한다(앱 출처에서 온 것만): 찾아보기(고르기 창) · 저장 안 한 변경(닫을 때 묻기).
/// </summary>
internal sealed class MainForm : Form
{
    private const string BaseTitle = "HB_Lint 규칙 편집";

    private readonly Uri _app;
    private readonly string _startUrl;
    private readonly string _dataFolder;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private bool _dirty;
    private string? _dirtyFile;
    /// <summary>닫기 전에 페이지 칸을 떠나게 했는가 (한 번 닫기 시도에 한 번).</summary>
    private bool _inputsCommitted;

    public MainForm(Uri app, string startUrl, string dataFolder)
    {
        _app = app;
        _startUrl = startUrl;
        _dataFolder = dataFolder;
        Text = BaseTitle;
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_view);
        RestorePlacement();
        Load += async (_, _) => await StartAsync();
        FormClosing += OnClosing;
    }

    private string PlacementPath => Path.Combine(_dataFolder, "window.json");

    private void RestorePlacement()
    {
        if (WindowPlacement.Load(PlacementPath) is not { } saved
            || !WindowPlacement.Fits(saved, Screen.AllScreens.Select(s => s.WorkingArea)))
            return;
        StartPosition = FormStartPosition.Manual;
        Bounds = new System.Drawing.Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
        if (saved.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private async Task StartAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataFolder, "WebView2"));
            await _view.EnsureCoreWebView2Async(env);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException || e is COMException || e is InvalidOperationException)
        {
            MessageBox.Show(this, "화면 엔진(WebView2)을 시작하지 못했습니다:\n" + e.Message, BaseTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _view.CoreWebView2;
        var settings = core.Settings;
        settings.IsWebMessageEnabled = true;     // 찾아보기 · 저장 안 한 변경 — HostMessages가 앱 출처 · kind만 받는다
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreDevToolsEnabled = true;       // 문제 볼 때 (F12)

        core.NavigationStarting += (_, e) =>
        {
            var action = NavigationPolicy.Decide(_app, e.Uri, e.IsUserInitiated, e.IsRedirected);
            if (action == NavAction.Allow)
                return;
            e.Cancel = true;
            if (action == NavAction.External)
                OpenExternal(e.Uri);
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true; // 앱에 하위 틀 없음
        core.NewWindowRequested += (_, e) =>
        {
            // 새 창은 열지 않는다 — 바깥 http(s)를 사용자가 누른 것만 기본 브라우저로 (앱 주소 · about:blank는 그냥 버림)
            e.Handled = true;
            if (NavigationPolicy.Decide(_app, e.Uri, e.IsUserInitiated, redirected: false) == NavAction.External)
                OpenExternal(e.Uri);
        };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? BaseTitle : core.DocumentTitle;
        core.WebMessageReceived += (_, e) => OnMessage(HostMessages.Parse(_app, e.Source, e.WebMessageAsJson));
        core.Navigate(_startUrl);
    }

    private void OnMessage(HostMessage? message)
    {
        switch (message)
        {
            case DirtyMessage dirty:
                _dirty = dirty.Dirty;
                _dirtyFile = dirty.File;
                break;
            case PickMessage pick:
                // 메시지 처리기 안에서 모달 창을 띄우지 않는다 — 다음 메시지 틈에
                BeginInvoke(() => _view.CoreWebView2?.PostWebMessageAsJson(HostMessages.Picked(pick.Field, Pick(pick))));
                break;
        }
    }

    /// <summary>규칙 폴더 · 대상 프로젝트는 폴더, 생성물은 .cs 파일 (덮어쓰기는 묻지 않음 — 쓰기는 생성 때 서버 규칙으로). 취소면 null.</summary>
    private string? Pick(PickMessage pick)
    {
        var current = string.IsNullOrWhiteSpace(pick.Current) ? null : pick.Current.Trim().Trim('"');
        if (pick.Field == "out")
        {
            using var save = new SaveFileDialog
            {
                Title = "생성물 (LintRules.g.cs)",
                Filter = "C# (*.cs)|*.cs",
                OverwritePrompt = false,
                FileName = current != null ? Path.GetFileName(current) : "LintRules.g.cs",
                InitialDirectory = current != null && Path.GetDirectoryName(current) is { } dir && Directory.Exists(dir) ? dir : "",
            };
            return save.ShowDialog(this) == DialogResult.OK ? save.FileName : null;
        }

        using var folder = new FolderBrowserDialog
        {
            Description = pick.Field == "rules" ? "규칙 폴더 (*.hblint.json이 있는 곳)" : "대상 프로젝트 (Unity 프로젝트 폴더 또는 csproj가 있는 폴더)",
            UseDescriptionForTitle = true,
            InitialDirectory = current != null && Directory.Exists(current) ? current : "",
        };
        return folder.ShowDialog(this) == DialogResult.OK ? folder.SelectedPath : null;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _view.CoreWebView2 != null && !_inputsCommitted)
        {
            // 칸은 떠날 때 값을 문서에 넣는데 창의 X는 칸을 떠나게 하지 않는다 — 먼저 칸을 떠나게 하고 dirty 보고를 받은 뒤 다시 닫는다
            e.Cancel = true;
            _ = CommitInputsThenCloseAsync();
            return;
        }
        _inputsCommitted = false;
        if (_dirty && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, $"{_dirtyFile ?? "규칙"}에 저장하지 않은 변경이 있습니다. 닫을까요?", BaseTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowPlacement.Save(PlacementPath, new Placement(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized));
    }

    private async Task CommitInputsThenCloseAsync()
    {
        try
        {
            await _view.CoreWebView2.ExecuteScriptAsync("document.activeElement && document.activeElement.blur && document.activeElement.blur();");
            // 페이지가 렌더 · effect 뒤 dirty를 보낸다 (메시지는 이 기다림 동안 UI 스레드에서 받는다)
            await Task.Delay(300);
        }
        catch (Exception e) when (e is InvalidOperationException || e is COMException)
        {
            // 화면 엔진이 이미 내려감 — 지금 아는 dirty로 묻는다
        }
        _inputsCommitted = true;
        Close();
    }

    /// <summary>파싱한 http(s) 주소만 넘긴다 (Decide가 External이라고 한 것).</summary>
    private void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // 기본 브라우저가 없으면 열지 않는다
        }
    }
}
