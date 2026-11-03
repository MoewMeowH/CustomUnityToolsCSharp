namespace HB.Chat;

/// <summary>
/// 설정 탭 프로필 줄에 보일 서브에이전트 종류: Claude Code 기본 종류 + 작업 폴더 · 사용자 폴더의 <c>.claude/agents/*.md</c> 이름.
/// 이름은 정의 파일 앞머리(frontmatter)의 <c>name:</c> (= subagent_type), 없으면 파일 이름(확장자 뺌). 앞뒤 공백은 걷는다 (프로필 키와 같게).
/// 플러그인 에이전트 · 기록에만 나온 종류는 여기 없다. 읽기만 하고 실패하면 건너뛴다.
/// </summary>
public static class SubagentTypes
{
    public static readonly IReadOnlyList<string> BuiltIn = ["general-purpose", "Explore", "Plan"];

    /// <summary>폴더 하나에서 볼 정의 파일 수 · 앞머리에서 볼 줄 수 상한.</summary>
    public const int MaxPerFolder = 50, MaxHeadLines = 30;

    public static IReadOnlyList<string> Known(IEnumerable<string> workspacePaths, string? userHome)
    {
        var names = new SortedSet<string>(BuiltIn, StringComparer.Ordinal);
        foreach (var root in workspacePaths.Append(userHome).OfType<string>())
        {
            try
            {
                var dir = Path.Combine(root, ".claude", "agents");
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.md").Take(MaxPerFolder))
                    if ((NameOf(file) ?? Path.GetFileNameWithoutExtension(file)).Trim() is { Length: > 0 } name && ProfileStore.SubKey(name) is not null)
                        names.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return [.. names];
    }

    /// <summary>앞머리(첫 줄 "---" ~ 다음 "---")의 <c>name:</c> 값 (따옴표 걷음). 없거나 못 읽으면 null.</summary>
    internal static string? NameOf(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            if (reader.ReadLine()?.Trim() != "---") return null;
            for (var i = 0; i < MaxHeadLines && reader.ReadLine() is { } line; i++)
            {
                if (line.Trim() == "---") return null;
                if (line.StartsWith("name:", StringComparison.Ordinal))
                    return line["name:".Length..].Trim().Trim('"', '\'') is { Length: > 0 } n ? n : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
