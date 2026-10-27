using HB.Orchestration.Dashboard;
using HB.Orchestration.Dashboard.Pages;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>첫 화면 표 쪽 나누기: 합계는 전체, 표만 쪽 (계획서 2026-10-23_호출기록-blobs-분리와-첫화면-쪽나누기).</summary>
public sealed class IndexPagingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-index-page-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<IndexModel> Model(int records, int page, string? q = null)
    {
        var log = Path.Combine(_dir, "calls.jsonl");
        var now = DateTimeOffset.Now;
        for (var i = 0; i < records; i++)
            await CallLog.AppendAsync(new CallRecord
            {
                Id = $"id{i:D4}", Timestamp = now.AddSeconds(-i), Tool = "review", Model = "m", Prompt = "p", Success = true,
                InputTokens = 10, OutputTokens = 1, RunId = $"run{i / 3}", Step = i % 3 + 1,
            }, log);
        // 서브에이전트 결과 Sync가 실제 ~/.claude/projects를 읽지 않게 빈 폴더를 준다.
        var options = Options.Create(new DashboardOptions { LogPath = log, McpConfigDir = _dir, ClaudeProjectsDir = Path.Combine(_dir, "projects") });
        var modes = new ModesConfigService(options, _ => null);
        var model = new IndexModel(options, new PricingService(options, modes), new CallLogCache(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<CallLogCache>.Instance), new VerdictLogCache(options),
            new SubagentResultsService(options, new ClaudeSessionsService(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeSessionsService>.Instance),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SubagentResultsService>.Instance))
        {
            P = page, Q = q,
        };
        model.OnGet();
        return model;
    }

    [Fact]
    public async Task Table_shows_one_page_but_totals_cover_all()
    {
        var m = await Model(IndexModel.PageSize * 2 + 50, page: 3);

        Assert.Equal(3, m.PageCount);
        Assert.Equal(50, m.PageCalls.Count);
        Assert.Equal(IndexModel.PageSize * 2 + 50, m.Calls.Count);
        Assert.Equal((IndexModel.PageSize * 2 + 50) * 10L, m.TotalIn); // 합계는 전체 기준
        Assert.Equal(m.Calls[IndexModel.PageSize * 2].Id, m.PageCalls[0].Id);
        Assert.True(m.IsRunHead(m.PageCalls[0])); // 앞 쪽에서 이어진 실행도 이 쪽 첫 줄이 머리
    }

    [Fact]
    public async Task Page_out_of_range_is_clamped_and_url_keeps_filters()
    {
        var m = await Model(10, page: 99, q: "a b");

        Assert.Equal((1, 1), (m.P, m.PageCount));
        Assert.Equal("?Days=7&P=2&Q=a%20b", m.PageUrl(2));
    }
}
