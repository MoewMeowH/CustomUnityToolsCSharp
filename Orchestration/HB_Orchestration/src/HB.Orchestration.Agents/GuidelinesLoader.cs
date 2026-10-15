namespace HB.Orchestration.Agents;

/// <summary>공통 리뷰 지침을 실행할 때마다 읽는다. Unity 프로젝트 쪽 파일을 고치면 바로 반영된다.</summary>
public static class GuidelinesLoader
{
    public static string Load(OrchestrationOptions options)
    {
        var candidates = new[]
        {
            options.GuidelinesPath,
            Environment.GetEnvironmentVariable("REVIEW_GUIDELINES_PATH"),
            Path.Combine(Environment.CurrentDirectory, "REVIEW_GUIDELINES.md"),
        };

        var path = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        return path is null ? "" : "\n\n## 프로젝트 지침\n" + File.ReadAllText(path);
    }
}
