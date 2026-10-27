using System.Text.Json;

namespace HB.Chat;

/// <summary>claude -p --output-format stream-json 한 줄을 읽은 결과. 화면에 필요한 것만 꺼내고 나머지는 <see cref="OtherEvent"/>.</summary>
public abstract record StreamEvent;

/// <summary>system/init: 실제로 쓰는 세션 ID · CLI 버전 · 붙은 MCP 서버 (이름, 상태).</summary>
public sealed record InitEvent(string? SessionId, string? Version, IReadOnlyList<(string Name, string Status)> McpServers) : StreamEvent;

/// <summary>흘림 글 조각 (stream_event content_block_delta / text_delta).</summary>
public sealed record DeltaEvent(string Text) : StreamEvent;

/// <summary>도구 호출 시작 (stream_event content_block_start / tool_use).</summary>
public sealed record ToolEvent(string Name) : StreamEvent;

/// <summary>system/permission_denied: 승인이 필요해 자동으로 거절된 호출.</summary>
public sealed record DeniedEvent(string? Tool, string? Reason) : StreamEvent;

/// <summary>system/api_retry: API 재시도.</summary>
public sealed record RetryEvent(int Attempt, string? Error) : StreamEvent;

/// <summary>
/// result: 턴 끝. CostUsd는 클라이언트 추정이고, resume한 세션이면 앞 실행까지 합친 값이다 (문서).
/// Denials는 permission_denials 수 (훅이 막은 것도 들어간다 — 0번 실측 B).
/// </summary>
public sealed record ResultEvent(bool Ok, string? Text, string? Subtype, double? CostUsd, int Denials) : StreamEvent;

/// <summary>화면에 쓰지 않는 줄 (status · message_start · rate_limit_event · 모르는 종류 · 깨진 JSON).</summary>
public sealed record OtherEvent(string Type) : StreamEvent;

public static class StreamParser
{
    /// <summary>한 줄을 읽는다. 빈 줄은 null. 예외를 던지지 않는다.</summary>
    public static StreamEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return new OtherEvent("invalid");
            var type = Str(e, "type") ?? "";
            var subtype = Str(e, "subtype");
            return type switch
            {
                "stream_event" => ParseStream(e),
                "system" when subtype == "init" => new InitEvent(Str(e, "session_id"), Str(e, "claude_code_version"), McpServers(e)),
                "system" when subtype == "permission_denied" => new DeniedEvent(Str(e, "tool_name"), Str(e, "decision_reason")),
                "system" when subtype == "api_retry" => new RetryEvent(Int(e, "attempt") ?? 0, Str(e, "error")),
                "result" => new ResultEvent(
                    Ok: subtype == "success" && !(e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True),
                    Text: Str(e, "result"),
                    Subtype: subtype,
                    CostUsd: e.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : null,
                    Denials: e.TryGetProperty("permission_denials", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0),
                _ => new OtherEvent(subtype is null ? type : $"{type}/{subtype}"),
            };
        }
        catch (JsonException)
        {
            return new OtherEvent("invalid");
        }
    }

    private static StreamEvent ParseStream(JsonElement e)
    {
        if (!e.TryGetProperty("event", out var inner) || inner.ValueKind != JsonValueKind.Object) return new OtherEvent("stream_event");
        var type = Str(inner, "type");
        if (type == "content_block_delta" && inner.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta")
            return new DeltaEvent(Str(delta, "text") ?? "");
        if (type == "content_block_start" && inner.TryGetProperty("content_block", out var block) && Str(block, "type") == "tool_use")
            return new ToolEvent(Str(block, "name") ?? "?");
        return new OtherEvent($"stream_event/{type}");
    }

    private static IReadOnlyList<(string, string)> McpServers(JsonElement e)
    {
        if (!e.TryGetProperty("mcp_servers", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.Object)
            .Select(s => (Str(s, "name") ?? "?", Str(s, "status") ?? "?"))
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
