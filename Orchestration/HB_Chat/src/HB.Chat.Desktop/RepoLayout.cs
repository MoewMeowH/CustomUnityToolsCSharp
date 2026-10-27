namespace HB.Chat.Desktop;

/// <summary>
/// 저장소 안의 서버 위치. 데스크톱 exe(…/HB_Chat/src/HB.Chat.Desktop/bin/…)에서 위로 올라가며 `src/HB.Chat/HB.Chat.csproj`가 있는
/// HB_Chat 폴더를 찾고, 그 옆 HB_Orchestration의 대시보드를 쓴다. 서버는 빌드된 exe를 프로젝트 폴더를 content root로 띄운다
/// (배치가 프로젝트 폴더에서 dotnet run으로 띄우는 것과 같은 wwwroot · appsettings).
/// </summary>
public sealed record RepoLayout(string ChatRoot, string ChatProjectDir, string OrchestrationDir, string DashboardProjectDir)
{
    public const string ChatAssembly = "HB.Chat", DashboardAssembly = "HB.Orchestration.Dashboard";

    /// <summary>데스크톱과 같은 빌드 구성 (Debug로 띄운 데스크톱은 Debug 서버를 찾는다).</summary>
    public static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static RepoLayout? Find(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var chatProject = Path.Combine(dir.FullName, "src", ChatAssembly);
            if (!File.Exists(Path.Combine(chatProject, ChatAssembly + ".csproj"))) continue;
            var orchestration = Path.GetFullPath(Path.Combine(dir.FullName, "..", "HB_Orchestration"));
            return new RepoLayout(dir.FullName, chatProject, orchestration, Path.Combine(orchestration, "src", DashboardAssembly));
        }
        return null;
    }

    public string ChatServerExe => ServerExe(ChatProjectDir, ChatAssembly);
    public string DashboardServerExe => ServerExe(DashboardProjectDir, DashboardAssembly);

    public static string ServerExe(string projectDir, string assembly) =>
        Path.Combine(projectDir, "bin", Configuration, "net10.0", assembly + ".exe");

    /// <summary>
    /// 대시보드에 넘길 MCP 게시 폴더: HB_Orchestration.config의 PUBLISH_DIR (배치처럼 그 폴더 기준 상대 경로), 없으면 ..\Publish_Output.
    /// `#`로 시작하는 줄 · 빈 값은 건너뛴다 (배치의 for /f eol=# 과 같다).
    /// </summary>
    public string PublishDir()
    {
        var value = @"..\Publish_Output";
        var config = Path.Combine(OrchestrationDir, "HB_Orchestration.config");
        if (File.Exists(config))
        {
            foreach (var line in File.ReadAllLines(config))
            {
                if (line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0 || !line[..eq].Trim().Equals("PUBLISH_DIR", StringComparison.OrdinalIgnoreCase)) continue;
                if (line[(eq + 1)..].Trim() is { Length: > 0 } v) value = v;
            }
        }
        return Path.GetFullPath(Path.Combine(OrchestrationDir, value)).TrimEnd(Path.DirectorySeparatorChar);
    }
}
