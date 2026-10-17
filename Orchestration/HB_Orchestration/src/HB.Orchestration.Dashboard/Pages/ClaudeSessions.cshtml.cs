using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>Claude Code 세션 기록 목록: 지침 파일 수와 연결된 run_mode 호출 수. 대화 본문은 보여 주지 않는다.</summary>
public sealed class ClaudeSessionsModel(CallLogCache log, ClaudeSessionsService service) : PageModel
{
    public ClaudeSessionsService.Snapshot Snapshot { get; private set; } = null!;

    public void OnGet() => Snapshot = service.Load(log.All);

    public static string Short(string? id) => id is { Length: >= 8 } ? id[..8] : id ?? "-";

    public static string ProjectName(string? dir) =>
        string.IsNullOrEmpty(dir) ? "-" : Path.GetFileName(dir.TrimEnd('/', '\\'));

    public int Count(TranscriptSummary s, RunLinkKind kind) =>
        Snapshot.Links.TryGetValue(s, out var links) ? links.Count(l => l.Kind == kind) : 0;
}
