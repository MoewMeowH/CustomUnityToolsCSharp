using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>앱 탭(공식 웹 대화) 이동 · 새 창 · localhost 규칙.</summary>
public sealed class WebAppPolicyTests
{
    [Fact]
    public void Sites_AreFixedThreeHttps()
    {
        Assert.Equal(["Claude", "ChatGPT", "Gemini"], WebAppPolicy.Sites.Select(s => s.Name));
        Assert.All(WebAppPolicy.Sites, s => Assert.Equal(WebNav.Allow, WebAppPolicy.Navigate(s.Home.AbsoluteUri, userInitiated: false)));
    }

    [Theory]
    [InlineData("https://claude.ai/new", false, WebNav.Allow)]
    [InlineData("https://accounts.google.com/v3/signin/identifier?x=1", false, WebNav.Allow)] // 로그인 리다이렉트 (Gemini 실측)
    [InlineData("about:blank", false, WebNav.Allow)]
    [InlineData("http://example.com/", true, WebNav.External)]
    [InlineData("http://example.com/", false, WebNav.Block)]                // 스크립트 · 리다이렉트로 브라우저를 열지 못하게
    [InlineData("https://localhost:5290/", true, WebNav.Block)]
    [InlineData("http://localhost:5290/Room/0123456789ab", true, WebNav.Block)]
    [InlineData("http://127.0.0.1:5280/", true, WebNav.Block)]
    [InlineData("https://[::1]/", true, WebNav.Block)]
    [InlineData("https://app.localhost/", true, WebNav.Block)]
    [InlineData("file:///C:/Windows/win.ini", true, WebNav.Block)]
    [InlineData("ms-settings:privacy", true, WebNav.Block)]
    [InlineData("javascript:alert(1)", true, WebNav.Block)]
    [InlineData("not a url", true, WebNav.Block)]
    [InlineData(null, true, WebNav.Block)]
    public void Navigate_HttpsOnly(string? url, bool user, WebNav expected) => Assert.Equal(expected, WebAppPolicy.Navigate(url, user));

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?display=popup", true, WebNav.Popup)] // Claude Google 로그인 (실측)
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", false, WebNav.Popup)]              // 스크립트가 연 로그인 창도
    [InlineData("https://auth.openai.com/authorize", true, WebNav.Popup)]
    [InlineData("https://login.microsoftonline.com/common", true, WebNav.Popup)]
    [InlineData("https://www.claude.ai/x", true, WebNav.Popup)]                                    // 아래 이름
    [InlineData("http://accounts.google.com/", true, WebNav.External)]                             // https만 팝업
    [InlineData("https://evilaccounts.google.com.example/", true, WebNav.External)]                // 이름 끝 맞추기만으로 속지 않음
    [InlineData("https://example.com/docs", true, WebNav.External)]
    [InlineData("https://example.com/docs", false, WebNav.Block)]
    [InlineData("about:blank", true, WebNav.Popup)]
    [InlineData("", true, WebNav.Popup)]
    [InlineData("about:blank", false, WebNav.Block)]
    [InlineData("http://localhost:5290/", true, WebNav.Block)]
    [InlineData("ms-settings:privacy", true, WebNav.Block)]
    public void NewWindow_LoginPopupElseBrowser(string? url, bool user, WebNav expected) => Assert.Equal(expected, WebAppPolicy.NewWindow(url, user));

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", false, WebNav.Allow)]
    [InlineData("https://gds.google.com/web/landing", false, WebNav.Allow)]          // Google 로그인 흐름이 거침 (Gemini 실측)
    [InlineData("https://accounts.youtube.com/accounts/SetSID", false, WebNav.Allow)]
    [InlineData("https://claude.ai/login/callback", false, WebNav.Allow)]            // 로그인 뒤 돌아오는 곳
    [InlineData("about:blank", false, WebNav.Allow)]
    [InlineData("https://evil.example/fake-login", false, WebNav.Block)]             // 빈 팝업을 바깥 사이트로 옮기기 (5fb19b1572 C2)
    [InlineData("https://evil.example/fake-login", true, WebNav.External)]
    [InlineData("http://accounts.google.com/", false, WebNav.Block)]
    [InlineData("http://localhost:5290/", true, WebNav.Block)]
    [InlineData("ms-settings:privacy", true, WebNav.Block)]
    public void PopupNavigate_LoginFlowOnly(string? url, bool user, WebNav expected) => Assert.Equal(expected, WebAppPolicy.PopupNavigate(url, user));

    [Fact]
    public void NewWindow_DoesNotUsePopupOnlyHosts() =>
        Assert.Equal(WebNav.External, WebAppPolicy.NewWindow("https://www.google.com/search?q=x", userInitiated: true)); // 검색 링크는 브라우저로

    [Theory]
    [InlineData("http://app.localhost:5290/", true)]  // 요청 필터 틀 *.localhost (5fb19b1572 C1)
    [InlineData("http://localhost.:5290/", true)]      // 끝 점
    [InlineData("http://[::ffff:127.0.0.1]:5290/", true)]
    [InlineData("http://[::ffff:7f00:1]/", true)]
    [InlineData("http://localhost:5290/api/rooms", true)]
    [InlineData("ws://localhost:5290/x", true)]
    [InlineData("http://127.0.0.1:5280/", true)]
    [InlineData("http://127.5.6.7/", true)]
    [InlineData("http://[::1]:5290/", true)]
    [InlineData("http://0.0.0.0:5290/", true)]
    [InlineData("http://localhost.example.com/", false)] // 요청 필터 틀에는 걸리지만 이 PC가 아님
    [InlineData("http://127.example.com/", false)]
    [InlineData("https://claude.ai/", false)]
    [InlineData("garbage", false)]
    public void IsLocal(string url, bool expected) => Assert.Equal(expected, WebAppPolicy.IsLocal(url));

    [Fact]
    public void Short_DropsQueryAndFragment() =>
        Assert.Equal("accounts.google.com/o/oauth2/v2/auth", WebAppPolicy.Short("https://accounts.google.com/o/oauth2/v2/auth?code=secret#x"));
}
