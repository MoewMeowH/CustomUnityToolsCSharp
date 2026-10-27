using HB.Chat;
using HB.Orchestration.ChatUi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>위젯 (계획서 2-2 f): 서버 쪽 약속 — 코드 블록은 이스케이프된 글로만 오고, 방 규칙에 형식 안내, 페이지 CSP, 목록 미리보기.</summary>
public sealed class WidgetTests
{
    [Fact]
    public void WidgetFence_RendersAsEscapedCodeWithLanguageClass()
    {
        // 화면(chat.js)은 code.language-widget의 textContent를 iframe srcdoc으로 넘긴다 — 부모 페이지에는 글자로만 들어와야 한다
        var html = SafeMarkdown.ToHtml("보세요\n\n```widget\n<script>alert(1)</script><b>표</b>\n```\n", SafeMarkdown.Default);
        Assert.Contains("<code class=\"language-widget\">", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;&lt;b&gt;표&lt;/b&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<code class=\"language-widget\">", SafeMarkdown.ToHtml("~~~widget\n<b>x</b>\n~~~\n", SafeMarkdown.Default));
    }

    [Theory]
    [InlineData(PermissionLevel.Chat)]
    [InlineData(PermissionLevel.Read)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.Command)]
    public void RoomRules_TellWidgetFormat(PermissionLevel level)
    {
        var rules = RoomRules.For(level);
        Assert.Contains("```widget", rules);
        Assert.Contains("```html", rules);
        Assert.Contains("'실행'을 눌러야", rules); // 스크립트는 눌러야 돈다 (803e5f13eb C3 · abc6cec3a6 C1)
        Assert.Contains("흉내 내지 않는다", rules); // 승인 카드 · 입력칸 흉내 (abc6cec3a6 C2)
    }

    [Fact]
    public void Preview_CollapsesWidgetBlock()
    {
        var text = "그래프입니다\n\n```widget\n<!DOCTYPE html>\n<html><body>막대</body></html>\n```\n\n막대를 누르면 값이 보입니다.";
        Assert.Equal("그래프입니다 [위젯] 막대를 누르면 값이 보입니다.", RoomList.Preview(text));
        Assert.Equal("코드 <b>x</b>", RoomList.Preview("코드\n```html\n<b>x</b>\n```")); // 다른 코드 블록은 그대로
    }

    [Theory]
    [InlineData("앞\n~~~widget\n<b>x</b>\n~~~\n뒤", "앞 [위젯] 뒤")]                // ~~~ 울타리
    [InlineData("앞\n````widget\n```\n<b>x</b>\n````\n뒤", "앞 [위젯] 뒤")]        // 긴 울타리 안의 짧은 울타리
    [InlineData("앞\r\n```widget\r\n<b>x</b>\r\n```\r\n뒤", "앞 [위젯] 뒤")]       // CRLF
    [InlineData("앞\n```widget\n<b>x</b> 끝나지 않음", "앞 [위젯]")]               // 닫히지 않음 (중지된 턴)
    [InlineData("앞\n```widgets\n<b>x</b>\n```", "앞 <b>x</b>")]                  // 다른 언어 이름은 접지 않음 (울타리 줄만 지움)
    public void Preview_WidgetBlockShapesMatchRenderer(string text, string preview) =>
        Assert.Equal(preview, RoomList.Preview(text)); // (803e5f13eb C6)

    [Fact]
    public async Task UseWidgetCsp_SetsFrameSrcNoneOnResponses()
    {
        // 상수만이 아니라 미들웨어가 응답에 다는지 (803e5f13eb C7 · abc6cec3a6 C4). Program.cs 연결은 실제 확인(curl 헤더)으로.
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseWidgetCsp();
        app.Run(ctx => ctx.Response.WriteAsync("ok"));
        var ctx = new DefaultHttpContext();
        await app.Build()(ctx);
        Assert.Equal("frame-src 'none'", ctx.Response.Headers.ContentSecurityPolicy.ToString());
    }
}
