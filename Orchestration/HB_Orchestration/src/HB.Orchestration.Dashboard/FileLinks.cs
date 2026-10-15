namespace HB.Orchestration.Dashboard;

public static class FileLinks
{
    public sealed record Link(string Display, string Url, string FullPath, bool Exists);

    /// <summary>"Assets/A.cs:42" 같은 기록을 IDE에서 여는 링크로 바꾼다.</summary>
    public static Link Build(string raw, string? projectDir, string template)
    {
        var (path, line) = Split(raw.Trim());

        var full = Path.IsPathRooted(path) || string.IsNullOrEmpty(projectDir)
            ? path
            : Path.GetFullPath(Path.Combine(projectDir, path));

        var url = template
            .Replace("{path}", Uri.EscapeDataString(full.Replace('\\', '/')))
            .Replace("{line}", (line ?? 1).ToString());

        return new Link(raw, url, full, File.Exists(full));
    }

    // "C:/x/a.cs:12" → ("C:/x/a.cs", 12). 드라이브 문자 콜론(인덱스 1)은 건드리지 않는다.
    private static (string Path, int? Line) Split(string raw)
    {
        var idx = raw.LastIndexOf(':');
        if (idx > 1 && int.TryParse(raw[(idx + 1)..], out var n)) return (raw[..idx], n);
        return (raw, null);
    }
}
