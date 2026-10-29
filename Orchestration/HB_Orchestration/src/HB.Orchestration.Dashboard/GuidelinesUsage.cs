using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>최근 호출이 실제로 붙인 리뷰 지침 파일 하나 (경로 · 내용 해시 단위).</summary>
/// <param name="Warning">경고 이유 (review_tmp/ 아래 · 이름이 REVIEW_GUIDELINES.md 아님). 없으면 목록만.</param>
public sealed record GuidelinesFileUse(string Path, string? Sha256, int Calls, int Runs, DateTimeOffset LastUsed, string? Warning);

/// <summary>"지침 파일 없음"으로 돈 호출 (프로젝트 폴더 단위).</summary>
public sealed record MissingGuidelinesUse(string? ProjectDir, int Calls, int Runs, DateTimeOffset LastUsed);

/// <summary>
/// 호출 기록의 provenance.guidelines로 최근 실행이 쓴 리뷰 지침을 모은다.
/// 프로젝트마다 자기 지침을 고쳐 쓰므로 내용 해시는 비교하지 않고, 경로 규칙으로 의도하지 않은 지침만 경고한다.
/// 역할 UseGuidelines=false · 골든 --no-guidelines는 일부러 끈 것이라 어느 쪽에도 넣지 않는다.
/// </summary>
public static class GuidelinesUsage
{
    public const string FileName = "REVIEW_GUIDELINES.md";
    public const string TempFolder = "review_tmp";

    /// <summary>지침을 붙인 호출을 (경로, 해시)로 묶는다. 마지막 사용이 최근인 순.</summary>
    public static List<GuidelinesFileUse> Recent(IEnumerable<CallRecord> calls, DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        return [.. calls
            .Where(c => c.Timestamp >= from && c.Provenance?.Guidelines is { Attached: true, Path.Length: > 0 })
            .GroupBy(c => (Path: c.Provenance!.Guidelines!.Path!, c.Provenance!.Guidelines!.Sha256), ModesUsage.PathShaComparer.Instance)
            .Select(g => new GuidelinesFileUse(g.Key.Path, g.Key.Sha256, g.Count(), Runs(g), g.Max(c => c.Timestamp), Warning(g.Key.Path)))
            .OrderByDescending(u => u.LastUsed)];
    }

    /// <summary>지침 파일을 찾지 못해 지침 없이 돈 호출을 프로젝트 폴더로 묶는다.</summary>
    public static List<MissingGuidelinesUse> Missing(IEnumerable<CallRecord> calls, DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        return [.. calls
            .Where(c => c.Timestamp >= from && c.Provenance?.Guidelines is { Attached: false, Reason: GuidelinesProvenance.ReasonNoFile })
            .GroupBy(c => c.ProjectDir ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(g => new MissingGuidelinesUse(g.Key.Length == 0 ? null : g.Key, g.Count(), Runs(g), g.Max(c => c.Timestamp)))
            .OrderByDescending(u => u.LastUsed)];
    }

    /// <summary>
    /// 경고 이유. 경로 구성 요소에 review_tmp가 있거나 파일 이름이 REVIEW_GUIDELINES.md가 아니면.
    /// 대소문자는 Windows에서만 무시한다 (경로 비교와 같음) — 다른 OS에서 review_guidelines.md는 기본 탐색이 못 찾는 다른 파일이라 지정 실수로 본다.
    /// </summary>
    public static string? Warning(string path) => Warning(path, OperatingSystem.IsWindows());

    public static string? Warning(string path, bool ignoreCase)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parts = path.Split('/', '\\');
        if (parts.Any(p => p.Equals(TempFolder, cmp))) return $"{TempFolder}/ 아래";
        if (!parts[^1].Equals(FileName, cmp)) return $"이름이 {FileName} 아님";
        return null;
    }

    private static int Runs(IEnumerable<CallRecord> calls) =>
        calls.Select(c => c.RunId).Where(r => r is not null).Distinct(StringComparer.Ordinal).Count();
}
