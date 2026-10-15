using System.Text;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Agents;

/// <summary>run_mode의 context 항목 하나: 프로젝트 루트 기준 파일의 줄 범위 (1부터, 끝 줄 포함).</summary>
public sealed record ContextRange(string Path, int StartLine, int EndLine)
{
    public override string ToString() => $"{Path}:{StartLine}-{EndLine}";
}

/// <summary>
/// Claude가 이미 읽은 코드를 input에 다시 쓰지 않도록, 경로·줄 범위만 받아 서버가 디스크에서 읽어 붙인다.
/// 외부 호출 전에 모두 검증하고, 하나라도 문제가 있으면 전체를 거절한다 (조용히 자르거나 건너뛰지 않음).
/// 한계: 검사와 읽기 사이에 경로를 링크로 바꿔치기하는 경쟁(TOCTOU)은 막지 않는다 (단일 사용자 PC 전제).
/// </summary>
public static class ContextLoader
{
    public sealed record Result(string Text, IReadOnlyList<string> Sources);

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

        foreach (var range in ranges)
        {
            ct.ThrowIfCancellationRequested();
            if (range is null) return (null, "context에 빈 항목이 있습니다.");

            if (range.StartLine < 1 || range.EndLine < range.StartLine)
                return (null, $"'{range}': 줄 범위가 잘못됐습니다 (1 ≤ startLine ≤ endLine).");

            // 편집·빌드 중 잠김, 권한, 삭제 같은 파일 시스템 오류도 예외가 아니라 첨부 오류로 돌려준다.
            string? error;
            try { error = AppendRange(sb, range, root, options, maxChars, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error = $"'{range.Path}': 읽기 실패 ({ex.GetType().Name}: {ex.Message})"; }

            if (error is not null) return (null, error);
            sources.Add(range.ToString());
        }

        return (new Result(sb.ToString(), sources), null);
    }

    /// <returns>오류 메시지, 성공이면 null.</returns>
    private static string? AppendRange(
        StringBuilder sb, ContextRange range, string root, ContextOptions options, int maxChars, CancellationToken ct)
    {
        var (full, pathError) = ResolveSafePath(range.Path, root, options);
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

    /// <summary>루트 밖, 링크(심볼릭·junction)를 거치는 경로, 전송 금지 파일을 거절한다.</summary>
    private static (string? FullPath, string? Error) ResolveSafePath(string raw, string root, ContextOptions options)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "경로가 비어 있습니다.");
        if (Path.IsPathRooted(raw)) return (null, "프로젝트 루트 기준 상대 경로만 받습니다.");

        string full;
        try { full = Path.GetFullPath(Path.Combine(root, raw)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, $"경로 형식 오류: {ex.Message}"); }

        // 접두사 비교는 구분자까지 포함해야 "F:/Proj2"가 "F:/Proj" 안으로 통과하지 않는다.
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (null, "프로젝트 루트 밖입니다.");

        if (!File.Exists(full)) return (null, "파일이 없습니다.");

        // 루트부터 파일까지 모든 단계에서 링크를 거절한다 (정규화된 경로 문자열만으로는 링크 이탈을 못 막음).
        for (FileSystemInfo? node = new FileInfo(full); node is not null; node = Parent(node))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(node.FullName), root, StringComparison.OrdinalIgnoreCase)) break;
            if (node.LinkTarget is not null) return (null, "심볼릭 링크·junction을 거치는 경로는 받지 않습니다.");
        }

        // 금지 패턴은 실제 긴 이름으로 비교한다. 8.3 짧은 이름(CREDEN~1.JSON)으로 요청해도 같은 파일이기 때문.
        var relative = LongRelativePath(root, full);
        var denied = options.AllDenyPatterns.FirstOrDefault(p => GlobToRegex(p).IsMatch(relative));
        if (denied is not null) return (null, $"외부 전송 금지 파일입니다 (DenyPatterns '{denied}').");

        return (full, null);

        static FileSystemInfo? Parent(FileSystemInfo n) => n switch
        {
            FileInfo f => f.Directory,
            DirectoryInfo d => d.Parent,
            _ => null,
        };
    }

    /// <summary>
    /// 경로 구성 요소마다 디렉터리 항목을 조회해 실제 긴 이름으로 바꾼다.
    /// 운영체제는 짧은 이름으로 조회해도 긴 이름 항목을 돌려준다. 항목을 못 찾으면 요청한 이름을 그대로 쓴다.
    /// </summary>
    private static string LongRelativePath(string root, string full)
    {
        var current = root;
        var parts = new List<string>();
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            var entries = new DirectoryInfo(current).GetFileSystemInfos(part);
            var name = (entries.FirstOrDefault(e => e.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                        ?? entries.FirstOrDefault())?.Name ?? part;
            parts.Add(name);
            current = Path.Combine(current, name);
        }
        return string.Join('/', parts);
    }

    /// <summary>
    /// 간단한 glob: '*'는 '/'를 넘지 않고 '**'는 넘는다. '/'가 없는 패턴은 어느 폴더의 파일 이름이든 매칭한다.
    /// </summary>
    private static Regex GlobToRegex(string pattern)
    {
        var p = pattern.Replace('\\', '/');
        if (!p.Contains('/')) p = "**/" + p;

        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                // "**/"는 0개 이상의 폴더
                if (i + 2 < p.Length && p[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (p[i] == '*') sb.Append("[^/]*");
            else if (p[i] == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(p[i].ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
