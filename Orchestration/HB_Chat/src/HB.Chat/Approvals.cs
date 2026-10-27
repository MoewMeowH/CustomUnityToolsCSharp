using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// claude의 PermissionRequest HTTP 훅이 보낸 승인 요청 하나 (2차 계획서 2-1, 0번 실측 A로 받는 키를 확인).
/// Summary는 카드에 보일 명령 전문(명령 · 파일 경로 · 그 밖은 입력 JSON). Rule은 CLI가 제안한 규칙 중 이 앱이 "항상 허용"으로 받는 것.
/// Truncated면 전문이 너무 길어 잘린 것 — 사용자가 다 보지 못한 명령이라 거절만 받는다 (/self-review 36e4e20e04 C5 · ab79e61df5 C5).
/// </summary>
public sealed record ApprovalRequest(string Tool, string Summary, string? Description, string? Rule, bool Truncated = false)
{
    /// <summary>카드에 보일 명령 전문의 상한 (글자). 넘으면 잘라 보이고 허용을 막는다.</summary>
    public const int MaxSummaryChars = 20_000;

    /// <summary>훅 본문(JSON)을 읽는다. 모양이 아니면 null.</summary>
    public static ApprovalRequest? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "tool_name") is not { Length: > 0 } tool) return null;
            string summary;
            string? description = null;
            if (e.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
            {
                summary = Str(input, "command") ?? Str(input, "file_path") ?? input.GetRawText();
                description = Str(input, "description");
            }
            else summary = "";
            var truncated = summary.Length > MaxSummaryChars;
            if (truncated) summary = summary[..MaxSummaryChars] + "…";
            return new ApprovalRequest(tool, summary, description, truncated ? null : SuggestedRule(e), truncated);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// permission_suggestions에서 "항상 허용"으로 받을 규칙: addRules · allow인 첫 규칙이 명령 도구(Bash · PowerShell)이고 내용이 안전한 모양일 때만.
    /// destination(localSettings)은 따르지 않는다 — 돌려주면 작업 폴더 .claude/settings.local.json에 써지므로, 방에 저장해 다음 실행의 --allowedTools로 넣는다.
    /// </summary>
    private static string? SuggestedRule(JsonElement e)
    {
        if (!e.TryGetProperty("permission_suggestions", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in list.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || Str(s, "type") != "addRules" || Str(s, "behavior") != "allow") continue;
            if (!s.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) continue;
            foreach (var r in rules.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var rule = $"{Str(r, "toolName")}({Str(r, "ruleContent")})";
                if (ApprovalRules.IsAcceptable(rule)) return rule;
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public enum ApprovalChoice
{
    Allow,
    /// <summary>허용 + 제안 규칙을 방에 저장 (다음 실행부터 묻지 않음).</summary>
    AlwaysAllow,
    Deny,
}

/// <summary>훅에 돌려줄 답. Message는 거절 때 Claude가 보는 문구.</summary>
public sealed record ApprovalAnswer(bool Allow, string? Message)
{
    /// <summary>PermissionRequest 훅 응답 JSON (hooks 문서 모양).</summary>
    public string ToHookJson()
    {
        var decision = Allow
            ? (object)new { behavior = "allow" }
            : new { behavior = "deny", message = Message ?? "사용자가 거절했습니다." };
        return JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision } });
    }
}

/// <summary>"이 방에서 항상 허용" 규칙 모양 검사.</summary>
public static partial class ApprovalRules
{
    public const int MaxRuleChars = 500;

    /// <summary>
    /// "항상 허용"으로 받는 규칙: 명령 도구(Bash · PowerShell)의 정확한 명령 하나 — 와일드카드(*)가 없고, 줄바꿈이 없고, 상한 안.
    /// `Bash(python:*)` 같은 접두 규칙 하나면 그 방에서 사실상 모든 코드 실행이 카드 없이 통과해 받지 않는다 (/self-review 36e4e20e04 C4 · ab79e61df5 C1).
    /// 괄호 짝이 맞지 않으면(`x) Bash(y`처럼 --allowedTools 값이 둘로 읽힐 수 있는 모양) 받지 않는다.
    /// 인자 검사(위험 인자)에 걸릴 글자가 있으면 받지 않는다 — 저장하면 그 방의 모든 실행이 막힌다.
    /// </summary>
    public static bool IsAcceptable(string? rule)
    {
        if (rule is null || rule.Length > MaxRuleChars) return false;
        var m = RuleShape().Match(rule);
        if (!m.Success) return false;
        var content = m.Groups[2].Value;
        return content.Trim().Length > 0 && !content.Contains('*') && Balanced(content) && !Dangerous().IsMatch(rule);
    }

    private static bool Balanced(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    [GeneratedRegex(@"^(Bash|PowerShell)\(([^\r\n]+)\)$")]
    private static partial Regex RuleShape();

    [GeneratedRegex("dangerously|bypass", RegexOptions.IgnoreCase)]
    private static partial Regex Dangerous();
}

/// <summary>승인 기록 approvals.jsonl (데이터 폴더). 한 줄에 하나: 시각 · 방 · 도구 · 요약 · 결정.</summary>
public sealed class ApprovalLog(Func<string> dataFolder)
{
    /// <summary>
    /// 사람이 읽는 로컬 기록이라 한글 · 따옴표 · + 를 \u로 바꾸지 않는다. 이 파일은 HTML에 넣지 않으므로 HTML용 이스케이프가 필요 없다.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Lock _gate = new();

    public void Write(string roomId, ApprovalRequest request, string decision)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now,
            room = roomId,
            tool = request.Tool,
            summary = request.Summary.Length > 300 ? request.Summary[..300] + "…" : request.Summary,
            decision,
        }, Json);
        try
        {
            lock (_gate)
            {
                var dir = dataFolder();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "approvals.jsonl"), line + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 기록 실패가 승인을 막지 않게
    }
}

public static class ApprovalEndpoint
{
    /// <summary>훅 본문 상한 (바이트).</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// 훅이 부를 주소: 서버가 연 주소의 호스트를 127.0.0.1로 바꾼다 (localhost · 0.0.0.0 · + · * · [::]로 열어도 연결되는 루프백 주소로 —
    /// /self-review 36e4e20e04 C10). 포트 · 경로는 그대로.
    /// </summary>
    public static string LoopbackUrl(string address)
    {
        var m = System.Text.RegularExpressions.Regex.Match(address, @"^(https?://)(\[[^\]]*\]|[^:/]+)(.*)$");
        return m.Success ? m.Groups[1].Value + "127.0.0.1" + m.Groups[3].Value : address;
    }

    /// <summary>
    /// POST /approve?room=&amp;token= — claude의 PermissionRequest HTTP 훅이 부른다. 같은 출처 검사에서 빠지는 대신 실행마다 바뀌는 토큰으로 지킨다.
    /// 토큰이 그 방의 지금 실행과 맞지 않으면 본문을 읽기 전에 403 (훅 오류 → claude가 거절). 본문은 서버 단계에서 상한을 건다(chunked 포함).
    /// 답할 때까지 요청을 붙잡고, 답을 훅 응답 JSON으로 돌려준다.
    /// </summary>
    public static IEndpointConventionBuilder MapApprovals(this IEndpointRouteBuilder app) =>
        app.MapPost("/approve", async (HttpContext ctx, RoomHub hub) =>
        {
            var room = ctx.Request.Query["room"].ToString();
            var token = ctx.Request.Query["token"].ToString();
            // 토큰부터: 토큰 없는 요청이 큰 본문을 읽고 파싱하게 만들지 못하게 (/self-review 36e4e20e04 C7 · ab79e61df5 C6).
            if (!RoomStore.IsRoomId(room) || !hub.IsCurrentToken(room, token)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (ctx.Request.ContentLength is > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxBodyBytes;

            string body;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                body = await reader.ReadToEndAsync(ctx.RequestAborted);
            }
            catch (BadHttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            if (ApprovalRequest.Parse(body) is not { } request) return Results.BadRequest();

            var answer = await hub.RequestApprovalAsync(room, token, request, ctx.RequestAborted);
            return answer is null
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Content(answer.ToHookJson(), "application/json; charset=utf-8");
        });
}
