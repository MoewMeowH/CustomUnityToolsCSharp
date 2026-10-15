using HB.Orchestration.Shared;
using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

public sealed class CallModel(IOptions<DashboardOptions> options) : PageModel
{
    // 에이전트 응답을 렌더링하므로 원시 HTML은 막는다.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public DashboardOptions Opt => options.Value;
    public CallRecord Call { get; private set; } = null!;
    public string ResponseHtml { get; private set; } = "";
    public List<FileLinks.Link> Links { get; private set; } = [];

    /// <summary>서버가 프롬프트에 첨부한 코드 범위 (에이전트가 실제로 본 코드).</summary>
    public List<FileLinks.Link> ContextLinks { get; private set; } = [];
    public decimal? Cost { get; private set; }

    /// <summary>같은 모드 실행(RunId)에 속한 단계들. 단계 순서대로.</summary>
    public List<CallRecord> RunSteps { get; private set; } = [];

    public IActionResult OnGet(string id)
    {
        var all = CallLog.ReadAll(Opt.ResolvedLogPath);
        var call = all.FirstOrDefault(c => c.Id == id);
        if (call is null) return NotFound();

        Call = call;
        ResponseHtml = Markdown.ToHtml(call.Response ?? "", Pipeline);
        Links = call.Files.Select(f => FileLinks.Build(f, call.ProjectDir, Opt.FileLinkTemplate)).ToList();
        ContextLinks = call.Context.Select(f => FileLinks.Build(f, call.ProjectDir, Opt.FileLinkTemplate)).ToList();
        Cost = Opt.CostOf(call);
        if (call.RunId is { } runId)
            RunSteps = all.Where(c => c.RunId == runId).OrderBy(c => c.Step).ToList();
        return Page();
    }
}
