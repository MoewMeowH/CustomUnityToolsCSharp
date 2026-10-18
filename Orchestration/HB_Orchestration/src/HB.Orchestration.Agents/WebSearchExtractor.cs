using System.Text.RegularExpressions;
using HB.Orchestration.Shared;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents;

/// <summary>
/// 응답 메시지에서 웹검색 기록을 뽑고, 응답 본문에 적힌 URL이 실제 검색 결과에 있는지 대조한다.
/// MEAI(OpenAI Responses)가 web_search_call을 WebSearchToolCallContent(검색어)·WebSearchToolResultContent(참고 URL)로,
/// url_citation을 TextContent의 CitationAnnotation으로 돌려준다 (2026-10-19 Microsoft.Extensions.AI.OpenAI 10.10.1로 확인).
/// </summary>
public static partial class WebSearchExtractor
{
    /// <param name="raw">응답 원본 (AgentResponse 등). open_page·find_in_page로 연 페이지 URL을 여기서 꺼낸다 — MEAI 변환에는 없다.</param>
    public static WebSearchRecord From(IEnumerable<ChatMessage>? messages, string? responseText = null, object? raw = null)
    {
        var calls = 0;
        var queries = new List<string>();
        var sources = new List<string>();
        var citations = new List<string>();

        foreach (var content in (messages ?? []).SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case WebSearchToolCallContent call:
                    calls++;
                    foreach (var q in call.Queries ?? []) AddDistinct(queries, q);
                    break;
                case WebSearchToolResultContent result:
                    foreach (var output in result.Outputs ?? [])
                        if (output is UriContent uri) AddDistinct(sources, uri.Uri.ToString());
                    break;
            }

            foreach (var annotation in content.Annotations ?? [])
                if (annotation is CitationAnnotation { Url: { } url }) AddDistinct(citations, url.ToString());
        }

        var opened = OpenedPages(raw);
        return new WebSearchRecord
        {
            Calls = calls, Queries = queries, Sources = sources, Citations = citations, Opened = opened,
            Unverified = Unverified(responseText, sources.Concat(citations).Concat(opened)),
        };
    }

    /// <summary>
    /// 응답 원본(OpenAI ResponseResult)의 web_search_call 중 open_page·find_in_page 동작의 URL.
    /// 연 페이지는 Sources에 없을 수 있어, 그 URL을 인용하면 출처 대조가 잘못 경고한다 (2026-10-19 실제 호출에서
    /// 원인을 가리지 못한 경고 1건 — 검색 6회 중 연 페이지 URL을 기록하지 않고 있었음).
    /// </summary>
    public static IReadOnlyList<string> OpenedPages(object? raw)
    {
        var list = new List<string>();
        try
        {
            for (var depth = 0; depth < 4 && raw is not null; depth++)
            {
                switch (raw)
                {
                    case Microsoft.Agents.AI.AgentResponse agent: raw = agent.RawRepresentation; break;
                    case ChatResponse chat: raw = chat.RawRepresentation; break;
#pragma warning disable OPENAI001 // Responses 형식은 SDK에서 평가용 표시 (웹검색 단계만 씀)
                    case OpenAI.Responses.ResponseResult result:
                        foreach (var item in result.OutputItems.OfType<OpenAI.Responses.WebSearchCallResponseItem>())
                        {
                            var uri = item.Action switch
                            {
                                OpenAI.Responses.WebSearchOpenPageAction open => open.Uri,
                                OpenAI.Responses.WebSearchFindInPageAction find => find.Uri,
                                _ => null,
                            };
                            if (uri is not null) AddDistinct(list, uri.ToString());
                        }
                        return list;
#pragma warning restore OPENAI001
                    default: return list;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 기록용 정보라 실패해도 무시한다 (대조에서 연 페이지만 빠진다).
        }
        return list;
    }

    /// <summary>
    /// 본문의 URL 중 검색 결과(known)에 없는 것. 비교 키는 호스트(소문자, www. 제거) + 경로(끝 '/' 제거)이고
    /// scheme·쿼리·조각은 무시한다 — OpenAI 인용 URL에 ?utm_source=openai가 붙는 경우가 있다.
    /// </summary>
    public static IReadOnlyList<string> Unverified(string? text, IEnumerable<string> known)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var knownKeys = known.Select(Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match m in UrlRegex().Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '`', '*', '>');
            // 괄호로 감싼 URL: 여는 괄호가 URL 안에 없으면 끝 ')'는 문장 괄호다 (위키 주소처럼 괄호가 짝을 이루면 둔다).
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '(')) url = url[..^1];
            while (url.EndsWith(']') && url.Count(c => c == ']') > url.Count(c => c == '[')) url = url[..^1];
            if (Key(url) is { } key && !knownKeys.Contains(key)) AddDistinct(result, url);
        }
        return result;
    }

    /// <summary>비교 키. URL로 읽을 수 없으면 null (대조에서 뺀다).</summary>
    internal static string? Key(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        return host + path;
    }

    [GeneratedRegex(@"https?://[^\s<>""`|　-〿（）]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    private static void AddDistinct(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.Ordinal)) list.Add(value);
    }
}
