using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>record_verdicts로 남긴 판정의 에이전트·모드별 채택률과 채택 1건당 비용.</summary>
public sealed class VerdictsModel(PricingService pricing, CallLogCache log, VerdictLogCache verdicts) : PageModel
{
    public PricingService Pricing => pricing;
    public string VerdictPath => verdicts.Path;

    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;

    public VerdictSummary Summary { get; private set; } = null!;

    public void OnGet()
    {
        DateTimeOffset? since = Days > 0 ? DateTimeOffset.Now.AddDays(-Days) : null;
        Summary = VerdictStats.Build(verdicts.Latest, log.All, pricing.CostOf, since);
    }

    public static string Percent(double? rate) => rate is { } r ? $"{r * 100:0}%" : "-";
}
