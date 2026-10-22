using HB.Orchestration.Agents;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>리뷰 지침 찾는 순서와 지정 경로 오류 (2026-10-22, modes.json 지정 경로와 같은 규칙).</summary>
public sealed class GuidelinesLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-guidelines-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Missing => Path.Combine(_dir, "nope", GuidelinesLoader.FileName);

    [Fact]
    public void Unspecified_uses_current_directory_file_or_none()
    {
        var root = Path.Combine(_dir, "proj");
        Write(Path.Combine("proj", GuidelinesLoader.FileName), "- 프로젝트 지침\n");

        var found = GuidelinesLoader.Load(new OrchestrationOptions(), _ => null, root);
        var none = GuidelinesLoader.Load(new OrchestrationOptions(), _ => null, Path.Combine(_dir, "empty"));

        Assert.Equal(Path.GetFullPath(Path.Combine(root, GuidelinesLoader.FileName)), found.Path);
        Assert.Contains("프로젝트 지침", found.Text);
        Assert.Null(none.Path);
    }

    [Fact]
    public void Env_path_missing_throws_instead_of_falling_back_to_current_directory()
    {
        Write(GuidelinesLoader.FileName, "- 루트 지침\n"); // 예전에는 여기로 조용히 넘어갔다
        var ex = Assert.Throws<GuidelinesPathMissingException>(() =>
            GuidelinesLoader.Load(new OrchestrationOptions(), name => name == GuidelinesLoader.EnvVar ? Missing : null, _dir));

        Assert.Contains(GuidelinesLoader.EnvVar, ex.Message);
        Assert.Contains(Missing, ex.Message);
    }

    [Fact]
    public void Setting_path_missing_throws_even_when_env_path_exists()
    {
        var envFile = Write("env.md", "- 환경 변수 지침\n");
        var ex = Assert.Throws<GuidelinesPathMissingException>(() =>
            GuidelinesLoader.Load(new OrchestrationOptions { GuidelinesPath = Missing }, _ => envFile, _dir));

        Assert.Contains("Orchestration:GuidelinesPath", ex.Message);
    }

    [Fact]
    public void Setting_path_wins_over_env_path()
    {
        var setting = Write("setting.md", "- 설정 지침\n");
        var result = GuidelinesLoader.Load(new OrchestrationOptions { GuidelinesPath = setting }, _ => Missing, _dir); // 환경 변수는 보지 않음

        Assert.Equal(Path.GetFullPath(setting), result.Path);
    }

    [Fact]
    public void Env_path_used_when_no_setting()
    {
        var envFile = Write("env.md", "- 환경 변수 지침\n");
        var result = GuidelinesLoader.Load(new OrchestrationOptions(), name => name == GuidelinesLoader.EnvVar ? envFile : null, _dir);

        Assert.Equal(Path.GetFullPath(envFile), result.Path);
    }

    [Fact]
    public async Task Run_mode_refuses_before_calling_when_specified_path_missing()
    {
        using var h = new ModeRunnerHarness(guidelines: null!); // Options.GuidelinesPath는 지정됐지만 파일 없음

        var result = await h.Runner.RunAsync("seq", "요청", null, null, null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

        Assert.StartsWith("[리뷰 지침 경로 없음]", result);
        Assert.Contains(h.GuidelinesPath, result);
        Assert.Equal(0, h.Client.Calls);
    }
}
