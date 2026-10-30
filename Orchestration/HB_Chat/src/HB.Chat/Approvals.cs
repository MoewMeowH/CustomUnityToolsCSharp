using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// CLI가 stdio 호스트(앱)에 보낸 명령 승인 요청 하나 (control_request can_use_tool — 3차 0번 실측, 그 전 2-1은 PermissionRequest 훅 본문 · 같은 키).
/// Summary는 카드에 보일 명령 전문(명령 · 파일 경로 · 그 밖은 입력 JSON).
/// Truncated면 전문이 너무 길어 잘린 것 — 사용자가 다 보지 못한 명령이라 거절만 받는다 (/self-review 36e4e20e04 C5 · ab79e61df5 C5).
/// Extra는 명령 · 설명 밖의 입력(예 <c>run_in_background=true</c>) — 허용하면 입력 전체가 그대로 실행되므로 카드에 같이 보인다.
/// Risky면 그 입력 이름에 dangerously · bypass가 있다 (예 <c>dangerouslyDisableSandbox</c>) — 거절만 받는다 (/self-review 5e747ff851 C1).
/// </summary>
public sealed record ApprovalRequest(string Tool, string Summary, string? Description, bool Truncated = false)
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

    /// <summary>
    /// "이 턴 동안 묻지 않기"로 풀 수 있는 명령 카드 (사용자 결정: Bash · PowerShell만, 백그라운드 · 서브에이전트 명령 포함).
    /// 거절만 받는 카드와 꼭 묻는 명령(<see cref="ApprovalRules.MustAsk"/>)은 켜져 있어도 카드로 간다.
    /// </summary>
    public bool TurnAllowable => ClaudeArgs.CommandTools.Contains(Tool) && !DenyOnly && !ApprovalRules.MustAsk(Summary);

    /// <summary>서브에이전트가 낸 요청 (request의 agent_id — Claude 앱 기능 D). 카드에 "서브에이전트"로 보인다. 허용은 같다.</summary>
    public bool FromAgent { get; init; }

    /// <summary>백그라운드 실행 명령 (input의 run_in_background = true) — 답이 끝난 뒤에도 계속 돈다는 줄을 카드에 보인다.</summary>
    public bool Background { get; init; }

    /// <summary>run_mode 모델 선택 카드 — 허가하면 고른 교체 · 강도로 입력을 다시 쓴다. 아니면 null.</summary>
    public ModeCard? Mode { get; init; }

    /// <summary>웹 가져오기(WebFetch) 사이트 카드 — 읽기 이상 단계에서 허용을 받는다 (명령 카드는 명령 단계만).</summary>
    public bool Web { get; init; }

    /// <summary>명령 방의 작업 폴더 밖 파일 쓰기 카드 (<see cref="ForWrite"/>) — 화면이 "쓸 내용 미리보기"와 안내 줄을 보인다.</summary>
    public bool OutsideWrite { get; init; }

    /// <summary>작업 폴더 밖 쓰기 카드의 내용 미리보기 상한 (글자, 결정 "앞 2,000자 · 잘려도 허용").</summary>
    public const int WritePreviewChars = 2_000;

    /// <summary>쓰기 도구가 쓰는 경로 키 — NotebookEdit는 notebook_path, 그 밖은 file_path.</summary>
    private static string PathKey(string tool) => tool == "NotebookEdit" ? "notebook_path" : "file_path";

    /// <summary>
    /// 쓰기 도구 입력의 대상 경로 — 그 도구가 쓰는 키(<see cref="PathKey"/>)만 본다. 다른 경로 키가 같이 있으면 null: 검사 · 카드에 보인 경로와 실제로 쓰는
    /// 경로가 달라질 수 있다 (/cross-review a43e6f3e7b 자료 요청 1-N3). 없거나 읽지 못해도 null.
    /// </summary>
    public static string? WritePath(string tool, string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var key = PathKey(tool);
            var other = key == "file_path" ? "notebook_path" : "file_path";
            return doc.RootElement.TryGetProperty(other, out _) ? null : Str(doc.RootElement, key);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>미리보기에서 짧은 값으로 보는 길이 — 이보다 짧은 값(replace_all · edit_mode · cell_type …)은 잘리지 않게 먼저 보인다.</summary>
    private const int ShortValueChars = 200;

    /// <summary>긴 값을 보일 차례 — 실제로 써질 내용이 먼저 (old_string은 뒤).</summary>
    private static readonly string[] BodyOrder = ["content", "new_string", "new_source", "edits", "old_string"];

    /// <summary>
    /// 명령 방의 작업 폴더 밖 파일 쓰기 카드 (Write · Edit · MultiEdit · NotebookEdit): 요약 = 대상 경로, 추가 입력 = 쓸 내용(경로 밖의 입력) 미리보기.
    /// 짧은 값(동작 플래그)을 먼저 다 보이고, 긴 값은 써질 내용부터 키마다 2,000자를 나눠 앞부분을 보인다 — 잘린 값은 키 이름과 전체 길이를 적는다
    /// (/cross-review a43e6f3e7b — 앞 값이 길면 new_string · replace_all이 가려졌다). 명령 카드와 달리 잘려도 허용을 받는다 (결정 "앞 2,000자 · 잘려도 허용" —
    /// 위험은 대상 경로에 있고, 위험한 이름 · 폴더는 앞에서 카드 없이 거절). 위험한 이름의 입력(dangerously · bypass)이 있으면 거절만.
    /// 입력을 읽지 못하면 null.
    /// </summary>
    public static ApprovalRequest? ForWrite(string tool, string inputJson, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var values = new List<(string Name, string Text)>();
            var risky = false;
            foreach (var p in doc.RootElement.EnumerateObject().Where(p => p.Name != PathKey(tool)))
            {
                values.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));
                if (ApprovalRules.IsDangerousName(p.Name) && p.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) risky = true;
            }
            var lines = values.Where(v => v.Text.Length <= ShortValueChars).Select(v => $"{v.Name}={v.Text}").ToList();
            var bodies = values.Where(v => v.Text.Length > ShortValueChars)
                .OrderBy(v => Array.IndexOf(BodyOrder, v.Name) is var i and >= 0 ? i : BodyOrder.Length).ToList();
            var share = bodies.Count == 0 ? 0 : Math.Max(300, WritePreviewChars / bodies.Count);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var (name, text) in bodies)
                lines.Add(text.Length <= share
                    ? $"{name}={text}"
                    : $"{name}={text[..share]}\n… ({name} 앞 {share.ToString("N0", inv)}자만 보임 — 전체 {text.Length.ToString("N0", inv)}자)");
            var preview = string.Join("\n", lines);
            return new ApprovalRequest(tool, path, null)
            {
                Extra = preview.Length == 0 ? null : preview,
                Risky = risky,
                OutsideWrite = true,
            };
        }
        catch (JsonException) { return null; }
    }

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

    /// <summary>찾기 · 목록 도구의 요약 키 (command · file_path가 없을 때만 — 위 Parse).</summary>
    private static readonly HashSet<string> SearchKeys = new(StringComparer.Ordinal) { "pattern", "path", "notebook_path" };

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
                return new ApprovalRequest(tool, cut ? plan[..MaxSummaryChars] + "…" : plan, null, cut)
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
                // 찾기 · 목록 도구(Grep · Glob · LS · NotebookRead)는 command · file_path가 없다 — 입력 JSON 대신 "패턴 — 경로"로 (사용자 보고 2026-10-30:
                // 승인 기록에 JSON이 백슬래시 두 번으로 남았다). 그 키는 이때만 요약으로 본다 — 명령 카드의 추가 입력을 가리지 않게
                var searchShown = shown is null && (Str(input, "pattern") is not null || Str(input, "path") is not null || Str(input, "notebook_path") is not null);
                if (searchShown)
                    shown = Str(input, "notebook_path") ?? (Str(input, "pattern") is { } pattern
                        ? Str(input, "path") is { } where ? $"{pattern} — {where}" : pattern
                        : Str(input, "path"));
                summary = shown ?? input.GetRawText();
                description = Str(input, "description") ?? Str(e, "description");
                if (shown is not null)
                    foreach (var p in input.EnumerateObject().Where(p => !ShownKeys.Contains(p.Name) && !(searchShown && SearchKeys.Contains(p.Name))))
                    {
                        var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                        if (value.Length > MaxExtraValueChars) (value, truncated) = (value[..MaxExtraValueChars] + "…", true);
                        extras.Add($"{p.Name}={value}");
                        if (ApprovalRules.IsDangerousName(p.Name) && p.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) risky = true;
                    }
            }
            else summary = "";
            if (summary.Length > MaxSummaryChars) (summary, truncated) = (summary[..MaxSummaryChars] + "…", true);
            return new ApprovalRequest(tool, summary, description, truncated)
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

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public enum ApprovalChoice
{
    Allow,
    /// <summary>허용 + 이 턴이 끝날 때까지 명령을 묻지 않음 (저장하지 않음).</summary>
    AllowTurn,
    Deny,
}

/// <summary>승인 카드의 답. Message는 거절 때 Claude가 보는 문구.</summary>
public sealed record ApprovalAnswer(bool Allow, string? Message)
{
    /// <summary>모델 선택 카드에서 고른 값 (원래 에이전트 → 고른 값). 다른 카드 · 고르지 않음은 null.</summary>
    public IReadOnlyDictionary<string, ModeRowPick>? Picks { get; init; }

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

/// <summary>명령 카드의 입력 검사 (꼭 묻는 명령 · 위험한 이름).</summary>
public static partial class ApprovalRules
{
    /// <summary>
    /// "이 턴 동안 묻지 않기"가 켜져 있어도 카드로 묻는 명령 (사용자 결정 "짧은 목록 유지" · "넓히되 일상 경로는 뺀"): git push · clean · reset --hard, 재귀 삭제,
    /// 숨긴 실행(-EncodedCommand · iex), 지시 · 설정 · 다음 실행으로 이어지는 파일을 건드리는 모양. 편집 금지 목록의 이 앱 폴더 · 스크립트 확장자는 뺐다 — 평소 명령마다 걸려 단추가 소용없어진다.
    /// 글자 검사라 스크립트 · 변수로 비껴갈 수 있다 — 실수를 막는 장치이지 보안 경계가 아니다. 애매하면 카드 쪽으로 (읽기만 해도 걸릴 수 있음, 시간 초과도).
    /// </summary>
    public static bool MustAsk(string command)
    {
        try { return MustAskPattern().IsMatch(command); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>입력 이름이 우회 · 위험 인자 모양인지 (예 dangerouslyDisableSandbox).</summary>
    public static bool IsDangerousName(string name) => Dangerous().IsMatch(name);

    [GeneratedRegex(@"\bgit\b[^\n;|&]*\b(?:push|clean|reset\b[^\n;|&]*--hard)\b" +
        @"|\brm\b[^\n;|&]*\s(?:-[a-z]*r|--recursive)" +
        @"|\b(?:Remove-Item|ri|del|erase|rd|rmdir)\b[^\n;|&]*(?:\s-r[a-z]*\b|/s\b)" +
        @"|\s-r[a-z]*\b[^\n;&]*\|[^\n]*\b(?:Remove-Item|ri|rm|del|erase)\b" +
        @"|\bfind\b[^\n;|&]*\s-delete\b|\bDirectory\]?::Delete\b|\bshutil\.rmtree\b" +
        @"|\s-(?:ec|enc|encodedcommand)\b|\biex\b|\bInvoke-Expression\b" +
        @"|\.claude\b|\.mcp[^\s'""]*\.json\b|\bCLAUDE(?:\.local)?\.md\b|\b(?:PROJECT|MISTAKES|REVIEW_GUIDELINES)\.md\b" +
        @"|\bappsettings[^\s'""]*\.json\b|\bPublish_Output\b|\.git[\\/]hooks\b|\.gitconfig\b|\.vscode\b" +
        @"|[\\/]Programs[\\/]Startup\b|shell:startup", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex MustAskPattern();

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
        Write(roomId, new ApprovalRequest(tool, "", null), text);

    private static string Cut(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
