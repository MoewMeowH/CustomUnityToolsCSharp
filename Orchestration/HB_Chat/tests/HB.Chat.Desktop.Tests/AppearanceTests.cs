using System.Drawing;
using HB.Chat.Desktop;
using Microsoft.Web.WebView2.Core;

namespace HB.Chat.Desktop.Tests;

/// <summary>5차 b: 테마 읽기 · WebView2 색 설정 · 창 틀 색, 맨 위 줄 배치 · 맞닿기 판정.</summary>
public sealed class AppearanceTests
{
    [Theory]
    [InlineData("""{"theme":"discord"}""", AppTheme.Discord)]
    [InlineData("""{"theme":"light"}""", AppTheme.Light)]
    [InlineData("""{"theme":"system","x":1}""", AppTheme.System)]
    [InlineData("""{"theme":"dark"}""", null)]    // 모르는 이름
    [InlineData("""{"theme":"Discord"}""", null)] // 대소문자도 서버 값 그대로만
    [InlineData("""{"theme":1}""", null)]
    [InlineData("""["discord"]""", null)]
    [InlineData("{ broken", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_OnlyKnownNames(string? json, AppTheme? expected) => Assert.Equal(expected, Appearance.Parse(json));

    [Theory]
    [InlineData("""{"theme":"discord","notifyBody":true}""", true)]
    [InlineData("""{"theme":"discord","notifyBody":false}""", false)]
    [InlineData("""{"theme":"discord"}""", false)]          // 옛 서버
    [InlineData("""{"notifyBody":"true"}""", false)]        // 글자는 받지 않는다
    [InlineData("{ broken", false)]
    [InlineData(null, false)]
    public void NotifyBody_OnlyTrueLiteral(string? json, bool expected) => Assert.Equal(expected, Appearance.NotifyBody(json));

    [Theory]
    [InlineData(AppTheme.Discord, false, CoreWebView2PreferredColorScheme.Dark, true)]
    [InlineData(AppTheme.Light, true, CoreWebView2PreferredColorScheme.Light, false)]
    [InlineData(AppTheme.System, true, CoreWebView2PreferredColorScheme.Auto, true)]
    [InlineData(AppTheme.System, false, CoreWebView2PreferredColorScheme.Auto, false)]
    public void Theme_SchemeAndFrame(AppTheme theme, bool systemDark, CoreWebView2PreferredColorScheme scheme, bool dark)
    {
        Assert.Equal(scheme, Appearance.Scheme(theme));
        Assert.Equal(dark, Appearance.IsDark(theme, systemDark));
    }

    [Fact]
    public void Colors_MatchWebPageBackground()
    {
        // 화면이 뜨기 전 바탕이 웹 화면 --bg와 같아야 번쩍이지 않는다 (HB_Chat _Layout의 디코 · 밝은 --bg)
        Assert.Equal(Color.FromArgb(0x31, 0x33, 0x38), Appearance.Colors(true).Page);
        Assert.Equal(Color.FromArgb(0xf6, 0xf7, 0xf9), Appearance.Colors(false).Page);
        Assert.Equal(Color.FromArgb(0x1e, 0x1f, 0x22), Appearance.Colors(true).Back);
        Assert.Equal(AppTheme.Discord, Appearance.Default); // 서버 기본값과 같게
    }

    private static TitleParts Arrange(int width = 1000, float scale = 1f) => TitleLayout.Arrange(width, 36, scale, 60, [30, 50, 30]);

    [Fact]
    public void Layout_ButtonsRight_TabsAfterName_StatusBetween()
    {
        var p = Arrange();
        Assert.Equal(new Rectangle(1000 - 46, 0, 46, 36), p.Close);
        Assert.Equal(p.Close.Left, p.Maximize.Right);
        Assert.Equal(p.Maximize.Left, p.Minimize.Right);
        Assert.Equal(new Rectangle(12, 0, 60, 36), p.Name);
        Assert.Equal(3, p.Tabs.Count);
        Assert.Equal(12 + 60 + 12, p.Tabs[0].Left);
        Assert.Equal(30 + 20, p.Tabs[0].Width);           // 글 폭 + 양쪽 여백
        Assert.True(p.Tabs[1].Left > p.Tabs[0].Right);
        Assert.True(p.Status.Left > p.Tabs[2].Right && p.Status.Right < p.Minimize.Left);
    }

    [Fact]
    public void Layout_ScalesWithDpi_AndNarrowStatusIsEmpty()
    {
        var p = Arrange(1000, 1.5f);
        Assert.Equal(69, p.Close.Width); // 46 × 1.5
        Assert.Equal(18, p.Name.Left);
        Assert.Equal(0, Arrange(300).Status.Width); // 좁으면 상태 글 칸 없음 (음수 폭 아님)
    }

    [Fact]
    public void Hit_ButtonsTabsAndCaption()
    {
        var p = Arrange();
        Assert.Equal(TitleHit.Close, TitleLayout.Hit(p, new Point(999, 18), out _));
        Assert.Equal(TitleHit.Maximize, TitleLayout.Hit(p, new Point(p.Maximize.Left + 1, 1), out _));
        Assert.Equal(TitleHit.Minimize, TitleLayout.Hit(p, new Point(p.Minimize.Left, 35), out _));
        Assert.Equal(TitleHit.Tab, TitleLayout.Hit(p, new Point(p.Tabs[1].Left + 3, 18), out var tab));
        Assert.Equal(1, tab);
        Assert.Equal(TitleHit.Caption, TitleLayout.Hit(p, new Point(p.Tabs[1].Left + 3, 2), out tab)); // 탭 위아래 여백은 끌기
        Assert.Equal(-1, tab);
        Assert.Equal(TitleHit.Caption, TitleLayout.Hit(p, new Point(20, 18), out _));       // 앱 이름
        Assert.Equal(TitleHit.Caption, TitleLayout.Hit(p, new Point(p.Status.Left + 5, 18), out _));
    }

    [Fact]
    public void Hit_ClippedStatusIsHoverable_OtherwiseCaption()
    {
        var p = Arrange();
        var inStatus = new Point(p.Status.Left + 5, 18);
        Assert.Equal(TitleHit.Status, TitleLayout.Hit(p, inStatus, out _, statusClipped: true));
        Assert.Equal(TitleHit.Caption, TitleLayout.Hit(p, inStatus, out _, statusClipped: false));
        Assert.Equal(TitleHit.Caption, TitleLayout.Hit(p, new Point(20, 18), out _, statusClipped: true)); // 이름은 그대로 끌기
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void RequiredWidth_KeepsTabsLeftOfButtons(float scale)
    {
        // 993ef3b338 C1: 이 폭에서 마지막 탭이 최소화 단추에 닿지 않는다
        var tabs = new[] { (int)(26 * scale), (int)(52 * scale), (int)(26 * scale) };
        var name = (int)(64 * scale);
        var width = TitleLayout.RequiredWidth(scale, name, tabs);
        var p = TitleLayout.Arrange(width, (int)(36 * scale), scale, name, tabs);
        Assert.True(p.Tabs[^1].Right < p.Minimize.Left);
        Assert.Equal(0, p.Status.Width);
        Assert.True(width > 640 || scale < 2); // 200%면 기본 최소 폭(640)보다 넓어야 한다 — 겹치던 경우
    }

    [Fact]
    public void LastTheme_RoundTrips_AndBadFileIsIgnored()
    {
        var dir = Directory.CreateTempSubdirectory("hbchat-theme-").FullName;
        try
        {
            var path = Path.Combine(dir, "sub", "desktop-theme.json");
            Assert.Null(Appearance.LoadLast(path));
            Appearance.SaveLast(path, AppTheme.Light); // 폴더가 없어도 만든다
            Assert.Equal(AppTheme.Light, Appearance.LoadLast(path));
            Appearance.SaveLast(path, AppTheme.System);
            Assert.Equal(AppTheme.System, Appearance.LoadLast(path));
            File.WriteAllText(path, "{ broken");
            Assert.Null(Appearance.LoadLast(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(500, 3, false, true, TitleLayout.HtTop)]
    [InlineData(5, 3, false, true, TitleLayout.HtTopLeft)]
    [InlineData(995, 3, false, true, TitleLayout.HtTopRight)]
    [InlineData(500, 3, true, true, TitleLayout.HtCaption)]   // 최대화면 크기 조절 없음
    [InlineData(500, 20, false, true, TitleLayout.HtCaption)]
    [InlineData(500, 300, false, false, TitleLayout.HtClient)] // 화면 안
    public void FrameHit_TopBandResizes_CaptionDrags(int x, int y, bool maximized, bool onCaption, int expected) =>
        Assert.Equal(expected, TitleLayout.FrameHit(new Point(x, y), 1000, 8, maximized, onCaption));
}
