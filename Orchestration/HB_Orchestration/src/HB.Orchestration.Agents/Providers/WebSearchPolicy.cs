using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// 지금 호출의 웹검색 설정. AgentRunner가 호출 동안 놓고, 같은 비동기 흐름의 HTTP 정책이 읽는다 (PromptCacheScope와 같은 방식).
/// MEAI의 HostedWebSearchTool은 {"type":"web_search"}만 보내므로 상한·필터는 정책이 요청 본문에 넣는다.
/// </summary>
public static class WebSearchScope
{
    private static readonly AsyncLocal<WebSearchSettings?> s_current = new();

    public static WebSearchSettings? Current => s_current.Value;

    public static IDisposable Enter(WebSearchSettings? settings)
    {
        var previous = s_current.Value;
        s_current.Value = settings;
        return new Restore(previous);
    }

    private sealed class Restore(WebSearchSettings? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

/// <summary>
/// OpenAI Responses 요청(/responses) 본문을 고친다.
/// - 항상: store = false (응답을 OpenAI 쪽에 남기지 않는다. 대화를 이어 쓰지 않으므로 필요 없음).
/// - 웹검색 범위 안이고 web_search 도구가 있으면: max_tool_calls, include에 web_search_call.action.sources,
///   도구에 filters.blocked_domains · search_context_size.
/// 본문이 예상한 모양이 아니거나 처리에 실패하면 손대지 않고 보낸다 (이 정책 때문에 호출이 실패하면 안 된다).
/// </summary>
public sealed class WebSearchPolicy : PipelinePolicy
{
    public const string SourcesInclude = "web_search_call.action.sources";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void Apply(PipelineMessage message)
    {
        var request = message.Request;
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            || request.Uri is null
            || !request.Uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)
            || request.Content is null)
            return;

        try
        {
            using var buffer = new MemoryStream();
            request.Content.WriteTo(buffer);
            if (Rewrite(BinaryData.FromBytes(buffer.ToArray()), WebSearchScope.Current) is { } rewritten)
                request.Content = BinaryContent.Create(rewritten);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 원래 본문 그대로 보낸다.
        }
    }

    /// <summary>바꿀 것이 있으면 새 본문, 없거나 예상한 모양이 아니면 null.</summary>
    public static BinaryData? Rewrite(BinaryData body, WebSearchSettings? search)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body.ToMemory().Span); }
        catch (JsonException) { return null; }
        if (root is not JsonObject request) return null;

        var changed = false;
        if (!request.ContainsKey("store"))
        {
            request["store"] = false;
            changed = true;
        }

        var webSearch = search is null || request["tools"] is not JsonArray tools
            ? null
            : tools.OfType<JsonObject>().FirstOrDefault(t => t["type"] is JsonValue v && v.TryGetValue<string>(out var n) && n == "web_search");
        if (search is not null && webSearch is not null)
        {
            if (search.BlockedDomains.Count > 0 && !webSearch.ContainsKey("filters"))
            {
                webSearch["filters"] = new JsonObject
                {
                    ["blocked_domains"] = new JsonArray([.. search.BlockedDomains.Select(d => (JsonNode)JsonValue.Create(d.Trim()))]),
                };
                changed = true;
            }
            if (search.ContextSize is { Length: > 0 } size && !webSearch.ContainsKey("search_context_size"))
            {
                webSearch["search_context_size"] = size;
                changed = true;
            }
            if (!request.ContainsKey("max_tool_calls"))
            {
                request["max_tool_calls"] = search.MaxCalls;
                changed = true;
            }

            if (request["include"] is not JsonArray include)
            {
                include = [];
                request["include"] = include;
            }
            if (!include.Any(i => i is JsonValue v && v.TryGetValue<string>(out var s) && s == SourcesInclude))
            {
                include.Add(SourcesInclude);
                changed = true;
            }
        }

        return changed ? BinaryData.FromString(request.ToJsonString()) : null;
    }
}
