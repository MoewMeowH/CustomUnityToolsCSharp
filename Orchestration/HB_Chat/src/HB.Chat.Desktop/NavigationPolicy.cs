namespace HB.Chat.Desktop;

public enum AppTab { Chat, Dashboard }

public enum NavAction
{
    /// <summary>이 탭 안에서 그대로.</summary>
    Allow,
    /// <summary>다른 탭으로 옮겨 그 주소를 연다.</summary>
    Switch,
    /// <summary>기본 브라우저로 (앱 밖 http · https).</summary>
    External,
    /// <summary>막는다 (file · 사용자 프로토콜 · 잘못된 주소 등).</summary>
    Block,
}

public readonly record struct NavDecision(NavAction Action, AppTab Tab, string? Url);

/// <summary>
/// 큰 틀 이동 · 새 창 요청 규칙: 채팅 출처는 채팅 탭, 대시보드 출처는 대시보드 탭, 그 밖의 http(s)는 기본 브라우저, 나머지는 막음.
/// 하위 틀(위젯 iframe)은 about:srcdoc · about:blank만 — 위젯이 스스로 밖으로 이동하는 길을 페이지 CSP frame-src 'none'과 이중으로 막는다.
/// </summary>
public sealed class NavigationPolicy(Uri chat, Uri dashboard)
{
    public NavDecision Main(string? url, AppTab current)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return new(NavAction.Block, current, null);
        AppTab? tab = SameOrigin(u, chat) ? AppTab.Chat : SameOrigin(u, dashboard) ? AppTab.Dashboard : null;
        if (tab is { } t) return t == current ? new(NavAction.Allow, t, u.AbsoluteUri) : new(NavAction.Switch, t, u.AbsoluteUri);
        return u.Scheme is "http" or "https" ? new(NavAction.External, current, u.AbsoluteUri) : new(NavAction.Block, current, null);
    }

    /// <summary>하위 틀이 갈 수 있는 곳: 위젯 srcdoc과 빈 틀뿐.</summary>
    public static bool FrameAllowed(string? url) =>
        string.Equals(url, "about:srcdoc", StringComparison.OrdinalIgnoreCase) || string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
