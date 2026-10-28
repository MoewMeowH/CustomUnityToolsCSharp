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
/// assistant 메시지의 usage: 그 요청의 컨텍스트 크기 = input + cache_creation_input + cache_read_input + output 토큰
/// (2차 2-2 0번 실측 — result의 usage는 턴 안 요청들의 합이라 컨텍스트 크기로 쓰지 않는다). 서브에이전트(parent_tool_use_id) 메시지는 넣지 않는다.
/// </summary>
public sealed record UsageEvent(long ContextTokens) : StreamEvent
{
    /// <summary>그 메시지의 모델 (message.model). result modelUsage에서 이 모델의 창을 고른다.</summary>
    public string? Model { get; init; }
}

/// <summary>result modelUsage 한 항목: 모델 · 컨텍스트 창 · 이 턴의 입력(input + cache 읽기 · 생성) 토큰.</summary>
public sealed record ModelWindow(string Model, int Window, long Input);

/// <summary>
/// result: 턴 끝. CostUsd는 클라이언트 추정이고, resume한 세션이면 앞 실행까지 합친 값이다 (문서).
/// Denials는 permission_denials 수 (훅이 막은 것도 들어간다 — 0번 실측 B).
/// Models는 modelUsage의 모델별 창 (2-2 0번 실측: haiku 5.5 = 1,000,000). 보조 호출에 다른 모델이 섞일 수 있다.
/// </summary>
public sealed record ResultEvent(bool Ok, string? Text, string? Subtype, double? CostUsd, int Denials) : StreamEvent
{
    public IReadOnlyList<ModelWindow> Models { get; init; } = [];

    /// <summary>
    /// 컨텍스트 창: 마지막 assistant 메시지의 모델과 같은 항목, 없으면 입력이 가장 많은 모델 (/self-review 83b453653b C2 —
    /// 가장 큰 창을 고르면 보조 모델(예 1M)이 섞일 때 막대가 실제보다 여유 있어 보인다). 항목이 없으면 null.
    /// </summary>
    public int? WindowFor(string? model) =>
        (model is null ? null : Models.FirstOrDefault(m => m.Model == model))?.Window
        ?? Models.OrderByDescending(m => m.Input).FirstOrDefault()?.Window;
}

/// <summary>
/// control_request: CLI가 호스트(앱)에 묻는 요청 (3차 0번 실측 — <c>{"type":"control_request","request_id":…,"request":{"subtype":"can_use_tool",…}}</c>).
/// RequestJson은 request 객체 원문. 답은 <see cref="ClaudeRunner"/>가 같은 request_id의 control_response로 stdin에 쓴다.
/// </summary>
public sealed record ControlRequestEvent(string RequestId, string Subtype, string RequestJson) : StreamEvent;

/// <summary>
/// control_cancel_request: CLI가 앞서 낸 제어 요청을 거둔다 (<c>{"type":"control_cancel_request","request_id":…}</c> — SDK 규약의 모양, 3차 0번 실측에서는 못 봄 · 추정).
/// 그 요청의 카드를 "끊김"으로 닫는다 (/self-review 7d839f4aa5 C1).
/// </summary>
public sealed record ControlCancelEvent(string RequestId) : StreamEvent;

/// <summary>화면에 쓰지 않는 줄 (status · message_start · rate_limit_event · control_response · 모르는 종류 · 깨진 JSON).</summary>
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
                "control_request" when Str(e, "request_id") is { Length: > 0 } id && e.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object
                    => new ControlRequestEvent(id, Str(req, "subtype") ?? "", req.GetRawText()),
                "control_cancel_request" when Str(e, "request_id") is { Length: > 0 } cancelId => new ControlCancelEvent(cancelId),
                "system" when subtype == "init" => new InitEvent(Str(e, "session_id"), Str(e, "claude_code_version"), McpServers(e)),
                "system" when subtype == "permission_denied" => new DeniedEvent(Str(e, "tool_name"), Str(e, "decision_reason")),
                "system" when subtype == "api_retry" => new RetryEvent(Int(e, "attempt") ?? 0, Str(e, "error")),
                "assistant" => ParseUsage(e),
                "result" => new ResultEvent(
                    Ok: subtype == "success" && !(e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True),
                    Text: Str(e, "result"),
                    Subtype: subtype,
                    CostUsd: e.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : null,
                    Denials: e.TryGetProperty("permission_denials", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0)
                {
                    Models = ModelWindows(e),
                },
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

    private static StreamEvent ParseUsage(JsonElement e)
    {
        if (e.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return new OtherEvent("assistant/subagent");
        if (!e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return new OtherEvent("assistant");
        var total = Long(usage, "input_tokens") + Long(usage, "cache_creation_input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "output_tokens");
        return total > 0 ? new UsageEvent(total) { Model = Str(message, "model") } : new OtherEvent("assistant");
    }

    private static IReadOnlyList<ModelWindow> ModelWindows(JsonElement e)
    {
        if (!e.TryGetProperty("modelUsage", out var models) || models.ValueKind != JsonValueKind.Object) return [];
        var list = new List<ModelWindow>();
        foreach (var m in models.EnumerateObject())
            if (m.Value.ValueKind == JsonValueKind.Object && Int(m.Value, "contextWindow") is > 0 and var w)
                list.Add(new ModelWindow(m.Name, w,
                    Long(m.Value, "inputTokens") + Long(m.Value, "cacheReadInputTokens") + Long(m.Value, "cacheCreationInputTokens")));
        return list;
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) && l > 0 ? l : 0;

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
