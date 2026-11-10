using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace HB.Chat;

/// <summary>새 방 하나의 값: 작업 폴더 이름 · 권한 단계 · 모델 · 제목(없으면 null) · 첫 말(인계 글).</summary>
public sealed record RoomOpenSpec(string Workspace, PermissionLevel Level, string Model, string? Title, string Prompt);

/// <summary>
/// 새 방 열기 카드 (지휘자 도구 room_open): 지휘자가 낸 값을 미리 채우고, 사용자가 작업 폴더 · 단계 · 모델 · 제목 · 첫 말을 고쳐 허용한다.
/// Note는 채우며 바꾼 것 (여는 방보다 높은 단계를 낮춤 · 모르는 폴더 · 모델). FromLevel은 여는 방 단계 — 화면이 더 높은 단계를 고르면 경고 줄.
/// </summary>
public sealed record RoomOpenCard(string Workspace, string Level, string Model, string? Title, string Prompt,
    IReadOnlyList<string> Workspaces, IReadOnlyList<string> Models, IReadOnlyList<string> Levels, string FromLevel, string? Note)
{
    /// <summary>여는 방 작업 폴더 — 화면이 다른 폴더를 고르면(또는 지휘자가 냈으면) 경고 줄.</summary>
    public string? FromWorkspace { get; init; }
}

/// <summary>새 방 열기 도구 · 카드의 입력 모양과 검사 (새 방 화면 Index.OnPostCreate와 같은 규칙).</summary>
public static class RoomOpen
{
    public const string ToolName = "room_open";
    public const string Tool = "mcp__" + TerminalMcp.Server + "__" + ToolName;
    public const int MaxTitleChars = 200;

    /// <summary>도구 · 카드에서 쓰는 단계 이름 (소문자).</summary>
    public static string LevelName(PermissionLevel level) => level switch
    {
        PermissionLevel.Read => "read",
        PermissionLevel.Command => "command",
        PermissionLevel.App => "app",
        _ => level.ToString().ToLowerInvariant(),
    };

    public static PermissionLevel? ParseLevel(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "read" => PermissionLevel.Read,
        "command" => PermissionLevel.Command,
        "app" => PermissionLevel.App,
        _ => null,
    };

    public static readonly IReadOnlyList<string> LevelNames = ["read", "command", "app"];

    /// <summary>도구 입력 · 카드에서 고른 값 JSON → 값. 글자 칸이 아니거나 단계 이름을 모르면 null (검사는 <see cref="Check"/>).</summary>
    public static RoomOpenSpec? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            string? S(string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            if (S("workspace") is not { } workspace || S("model") is not { } model || S("prompt") is not { } prompt || ParseLevel(S("level")) is not { } level)
                return null;
            var title = S("title");
            return new RoomOpenSpec(workspace.Trim(), level, model.Trim(), string.IsNullOrWhiteSpace(title) ? null : title.Trim(), prompt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>값 → 도구 입력 JSON (허용 때 updatedInput — 카드에서 고친 값).</summary>
    public static string Input(RoomOpenSpec s) => new JsonObject
    {
        ["workspace"] = s.Workspace,
        ["level"] = LevelName(s.Level),
        ["model"] = s.Model,
        ["title"] = s.Title ?? "", // 도구 인자는 글자 — null을 넣지 않는다
        ["prompt"] = s.Prompt,
    }.ToJsonString();

    /// <summary>새 방 화면과 같은 검사. 오류 문구, 통과면 null.</summary>
    public static string? Check(RoomOpenSpec s, ChatOptions o) =>
        o.WorkspacePath(s.Workspace) is null ? "허용 목록에 없는 작업 폴더입니다."
        : !o.ModelList.Contains(s.Model, StringComparer.Ordinal) ? "허용 목록에 없는 모델입니다."
        : !Enum.IsDefined(s.Level) ? "알 수 없는 권한 단계입니다."
        : s.Title is { Length: > MaxTitleChars } ? $"제목은 {MaxTitleChars}자까지입니다."
        : s.Title is { } t && t.Any(char.IsControl) ? "제목에 줄바꿈 · 제어 문자를 넣을 수 없습니다."
        : string.IsNullOrWhiteSpace(s.Prompt) ? "첫 말이 비었습니다."
        : s.Prompt.Length > RoomHub.MaxPromptChars ? $"첫 말이 너무 깁니다 ({s.Prompt.Length:N0}자, 상한 {RoomHub.MaxPromptChars:N0}자)."
        : PromptProblem(s.Prompt);

    /// <summary>
    /// 새 방 첫 말은 그 방에서 사용자 말로 읽힌다 — 사용자 화면만 쓰는 모양은 받지 않는다 (/self-review 4f5fd81b8e C4 · 지휘자 O3):
    /// "/"로 시작(슬래시 명령으로 돈다), 첫 줄이 요청 창 머리 줄, "[첨부] " 줄(사용자가 올린 파일로 읽힌다).
    /// </summary>
    private static string? PromptProblem(string prompt)
    {
        var text = prompt.TrimStart();
        if (text.StartsWith('/')) return "첫 말이 '/'로 시작하면 새 방에서 슬래시 명령으로 돕니다 — 앞에 글을 붙이세요.";
        if (text.StartsWith(ModeCard.HeaderMarker, StringComparison.Ordinal)) return $"첫 말이 요청 창 머리 줄({ModeCard.HeaderMarker})로 시작할 수 없습니다.";
        if (prompt.Replace("\r\n", "\n").Split('\n').Any(l => l.StartsWith(Attachments.Marker, StringComparison.Ordinal)))
            return $"첫 말에 첨부 줄({Attachments.Marker.Trim()})을 넣을 수 없습니다 — 새 방에서 사용자가 올린 파일로 읽힙니다. 경로는 글로 적으세요.";
        return null;
    }

    /// <summary>
    /// 카드에 미리 채울 값. 지휘자가 여는 방보다 높은 단계를 냈으면 여는 방 단계로 낮춘다 (결정: 올리는 것은 사용자가 카드에서 직접 고를 때만).
    /// 모르는 작업 폴더 · 모델 · 단계는 여는 방 것으로. 입력을 아예 못 읽으면 null.
    /// </summary>
    public static RoomOpenCard? Build(string inputJson, Room from, ChatOptions o)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(inputJson) as JsonObject; }
        catch (JsonException) { obj = null; }
        if (obj is null) return null;
        string? S(string key) => obj[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var notes = new List<string>();
        var workspaces = o.AllWorkspaces.Select(w => w.Name).ToList();
        var workspace = S("workspace")?.Trim();
        if (workspace is null || o.WorkspacePath(workspace) is null)
        {
            if (!string.IsNullOrEmpty(workspace)) notes.Add($"작업 폴더 '{workspace}'는 목록에 없어 이 방 폴더로 채웠습니다.");
            workspace = from.Workspace;
        }
        var models = o.ModelList.ToList();
        var model = S("model")?.Trim();
        if (model is null || !models.Contains(model, StringComparer.Ordinal))
        {
            if (!string.IsNullOrEmpty(model)) notes.Add($"모델 '{model}'은 목록에 없어 이 방 모델로 채웠습니다.");
            model = models.Contains(from.Model, StringComparer.Ordinal) ? from.Model : models[0];
        }
        var level = ParseLevel(S("level")) ?? from.Level;
        if (level > from.Level)
        {
            notes.Add($"지휘자가 이 방보다 높은 단계({LevelName(level)})를 냈습니다 — 이 방 단계로 낮춰 채웠습니다.");
            level = from.Level;
        }
        var title = S("title")?.Trim();
        return new RoomOpenCard(workspace, LevelName(level), model, string.IsNullOrEmpty(title) ? null : title, S("prompt") ?? "",
            workspaces, models, LevelNames, LevelName(from.Level), notes.Count == 0 ? null : string.Join(" ", notes)) { FromWorkspace = from.Workspace };
    }
}

/// <summary>
/// 지휘자 도구 room_open. 어느 방인지는 요청 헤더의 실행 토큰으로 (인자로 받지 않는다). 카드에서 허용한 값(표)과 같아야 연다.
/// 실패는 '['로 시작하는 한 줄.
/// </summary>
[McpServerToolType]
public sealed class RoomTools(Terminals terminals, RoomStore store, RoomHub hub, IOptions<ChatOptions> options, IHttpContextAccessor http)
{
    [McpServerTool(Name = RoomOpen.ToolName)]
    [Description("새 채팅방을 열어 첫 말로 바로 시작한다 — 다른 일 · 긴 작업을 새 세션으로 넘길 때. 새 방은 이 대화를 모르므로 prompt에 인계할 내용" +
                 "(목적 · 지금까지 결정 · 파일 · 다음 할 일)을 모두 쓴다. 사용자 승인 카드를 거치고, 사용자가 카드에서 값을 고칠 수 있다.")]
    public string Open(
        [Description("작업 폴더 이름 (방 목록의 작업 폴더). 보통 이 방과 같은 것")] string workspace,
        [Description("권한 단계: read · command · app — 이 방보다 높게 낼 수 없다")] string level,
        [Description("모델 (예: opus · sonnet)")] string model,
        [Description("새 방에 보낼 첫 말 — 인계 글 전체")] string prompt,
        [Description("새 방 제목 (200자까지, 비우면 첫 말로)")] string? title = null)
    {
        if (terminals.RoomFor(http.HttpContext?.Request.Headers[TerminalMcp.TokenHeader]) is not { } fromId || store.Get(fromId) is not { } from)
            return "[거절] 채팅방 실행에서만 쓸 수 있습니다.";
        if (from.GroupId is not null) return "[거절] 단톡방 구성원은 새 방을 열 수 없습니다.";
        if (RoomOpen.ParseLevel(level) is not { } lv) return "[거절] 단계는 read · command · app 중 하나입니다.";
        var spec = new RoomOpenSpec(workspace.Trim(), lv, model.Trim(), string.IsNullOrWhiteSpace(title) ? null : title.Trim(), prompt);
        if (!hub.TakeRoomOpen(from.Id, spec)) return "[거절] 승인 카드에서 허용되지 않은 방 열기입니다.";
        if (RoomOpen.Check(spec, options.Value) is { } bad) return "[거절] " + bad;
        Room opened;
        try
        {
            opened = store.Create(spec.Workspace, spec.Level, spec.Model, spec.Title, effort: from.Effort);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return "[실패] 방을 만들지 못했습니다: " + ex.Message;
        }
        hub.Touch(opened.Id); // 열린 화면들의 방 목록에 보이게
        var name = opened.Title ?? opened.Id;
        hub.NoteRoomOpened(from.Id, opened.Id, name);
        return hub.Send(opened.Id, spec.Prompt) is { } why
            ? $"[실패] 방 '{name}'(id {opened.Id})은 만들었지만 첫 말을 보내지 못했습니다: {why}"
            : $"열었음: '{name}' (방 id {opened.Id}) — 첫 말로 시작했습니다. 사용자는 이 방 기록의 [열기]로 그 방에 갑니다.";
    }
}
