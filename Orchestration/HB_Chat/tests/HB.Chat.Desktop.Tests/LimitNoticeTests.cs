using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>한도 문턱 알림: `/api/limits/alerts` 읽기 · 처음 목록은 알리지 않음 · 새 ID만 · 풍선 글.</summary>
public sealed class LimitNoticeTests
{
    private const string Two = """
        [{"id":"claude/seven_day/1/80","source":"claude","window":"seven_day","level":80,"percent":81,"title":"한도 80% 넘음 · Claude 7일","text":"81% 사용"},
         {"id":"agy/gemini-weekly/2/95","title":"한도 95% 넘음 · AGY Gemini 7일","text":null},
         {"id":"","title":"빈 ID"}, null]
        """;

    [Fact]
    public void Parse_KeepsRowsWithId()
    {
        var rows = LimitNotices.Parse(Two)!;
        Assert.Equal(["claude/seven_day/1/80", "agy/gemini-weekly/2/95"], rows.Select(r => r.Id));
        Assert.Equal("", rows[1].Text);
        Assert.Null(LimitNotices.Parse("{ 깨짐"));
        Assert.Null(LimitNotices.Parse("null"));
    }

    [Fact]
    public void Update_FirstListIsBaseline_ThenOnlyNew()
    {
        var notices = new LimitNotices(DateTimeOffset.Now);
        var rows = LimitNotices.Parse(Two)!;
        Assert.Empty(notices.Update([rows[0]])); // 앱을 켰을 때 있던 것은 알리지 않음
        Assert.Equal(["agy/gemini-weekly/2/95"], notices.Update(rows).Select(r => r.Id));
        Assert.Empty(notices.Update(rows));
    }

    [Fact]
    public void Balloon_OneOrMany()
    {
        var rows = LimitNotices.Parse(Two)!;
        Assert.Equal(("한도 80% 넘음 · Claude 7일", "81% 사용"), LimitNotices.Balloon([rows[0]]));
        Assert.Equal(("한도 알림 2개", "한도 80% 넘음 · Claude 7일\n한도 95% 넘음 · AGY Gemini 7일"), LimitNotices.Balloon(rows));
        Assert.Equal(("한도 95% 넘음 · AGY Gemini 7일", "한도 95% 넘음 · AGY Gemini 7일"), LimitNotices.Balloon([rows[1]])); // 빈 글은 제목으로 (풍선은 빈 글을 받지 않음)
    }

    [Fact]
    public void Update_FirstList_StillNotifiesAlertsMadeAfterStart()   // /self-review sub C3
    {
        var started = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(9));
        var rows = LimitNotices.Parse($$"""
            [{"id":"old","title":"켜기 전","text":"a","at":"{{started.AddMinutes(-5):o}}"},
             {"id":"new","title":"켠 뒤 첫 그리기","text":"b","at":"{{started.AddSeconds(2):o}}"}]
            """)!;
        var notices = new LimitNotices(started);
        Assert.Equal(["new"], notices.Update(rows).Select(r => r.Id));
        Assert.Empty(notices.Update(rows));
    }
}
