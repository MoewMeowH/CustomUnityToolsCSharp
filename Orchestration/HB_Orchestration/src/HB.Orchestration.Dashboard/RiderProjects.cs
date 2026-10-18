using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>Rider 프로젝트 하나: 솔루션 폴더(절대 경로)와 jetbrains:// 링크의 project 값.</summary>
public sealed record RiderProject(string BaseDir, string Name);

/// <summary>
/// "폴더 → Rider 프로젝트 이름" 목록. jetbrains://rider/navigate/reference 링크는 열린 프로젝트 기준 상대 경로만 연다
/// (2026-10-19 Rider 2026.2.3 확인: 절대 경로·'/'까지 인코딩한 경로·솔루션 폴더 밖 파일은 열리지 않음).
/// 설정 Dashboard:RiderProjects(폴더 → 이름)가 먼저, 그다음 Rider가 남긴 최근 솔루션 목록(recentSolutions.xml)을 쓴다.
/// 최근 목록은 파일 수정 시각이 바뀔 때만 다시 읽는다.
/// </summary>
public sealed class RiderProjects(IOptions<DashboardOptions> options)
{
    private readonly object _gate = new();
    private (string? Path, DateTime Stamp, IReadOnlyList<RiderProject> Projects) _cache = (null, default, []);

    public IReadOnlyList<RiderProject> All
    {
        get
        {
            var configured = options.Value.RiderProjects
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => new RiderProject(Normalize(kv.Key), kv.Value.Trim()));
            return [.. configured, .. Recent()];
        }
    }

    private IReadOnlyList<RiderProject> Recent()
    {
        var path = options.Value.RiderRecentSolutionsPath is { Length: > 0 } p ? p : FindRecentSolutions();
        if (path is null || !File.Exists(path)) return [];
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_gate)
            {
                if (_cache.Path == path && _cache.Stamp == stamp) return _cache.Projects;
                var projects = Parse(File.ReadAllText(path), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                _cache = (path, stamp, projects);
                return projects;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return []; // 링크는 부가 기능이다. 읽지 못하면 Rider 링크 없이 보여 준다.
        }
    }

    /// <summary>%APPDATA%/JetBrains/Rider*/options/recentSolutions.xml 중 가장 최근에 바뀐 것.</summary>
    private static string? FindRecentSolutions()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetBrains");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, "Rider*")
            .Select(d => Path.Combine(d, "options", "recentSolutions.xml"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>recentSolutions.xml의 entry마다 (솔루션 폴더, 이름). 키가 .sln·.slnx·.csproj면 그 폴더, 아니면 키 자체가 폴더.</summary>
    public static IReadOnlyList<RiderProject> Parse(string xml, string userHome)
    {
        var list = new List<RiderProject>();
        foreach (var entry in XDocument.Parse(xml).Descendants("entry"))
        {
            var key = entry.Attribute("key")?.Value;
            var meta = entry.Descendants("RecentProjectMetaInfo").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(key) || meta is null) continue;

            key = key.Replace("$USER_HOME$", userHome.Replace('\\', '/'));
            var isFile = Path.GetExtension(key).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj";
            var baseDir = isFile ? Path.GetDirectoryName(key) : key;
            var name = meta.Elements("option").FirstOrDefault(o => o.Attribute("name")?.Value == "customProjectName")?.Attribute("value")?.Value
                       ?? meta.Attribute("displayName")?.Value
                       ?? (isFile ? Path.GetFileNameWithoutExtension(key) : Path.GetFileName(key));
            if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new RiderProject(Normalize(baseDir), name));
        }
        return list;
    }

    private static string Normalize(string dir) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim()));
}
