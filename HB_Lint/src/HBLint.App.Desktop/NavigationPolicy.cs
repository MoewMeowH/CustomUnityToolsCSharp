namespace HBLint.App.Desktop;

internal enum NavAction
{
    /// <summary>창 안에서 그대로.</summary>
    Allow,
    /// <summary>창은 취소하고 기본 브라우저로.</summary>
    External,
    Cancel,
}

/// <summary>
/// 창 안 이동 규칙. 앱 출처(서버 주소의 scheme · host · port)만 창 안에서 연다.
/// 그 밖의 http(s)는 사용자가 누른 이동일 때만 기본 브라우저로 — 스크립트 · 리다이렉트가 브라우저를 열지 못하게. 다른 scheme은 취소.
/// </summary>
internal static class NavigationPolicy
{
    public static bool IsAppOrigin(Uri app, string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var target)
        && target.Scheme == app.Scheme
        && string.Equals(target.Host, app.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == app.Port;

    public static NavAction Decide(Uri app, string uri, bool userInitiated, bool redirected)
    {
        if (IsAppOrigin(app, uri) || uri == "about:blank")
            return NavAction.Allow;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target)
            && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)
            && userInitiated && !redirected)
            return NavAction.External;
        return NavAction.Cancel;
    }
}
