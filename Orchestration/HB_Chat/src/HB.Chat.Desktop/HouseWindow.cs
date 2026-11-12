using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HB.Chat.Desktop;

/// <summary>
/// 놀이방 창: 방 하나의 /House/{방} 화면만 띄우는 따로 된 창 (채팅 화면의 놀이방 단추 → 채팅 탭 새 창 요청 → MainForm.OpenHouse).
/// 채팅 탭과 같은 WebView2 환경(같은 로컬 앱 출처). 이 방 놀이방 주소 말고는 이동 · 새 창 · 권한을 모두 막는다.
/// 방마다 하나 — 다시 누르면 그 창을 앞으로. 앱을 끄면 같이 닫힌다.
/// </summary>
public sealed class HouseWindow : Form
{
    private readonly NavigationPolicy _policy;
    private readonly Action<string> _trace;

    public string RoomId { get; }

    public WebView2 View { get; } = new() { Dock = DockStyle.Fill };

    public HouseWindow(string roomId, NavigationPolicy policy, Action<string> trace)
    {
        RoomId = roomId;
        _policy = policy;
        _trace = trace;
        Text = "놀이방";
        Size = new Size(1000, 720);
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(View);
    }

    /// <summary>WebView2를 채팅 탭과 같은 환경으로 띄우고 그 방 놀이방을 연다.</summary>
    public async Task OpenAsync(CoreWebView2Environment env, Uri chatUrl)
    {
        await View.EnsureCoreWebView2Async(env);
        if (IsDisposed || View.CoreWebView2 is not { } core) return; // 띄우는 사이 창이 닫힘
        var s = core.Settings;
        s.IsWebMessageEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            if (_policy.HouseRoom(e.Uri) == RoomId) return;
            e.Cancel = true;
            _trace($"놀이방 창 이동 막음 {e.Uri.Split('?', '#')[0]}");
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (NavigationPolicy.FrameAllowed(e.Uri)) return;
            e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "놀이방" : core.DocumentTitle;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(MainForm.WebRtcBlock); // 다른 앱 화면과 같은 WebRTC 막기
        if (IsDisposed) return;
        core.Navigate(new Uri(chatUrl, "/House/" + RoomId).AbsoluteUri);
    }
}
