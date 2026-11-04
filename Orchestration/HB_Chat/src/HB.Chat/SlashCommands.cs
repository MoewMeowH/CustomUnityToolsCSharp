namespace HB.Chat;

/// <summary>앱이 CLI로 보내지 않고 직접 처리하는 기본 명령의 종류.</summary>
public enum SlashKind { NewSession, Model, Effort }

/// <summary>가로챈 명령 하나: 종류 · 쓴 이름(/clear 등) · 인자(없으면 null).</summary>
public sealed record SlashCommand(SlashKind Kind, string Name, string? Arg);

/// <summary>
/// 채팅창 기본 명령 중 앱이 처리하는 것 (2.1.316 stream-json 실측):
/// /clear · /reset · /new는 CLI가 새 세션 id로 넘어가 방 세션과 어긋나 멈추고, /model · /effort는 "이 세션에서만" 바뀌어 방 설정과 어긋난다.
/// 그래서 앞의 셋은 같은 설정의 새 방으로, 뒤의 둘은 방 설정 바꾸기로 처리한다. /compact처럼 같은 세션에 머무는 명령은 그대로 CLI로 간다.
/// </summary>
public static class SlashCommands
{
    private static readonly string[] NewSessionNames = ["clear", "reset", "new"];

    /// <summary>
    /// 말의 첫 낱말(앞 공백 걷음)이 처리할 명령이면 그 명령, 아니면 null. 나머지는 Arg(걷어서, 비면 null) — 여러 줄이어도 명령으로 본다
    /// (CLI로 보내면 그 명령으로 처리될 수 있어서). 새 세션 명령은 인자를 쓰지 않는다.
    /// </summary>
    public static SlashCommand? Classify(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t[0] != '/') return null;
        var end = 1;
        while (end < t.Length && !char.IsWhiteSpace(t[end])) end++;
        var name = t[1..end];
        var rest = t[end..].Trim();
        var arg = rest.Length == 0 ? null : rest;
        if (NewSessionNames.Contains(name, StringComparer.Ordinal)) return new SlashCommand(SlashKind.NewSession, name, arg);
        return name switch
        {
            "model" => new SlashCommand(SlashKind.Model, name, arg),
            "effort" => new SlashCommand(SlashKind.Effort, name, arg),
            _ => null,
        };
    }

    /// <summary>모델 이름을 허용 목록 값으로 (대소문자 무시로 같은 것만). 없으면 null.</summary>
    public static string? ResolveModel(string? arg, IReadOnlyList<string> allowed) =>
        string.IsNullOrWhiteSpace(arg) ? null : allowed.FirstOrDefault(m => string.Equals(m, arg.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>인자 없는 /model 안내 · 목록에 없는 이름 거절 글.</summary>
    public static string ModelHelp(string current, IReadOnlyList<string> allowed, string? bad = null) =>
        (bad is null ? "" : $"'{bad}'는 고를 수 있는 모델이 아닙니다. ") + $"지금 모델: {current} · 고를 수 있는 것: {string.Join(", ", allowed)} — /model 이름";

    /// <summary>인자 없는 /effort 안내 · 목록에 없는 값 거절 글. "default"는 기본(인자 없음)으로 돌린다.</summary>
    public static string EffortHelp(string? current, string? bad = null) =>
        (bad is null ? "" : $"'{bad}'는 추론 강도가 아닙니다. ") + $"지금 강도: {RoomEffort.Name(current)} · 고를 수 있는 것: {string.Join(", ", RoomEffort.Levels)}, default — /effort 값";

    /// <summary>/effort 인자를 방 값으로: "default"는 null(CLI 기본), 목록 값은 그대로. 그 밖은 Error.</summary>
    public static (string? Value, string? Error) CleanEffort(string arg) =>
        string.Equals(arg.Trim(), "default", StringComparison.OrdinalIgnoreCase) ? (null, null) : RoomEffort.Clean(arg.Trim().ToLowerInvariant());

    /// <summary>새 세션 명령으로 만든 방이 원래 방에서 이어받는 설정 (대화 · 제목 · 첨부는 이어받지 않는다).</summary>
    public static Room CarryOver(Room fresh, Room from) => fresh with
    {
        StickerPack = from.StickerPack,
        McpExtra = from.McpExtra,
        McpOff = from.McpOff,
        UnityProject = from.UnityProject,
        AskRunMode = from.AskRunMode,
    };
}
