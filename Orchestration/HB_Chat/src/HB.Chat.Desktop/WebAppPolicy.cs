namespace HB.Chat.Desktop;

/// <summary>앱 탭 사이트 하나: 단추 이름 · 첫 주소.</summary>
public sealed record WebApp(string Name, Uri Home);

public enum WebNav
{
    /// <summary>그 화면 안에서 그대로.</summary>
    Allow,
    /// <summary>앱 안 팝업 창 (같은 웹 환경) — 로그인 창이 opener로 결과를 돌려준다.</summary>
    Popup,
    /// <summary>기본 브라우저로.</summary>
    External,
    /// <summary>막는다.</summary>
    Block,
}

/// <summary>
/// 앱 탭(사용자가 손으로 쓰는 공식 웹 대화) 규칙. 채팅 · 대시보드 화면과 따로 된 WebView2 환경에서 돈다 —
/// 그 환경은 localhost 밖 이름 조회를 막아 바깥 사이트를 열 수 없고, 풀면 위젯 · WebRTC 막기가 약해진다.
/// 사이트는 고정 3개 (Antigravity는 웹판이 없어 같은 회사 Gemini 웹으로 대신 — 사용자 결정).
/// </summary>
public static class WebAppPolicy
{
    public static readonly IReadOnlyList<WebApp> Sites =
    [
        new("Claude", new Uri("https://claude.ai/")),
        new("ChatGPT", new Uri("https://chatgpt.com/")),
        new("Gemini", new Uri("https://gemini.google.com/")),
    ];

    /// <summary>
    /// 새 창을 앱 안 팝업으로 여는 로그인 주소 (그 이름이거나 그 아래 이름). 그 밖의 새 창은 기본 브라우저로 —
    /// 사용자 결정 "로그인만 앱 안 팝업". Claude의 Google 로그인은 accounts.google.com 팝업이다(실측).
    /// </summary>
    public static readonly IReadOnlyList<string> LoginHosts =
    [
        "accounts.google.com", "appleid.apple.com", "login.microsoftonline.com", "login.live.com",
        "auth.openai.com", "openai.com", "chatgpt.com", "claude.ai", "anthropic.com",
    ];

    /// <summary>
    /// 로그인 팝업 창 안에서 더 갈 수 있는 곳 — 로그인 주소 + 로그인 흐름이 거치는 이름 (Google은 gds.google.com 안내 · accounts.youtube.com 쿠키 맞추기를
    /// 거친다). 그 밖으로 가면 앱 안 창 대신 기본 브라우저 — 주소 줄 없는 창에 아무 사이트나 뜨지 않게 (/self-review security 5fb19b1572 C2).
    /// 새 창을 팝업으로 열지는 <see cref="LoginHosts"/>만 본다.
    /// </summary>
    public static readonly IReadOnlyList<string> PopupHosts =
        [.. LoginHosts, "google.com", "accounts.youtube.com", "idmsa.apple.com", "account.live.com"];

    /// <summary>
    /// 이 환경에서 끊을 요청 주소 틀 (WebView2 요청 필터) — 바깥 페이지가 채팅 · 대시보드 서버(localhost)를 부르지 못하게.
    /// 틀은 "https://127.example.com"처럼 넓게 걸리므로 끊기 전에 <see cref="IsLocal"/>로 다시 본다. *.localhost · 끝 점 · 매핑 IPv6까지
    /// (/self-review security 5fb19b1572 C1).
    /// </summary>
    public static readonly IReadOnlyList<string> LocalFilters =
        ["*://localhost*", "*://*.localhost*", "*://127.*", "*://[::1]*", "*://[::ffff:*", "*://0.0.0.0*"];

    /// <summary>큰 틀 이동: https만 그 화면에서, http는 사용자 동작이면 기본 브라우저, 나머지(file · 사용자 프로토콜 · localhost)는 막음.</summary>
    public static WebNav Navigate(string? url, bool userInitiated)
    {
        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return WebNav.Allow; // 팝업 첫 화면
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || IsLocal(u)) return WebNav.Block;
        return u.Scheme switch
        {
            "https" => WebNav.Allow,
            "http" => userInitiated ? WebNav.External : WebNav.Block,
            _ => WebNav.Block,
        };
    }

    /// <summary>
    /// 새 창(window.open · target=_blank): 로그인 주소는 앱 안 팝업, 사용자가 누른 그 밖 http(s)는 기본 브라우저, 나머지는 막음.
    /// 주소 없이 연 창(about:blank — 열고 나서 주소를 넣는 로그인 방식)은 사용자가 눌렀을 때만 팝업 — 그 안의 이동은 <see cref="PopupNavigate"/>가 본다.
    /// </summary>
    public static WebNav NewWindow(string? url, bool userInitiated)
    {
        if (string.IsNullOrEmpty(url) || string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase))
            return userInitiated ? WebNav.Popup : WebNav.Block;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || IsLocal(u) || u.Scheme is not ("http" or "https")) return WebNav.Block;
        if (u.Scheme == "https" && IsLoginHost(u.Host)) return WebNav.Popup;
        return userInitiated ? WebNav.External : WebNav.Block;
    }

    /// <summary>
    /// 로그인 팝업 창 안의 큰 틀 이동: 로그인 흐름 주소(<see cref="PopupHosts"/>, https)만 그 창에서, 그 밖 http(s)는 사용자 동작이면 기본 브라우저, 나머지는 막음.
    /// </summary>
    public static WebNav PopupNavigate(string? url, bool userInitiated)
    {
        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return WebNav.Allow;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || IsLocal(u) || u.Scheme is not ("http" or "https")) return WebNav.Block;
        if (u.Scheme == "https" && HostIn(PopupHosts, u.Host)) return WebNav.Allow;
        return userInitiated ? WebNav.External : WebNav.Block;
    }

    public static bool IsLoginHost(string host) => HostIn(LoginHosts, host);

    private static bool HostIn(IReadOnlyList<string> hosts, string host) =>
        hosts.Any(h => string.Equals(host, h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>이 PC를 가리키는 주소: localhost(·*.localhost, 끝 점 포함) · 127.0.0.0/8 · ::1 · IPv4 매핑 IPv6 · 0.0.0.0.</summary>
    public static bool IsLocal(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && IsLocal(u);

    private static bool IsLocal(Uri u)
    {
        var host = u.IdnHost.Trim('[', ']').TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any);
    }

    /// <summary>앱 탭 화면에 짧게 보일 주소 (호스트 + 경로, 쿼리 · 조각 뺌 — 로그인 토큰이 보이지 않게).</summary>
    public static string Short(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.Host + u.AbsolutePath : url ?? "";
}
