using System.Drawing;
using System.Text.Json;

namespace HBLint.App.Desktop.Tests;

public class NavigationPolicyTests
{
    private static readonly Uri App = new("http://127.0.0.1:5317");

    [Theory]
    [InlineData("http://127.0.0.1:5317/", true, "Allow")]
    [InlineData("http://127.0.0.1:5317/index.html#token=ab", false, "Allow")]
    [InlineData("about:blank", false, "Allow")]
    [InlineData("http://127.0.0.1:5318/", false, "Cancel")]
    [InlineData("http://localhost:5317/", false, "Cancel")]
    [InlineData("https://127.0.0.1:5317/", false, "Cancel")]
    [InlineData("https://example.com/", true, "External")]
    [InlineData("https://example.com/", false, "Cancel")]
    [InlineData("file:///C:/Windows/win.ini", true, "Cancel")]
    [InlineData("ms-settings:", true, "Cancel")]
    [InlineData("javascript:alert(1)", true, "Cancel")]
    public void 앱_출처만_창_안에서(string uri, bool userInitiated, string expected)
    {
        Assert.Equal(Enum.Parse<NavAction>(expected), NavigationPolicy.Decide(App, uri, userInitiated, redirected: false));
    }

    [Fact]
    public void 리다이렉트로_가는_바깥은_브라우저로_열지_않음()
    {
        Assert.Equal(NavAction.Cancel, NavigationPolicy.Decide(App, "https://example.com/", userInitiated: true, redirected: true));
    }
}

public class HostMessagesTests
{
    private static readonly Uri App = new("http://127.0.0.1:5317");
    private const string Source = "http://127.0.0.1:5317/";

    [Fact]
    public void 찾아보기와_저장_안_한_변경을_읽음()
    {
        Assert.Equal(new PickMessage("rules", @"D:\Game"), HostMessages.Parse(App, Source, """{"kind":"pick","field":"rules","current":"D:\\Game"}"""));
        Assert.Equal(new PickMessage("out", null), HostMessages.Parse(App, Source, """{"kind":"pick","field":"out"}"""));
        Assert.Equal(new DirtyMessage(true, "a.hblint.json"), HostMessages.Parse(App, Source, """{"kind":"dirty","dirty":true,"file":"a.hblint.json"}"""));
        Assert.Equal(new DirtyMessage(false, null), HostMessages.Parse(App, Source, """{"kind":"dirty","dirty":false}"""));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5318/", """{"kind":"pick","field":"rules"}""")]
    [InlineData("https://evil.example/", """{"kind":"pick","field":"rules"}""")]
    [InlineData("http://localhost:5317/", """{"kind":"pick","field":"rules"}""")]
    [InlineData(Source, """{"kind":"pick","field":"secrets"}""")]
    [InlineData(Source, """{"kind":"pick"}""")]
    [InlineData(Source, """{"kind":"run","command":"calc"}""")]
    [InlineData(Source, """{"kind":"dirty","dirty":"yes"}""")]
    [InlineData(Source, """["pick"]""")]
    [InlineData(Source, """{ not json""")]
    [InlineData(Source, "\"pick\"")]
    public void 다른_출처_모르는_말은_버림(string source, string json)
    {
        Assert.Null(HostMessages.Parse(App, source, json));
    }

    [Fact]
    public void 고른_답은_JSON으로_취소는_null()
    {
        var picked = JsonDocument.Parse(HostMessages.Picked("rules", @"D:\Game ""x""")).RootElement;
        Assert.Equal("picked", picked.GetProperty("kind").GetString());
        Assert.Equal(@"D:\Game ""x""", picked.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(HostMessages.Picked("out", null)).RootElement.GetProperty("path").ValueKind);
    }
}

public class WindowPlacementTests
{
    private static readonly Rectangle[] Screens = [new(0, 0, 1920, 1040), new(1920, 0, 1920, 1040)];

    [Fact]
    public void 저장한_자리를_다시_읽음()
    {
        var path = Path.Combine(Path.GetTempPath(), "hblint-desktop-tests", Guid.NewGuid().ToString("N"), "window.json");
        try
        {
            WindowPlacement.Save(path, new Placement(100, 50, 1200, 800, true));
            Assert.Equal(new Placement(100, 50, 1200, 800, true), WindowPlacement.Load(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void 없거나_깨졌거나_너무_작으면_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hblint-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(WindowPlacement.Load(Path.Combine(dir, "none.json")));
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ nope");
            Assert.Null(WindowPlacement.Load(Path.Combine(dir, "bad.json")));
            WindowPlacement.Save(Path.Combine(dir, "small.json"), new Placement(0, 0, 10, 10, false));
            Assert.Null(WindowPlacement.Load(Path.Combine(dir, "small.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(100, 50, true)]
    [InlineData(2500, 100, true)]       // 둘째 화면
    [InlineData(-1000, 100, true)]      // 왼쪽으로 일부 나갔지만 제목 줄 200px이 보임
    [InlineData(-1150, 100, false)]     // 50px만 보임 — 잡기 어려움
    [InlineData(5000, 100, false)]      // 떼어 낸 화면
    [InlineData(100, -500, false)]      // 제목 줄이 위로 나감
    [InlineData(100, 1030, false)]      // 제목 줄이 아래 끝에 걸림
    public void 제목_줄이_화면에_보여야_그_자리(int x, int y, bool fits)
    {
        Assert.Equal(fits, WindowPlacement.Fits(new Placement(x, y, 1200, 800, false), Screens));
    }
}
