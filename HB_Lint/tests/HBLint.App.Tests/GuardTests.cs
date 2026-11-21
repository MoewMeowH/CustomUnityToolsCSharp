using System.Net;

namespace HBLint.App.Tests;

public class GuardTests
{
    private static async Task<HttpStatusCode> Send(TestApp app, Action<HttpRequestMessage> setup, string path = "/api/catalog")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost" + path);
        setup(request);
        return (await app.Raw().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task 토큰이_없거나_틀리면_401()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, _ => { }));
        // 첫 글자만 다른 토큰 (원래 첫 글자와 반드시 다르게)
        var wrong = (app.Options.Token[0] == '0' ? '1' : '0') + app.Options.Token[1..];
        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, r => r.Headers.Add(LocalGuard.TokenHeader, wrong)));
        Assert.Equal(HttpStatusCode.OK, await Send(app, r => r.Headers.Add(LocalGuard.TokenHeader, app.Options.Token)));
    }

    [Fact]
    public async Task 쿼리로_준_토큰은_받지_않음()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        Assert.Equal(HttpStatusCode.Unauthorized, await Send(app, _ => { }, "/api/catalog?token=" + app.Options.Token));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:5317")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.2")]
    public async Task 다른_Host는_거절_DNS_리바인딩(string host)
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        Assert.Equal(HttpStatusCode.BadRequest, await Send(app, r =>
        {
            r.Headers.Host = host;
            r.Headers.Add(LocalGuard.TokenHeader, app.Options.Token);
        }));
        // 정적 파일 요청도 같은 문지기
        Assert.Equal(HttpStatusCode.BadRequest, await Send(app, r => r.Headers.Host = host, "/"));
    }

    [Theory]
    [InlineData("localhost:5317")]
    [InlineData("127.0.0.1:5317")]
    [InlineData("LOCALHOST")]
    public async Task 로컬_Host는_받음(string host)
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        Assert.Equal(HttpStatusCode.OK, await Send(app, r =>
        {
            r.Headers.Host = host;
            r.Headers.Add(LocalGuard.TokenHeader, app.Options.Token);
        }));
    }

    [Theory]
    [InlineData("http://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("http://localhost:5173", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    [InlineData("http://localhost", HttpStatusCode.OK)]
    public async Task Origin은_자기_출처만(string origin, HttpStatusCode expected)
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        Assert.Equal(expected, await Send(app, r =>
        {
            r.Headers.Add("Origin", origin);
            r.Headers.Add(LocalGuard.TokenHeader, app.Options.Token);
        }));
    }

    [Fact]
    public async Task 개발_출처를_주면_그_출처도_받음()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir, devOrigin: "http://localhost:5173");

        Assert.Equal(HttpStatusCode.OK, await Send(app, r =>
        {
            r.Headers.Add("Origin", "http://localhost:5173");
            r.Headers.Add(LocalGuard.TokenHeader, app.Options.Token);
        }));
    }

    [Fact]
    public async Task 응답에_CSP와_보안_헤더()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);

        var response = await app.Client.GetAsync("/api/catalog");

        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task 웹_UI는_토큰_없이_주고_없는_API는_404()
    {
        using var dir = new TempDir();
        await using var app = await TestApp.Start(dir);
        var index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        var built = File.Exists(index);
        if (!built)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(index)!);
            File.WriteAllText(index, "<!doctype html><title>t</title>");
        }
        try
        {
            var response = await app.Raw().GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<title>", await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/nope")).StatusCode);
        }
        finally
        {
            if (!built)
                File.Delete(index);
        }
    }

    [Fact]
    public void 토큰은_실행마다_다르고_64자()
    {
        var a = new AppOptions().Token;
        var b = new AppOptions().Token;

        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
    }

    [Theory]
    [InlineData("--port 70000")]
    [InlineData("--rules")]
    [InlineData("--what")]
    public void 모르는_인자는_null(string args)
    {
        Assert.Null(AppOptions.Parse(args.Split(' ')));
    }
}
