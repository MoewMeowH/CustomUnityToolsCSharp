using System.Net;
using HB.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>
/// 실제 앱 응답 (계획서 2026-10-29_HB_Chat-화면-스크립트-시험-틀): Program.cs에 이어진 미들웨어 · 끝점의 헤더와 상한.
/// 그전에는 미들웨어만 따로 시험하고 연결은 curl로 봤다 (f 리뷰 self C7 · 4차 /self-review dc27a724bf C6 보류).
/// </summary>
public sealed class AppHeaderTests(ChatApp app) : IClassFixture<ChatApp>
{
    // Kestrel 주소로 바로 — Kestrel 모드의 Factory.CreateClient는 AllowAutoRedirect=false를 듣지 않아 303을 따라가 버렸다(실측)
    private HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = app.BaseAddress };

    private string Origin => app.BaseAddress.GetLeftPart(UriPartial.Authority);

    [Fact]
    public async Task Pages_CarryWidgetCsp()
    {
        using var client = Client();
        var room = app.Room("csp", ChatApp.User("hi", 0));
        foreach (var path in new[] { "/", "/Room/" + room.Id, "/Settings", "/chat.js" })
        {
            using var res = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(Widgets.PageCsp, Assert.Single(res.Headers.GetValues("Content-Security-Policy")));
        }
    }

    [Fact]
    public async Task Apis_AreNoStore()
    {
        using var client = Client();
        foreach (var path in new[] { "/api/rooms", "/api/appearance" })
        {
            using var res = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(res.Headers.CacheControl?.NoStore, path);
            Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task ProfileImage_NosniffSameOriginOnly()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        Assert.Null(await profiles.SetImageAsync(ProfileStore.Conductor, new MemoryStream(png), default));
        var url = app.Services.GetRequiredService<ProfileView>().ImageUrl(ProfileStore.Conductor);
        Assert.NotNull(url);

        using var client = Client();
        using var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("nosniff", Assert.Single(res.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(res.Headers.GetValues("Cross-Origin-Resource-Policy")));
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ProfileUpload_OverLimit_RedirectsToSettingsWithFailLine()
    {
        using var client = Client();
        // 본문을 보내기 전에 막히는지 — Expect: 100-continue면 앱이 본문을 읽지 않고 답할 때 클라이언트도 보내지 않는다
        using var req = new HttpRequestMessage(HttpMethod.Post, "/Settings?handler=Profile")
        {
            Content = new ByteArrayContent(new byte[ProfileStore.MaxImageBytes + 64 * 1024 + 1]),
        };
        req.Headers.Add("Origin", Origin);
        req.Headers.ExpectContinue = true;
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
        Assert.Equal("/Settings?fail=image-size", res.Headers.Location?.OriginalString);

        // 돌아간 설정 화면의 한 줄 (4차 B2)
        using var page = await client.GetAsync("/Settings?fail=image-size");
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains($"그림이 너무 커서 받지 않았습니다 ({ProfileStore.MaxImageBytes / 1024}KB까지)", html);
        using var other = await client.GetAsync("/Settings?fail=unknown");
        Assert.DoesNotContain("role=\"alert\"", await other.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AttachUpload_OverLimit_Is413WithCharset()
    {
        var room = app.Room("attach", ChatApp.User("hi", 0));
        using var client = Client();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/Room/{room.Id}?handler=Attach")
        {
            Content = new ByteArrayContent(new byte[Attachments.MaxBytes + 1024 * 1024 + 1]),
        };
        req.Headers.Add("Origin", Origin);
        req.Headers.ExpectContinue = true;
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        Assert.Equal($"파일 하나는 {Attachments.MaxBytes / 1024 / 1024}MB까지입니다.", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_FromOtherOrigin_Is403()
    {
        var room = app.Room("origin", ChatApp.User("hi", 0));
        using var client = Client();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/Room/{room.Id}?handler=Rename") { Content = new FormUrlEncodedContent([new("title", "x")]) };
        req.Headers.Add("Origin", "http://evil.example");
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("origin", app.Store.Get(room.Id)?.Title);
    }
}
