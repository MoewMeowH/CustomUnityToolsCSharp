using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// CLI가 stdio 호스트(앱)에 보낸 명령 승인 요청 하나 (control_request can_use_tool — 3차 0번 실측, 그 전 2-1은 PermissionRequest 훅 본문 · 같은 키).
/// Summary는 카드에 보일 명령 전문(명령 · 파일 경로 · 그 밖은 입력 JSON). Rule은 CLI가 제안한 규칙 중 이 앱이 "항상 허용"으로 받는 것.
/// Truncated면 전문이 너무 길어 잘린 것 — 사용자가 다 보지 못한 명령이라 거절만 받는다 (/self-review 36e4e20e04 C5 · ab79e61df5 C5).
/// Extra는 명령 · 설명 밖의 입력(예 <c>run_in_background=true</c>) — 허용하면 입력 전체가 그대로 실행되므로 카드에 같이 보인다.
/// Risky면 그 입력 이름에 dangerously · bypass가 있다 (예 <c>dangerouslyDisableSandbox</c>) — 거절만 받는다 (/self-review 5e747ff851 C1).
/// </summary>
public sealed record ApprovalRequest(string Tool, string Summary, string? Description, string? Rule, bool Truncated = false)
{
    /// <summary>카드에 보일 명령 전문의 상한 (글자). 넘으면 잘라 보이고 허용을 막는다.</summary>
    public const int MaxSummaryChars = 20_000;

    /// <summary>추가 입력 값 하나를 보일 상한 (글자). 넘으면 잘리고 거절만 받는다 (다 보지 못한 입력).</summary>
    public const int MaxExtraValueChars = 500;

    public string? Extra { get; init; }
    public bool Risky { get; init; }

    /// <summary>
    /// 계획 승인 (Claude 앱 기능 C — ExitPlanMode): Summary가 계획 글. 계획이 길어 잘려도 허용은 받는다 — 허용은 명령 실행이 아니라
    /// "이 계획대로 진행"이고, 그 뒤 명령은 명령 카드로 다시 묻는다.
    /// </summary>
    public bool IsPlan { get; init; }

    /// <summary>카드에 보이는 것이 허용될 입력 전부가 아니어서 거절만 받는지.</summary>
    public bool DenyOnly => !IsPlan && (Truncated || Risky);

    /// <summary>서브에이전트가 낸 요청 (request의 agent_id — Claude 앱 기능 D). 카드에 "서브에이전트"로 보인다. 허용 · 규칙은 같다.</summary>
    public bool FromAgent { get; init; }

    /// <summary>백그라운드 실행 명령 (input의 run_in_background = true) — 답이 끝난 뒤에도 계속 돈다는 줄을 카드에 보인다 (계획서 2026-10-29_HB_Chat-백그라운드-명령).</summary>
    public bool Background { get; init; }

    /// <summary>계획 모드에서 지휘자가 계획 승인을 묻는 도구.</summary>
    public const string PlanTool = "ExitPlanMode";

    /// <summary>계획 승인 입력 중 앱이 돌려주는 키 (2.1.312 실측 입력 그대로). 그 밖의 키는 카드에 보이지 않으므로 허용하지 않는다.</summary>
    private static readonly HashSet<string> PlanKeys = new(StringComparer.Ordinal) { "plan", "planFilePath" };

    /// <summary>
    /// 계획 승인 허용 때 돌려줄 입력: 받은 입력에서 plan · planFilePath(글자일 때)만 새로 담는다 — "본 것 = 허용되는 것" (5e747ff851 C1),
    /// 보이지 않는 키(예 명령 권한을 적는 항목)가 같이 승인되지 않게 (/self-review security d3dd55b237 C1). 읽지 못하면 빈 객체.
    /// </summary>
    public static string PlanInput(string inputJson)
    {
        var clean = new JsonObject();
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var key in PlanKeys)
                    if (Str(doc.RootElement, key) is { } value) clean[key] = value;
        }
        catch (JsonException)
        {
        }
        return clean.ToJsonString();
    }

    /// <summary>요약으로 보이는 키 (명령이면 command · description, 파일이면 file_path · description).</summary>
    private static readonly HashSet<string> ShownKeys = new(StringComparer.Ordinal) { "command", "description", "file_path" };

    /// <summary>
    /// 승인 요청 원문(control_request의 request 객체: tool_name · input · description · permission_suggestions)을 읽는다. 모양이 아니면 null.
    /// </summary>
    public static ApprovalRequest? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Str(e, "tool_name") is not { Length: > 0 } tool) return null;
            // 계획 승인: 입력 plan(계획 글)만 보인다 — 2.1.312 실측 input {plan, planFilePath}
            if (tool == PlanTool)
            {
                var hasInput = e.TryGetProperty("input", out var planInput) && planInput.ValueKind == JsonValueKind.Object;
                var plan = hasInput ? Str(planInput, "plan") ?? "" : "";
                var cut = plan.Length > MaxSummaryChars;
                // 계획 글 · 파일 경로 밖의 입력은 허용 답에서 뺀다(PlanInput) — 카드에도 그 이름을 보인다 (/self-review security d3dd55b237 C1)
                var others = hasInput ? planInput.EnumerateObject().Select(p => p.Name).Where(n => !PlanKeys.Contains(n)).ToList() : [];
                return new ApprovalRequest(tool, cut ? plan[..MaxSummaryChars] + "…" : plan, null, null, cut)
                {
                    IsPlan = true,
                    Extra = others.Count == 0 ? null : string.Join(", ", others),
                };
            }
            string summary;
            string? description = null;
            var extras = new List<string>();
            var risky = false;
            var truncated = false;
            if (e.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
            {
                var shown = Str(input, "command") ?? Str(input, "file_path");
                summary = shown ?? input.GetRawText();
                description = Str(input, "description") ?? Str(e, "description");
                if (shown is not null)
                    foreach (var p in input.EnumerateObject().Where(p => !ShownKeys.Contains(p.Name)))
                    {
                        var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                        if (value.Length > MaxExtraValueChars) (value, truncated) = (value[..MaxExtraValueChars] + "…", true);
                        extras.Add($"{p.Name}={value}");
                        if (ApprovalRules.IsDangerousName(p.Name) && p.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) risky = true;
                    }
            }
            else summary = "";
            if (summary.Length > MaxSummaryChars) (summary, truncated) = (summary[..MaxSummaryChars] + "…", true);
            return new ApprovalRequest(tool, summary, description, truncated || risky ? null : SuggestedRule(e), truncated)
            {
                Extra = extras.Count == 0 ? null : string.Join(", ", extras),
                Risky = risky,
                FromAgent = Str(e, "agent_id") is { Length: > 0 },
                Background = e.TryGetProperty("input", out var bg) && bg.ValueKind == JsonValueKind.Object
                    && bg.TryGetProperty("run_in_background", out var flag) && flag.ValueKind == JsonValueKind.True,
            };
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

/// <summary>승인 카드의 답. Message는 거절 때 Claude가 보는 문구.</summary>
public sealed record ApprovalAnswer(bool Allow, string? Message)
{
    /// <summary>호스트 답 JSON (<see cref="PermissionDecision"/>). 허용이면 입력을 그대로 돌려준다.</summary>
    public string ToDecisionJson(string inputJson) =>
        Allow ? PermissionDecision.Allow(inputJson) : PermissionDecision.Deny(Message ?? "사용자가 거절했습니다.");
}

/// <summary>
/// stdio 호스트의 승인 답 (control_response의 response — SDK canUseTool과 같은 모양, 3차 0번 실측):
/// 허용 <c>{"behavior":"allow","updatedInput":{…}}</c>, 거절 <c>{"behavior":"deny","message":"…"}</c>.
/// </summary>
public static class PermissionDecision
{
    /// <summary>허용. updatedInput은 받은 입력 그대로 (질문 카드는 answers를 더한 입력 — <see cref="QuestionRequest.WithAnswers"/>).</summary>
    public static string Allow(string updatedInputJson) =>
        new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(updatedInputJson) }.ToJsonString();

    public static string Deny(string message) => JsonSerializer.Serialize(new { behavior = "deny", message });

    /// <summary>
    /// 계획 승인 허용 (Claude 앱 기능 C): 입력 그대로 + 세션 권한 모드를 그 방 단계의 원래 모드로 (편집 acceptEdits · 명령 default).
    /// 2.1.312 실측: setMode가 없으면 허용 뒤 default가 되어 편집 방의 Edit도 승인을 물어 와 앱이 거절했다.
    /// </summary>
    public static string AllowPlan(string updatedInputJson, string mode) => new JsonObject
    {
        ["behavior"] = "allow",
        ["updatedInput"] = JsonNode.Parse(updatedInputJson),
        ["updatedPermissions"] = new JsonArray(new JsonObject { ["type"] = "setMode", ["mode"] = mode, ["destination"] = "session" }),
    }.ToJsonString();
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

    /// <summary>입력 이름이 우회 · 위험 인자 모양인지 (예 dangerouslyDisableSandbox).</summary>
    public static bool IsDangerousName(string name) => Dangerous().IsMatch(name);

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
            summary = Cut(request.Summary),
            // 질문 카드의 답(기타 글 포함)도 300자까지만 — 세션 기록에 원문이 있다 (/self-review 5e747ff851 C2 · 지휘자 C4)
            decision = Cut(decision),
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

    /// <summary>방 기록 줄(정리 표시 등)을 같은 파일에 남긴다 — 기록 화면이 시각으로 제자리에 그린다 (RoomNotes).</summary>
    public void Note(string roomId, string tool, string text) =>
        Write(roomId, new ApprovalRequest(tool, "", null, null), text);

    private static string Cut(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
