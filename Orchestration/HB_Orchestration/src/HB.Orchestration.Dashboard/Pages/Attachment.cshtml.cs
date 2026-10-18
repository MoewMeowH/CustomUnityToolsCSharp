using System.Text;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>
/// 호출 기록의 첨부 코드·관련 파일·보낸 이미지를 대시보드 안에서 본다 (Rider 없이).
/// 기록에 적힌 항목만 연다 (id + 종류 + 순번). 그 밖은 모두 404.
/// </summary>
public sealed class AttachmentModel(IOptions<DashboardOptions> options, CallLogCache log, ModesConfigService config, RiderProjects rider) : PageModel
{
    public CallRecord Call { get; private set; } = null!;
    public AttachmentKind Kind { get; private set; }
    public int Index { get; private set; }
    public AttachmentTarget Target { get; private set; } = null!;
    /// <summary>Rider에서 여는 링크. 파일이 어느 Rider 프로젝트에도 속하지 않으면 null.</summary>
    public string? RiderUrl { get; private set; }

    /// <summary>텍스트 줄 (1부터). 이미지거나 열 수 없으면 비어 있음.</summary>
    public List<string> Lines { get; private set; } = [];

    /// <summary>파일이 상한보다 커서 앞부분만 읽었는지.</summary>
    public bool Truncated { get; private set; }
    public ImageMatch? Match { get; private set; }
    public string? ReadError { get; private set; }

    public IActionResult OnGet(string id, string kind, int i)
    {
        if (Resolve(id, kind, i) is not { } found) return NotFound();
        (Call, Kind, Index, Target) = found;
        RiderUrl = FileLinks.Build(Target.Display + (Target.StartLine is { } s ? $":{s}" : ""), Call.ProjectDir, options.Value.FileLinkTemplate, Call.ContextRoots, rider.All).Url;

        var mcp = config.McpOptionsOrDefault();
        if (Kind == AttachmentKind.Images)
        {
            Match = Attachments.ReadImage(Target, mcp.Images.MaxBytes).Match;
            return Page();
        }

        if (!Target.CanOpen) return Page();
        try
        {
            ReadText(Target.FullPath!, mcp.Context.MaxFileBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReadError = $"읽기 실패 ({ex.GetType().Name}: {ex.Message})";
        }
        return Page();
    }

    /// <summary>이미지 원본. 기록에 적힌 이미지이고 png·jpeg 머리가 맞을 때만 (sha가 달라도 보여 주고, 화면에서 경고).</summary>
    public IActionResult OnGetRaw(string id, string kind, int i)
    {
        if (Resolve(id, kind, i) is not { Kind: AttachmentKind.Images } found) return NotFound();
        var (_, bytes, mediaType) = Attachments.ReadImage(found.Target, config.McpOptionsOrDefault().Images.MaxBytes);
        if (bytes is null || mediaType is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "no-store"; // 파일이 바뀌면 바로 보이게 (일치 표시와 어긋나지 않도록)
        return File(bytes, mediaType);
    }

    private (CallRecord Call, AttachmentKind Kind, int Index, AttachmentTarget Target)? Resolve(string id, string kind, int i)
    {
        if (!Attachments.TryParseKind(kind, out var k)) return null;
        var call = log.All.FirstOrDefault(c => c.Id == id);
        if (call is null) return null;
        var target = Attachments.Find(call, k, i, config.McpOptionsOrDefault().Context.AllDenyPatterns);
        return target is null ? null : (call, k, i, target);
    }

    private void ReadText(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Truncated = maxBytes > 0 && stream.Length > maxBytes;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        long read = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains('\0')) { ReadError = "바이너리 파일로 보입니다."; Lines.Clear(); return; }
            Lines.Add(line);
            read += line.Length + 1;
            if (Truncated && read > maxBytes) break;
        }
    }

    public bool InRange(int line) =>
        Target.StartLine is { } s && line >= s && line <= (Target.EndLine ?? s);
}
