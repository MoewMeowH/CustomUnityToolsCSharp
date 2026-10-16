using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

public sealed class IndexModel(IOptions<DashboardOptions> options, PricingService pricing) : PageModel
{
    public DashboardOptions Opt => options.Value;
    public PricingService Pricing => pricing;

    /// <summary>최근 N일. 0이면 전체.</summary>
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 7;
    [BindProperty(SupportsGet = true)] public string? Tool { get; set; }
    [BindProperty(SupportsGet = true)] public string? Provider { get; set; }
    /// <summary>true면 10초마다 자동 새로고침.</summary>
    [BindProperty(SupportsGet = true)] public bool Live { get; set; }

    public List<CallRecord> Calls { get; private set; } = [];
    public List<string> ToolNames { get; private set; } = [];
    public List<string> ProviderNames { get; private set; } = [];
    public List<(DateOnly Day, long Tokens, int Count)> Daily { get; private set; } = [];

    public long TotalIn => Calls.Sum(c => c.InputTokens ?? 0);
    public long TotalCached => Calls.Sum(c => c.CachedInputTokens ?? 0);
    public long TotalOut => Calls.Sum(c => c.OutputTokens ?? 0);
    public int Failures => Calls.Count(c => !c.Success);
    public double AvgSeconds => Calls.Count == 0 ? 0 : Calls.Average(c => c.ElapsedMs) / 1000.0;

    public decimal? TotalCost => pricing.TotalOf(Calls);

    /// <summary>RunId·Step별 시도 수. 2 이상이면 그 단계에 재시도가 있었다 (시도마다 기록이 한 줄씩 남는다).</summary>
    private Dictionary<(string RunId, int Step), int> _attempts = [];

    /// <summary>재시도가 있었던 단계의 기록이면 (이 시도 번호, 전체 시도 수), 아니면 null.</summary>
    public (int Attempt, int Total)? AttemptOf(CallRecord c) =>
        c.RunId is { } run && c.Step is { } step && _attempts.TryGetValue((run, step), out var total) && total > 1
            ? (c.Attempt ?? 1, total)
            : null;

    public void OnGet()
    {
        var all = CallLog.ReadAll(Opt.ResolvedLogPath);
        ToolNames = all.Select(c => c.Tool).Distinct().Order().ToList();
        ProviderNames = all.Select(ProviderOf).Distinct().Order().ToList();

        var since = DateTimeOffset.Now.AddDays(-Days);
        Calls = all
            .Where(c => Days <= 0 || c.Timestamp >= since)
            .Where(c => string.IsNullOrEmpty(Tool) || c.Tool == Tool)
            .Where(c => string.IsNullOrEmpty(Provider) || ProviderOf(c) == Provider)
            .ToList();

        _attempts = all
            .Where(c => c.RunId is not null && c.Step is not null)
            .GroupBy(c => (c.RunId!, c.Step!.Value))
            .ToDictionary(g => g.Key, g => g.Count());

        Daily = Calls
            .GroupBy(c => DateOnly.FromDateTime(c.Timestamp.LocalDateTime))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0)), g.Count()))
            .ToList();
    }

    /// <summary>예전(GptAgents 시절) 기록은 Provider가 없으므로 openai로 본다.</summary>
    public static string ProviderOf(CallRecord c) => c.Provider ?? "openai";

    public static string ProjectName(string? dir) =>
        string.IsNullOrEmpty(dir) ? "-" : Path.GetFileName(dir.TrimEnd('/', '\\'));

    public static string Preview(string? text, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var oneLine = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => !l.StartsWith('#') && l != "```"));
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
