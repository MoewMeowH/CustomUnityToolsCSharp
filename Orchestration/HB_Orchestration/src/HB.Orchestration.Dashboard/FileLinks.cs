namespace HB.Orchestration.Dashboard;

public static class FileLinks
{
    public sealed record Link(string Display, string Url, string FullPath, bool Exists);

    /// <summary>"Assets/A.cs:42" 같은 기록을 IDE에서 여는 링크로 바꾼다.</summary>
    /// <param name="roots">기록의 ContextRoots. "@이름/…"은 그 루트 기준으로 연다.</param>
    public static Link Build(string raw, string? projectDir, string template, IReadOnlyDictionary<string, string>? roots = null)
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

        var url = template
            .Replace("{path}", Uri.EscapeDataString(full.Replace('\\', '/')))
            .Replace("{line}", (line ?? 1).ToString());

        return new Link(raw, url, full, File.Exists(full));
    }

    // "C:/x/a.cs:12" → ("C:/x/a.cs", 12), "a.cs:12-40" → ("a.cs", 12). 드라이브 문자 콜론(인덱스 1)은 건드리지 않는다.
    private static (string Path, int? Line) Split(string raw)
    {
        var idx = raw.LastIndexOf(':');
        if (idx <= 1) return (raw, null);

        var tail = raw[(idx + 1)..];
        var dash = tail.IndexOf('-');
        if (int.TryParse(dash < 0 ? tail : tail[..dash], out var n)) return (raw[..idx], n);
        return (raw, null);
    }
}
