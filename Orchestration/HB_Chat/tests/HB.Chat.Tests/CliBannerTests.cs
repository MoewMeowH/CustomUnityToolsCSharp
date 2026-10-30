using HB.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>
/// claude CLI 판 띠 (결정 "사이드바 + 설정"): 확인한 판과 다른 판을 보면 방 목록 위에 띠, 설정에 "확인 필요" · 자가 시험 단추. 같으면 띠 없음.
/// </summary>
public sealed class CliBannerTests(ChatApp app) : IClassFixture<ChatApp>
{
    private HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = app.BaseAddress };

    [Fact]
    public async Task Banner_OnlyWhenUnchecked()
    {
        app.Room("판 띠 시험"); // 방이 있어야 목록이 그려진다
        var cli = app.Services.GetRequiredService<CliVersions>();
        cli.Note("1.0.0");
        using var client = Client();
        Assert.DoesNotContain("cli-banner", await client.GetStringAsync("/"));

        cli.Note("1.0.1");
        var list = await client.GetStringAsync("/");
        Assert.Contains("id=\"cli-banner\"", list);
        Assert.Contains("claude CLI 1.0.0 → 1.0.1", System.Net.WebUtility.HtmlDecode(list)); // Razor는 화살표 같은 글자를 &#x…;로 쓴다
        var settings = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings"));
        Assert.Contains("id=\"cli\"", settings);
        Assert.Contains("확인 필요", settings);
        Assert.Contains("handler=CliSelfTest", settings);

        cli.Save(new CliVersions.TestResult(DateTimeOffset.Now, "1.0.1", true, [new(CliSelfTest.BashStep, CliSelfTest.Passed, null)], null));
        Assert.DoesNotContain("cli-banner", await client.GetStringAsync("/"));
        Assert.Contains("명령 승인 카드: 통과", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings")));
    }
}
