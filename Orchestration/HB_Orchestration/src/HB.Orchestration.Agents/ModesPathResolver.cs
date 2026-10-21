namespace HB.Orchestration.Agents;

/// <summary>
/// modes.json 찾는 순서 (MCP 서버 · 대시보드 공용).
/// 설정 ModesPath → HB_MODES_PATH 환경 변수 → 세션 폴더의 hb-modes.json → 서버 폴더의 modes.json.
/// 직접 지정한 경로(설정·환경 변수)는 파일이 없으면 오류다. 다음 후보로 조용히 넘어가면 오타가 있을 때
/// 다른 modes.json으로 돌면서 아무도 모르게 된다. 다음 후보로 넘어가는 건 지정이 없을 때만.
/// 상대 경로의 기준: 설정 ModesPath는 서버 폴더(appsettings.json이 있는 곳) — 서버와 대시보드가 같은 파일을 가리키게.
/// HB_MODES_PATH는 프로세스 환경 값이라 그 프로세스의 현재 폴더 기준 (넘길 때는 절대 경로를 쓴다).
/// </summary>
public static class ModesPathResolver
{
    public const string EnvVar = "HB_MODES_PATH";
    public const string ProjectFileName = "hb-modes.json";
    public const string DefaultFileName = "modes.json";

    public enum Source
    {
        /// <summary>appsettings의 Orchestration:ModesPath.</summary>
        Configured,
        /// <summary>HB_MODES_PATH 환경 변수.</summary>
        Environment,
        /// <summary>세션을 연 폴더(현재 폴더)의 hb-modes.json — 프로젝트별 모드.</summary>
        ProjectFile,
        /// <summary>서버 폴더(dll 옆)의 modes.json — 기본 모드.</summary>
        ServerFolder,
    }

    /// <param name="Path">찾은 파일의 전체 경로. 오류면 null.</param>
    /// <param name="Error">찾지 못한 이유 (지정 경로에 파일 없음, 후보 모두 없음). 찾았으면 null.</param>
    public sealed record Resolution(string? Path, Source Source, string? Error);

    /// <param name="cwd">세션 폴더. 대시보드처럼 세션 폴더가 없으면 null (hb-modes.json을 보지 않는다).</param>
    /// <param name="serverDir">서버 폴더 (dll·appsettings.json이 있는 곳). 설정 ModesPath의 상대 경로 기준이자 기본 modes.json 위치.</param>
    public static Resolution Resolve(string? configured, string? env, string? cwd, string? serverDir)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Specified(serverDir is not null && !System.IO.Path.IsPathRooted(configured) ? System.IO.Path.Combine(serverDir, configured) : configured,
                Source.Configured);
        if (!string.IsNullOrWhiteSpace(env)) return Specified(env, Source.Environment);

        if (cwd is not null && System.IO.Path.Combine(cwd, ProjectFileName) is var project && File.Exists(project))
            return new(System.IO.Path.GetFullPath(project), Source.ProjectFile, null);
        if (serverDir is not null && System.IO.Path.Combine(serverDir, DefaultFileName) is var server && File.Exists(server))
            return new(System.IO.Path.GetFullPath(server), Source.ServerFolder, null);
        return new(null, Source.ServerFolder, "modes.json을 찾을 수 없습니다.");
    }

    private static Resolution Specified(string path, Source source)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(null, source, $"{Label(source)} 경로 형식 오류: '{path}' ({ex.Message})"); }

        return File.Exists(full)
            ? new(full, source, null)
            : new(null, source, $"{Label(source)}로 지정한 modes.json이 없습니다: {full} (다른 modes.json으로 넘어가지 않습니다 — 경로를 고치거나 지정을 지우세요)");
    }

    public static string Label(Source source) => source switch
    {
        Source.Configured => "MCP appsettings Orchestration:ModesPath",
        Source.Environment => EnvVar,
        Source.ProjectFile => $"세션 폴더 {ProjectFileName}",
        _ => $"서버 폴더 {DefaultFileName}",
    };
}
