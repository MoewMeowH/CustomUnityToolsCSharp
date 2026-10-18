using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// 지금 호출의 프롬프트 캐시 방식. AgentRunner가 호출 동안 놓고, 같은 비동기 흐름의 HTTP 정책이 읽는다.
/// SDK 옵션 객체에 해당 필드가 없어서 호출 단위 값을 이렇게 넘긴다.
/// </summary>
public static class PromptCacheScope
{
    private static readonly AsyncLocal<PromptCacheMode?> s_current = new();

    public static PromptCacheMode? Current => s_current.Value;

    public static IDisposable Enter(PromptCacheMode mode)
    {
        var previous = s_current.Value;
        s_current.Value = mode;
        return new Restore(previous);
    }

    private sealed class Restore(PromptCacheMode? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

/// <summary>
/// OpenAI Chat Completions 요청 본문에 캐시 지시를 넣는다.
/// Off·Context: prompt_cache_options = explicit (저장 지점이 없으면 캐시 쓰기를 하지 않는다).
/// Context: 마지막 user 메시지의 첫 text 블록(첨부 자료)에 저장 지점.
/// 본문이 예상한 모양이 아니거나 처리에 실패하면 손대지 않고 보낸다 (이 정책 때문에 호출이 실패하면 안 된다).
/// </summary>
public sealed class PromptCachePolicy : PipelinePolicy
{
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
        if (PromptCacheScope.Current is not { } mode || mode == PromptCacheMode.ProviderDefault) return;

        var request = message.Request;
        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            || request.Uri is null
            || !request.Uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            || request.Content is null)
            return;

        try
        {
            using var buffer = new MemoryStream();
            request.Content.WriteTo(buffer);
            if (Rewrite(BinaryData.FromBytes(buffer.ToArray()), mode) is { } rewritten)
                request.Content = BinaryContent.Create(rewritten);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 원래 본문 그대로 보낸다.
        }
    }

    /// <summary>바꿀 것이 있으면 새 본문, 없거나 예상한 모양이 아니면 null.</summary>
    public static BinaryData? Rewrite(BinaryData body, PromptCacheMode mode)
    {
        if (mode == PromptCacheMode.ProviderDefault) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(body.ToMemory().Span); }
        catch (JsonException) { return null; }
        if (root is not JsonObject request) return null;

        var changed = false;
        if (!request.ContainsKey("prompt_cache_options"))
        {
            request["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };
            changed = true;
        }

        if (mode == PromptCacheMode.Context && LastUserContent(request) is { Count: >= 2 } parts
            && parts[0] is JsonObject first
            && first["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && typeName == "text"
            && !first.ContainsKey("prompt_cache_breakpoint"))
        {
            first["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
            changed = true;
        }

        return changed ? BinaryData.FromString(request.ToJsonString()) : null;
    }

    private static JsonArray? LastUserContent(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages) return null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject m && m["role"] is JsonValue role
                && role.TryGetValue<string>(out var name) && name == "user")
                return m["content"] as JsonArray;
        }
        return null;
    }
}
