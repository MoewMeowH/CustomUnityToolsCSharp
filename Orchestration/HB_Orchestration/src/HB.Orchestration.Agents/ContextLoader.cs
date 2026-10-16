using System.Text;

namespace HB.Orchestration.Agents;

/// <summary>
/// run_mode의 context 항목 하나: 프로젝트 루트 기준 파일의 줄 범위 (1부터, 끝 줄 포함).
/// Path가 "@이름/상대경로"이고 이름이 Context.ExtraRoots에 등록돼 있으면 그 추가 루트 기준이다.
/// </summary>
public sealed record ContextRange(string Path, int StartLine, int EndLine)
{
    public override string ToString() => $"{Path}:{StartLine}-{EndLine}";
}

/// <summary>
/// Claude가 이미 읽은 코드를 input에 다시 쓰지 않도록, 경로·줄 범위만 받아 서버가 디스크에서 읽어 붙인다.
/// 외부 호출 전에 모두 검증하고, 하나라도 문제가 있으면 전체를 거절한다 (조용히 자르거나 건너뛰지 않음).
/// 경로 규칙은 <see cref="SafePath"/> (save_result와 공용).
/// </summary>
public static class ContextLoader
{
    /// <param name="Roots">이번 호출에서 실제로 쓴 추가 루트 (설정의 이름 → 절대 경로). '@'를 쓰지 않았으면 null.</param>
    public sealed record Result(string Text, IReadOnlyList<string> Sources, IReadOnlyDictionary<string, string>? Roots = null);

    /// <returns>붙일 텍스트와 출처 목록, 또는 오류 메시지. context가 비었으면 빈 결과.</returns>
    public static (Result? Result, string? Error) Load(
        IReadOnlyList<ContextRange>? ranges, ContextOptions options, string rootDir, int maxChars, CancellationToken ct = default)
    {
        if (ranges is null || ranges.Count == 0) return (new Result("", []), null);

        if (options.MaxRanges > 0 && ranges.Count > options.MaxRanges)
            return (null, $"범위가 {ranges.Count}개 / 상한 {options.MaxRanges}개. 필요한 범위만 남기세요.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("## 첨부 코드 (서버가 디스크에서 읽음, 줄 번호 포함)");
        var sources = new List<string>(ranges.Count);
        Dictionary<string, string>? usedRoots = null;

        foreach (var range in ranges)
        {
            ct.ThrowIfCancellationRequested();
            if (range is null) return (null, "context에 빈 항목이 있습니다.");

            if (range.StartLine < 1 || range.EndLine < range.StartLine)
                return (null, $"'{range}': 줄 범위가 잘못됐습니다 (1 ≤ startLine ≤ endLine).");

            // "@이름/…"이고 등록된 이름이면 그 추가 루트, 아니면 메인 루트 (ExtraRoots가 비어 있으면 항상 기존 동작).
            // 등록 값 검사는 여기서, 이번 호출이 참조한 루트만 한다 (안 쓰는 항목이 깨져 있어도 다른 호출을 막지 않음).
            var shown = range;
            var relative = range.Path;
            var rangeRoot = root;
            if (TrySplitRootPrefix(range.Path, out var name, out var rest) && options.TryGetExtraRoot(name, out var key, out var rootPath))
            {
                if (ExtraRootProblem(key, rootPath) is { } problem) return (null, $"'{range.Path}': ExtraRoots '{key}' — {problem}");
                rangeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
                relative = rest;
                // 에이전트에게는 이름만 보낸다 (로컬 절대 경로는 외부로 보내지 않음).
                shown = range with { Path = $"@{key}/{rest.Replace('\\', '/')}" };
                (usedRoots ??= new(StringComparer.OrdinalIgnoreCase))[key] = rangeRoot;
            }

            // 편집·빌드 중 잠김, 권한, 삭제 같은 파일 시스템 오류도 예외가 아니라 첨부 오류로 돌려준다.
            string? error;
            try { error = AppendRange(sb, shown, relative, rangeRoot, options, maxChars, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error = $"'{shown.Path}': 읽기 실패 ({ex.GetType().Name}: {ex.Message})"; }

            if (error is not null) return (null, error);
            sources.Add(shown.ToString());
        }

        return (new Result(sb.ToString(), sources, usedRoots), null);
    }

    /// <summary>"@이름/나머지"를 나눈다. '@' 뒤에 이름과 구분자('/' 또는 '\')가 있어야 한다.</summary>
    public static bool TrySplitRootPrefix(string? path, out string name, out string rest)
    {
        name = rest = "";
        if (path is null || path.Length < 3 || path[0] != '@') return false;
        var sep = path.IndexOfAny(['/', '\\']);
        if (sep < 2) return false;
        name = path[1..sep];
        rest = path[(sep + 1)..];
        return true;
    }

    /// <summary>추가 루트 등록 값의 문제. 없으면 null. list_modes 경고와 참조 시점 검사에 같이 쓴다.</summary>
    public static string? ExtraRootProblem(string name, string? rootPath)
    {
        if (!ContextOptions.IsValidExtraRootName(name)) return "이름은 영문·숫자·_·- 2~32자만 쓸 수 있습니다.";
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath)) return "절대 경로가 아닙니다.";
        if (!Directory.Exists(rootPath)) return "폴더가 없습니다.";
        return null;
    }

    /// <returns>오류 메시지, 성공이면 null.</returns>
    /// <param name="range">표시용 (제목·오류 메시지). 추가 루트면 Path가 "@이름/…".</param>
    /// <param name="relative">root 기준 상대 경로.</param>
    private static string? AppendRange(
        StringBuilder sb, ContextRange range, string relative, string root, ContextOptions options, int maxChars, CancellationToken ct)
    {
        var (full, pathError) = SafePath.Resolve(relative, root, options.AllDenyPatterns, SafePath.Access.Read);
        if (pathError is not null) return $"'{range.Path}': {pathError}";

        // Unity·IDE가 연 파일도 읽도록 공유 모드를 넓힌다. 크기는 연 핸들 기준으로 본다.
        using var stream = new FileStream(full!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (options.MaxFileBytes > 0 && stream.Length > options.MaxFileBytes)
            return $"'{range.Path}': 파일이 {stream.Length:N0}바이트 / 상한 {options.MaxFileBytes:N0}바이트.";

        // 필요한 줄(EndLine)까지만 읽는다. 줄마다 상한을 확인하므로 메모리는 상한 + 한 줄로 묶인다.
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var width = range.EndLine.ToString().Length;
        sb.AppendLine().AppendLine($"### {range}").AppendLine($"```{FenceLanguage(full!)}");

        var n = 0;
        while (n < range.EndLine && reader.ReadLine() is { } line)
        {
            if (++n < range.StartLine) continue;
            if (n % 256 == 0) ct.ThrowIfCancellationRequested();
            if (line.Contains('\0')) return $"'{range.Path}': 바이너리 파일로 보입니다.";

            sb.Append(n.ToString().PadLeft(width)).Append("| ").AppendLine(line);

            // 상한을 넘는 순간 멈춘다 (나머지 줄·파일은 붙이지 않음). 최종 합계 검사는 호출부에서 input과 함께 한다.
            if (maxChars > 0 && sb.Length > maxChars)
                return $"첨부 코드가 '{range}'에서 상한 {maxChars:N0}자를 넘었습니다. 범위를 줄이세요.";
        }

        if (n < range.EndLine) return $"'{range}': 파일은 {n}줄입니다. 범위를 다시 확인하세요.";

        sb.AppendLine("```");
        return null;
    }

    private static string FenceLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".shader" or ".hlsl" or ".cginc" or ".compute" => "hlsl",
        ".cpp" or ".h" or ".hpp" or ".c" => "cpp",
        ".json" or ".asmdef" or ".asmref" => "json",
        ".uxml" or ".xml" or ".csproj" => "xml",
        ".uss" => "css",
        ".yaml" or ".yml" or ".unity" or ".prefab" or ".asset" => "yaml",
        ".md" => "markdown",
        _ => "",
    };
}
