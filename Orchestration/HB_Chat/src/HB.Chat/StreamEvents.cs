using System.Text.Json;

namespace HB.Chat;

/// <summary>claude -p --output-format stream-json 한 줄을 읽은 결과. 화면에 필요한 것만 꺼내고 나머지는 <see cref="OtherEvent"/>.</summary>
public abstract record StreamEvent;

/// <summary>system/init: 실제로 쓰는 세션 ID · CLI 버전 · 붙은 MCP 서버 (이름, 상태).</summary>
public sealed record InitEvent(string? SessionId, string? Version, IReadOnlyList<(string Name, string Status)> McpServers) : StreamEvent
{
    /// <summary>
    /// 그 세션에서 쓸 수 있는 '/' 명령 이름들 (사용자 명령 · 스킬 · 플러그인 스킬 · 기본 명령이 섞임 — 설명은 없다), 그중 스킬,
    /// 터미널 전용 명령, 켜진 플러그인 (이름, 경로). 입력칸 '/' 자동 완성 (Claude 앱 기능 A — 2.1.312 실측).
    /// </summary>
    public IReadOnlyList<string> SlashCommands { get; init; } = [];
    public IReadOnlyList<string> Skills { get; init; } = [];
    public IReadOnlyList<string> TerminalCommands { get; init; } = [];

    /// <summary>쓸 수 있는 서브에이전트 이름 (Claude 앱 기능 D — 곁칸 목록).</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<(string Name, string Path)> Plugins { get; init; } = [];

    /// <summary>쓸 수 있는 도구 이름 (MCP는 mcp__서버__도구, 설명 없음) — 연결 탭이 앱에서 확인할 수 없는 서버(OAuth)의 도구 목록으로 쓴다.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];
}

/// <summary>
/// 서브에이전트 진행 (Claude 앱 기능 D — 2.1.312 실측 system task_started · task_progress · task_notification).
/// Phase: started · progress · done. Agent는 subagent_type, Description은 지휘자가 준 설명 · 진행 글, Status는 끝 상태(completed · failed …).
/// </summary>
public sealed record TaskEvent(string Phase, string TaskId, string? Agent, string? Description, string? Status, int? ToolUses) : StreamEvent
{
    /// <summary>task_type — "local_bash"면 지휘자가 Bash 백그라운드 실행으로 띄운 명령 (실측, 계획서 2026-10-29_HB_Chat-백그라운드-명령). 서브에이전트는 local_agent 등.</summary>
    public string? Kind { get; init; }

    /// <summary>끝 알림(task_notification)의 출력 파일 경로.</summary>
    public string? OutputFile { get; init; }

    public const string BashKind = "local_bash";
}

/// <summary>
/// 메인 답(result)이 끝났는데 백그라운드 작업(서브에이전트 · 백그라운드 명령)이 남아 기다리는 중 — ClaudeRunner가 내는 이벤트(CLI 줄이 아님).
/// Count가 0이면 기다림이 끝났다. Until은 그때까지 안 끝나면 정리하는 시각.
/// </summary>
public sealed record BackgroundWaitEvent(int Count, DateTimeOffset? Until) : StreamEvent;

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

    /// <summary>
    /// 그 메시지의 도구 호출 (content의 tool_use — 입력 전체가 여기 온다, 도구 실행 전, 3차 0번 실측 C). ContextTokens가 0이면 usage가 없던 줄이다.
    /// </summary>
    public IReadOnlyList<ToolUse> Tools { get; init; } = [];
}

/// <summary>도구 호출 하나: 이름 · 화면에 보일 짧은 대상(파일 이름 · 명령 첫 줄 · 검색어 · 모드 — 없으면 null, 3차 b).</summary>
public sealed record ToolUse(string Name, string? Detail)
{
    /// <summary>tool_use id — 결과(tool_result)와 잇는다 (Unity 그림 기록, 계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결).</summary>
    public string? Id { get; init; }
}

/// <summary>
/// user 메시지의 tool_result 속 그림 (MCP 도구가 돌려준 base64 그림 — capture_game_view 등). 메인 대화만 (서브에이전트 줄은 뺀다).
/// 그림 하나가 <see cref="RoomShots.MaxBytes"/>를 넘거나 png · jpeg가 아니면 넣지 않는다.
/// </summary>
public sealed record ToolImagesEvent(IReadOnlyList<ToolImage> Images) : StreamEvent;

/// <summary>그림 하나. 풀기(base64)는 남길 그림에서만 — Read로 연 첨부 그림 · 브라우저 스크린샷도 여기로 온다.</summary>
public sealed record ToolImage(string ToolUseId, string MediaType, string Base64);

/// <summary>
/// system/post_turn_summary: CLI가 턴마다 내는 지휘자 상태 한 줄 (3차 0번 실측 B — status_category blocked · completed · review_ready,
/// status_detail 한 줄 상태, needs_action 사용자가 할 일).
/// </summary>
public sealed record StatusEvent(string? Category, string? Detail, string? NeedsAction) : StreamEvent;

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

/// <summary>
/// rate_limit_event: 구독 한도 창별 사용률 (2026-10-29 실측 — <c>rate_limit_info.unifiedWindows.{five_hour|seven_day}.{utilization 0~1, resetsAt 유닉스 초, status}</c>,
/// 그 밖에 status · isUsingOverage). 계획서 2026-10-29_사용량-한도-표시.
/// </summary>
public sealed record RateLimitEvent(IReadOnlyList<LimitWindow> Windows, string? Status, bool? UsingOverage) : StreamEvent;

/// <summary>화면에 쓰지 않는 줄 (status · message_start · control_response · 모르는 종류 · 깨진 JSON).</summary>
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
                "system" when subtype == "init" => new InitEvent(Str(e, "session_id"), Str(e, "claude_code_version"), McpServers(e))
                {
                    SlashCommands = Strs(e, "slash_commands"),
                    Skills = Strs(e, "skills"),
                    TerminalCommands = Strs(e, "terminal_slash_commands"),
                    Agents = Strs(e, "agents"),
                    Plugins = Plugins(e),
                    Tools = Strs(e, "tools"),
                },
                "system" when subtype is "task_started" or "task_progress" or "task_notification" && Str(e, "task_id") is { Length: > 0 } taskId =>
                    new TaskEvent(subtype switch { "task_started" => "started", "task_progress" => "progress", _ => "done" }, taskId,
                        Cap(Str(e, "subagent_type"), 80), Cap(Str(e, "description"), StatusChars), Cap(Str(e, "status"), 40),
                        e.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? Int(u, "tool_uses") : null)
                    {
                        Kind = Cap(Str(e, "task_type"), 40),
                        OutputFile = Cap(Str(e, "output_file"), 1000),
                    },
                "system" when subtype == "permission_denied" => new DeniedEvent(Str(e, "tool_name"), Str(e, "decision_reason")),
                "system" when subtype == "api_retry" => new RetryEvent(Int(e, "attempt") ?? 0, Str(e, "error")),
                "system" when subtype == "post_turn_summary" =>
                    new StatusEvent(Cap(Str(e, "status_category"), 40), Cap(Str(e, "status_detail"), StatusChars), Cap(Str(e, "needs_action"), StatusChars)),
                "rate_limit_event" => ParseRateLimit(e),
                "assistant" => ParseUsage(e),
                "user" => ParseToolImages(e),
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

    private static StreamEvent ParseRateLimit(JsonElement e)
    {
        if (!e.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object) return new OtherEvent("rate_limit_event");
        var windows = new List<LimitWindow>();
        if (info.TryGetProperty("unifiedWindows", out var unified) && unified.ValueKind == JsonValueKind.Object)
            foreach (var w in unified.EnumerateObject().Take(8))
                if (w.Value.ValueKind == JsonValueKind.Object)
                    windows.Add(new LimitWindow(Cap(w.Name, 40)!,
                        w.Value.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? Math.Round(u.GetDouble() * 100, 1) : null,
                        UnixSeconds(Long(w.Value, "resetsAt")),
                        Cap(Str(w.Value, "status"), 40)));
        // 창별 값이 없는 모양(옛 판 — 위쪽 rateLimitType · resetsAt만)이면 사용률 없이 그 창 하나라도 (지휘자 K2)
        if (windows.Count == 0 && Str(info, "rateLimitType") is { Length: > 0 } kind)
            windows.Add(new LimitWindow(Cap(kind, 40)!, null, UnixSeconds(Long(info, "resetsAt")), Cap(Str(info, "status"), 40)));
        return new RateLimitEvent(windows, Cap(Str(info, "status"), 40),
            info.TryGetProperty("isUsingOverage", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False ? o.GetBoolean() : null);
    }

    /// <summary>유닉스 초 → 시각. 0 이하 · 범위 밖(밀리초로 온 값 등)이면 null — Parse는 예외를 던지지 않는다 (/self-review b40d9efb00 C1).</summary>
    private static DateTimeOffset? UnixSeconds(long seconds) =>
        seconds > 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

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

    /// <summary>상태 한 줄 · 할 일 글 상한 (글자).</summary>
    public const int StatusChars = 300;

    /// <summary>도구 대상 글 상한 (글자, 칩).</summary>
    public const int DetailChars = HB.Orchestration.Shared.ToolTargets.MaxChars;

    private static StreamEvent ParseUsage(JsonElement e)
    {
        if (e.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return new OtherEvent("assistant/subagent");
        if (!e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return new OtherEvent("assistant");
        var total = message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? Long(usage, "input_tokens") + Long(usage, "cache_creation_input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "output_tokens")
            : 0;
        var tools = ToolUses(message);
        return total > 0 || tools.Count > 0 ? new UsageEvent(total) { Model = Str(message, "model"), Tools = tools } : new OtherEvent("assistant");
    }

    private static IReadOnlyList<ToolUse> ToolUses(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return [];
        var list = new List<ToolUse>();
        foreach (var c in content.EnumerateArray())
            if (c.ValueKind == JsonValueKind.Object && Str(c, "type") == "tool_use" && Str(c, "name") is { Length: > 0 } name)
                list.Add(new ToolUse(name, c.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object ? Detail(name, input) : null)
                {
                    Id = Str(c, "id"),
                });
        return list;
    }

    /// <summary>user 줄의 tool_result 그림들. 없으면 OtherEvent("user").</summary>
    private static StreamEvent ParseToolImages(JsonElement e)
    {
        if (e.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return new OtherEvent("user/subagent");
        if (!e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return new OtherEvent("user");
        var images = new List<ToolImage>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Str(block, "type") != "tool_result" || Str(block, "tool_use_id") is not { Length: > 0 } id
                || !block.TryGetProperty("content", out var inner) || inner.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in inner.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || Str(part, "type") != "image"
                    || !part.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object
                    || Str(source, "type") != "base64" || RoomShots.Ext(Str(source, "media_type")) is null
                    || Str(source, "data") is not { Length: > 0 } data || data.Length / 4 * 3 > RoomShots.MaxBytes) continue;
                images.Add(new ToolImage(id, Str(source, "media_type")!, data));
            }
        }
        return images.Count > 0 ? new ToolImagesEvent(images) : new OtherEvent("user");
    }

    /// <summary>칩에 보일 짧은 대상 — 끝난 턴 칩(세션 기록)과 같은 규칙이라 공용 <see cref="ToolTargets"/>로 옮김 (4차 C3).</summary>
    public static string? Detail(string name, JsonElement input) => HB.Orchestration.Shared.ToolTargets.Of(name, input);

    private static string? Cap(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length > max ? s[..max] + "…" : s.Trim();

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

    /// <summary>글자 배열 (글자가 아닌 칸은 건너뜀). 없으면 빈 목록.</summary>
    private static IReadOnlyList<string> Strs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

    private static IReadOnlyList<(string, string)> Plugins(JsonElement e)
    {
        if (!e.TryGetProperty("plugins", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.Object && Str(p, "name") is not null && Str(p, "path") is not null)
            .Select(p => (Str(p, "name")!, Str(p, "path")!))
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
