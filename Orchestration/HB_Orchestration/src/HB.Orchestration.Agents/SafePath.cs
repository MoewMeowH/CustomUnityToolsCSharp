using System.Text;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Agents;

/// <summary>
/// 프로젝트 루트 기준 상대 경로를 검사해 실제 경로로 바꾼다. context 첨부(읽기)와 save_result(쓰기)가 같은 규칙을 쓴다:
/// 절대 경로·루트 밖·링크(심볼릭·junction) 경유·DenyPatterns는 거절.
/// 한계: 검사와 실제 읽기·쓰기 사이에 경로를 링크로 바꿔치기하는 경쟁(TOCTOU)은 막지 않는다 (단일 사용자 PC 전제).
/// </summary>
public static class SafePath
{
    public enum Access
    {
        /// <summary>파일이 있어야 한다.</summary>
        Read,
        /// <summary>파일·상위 폴더가 아직 없어도 된다. 이미 있는 부분까지 링크를 검사한다.</summary>
        Write,
    }

    /// <param name="rootDir">프로젝트 루트. 정규화는 여기서 한다.</param>
    /// <returns>실제 경로, 또는 오류 메시지 (경로 이름은 붙이지 않음 — 호출부가 붙인다).</returns>
    /// <param name="writeDenyPatterns">
    /// 쓰기에만 적용하는 금지 목록 (save_result: ContextOptions.AllWriteDenyPatterns). 읽기에서는 무시한다.
    /// 쓰기에서 null이면 내장 목록(ContextOptions.BuiltInWriteDenyPatterns)을 쓴다 — 넘기는 것을 잊어도 막는 쪽으로 (/cross-review 1b661e26fe).
    /// </param>
    public static (string? FullPath, string? Error) Resolve(string? raw, string rootDir, IEnumerable<string> denyPatterns, Access access,
        IEnumerable<string>? writeDenyPatterns = null)
    {
        if (access == Access.Write) writeDenyPatterns ??= ContextOptions.BuiltInWriteDenyPatterns;
        // 권한 없는 폴더 열거·잠긴 항목 같은 파일 시스템 오류도 예외가 아니라 오류 메시지로 돌려준다.
        try { return Check(raw, rootDir, denyPatterns, access, access == Access.Write ? writeDenyPatterns : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return (null, $"경로 확인 실패 ({ex.GetType().Name}: {ex.Message})"); }
    }

    /// <summary>
    /// 경로 비교 방식. Windows·macOS(기본 APFS)는 대소문자를 구분하지 않고, Linux는 구분한다.
    /// 구분하는 파일 시스템에서 무시하고 비교하면 "../PROJ/x"처럼 대소문자만 다른 형제 폴더로 나갈 수 있다.
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Windows 예약 장치 이름(CON·PRN·AUX·NUL·COM0~9·LPT0~9·CONIN$·CONOUT$, 확장자가 붙어도 장치)과 끝이 점·공백인 구성 요소를 거절한다.
    /// 장치 이름으로 쓰면 파일이 아니라 콘솔·직렬 포트를 연다 (쓰기 모드 실측 2026-10-19). 끝 점·공백은 Windows가 잘라 다른 이름으로 열린다.
    /// 운영체제와 관계없이 막는다 (같은 경로 규칙을 Unity 프로젝트 쪽과 공유하므로).
    /// </summary>
    internal static string? WindowsNameProblem(string raw)
    {
        foreach (var part in raw.Split('/', '\\'))
        {
            if (part.Length == 0 || part is "." or "..") continue;
            if (part.EndsWith('.') || part.EndsWith(' ')) return $"경로 구성 요소 '{part}'가 점이나 공백으로 끝납니다 (Windows가 잘라 다른 이름으로 엽니다).";
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³')))
                return $"'{part}'은(는) Windows 장치 이름이라 쓸 수 없습니다.";
        }
        return null;
    }

    private static (string? FullPath, string? Error) Check(string? raw, string rootDir, IEnumerable<string> denyPatterns, Access access,
        IEnumerable<string>? writeDenyPatterns)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "경로가 비어 있습니다.");
        if (Path.IsPathRooted(raw)) return (null, "프로젝트 루트 기준 상대 경로만 받습니다.");
        // 상대 경로에 ':'가 있으면 NTFS 대체 데이터 스트림(".env::$DATA" = 주 스트림)이다. 금지 패턴은 이름 끝을 보므로
        // 그대로 두면 우회된다 (self-reviewer 시범에서 발견, 실측 확인 2026-10-19).
        if (raw.Contains(':')) return (null, "경로에 ':'를 쓸 수 없습니다 (대체 데이터 스트림).");
        if (WindowsNameProblem(raw) is { } nameProblem) return (null, nameProblem);

        string root, full;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
            full = Path.GetFullPath(Path.Combine(root, raw));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, $"경로 형식 오류: {ex.Message}"); }

        // 접두사 비교는 구분자까지 포함해야 "F:/Proj2"가 "F:/Proj" 안으로 통과하지 않는다.
        // 드라이브 루트("C:\")는 정리 후에도 구분자로 끝나므로 한 번 더 붙이지 않는다.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, PathComparison))
            return (null, "프로젝트 루트 밖입니다.");

        if (Directory.Exists(full)) return (null, "파일이 아니라 폴더입니다.");
        if (access == Access.Read && !File.Exists(full)) return (null, "파일이 없습니다.");

        // 루트부터 대상까지 이미 있는 모든 단계에서 링크를 거절한다 (정규화된 경로 문자열만으로는 링크 이탈을 못 막음).
        // 루트 자체와 그 위는 보지 않는다: 루트는 사용자가 연 프로젝트 폴더라, 루트가 junction이면 그 대상이 곧 프로젝트다.
        for (FileSystemInfo? node = DeepestExisting(full); node is not null; node = Parent(node))
        {
            if (string.Equals(Path.TrimEndingDirectorySeparator(node.FullName), root, PathComparison)) break;
            if (node.LinkTarget is not null) return (null, "심볼릭 링크·junction을 거치는 경로는 받지 않습니다.");
        }

        // 금지 패턴은 실제 긴 이름으로 비교한다. 8.3 짧은 이름(CREDEN~1.JSON)으로 요청해도 같은 파일이기 때문.
        var relative = LongRelativePath(root, full);
        var denied = denyPatterns.FirstOrDefault(p => GlobToRegex(p).IsMatch(relative));
        if (denied is not null)
        {
            return (null, access == Access.Read
                ? $"외부 전송 금지 파일입니다 (DenyPatterns '{denied}')."
                : $"보호된 파일이라 쓸 수 없습니다 (DenyPatterns '{denied}').");
        }

        if (writeDenyPatterns?.FirstOrDefault(p => GlobToRegex(p).IsMatch(relative)) is { } writeDenied)
            return (null, $"쓰기 금지 파일입니다 (WriteDenyPatterns '{writeDenied}'). 외부 응답은 실행·설정 파일에 쓰지 않습니다 — docs/·review_tmp/ 등에 저장한 뒤 옮기세요.");

        return (full, null);
    }

    /// <summary>대상이 있으면 대상, 없으면 가장 가까운 기존 상위 폴더.</summary>
    private static FileSystemInfo? DeepestExisting(string full)
    {
        if (File.Exists(full)) return new FileInfo(full);
        for (var dir = new DirectoryInfo(full).Parent; dir is not null; dir = dir.Parent)
            if (dir.Exists) return dir;
        return null;
    }

    private static FileSystemInfo? Parent(FileSystemInfo n) => n switch
    {
        FileInfo f => f.Directory,
        DirectoryInfo d => d.Parent,
        _ => null,
    };

    /// <summary>
    /// 경로 구성 요소마다 디렉터리 항목을 조회해 실제 긴 이름으로 바꾼다.
    /// 운영체제는 짧은 이름으로 조회해도 긴 이름 항목을 돌려준다. 항목을 못 찾으면(쓰기로 새로 만들 부분 포함) 요청한 이름을 그대로 쓴다.
    /// </summary>
    private static string LongRelativePath(string root, string full)
    {
        var current = root;
        var parts = new List<string>();
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            var name = part;
            if (Directory.Exists(current))
            {
                var entries = new DirectoryInfo(current).GetFileSystemInfos(part);
                name = (entries.FirstOrDefault(e => e.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                        ?? entries.FirstOrDefault())?.Name ?? part;
            }
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
}
