using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace HB.Orchestration.Agents;

/// <summary>
/// 제공사가 돌려준 usage 원문을 꺼낸다. SDK가 모르는 필드(예: 캐시 쓰기 토큰)는 UsageDetails에 오지 않으므로
/// 원문을 기록해 두고, 알려진 후보 키에서 값을 읽는다. 어떤 단계에서 실패해도 null을 돌려주고 호출은 막지 않는다.
/// </summary>
public static class UsageExtractor
{
    /// <summary>캐시 쓰기 토큰 후보 경로. 실제 키 이름은 측정 기록(UsageRaw)으로 확인한 뒤 줄인다.</summary>
    private static readonly string[][] CacheWritePaths =
    [
        ["prompt_tokens_details", "cache_write_tokens"],
        ["prompt_tokens_details", "cache_creation_tokens"],
        ["input_tokens_details", "cache_write_tokens"],
        ["cache_write_tokens"],
    ];

    /// <summary>캐시 적중 토큰 후보 경로. UsageDetails.CachedInputTokenCount가 비었을 때(OpenAI 호환 Gemini 등)만 쓴다.</summary>
    private static readonly string[][] CachedPaths =
    [
        ["prompt_tokens_details", "cached_tokens"],
        ["cached_content_token_count"],
        ["cachedContentTokenCount"],
    ];

    /// <summary>응답의 원본 표현을 따라가 Chat Completions usage JSON을 꺼낸다. 없거나 실패하면 null.</summary>
    public static string? RawUsageJson(object? raw)
    {
        try
        {
            // AgentResponse → ChatResponse → ChatCompletion 순서로 감싸져 있다. 버전에 따라 단계가 달라질 수 있어 짧게 따라간다.
            for (var depth = 0; depth < 4 && raw is not null; depth++)
            {
                switch (raw)
                {
                    case AgentResponse agent: raw = agent.RawRepresentation; break;
                    case ChatResponse chat: raw = chat.RawRepresentation; break;
                    case ChatCompletion completion:
                        return completion.Usage is null ? null : ModelReaderWriter.Write(completion.Usage).ToString();
                    default: return null;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 기록용 정보라 실패해도 무시한다.
        }
        return null;
    }

    public static long? CacheWriteTokens(string? usageJson) => FirstNumber(usageJson, CacheWritePaths);

    public static long? CachedTokens(string? usageJson) => FirstNumber(usageJson, CachedPaths);

    private static long? FirstNumber(string? json, string[][] paths)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var path in paths)
            {
                var node = doc.RootElement;
                var found = true;
                foreach (var key in path)
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node)) { found = false; break; }
                }
                if (found && node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value)) return value;
            }
        }
        catch (JsonException)
        {
            // 깨진 원문은 값 없음으로 둔다.
        }
        return null;
    }
}
