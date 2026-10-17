using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>세션 하나: 불러온 지침 파일(경로 · 종류 · 크기 · SHA-256 · 지금 디스크와 대조)과 run_mode 호출 연결.</summary>
public sealed class ClaudeSessionModel(CallLogCache log, ClaudeSessionsService service) : PageModel
{
    public TranscriptSummary Session { get; private set; } = null!;
    public List<RunLink> Links { get; private set; } = [];

    /// <summary>RunId → 그 실행의 첫 기록 Id (호출 상세 링크).</summary>
    public Dictionary<string, string> FirstCallOfRun { get; private set; } = [];

    public IActionResult OnGet(string id)
    {
        var calls = log.All;
        var snap = service.Load(calls);
        var session = snap.AllSessions.FirstOrDefault(s => string.Equals(s.FileSessionId, id, StringComparison.OrdinalIgnoreCase));
        if (session is null) return NotFound();

        Session = session;
        Links = snap.Links.TryGetValue(session, out var links) ? links : [];
        FirstCallOfRun = calls.Where(c => c.RunId is not null)
            .GroupBy(c => c.RunId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Timestamp).ThenBy(c => c.Step).First().Id);
        return Page();
    }

    public static string KindLabel(RunLink l) => l.Kind switch
    {
        RunLinkKind.Confirmed => "확정",
        RunLinkKind.Estimated => "추정(시각·모드)",
        RunLinkKind.Ambiguous => $"후보 {l.Candidates}개",
        _ when l.Call.ResultSource == ClaudeTranscriptReader.RejectedSource => "실행 전 거절",
        _ => "연결 없음",
    };
}
