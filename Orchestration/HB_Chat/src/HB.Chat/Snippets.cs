using System.Text.Json;

namespace HB.Chat;

/// <summary>지시 상용구 하나: 이름(입력칸 ';' 목록에서 찾는 말) · 넣을 글.</summary>
public sealed record Snippet(string Name, string Text);

/// <summary>
/// 지시 상용구 (사용자 결정 "지시 상용구", 처음 목록은 비움): 데이터 폴더 snippets.json. 설정 화면에서 더하고 고치고 지운다.
/// 입력칸에 ';'로 시작하면 이름으로 찾고, 고르면 글이 입력칸에 들어간다 (보내지 않음). 이름은 대소문자 무시로 하나씩.
/// </summary>
public sealed class Snippets(Func<string> dataFolder)
{
    public const int MaxName = 40, MaxText = 4000, MaxCount = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    private string FilePath() => Path.Combine(dataFolder(), "snippets.json");

    /// <summary>목록 (저장한 차례). 없거나 못 읽으면 빈 목록.</summary>
    public IReadOnlyList<Snippet> All()
    {
        lock (_gate) return Load(out _);
    }

    /// <summary>
    /// 더하거나 고친다. original이 있으면 그 이름의 상용구를 바꾸고(이름도 바꿀 수 있음), 없으면 새로 더한다. 오류 문구(null이면 됨).
    /// 파일을 못 읽었으면 덮어쓰지 않는다.
    /// </summary>
    public string? Save(string? original, string? name, string? text)
    {
        name = name?.Trim() ?? "";
        text = (text ?? "").Replace("\r\n", "\n").TrimEnd();
        if (name.Length is 0 or > MaxName || name.Any(char.IsControl)) return $"이름은 1~{MaxName}자, 줄바꿈 없이 쓰세요.";
        if (text.Length is 0 or > MaxText) return $"글은 1~{MaxText}자로 쓰세요.";
        lock (_gate)
        {
            var list = Load(out var error);
            if (error is not null) return error;
            var at = string.IsNullOrWhiteSpace(original) ? -1 : list.FindIndex(s => s.Name.Equals(original.Trim(), StringComparison.OrdinalIgnoreCase));
            // 고치던 상용구가 그 사이 지워졌거나 이름이 바뀌었으면 새로 만들지 않는다 (다른 탭에서 지운 것이 되살아나지 않게, sub_self C7)
            if (!string.IsNullOrWhiteSpace(original) && at < 0) return $"'{original.Trim()}' 상용구가 그 사이 지워졌거나 이름이 바뀌었습니다 — 화면을 다시 여세요.";
            if (list.Where((s, i) => i != at).Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return $"'{name}' 이름이 이미 있습니다.";
            if (at >= 0) list[at] = new Snippet(name, text);
            else if (list.Count >= MaxCount) return $"상용구는 {MaxCount}개까지입니다.";
            else list.Add(new Snippet(name, text));
            return Write(list);
        }
    }

    /// <summary>이름으로 지운다. 없으면 그대로. 오류 문구(null이면 됨).</summary>
    public string? Remove(string? name)
    {
        lock (_gate)
        {
            var list = Load(out var error);
            if (error is not null) return error;
            if (list.RemoveAll(s => s.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0) return null;
            return Write(list);
        }
    }

    private string? Write(List<Snippet> list)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath(), JsonSerializer.Serialize(list, Json));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "저장하지 못했습니다: " + ex.Message; }
    }

    /// <summary>_gate 안에서만. 없으면 빈 목록, 깨졌으면 빈 목록과 오류 (저장을 막는다 — 사용자가 손으로 고친 파일을 덮지 않게).</summary>
    private List<Snippet> Load(out string? error)
    {
        error = null;
        var path = FilePath();
        if (!File.Exists(path)) return [];
        try
        {
            return [.. (JsonSerializer.Deserialize<List<Snippet?>>(File.ReadAllText(path), Json) ?? [])
                .Where(s => s is { Name.Length: > 0, Text.Length: > 0 }).Select(s => s!)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = "snippets.json을 읽지 못해 저장을 막았습니다 (덮어쓰지 않음): " + ex.Message;
            return [];
        }
    }
}
