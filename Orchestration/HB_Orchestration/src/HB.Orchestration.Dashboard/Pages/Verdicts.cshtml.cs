using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>record_verdicts로 남긴 판정의 에이전트·모드별 채택률과 채택 1건당 비용.</summary>
public sealed class VerdictsModel(PricingService pricing, CallLogCache log, VerdictLogCache verdicts, ChatRoomService rooms, ILogger<VerdictsModel> logger) : PageModel
{
    public PricingService Pricing => pricing;
    public string VerdictPath => verdicts.Path;

    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;

    public VerdictSummary Summary { get; private set; } = null!;

    /// <summary>runId → 이어진 채팅방. 이어지지 않은 실행(골든 등)에는 방 링크를 두지 않는다.</summary>
    public IReadOnlyDictionary<string, RunRoom> Rooms { get; private set; } = new Dictionary<string, RunRoom>();

    public void OnGet()
    {
        DateTimeOffset? since = Days > 0 ? DateTimeOffset.Now.AddDays(-Days) : null;
        Summary = VerdictStats.Build(verdicts.Latest, log.All, pricing.CostOf, since);
        try
        {
            Rooms = rooms.RunRooms();
        }
        catch (Exception ex)
        {
            // 세션 기록은 부가 정보다. 읽지 못해도 판정은 보여 준다 (링크만 빠진다).
            logger.LogWarning(ex, "판정 화면: 채팅방 연결을 읽지 못함");
        }
    }

    public static string Percent(double? rate) => rate is { } r ? $"{r * 100:0}%" : "-";
}
