using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>
/// 검토형 서브에이전트 결과 원문과 서버 기록 연결 상태 (계획서 2026-10-27_서브에이전트-결과-보존).
/// 외부 호출이 안 돌아 ownReview로 남지 못한 결과도 여기서 볼 수 있다. 읽기 전용 — 기록(판정)은 지휘자가 한다.
/// </summary>
public sealed class SubagentsModel(CallLogCache log, SubagentResultsService service) : PageModel
{
    /// <summary>최근 N일. 0이면 전체.</summary>
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;
    [BindProperty(SupportsGet = true)] public string? Agent { get; set; }
    /// <summary>true면 기록 없음만.</summary>
    [BindProperty(SupportsGet = true)] public bool Unrecorded { get; set; }
    /// <summary>원문을 펼칠 결과 (보관 파일 이름).</summary>
    [BindProperty(SupportsGet = true)] public string? Id { get; set; }

    public SubagentResultsService.Snapshot Snapshot { get; private set; } = null!;
    public List<SubagentView> Views { get; private set; } = [];
    public SubagentView? Selected { get; private set; }

    public void OnGet()
    {
        Snapshot = service.Load(log.All);
        Days = Math.Clamp(Days, 0, 3650); // 아주 큰 값이면 AddDays가 범위를 넘는다 (/self-review C5)
        var since = DateTimeOffset.Now.AddDays(-Days);
        Views = Snapshot.Views
            // 넘긴 시각을 모르는 결과(기록 형식 변경)는 기간과 상관없이 보인다 — 조용히 빠지지 않게 (방식 비교 시험 (A) 지적).
            .Where(v => Days <= 0 || v.Result.ReturnedAt is null || v.Result.ReturnedAt >= since)
            .Where(v => string.IsNullOrEmpty(Agent) || v.Result.AgentType == Agent)
            .Where(v => !Unrecorded || v.Link.Kind == SubagentLinkKind.None)
            .ToList();
        // 원문은 필터와 상관없이 찾는다 (링크로 바로 열 때).
        if (!string.IsNullOrEmpty(Id)) Selected = Snapshot.Views.FirstOrDefault(v => v.Result.Key == Id);
    }

    public static string ProjectName(string? dir) =>
        string.IsNullOrEmpty(dir) ? "-" : Path.GetFileName(dir.TrimEnd('/', '\\'));

    public static string Percent(double rate) => $"{rate * 100:0}%";
}
