using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>최근 호출이 실제로 쓴 modes.json 하나 (경로 · 내용 해시 단위).</summary>
/// <param name="Relative">기록에 상대 경로로 남은 것 (2026-10-21 이전 서버가 HB_MODES_PATH 값을 받은 그대로 기록). 기준 폴더를 알 수 없어 풀지 않는다.</param>
public sealed record ModesFileUse(string Path, string? Sha256, int Calls, int Runs, DateTimeOffset LastUsed)
{
    public bool Relative => !System.IO.Path.IsPathRooted(Path);
}

/// <summary>
/// 호출 기록의 provenance.modes로 최근에 서버가 실제로 쓴 modes.json을 모은다.
/// 서버의 HB_MODES_PATH(프로세스마다 다름)·세션 폴더 hb-modes.json은 대시보드가 알 수 없으므로 기록으로 보인다.
/// </summary>
public static class ModesUsage
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>now 기준 window 안의 호출을 (경로, 해시)로 묶는다. 마지막 사용이 최근인 순.</summary>
    public static List<ModesFileUse> Recent(IEnumerable<CallRecord> calls, DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        return [.. calls
            .Where(c => c.Timestamp >= from && c.Provenance?.Modes?.Path is { Length: > 0 })
            .GroupBy(c => (Path: Normalize(c.Provenance!.Modes!.Path!), c.Provenance!.Modes!.Sha256), PathShaComparer.Instance)
            .Select(g => new ModesFileUse(g.Key.Path, g.Key.Sha256, g.Count(),
                g.Select(c => c.RunId).Where(r => r is not null).Distinct(StringComparer.Ordinal).Count(),
                g.Max(c => c.Timestamp)))
            .OrderByDescending(u => u.LastUsed)];
    }

    /// <summary>편집 중인 파일과 경로가 다른 사용 (화면 경고 대상).</summary>
    public static List<ModesFileUse> OtherFiles(IEnumerable<ModesFileUse> uses, string editingPath) =>
        [.. uses.Where(u => !SamePath(u.Path, editingPath))];

    /// <summary>경로는 같지만 내용 해시가 지금 파일과 다른 사용 ("그 뒤 수정됨" 표시용, 경고 아님).</summary>
    public static bool ModifiedSince(ModesFileUse use, string editingPath, string? editingSha256) =>
        SamePath(use.Path, editingPath) && use.Sha256 is not null && editingSha256 is not null
        && !string.Equals(use.Sha256, editingSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 편집 중인 파일을 쓴 사용을 지금 내용(해시가 같거나 비교할 해시가 없음)과 이전 내용(그 뒤 수정됨)으로 나눈다.
    /// 이전 내용은 마지막 사용이 최근인 순.
    /// </summary>
    public static (List<ModesFileUse> Current, List<ModesFileUse> Earlier) SameFile(IEnumerable<ModesFileUse> uses, string editingPath, string? editingSha256)
    {
        var same = uses.Where(u => SamePath(u.Path, editingPath)).ToList();
        var earlier = same.Where(u => ModifiedSince(u, editingPath, editingSha256)).OrderByDescending(u => u.LastUsed).ToList();
        return ([.. same.Except(earlier)], earlier);
    }

    /// <summary>절대 경로만 정규화한다. 상대 경로는 서버 프로세스의 현재 폴더 기준이었으므로 대시보드 폴더로 풀지 않고 그대로 둔다.</summary>
    private static string Normalize(string path)
    {
        if (!System.IO.Path.IsPathRooted(path)) return path.Replace('\\', '/');
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a).TrimEnd('\\', '/'), Normalize(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class PathShaComparer : IEqualityComparer<(string Path, string? Sha256)>
    {
        public static readonly PathShaComparer Instance = new();
        private static readonly StringComparer PathCmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public bool Equals((string Path, string? Sha256) x, (string Path, string? Sha256) y) =>
            PathCmp.Equals(x.Path, y.Path) && string.Equals(x.Sha256, y.Sha256, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, string? Sha256) k) =>
            HashCode.Combine(PathCmp.GetHashCode(k.Path), k.Sha256 is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(k.Sha256));
    }
}
