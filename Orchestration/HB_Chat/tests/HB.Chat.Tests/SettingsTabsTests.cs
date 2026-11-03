using System.Text.RegularExpressions;
using HB.Chat.Pages;

namespace HB.Chat.Tests;

/// <summary>설정 화면 탭 (노션 "채팅 설정내 탭", 결정 "이대로 5개"): 섹션이 맞는 탭에 들고, 방금 저장한 것의 탭을 처음 연다.</summary>
public sealed class SettingsTabsTests(ChatApp app) : IClassFixture<ChatApp>
{
    [Theory]
    [InlineData("theme", null, "general")]
    [InlineData("notify", null, "general")]
    [InlineData("profile", null, "general")]
    [InlineData("image-size", null, "general")]
    [InlineData("added", null, "work")]
    [InlineData("renamed", null, "work")]
    [InlineData("cli", null, "connect")]
    [InlineData("mcp-secret", null, "connect")]
    [InlineData("codex", null, "connect")]
    [InlineData("pack", null, "decor")]
    [InlineData("faces", null, "decor")]
    [InlineData(null, "Backup", "data")]
    [InlineData(null, "AddUnity", "work")]
    [InlineData(null, "AddMcpJson", "connect")]
    [InlineData("theme", "StickerPack", "decor")] // 다시 그린 POST는 handler가 먼저
    [InlineData(null, null, null)]
    [InlineData("unknown", null, null)]
    public void TabFor_DoneOrHandler(string? done, string? handler, string? tab) => Assert.Equal(tab, SettingsModel.TabFor(done, handler));

    [Fact]
    public async Task Page_SectionsSitInTheirTabs_AndActiveFromDone()
    {
        using var client = new HttpClient { BaseAddress = app.BaseAddress };
        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings?done=pack"));
        Assert.Equal(["general", "work", "connect", "decor", "data"], Regex.Matches(html, "<div class=\"settings-tab\" id=\"tab-(\\w+)\"").Select(m => m.Groups[1].Value));
        Assert.Contains("data-active=\"decor\"", html);

        string TabOf(string heading)
        {
            var at = html.IndexOf(">" + heading + "</h3>", StringComparison.Ordinal);
            Assert.True(at > 0, heading);
            return Regex.Matches(html[..at], "<div class=\"settings-tab\" id=\"tab-(\\w+)\"").Last().Groups[1].Value;
        }
        Assert.Equal("general", TabOf("화면 테마"));
        Assert.Equal("general", TabOf("프로필"));
        Assert.Equal("general", TabOf("데스크톱 알림"));
        Assert.Equal("work", TabOf("작업 폴더"));
        Assert.Equal("work", TabOf("Unity 프로젝트"));
        Assert.Equal("work", TabOf("방 이름"));
        Assert.Equal("connect", TabOf("claude CLI · 카드 자가 시험"));
        Assert.Equal("connect", TabOf("Codex (단톡방 구성원)"));
        Assert.Equal("connect", TabOf("MCP 서버 (앱)"));
        Assert.Equal("decor", TabOf("스티커 팩"));
        Assert.Equal("decor", TabOf("지휘자 표정"));
        Assert.Equal("data", TabOf("데이터 백업"));
        Assert.Contains("data-active=\"\"", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings"))); // 모르면 비움 — 화면이 마지막 탭
    }
}
