using System.Text.Json;

namespace HB.Orchestration.Shared;

/// <summary>
/// 도구 칩에 보일 짧은 대상 (HB_Chat 3차 b에서 옮김 — 4차 C3: 실행 중 칩(stream-json)과 끝난 턴 칩(세션 기록)이 같은 글을 쓰게).
/// 파일 도구는 파일 이름(경로는 빼고), 명령은 첫 줄, 검색은 패턴, run_mode는 모드, 할 일 도구는 제목 · 번호 → 상태. 모르는 도구는 null.
/// 한 줄 · <see cref="MaxChars"/>자로 자른다 (화면에만 — 세션 기록에 이미 있는 글).
/// </summary>
public static class ToolTargets
{
    public const int MaxChars = 60;

    public static string? Of(string name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var tool = name.StartsWith("mcp__", StringComparison.Ordinal) ? name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : name;
        var text = tool switch
        {
            "Read" or "Edit" or "Write" or "NotebookEdit" => Str(input, "file_path") is { } p ? FileName(p) : null,
            "Bash" or "PowerShell" => Str(input, "command"),
            "Grep" or "Glob" => Str(input, "pattern"),
            "run_mode" => Str(input, "mode"),
            "TaskCreate" => Str(input, "subject"),
            "TaskUpdate" => Str(input, "taskId") is { } id ? "#" + id + (Str(input, "status") is { } s ? " → " + s : "") : null,
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim();
        if (line.Length <= MaxChars) return line;
        var end = char.IsHighSurrogate(line[MaxChars - 1]) ? MaxChars - 1 : MaxChars;
        return line[..end] + "…";
    }

    private static string FileName(string path)
    {
        var cut = path.Replace('\\', '/').TrimEnd('/');
        return cut[(cut.LastIndexOf('/') + 1)..];
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
