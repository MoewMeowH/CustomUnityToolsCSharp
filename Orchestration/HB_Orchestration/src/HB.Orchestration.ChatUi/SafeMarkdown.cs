using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HB.Orchestration.ChatUi;

/// <summary>
/// 화면에 그리는 Markdown (외부 에이전트 응답 · 세션 대화 · 서브에이전트 결과 — 믿을 수 없는 글).
/// 원시 HTML은 끄고(DisableHtml), 링크는 http · https · mailto · 상대 경로만, 그림은 같은 출처 경로만 남긴다.
/// 나머지 그림은 글자 링크로, 나머지 링크는 "#"으로 바꾼다 (/self-review security 2026-10-27: javascript: 링크 · 외부 그림 비콘).
/// 속성 문법 <c>{onerror=…}</c>(GenericAttributes 확장)은 빼고, 그린 뒤에도 id · class 말고 속성은 지운다 —
/// DisableHtml은 태그만 막고 속성은 그대로 나가 이벤트 처리기가 앱 출처에서 돌았다 (sub_security caea21ac40 C1).
/// </summary>
public static partial class SafeMarkdown
{
    /// <summary>그린 HTML의 id · #앵커 앞붙이기 — 앱 화면 요소 id는 이것으로 시작하지 않는다.</summary>
    public const string IdPrefix = "md-";

    public static readonly MarkdownPipeline Default = Builder().Build();

    /// <summary>사용자 글용: 한 줄 바꿈도 줄바꿈으로 보인다 (사용자는 Markdown을 의식하지 않고 쓴다).</summary>
    public static readonly MarkdownPipeline KeepLineBreaks = Builder().UseSoftlineBreakAsHardlineBreak().Build();

    private static MarkdownPipelineBuilder Builder()
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        builder.Extensions.TryRemove<GenericAttributesExtension>();
        return builder;
    }

    public static string ToHtml(string markdown, MarkdownPipeline? pipeline = null)
    {
        pipeline ??= Default;
        var doc = Markdown.Parse(markdown, pipeline);
        foreach (var node in doc.Descendants().Prepend(doc))
        {
            if (node.TryGetAttributes() is not { } attrs) continue;
            attrs.Properties?.Clear();
            // 제목 자동 id는 md- 앞붙이기 — "# text" 제목이 입력칸 #text보다 앞에 놓여 앱이 말풍선 쪽을 집었다 (보내기가 안 됨)
            if (!string.IsNullOrEmpty(attrs.Id)) attrs.Id = IdPrefix + attrs.Id;
        }
        foreach (var inline in doc.Descendants<Inline>().ToList())
        {
            switch (inline)
            {
                case LinkInline link:
                    if (link.GetDynamicUrl is { } dynamic) // [제목] 자동 링크 — 아래 # 앵커 바꾸기를 받게 지금 값으로
                    {
                        link.Url = dynamic();
                        link.GetDynamicUrl = null;
                    }
                    if (link.Url is { Length: > 1 } anchor && anchor[0] == '#') link.Url = "#" + IdPrefix + anchor[1..];
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
