namespace HB.Orchestration.Dashboard;

public static class FileLinks
{
    /// <param name="Url">IDE에서 여는 링크. 템플릿이 {project}를 쓰는데 파일이 어느 Rider 프로젝트에도 속하지 않으면 null.</param>
    public sealed record Link(string Display, string? Url, string FullPath, bool Exists);

    /// <summary>"Assets/A.cs:42" 같은 기록을 IDE에서 여는 링크로 바꾼다.</summary>
    /// <param name="roots">기록의 ContextRoots. "@이름/…"은 그 루트 기준으로 연다.</param>
    /// <param name="projects">
    /// 템플릿에 {project}가 있을 때 쓰는 Rider 프로젝트 목록 (jetbrains:// 링크). 파일을 담은 가장 깊은 프로젝트 폴더를 고르고,
    /// {path}는 그 폴더 기준 상대 경로 (조각마다 인코딩, '/'는 그대로 — Rider가 '%2F'와 절대 경로는 열지 않음).
    /// {project}가 없는 예전 템플릿은 지금처럼 절대 경로 전체를 인코딩해 넣는다.
    /// </param>
    public static Link Build(string raw, string? projectDir, string template, IReadOnlyDictionary<string, string>? roots = null,
        IReadOnlyList<RiderProject>? projects = null)
    {
        var (path, line) = Split(raw.Trim());

        var baseDir = projectDir;
        if (roots is not null && HB.Orchestration.Agents.ContextLoader.TrySplitRootPrefix(path, out var name, out var rest)
            && roots.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) is { Value: { } rootDir })
        {
            baseDir = rootDir;
            path = rest;
        }

        var full = Path.IsPathRooted(path) || string.IsNullOrEmpty(baseDir)
            ? path
            : Path.GetFullPath(Path.Combine(baseDir, path));

        string? url;
        if (template.Contains("{project}", StringComparison.Ordinal))
        {
            url = FindProject(full, projects ?? []) is { } project
                ? template
                    .Replace("{project}", Uri.EscapeDataString(project.Name))
                    .Replace("{path}", EscapeSegments(Path.GetRelativePath(project.BaseDir, full)))
                    .Replace("{line}", (line ?? 1).ToString())
                : null;
        }
        else
        {
            url = template
                .Replace("{path}", Uri.EscapeDataString(full.Replace('\\', '/')))
                .Replace("{line}", (line ?? 1).ToString());
        }

        return new Link(raw, url, full, File.Exists(full));
    }

    /// <summary>파일을 담은 프로젝트 폴더 중 가장 깊은 것. Windows는 대소문자 무시.</summary>
    private static RiderProject? FindProject(string full, IReadOnlyList<RiderProject> projects)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string normalized;
        try { normalized = Path.GetFullPath(full); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        return projects
            .Where(p => normalized.StartsWith(p.BaseDir.EndsWith(Path.DirectorySeparatorChar) ? p.BaseDir : p.BaseDir + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(p => p.BaseDir.Length)
            .FirstOrDefault();
    }

    private static string EscapeSegments(string relative) =>
        string.Join('/', relative.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    // "C:/x/a.cs:12" → ("C:/x/a.cs", 12), "a.cs:12-40" → ("a.cs", 12). 드라이브 문자 콜론(인덱스 1)은 건드리지 않는다.
    internal static (string Path, int? Line) Split(string raw)
    {
        var idx = raw.LastIndexOf(':');
        if (idx <= 1) return (raw, null);

        var tail = raw[(idx + 1)..];
        var dash = tail.IndexOf('-');
        if (int.TryParse(dash < 0 ? tail : tail[..dash], out var n)) return (raw[..idx], n);
        return (raw, null);
    }
}
