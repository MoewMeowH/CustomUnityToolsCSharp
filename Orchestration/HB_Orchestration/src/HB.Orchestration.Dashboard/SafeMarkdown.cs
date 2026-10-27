using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// 화면에 그리는 Markdown (외부 에이전트 응답 · 세션 대화 · 서브에이전트 결과 — 믿을 수 없는 글).
/// 원시 HTML은 끄고(DisableHtml), 링크는 http · https · mailto · 상대 경로만, 그림은 같은 출처 경로만 남긴다.
/// 나머지 그림은 글자 링크로, 나머지 링크는 "#"으로 바꾼다 (/self-review security 2026-10-27: javascript: 링크 · 외부 그림 비콘).
/// </summary>
public static partial class SafeMarkdown
{
    public static readonly MarkdownPipeline Default =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    /// <summary>사용자 글용: 한 줄 바꿈도 줄바꿈으로 보인다 (사용자는 Markdown을 의식하지 않고 쓴다).</summary>
    public static readonly MarkdownPipeline KeepLineBreaks =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().UseSoftlineBreakAsHardlineBreak().DisableHtml().Build();

    public static string ToHtml(string markdown, MarkdownPipeline? pipeline = null)
    {
        pipeline ??= Default;
        var doc = Markdown.Parse(markdown, pipeline);
        foreach (var inline in doc.Descendants<Inline>().ToList())
        {
            switch (inline)
            {
                case LinkInline link:
                    if (link.IsImage && !IsSameOriginImage(link.Url)) link.IsImage = false;
                    if (!IsSafeLink(link.Url))
                    {
                        link.Url = "#";
                        link.GetDynamicUrl = null;
                    }
                    break;
                case AutolinkInline auto when !IsSafeLink(auto.Url):
                    auto.ReplaceBy(new LiteralInline(auto.Url));
                    break;
            }
        }
        return doc.ToHtml(pipeline);
    }

    /// <summary>스킴이 없거나(상대 경로 · //호스트) http · https · mailto면 true.</summary>
    public static bool IsSafeLink(string? url)
    {
        var u = Normalize(url);
        return SchemeRegex().Match(u) is not { Success: true } m
            || m.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto";
    }

    /// <summary>스킴 없는 같은 출처 경로만 true (//호스트 · 스킴 있는 주소는 열자마자 밖으로 요청이 나간다).</summary>
    public static bool IsSameOriginImage(string? url)
    {
        var u = Normalize(url);
        return u.Length > 0 && !SchemeRegex().IsMatch(u) && !u.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>브라우저처럼 제어 문자 · 공백을 빼고 \를 /로 본다 ("java\tscript:" · "/\host").</summary>
    private static string Normalize(string? url) =>
        url is null ? "" : new string(url.Where(c => c > ' ').Select(c => c == '\\' ? '/' : c).ToArray());

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):")]
    private static partial Regex SchemeRegex();
}
