using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

public sealed class CallModel(IOptions<DashboardOptions> options, PricingService pricing, ClaudeSessionsService sessions, CallLogCache log, ModesConfigService config, RiderProjects rider, VerdictLogCache verdicts) : PageModel
{
    public PricingService Pricing => pricing;

    public DashboardOptions Opt => options.Value;
    public CallRecord Call { get; private set; } = null!;

    /// <summary>프롬프트 원문 (큰 프롬프트는 blobs에서 읽는다). PromptMissing이면 원문 파일이 없어 기록의 앞부분이다.</summary>
    public string PromptText { get; private set; } = "";
    public bool PromptMissing { get; private set; }

    public string ResponseHtml { get; private set; } = "";
    public List<FileLinks.Link> Links { get; private set; } = [];

    /// <summary>서버가 프롬프트에 첨부한 코드 범위 (에이전트가 실제로 본 코드).</summary>
    public List<FileLinks.Link> ContextLinks { get; private set; } = [];

    /// <summary>보낸 이미지 (기록 순서). Match는 지금 파일이 기록의 sha와 같은지.</summary>
    public List<(int Index, string Path, string Detail, ImageMatch Match)> Images { get; private set; } = [];
    public decimal? Cost { get; private set; }

    /// <summary>같은 모드 실행(RunId)에 속한 단계들. 단계 순서대로.</summary>
    public List<CallRecord> RunSteps { get; private set; } = [];

    /// <summary>이 기록과 같은 단계(RunId·Step)의 시도 수. 2 이상이면 재시도가 있었다.</summary>
    public int StepAttempts { get; private set; } = 1;

    /// <summary>이 실행에 대한 지휘자의 판정 (record_verdicts). 단계 순서, Claude만 지적은 맨 뒤.</summary>
    public List<VerdictView> RunVerdicts { get; private set; } = [];

    /// <summary>지휘자의 자체 검토(ownReview)를 응답과 같은 방식(원시 HTML 금지)으로 렌더링한 것.</summary>
    public string OwnReviewHtml { get; private set; } = "";

    /// <summary>5장 이전 기록 (Provenance · Reason이 없음) → "기록 없음".</summary>
    public bool IsLegacy => Call.Provenance is null && Call.Reason is null;

    public List<ProvenanceRow> ProvenanceRows { get; private set; } = [];

    /// <summary>이 호출이 속한 Claude Code 세션 (세션 기록 화면의 연결 결과). 없으면 null.</summary>
    public (string SessionId, RunLinkKind Kind)? Session { get; private set; }

    public IActionResult OnGet(string id)
    {
        var all = log.All;
        var call = all.FirstOrDefault(c => c.Id == id);
        if (call is null) return NotFound();

        Call = call;
        (PromptText, PromptMissing) = CallLog.FullPrompt(call, options.Value.ResolvedLogPath);
        // 구조화 출력을 해석한 기록이면 지휘자가 받은 그린 텍스트를 보여 주고, 원문(JSON)은 따로 접어 둔다.
        // 에이전트 응답을 그리므로 원시 HTML · 위험한 링크 · 외부 그림은 막는다 (SafeMarkdown).
        ResponseHtml = SafeMarkdown.ToHtml(call.Structured is { Parsed: true } s ? HB.Orchestration.Agents.FindingRenderer.Render(s) : call.Response ?? "");
        var projects = rider.All;
        Links = call.Files.Select(f => FileLinks.Build(f, call.ProjectDir, Opt.FileLinkTemplate, projects: projects)).ToList();
        ContextLinks = call.Context.Select(f => FileLinks.Build(f, call.ProjectDir, Opt.FileLinkTemplate, call.ContextRoots, projects)).ToList();
        Cost = pricing.CostOf(call);
        if (call.Images is { Count: > 0 } images)
        {
            var mcp = config.McpOptionsOrDefault();
            for (var i = 0; i < images.Count; i++)
            {
                var target = Attachments.Find(call, AttachmentKind.Images, i, mcp.Context.AllDenyPatterns)!;
                var (path, _) = Attachments.SplitImageRecord(images[i]);
                var detail = images[i].Length > path.Length ? images[i][path.Length..].Trim().Trim('(', ')') : "";
                Images.Add((i, path, detail, Attachments.ReadImage(target, mcp.Images.MaxBytes).Match));
            }
        }
        if (call.RunId is { } runId)
        {
            RunSteps = all.Where(c => c.RunId == runId).OrderBy(c => c.Step).ThenBy(c => c.Attempt ?? 1).ToList();
            StepAttempts = Math.Max(1, RunSteps.Count(c => c.Step == call.Step));
            Session = FindSession(runId, all);
            RunVerdicts = VerdictStats.ForRun(verdicts.Latest, RunSteps, runId);
        }

        OwnReviewHtml = SafeMarkdown.ToHtml(call.OwnReview ?? "");
        if (call.Provenance is { } p) ProvenanceRows = ProvenanceView.Rows(p);
        return Page();
    }

    /// <summary>
    /// 지침·설정 원문 보기. 이 기록이 가리키는 해시만 연다 (64자리 소문자 hex, 저장 폴더는 기록의 값).
    /// 그 밖은 모두 404. 원문 바이트 그대로 text/plain으로 돌려준다.
    /// </summary>
    public IActionResult OnGetBlob(string id, string sha)
    {
        if (!BlobStore.IsValidSha(sha)) return NotFound();
        var call = log.All.FirstOrDefault(c => c.Id == id);
        if (call?.Provenance is not { BlobFolder: { } folder } p || !ProvenanceView.AllowedShas(p).Contains(sha)) return NotFound();

        // 링크(폴더 밖을 가리킬 수 있음)는 열지 않는다. 검사와 읽기 사이의 바꿔치기는 ContextLoader와 같은 한계로 막지 않는다.
        // 검사 뒤 지워지거나 잠기면 500이 아니라 404 (review 40e64499d8).
        byte[] bytes;
        try
        {
            var info = new FileInfo(BlobStore.PathFor(folder, sha));
            if (!info.Exists || info.LinkTarget is not null) return NotFound();
            bytes = System.IO.File.ReadAllBytes(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NotFound();
        }
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "text/plain; charset=utf-8");
    }

    private (string, RunLinkKind)? FindSession(string runId, IReadOnlyList<CallRecord> all)
    {
        try
        {
            foreach (var (summary, links) in sessions.Load(all).Links)
                if (links.FirstOrDefault(l => l.RunId == runId && l.Kind is RunLinkKind.Confirmed or RunLinkKind.Estimated) is { } link)
                    return (summary.FileSessionId, link.Kind);
        }
        catch (Exception)
        {
            // 세션 기록은 부가 정보다. 읽지 못해도 호출 상세는 보여 준다.
        }
        return null;
    }
}
