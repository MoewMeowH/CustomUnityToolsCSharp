using System.Text.Json;
using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

public sealed class ReviewEnvironmentTests
{
    [Theory]
    [InlineData("git version 2.49.0.windows.1", "2.49.0.windows.1")]
    [InlineData("2.1.312 (Claude Code)", "2.1.312")]
    [InlineData("codex-cli 0.165.0", "0.165.0")]
    [InlineData("PRIVATE_PATH failed PRIVATE_TOKEN", null)]
    public void Version_parser_returns_only_version_token(string output, string? expected)
        => Assert.Equal(expected, ReviewEnvironmentProbe.VersionToken(output));

    [Theory]
    [InlineData("git --help")]
    [InlineData("../claude")]
    [InlineData("powershell")]
    public void Environment_rejects_commands_and_paths(string tool)
    {
        using var fixture = new Fixture();
        var response = JsonSerializer.Deserialize<JsonElement>(new ReviewEvidence(fixture.Root).EnvironmentInfo(tool));
        Assert.True(response.TryGetProperty("error", out _));
    }

    [Fact]
    public void Observation_is_cached_without_path_or_behavior_claim()
    {
        using var fixture = new Fixture();
        var evidence = new ReviewEvidence(fixture.Root, original: true);
        var first = JsonSerializer.Deserialize<JsonElement>(evidence.EnvironmentInfo("git")).GetProperty("data");
        var second = JsonSerializer.Deserialize<JsonElement>(evidence.EnvironmentInfo("git")).GetProperty("data");
        Assert.Equal("version_only", first.GetProperty("scope").GetString());
        Assert.False(first.GetProperty("behaviorVerified").GetBoolean());
        Assert.False(first.GetProperty("cached").GetBoolean());
        Assert.True(second.GetProperty("cached").GetBoolean());
        Assert.Equal(first.GetProperty("observation").GetRawText(), second.GetProperty("observation").GetRawText());
        Assert.All(Strings(first), value => Assert.DoesNotContain(fixture.Root, value));
    }

    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Object => value.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        _ => []
    };

    [Fact]
    public void Snapshot_mode_does_not_start_environment_processes()
    {
        using var fixture = new Fixture();
        Assert.True(JsonSerializer.Deserialize<JsonElement>(new ReviewEvidence(fixture.Root).EnvironmentInfo("git")).TryGetProperty("error", out _));
    }

    [Fact]
    public void Empty_path_never_selects_relative_git_or_calls_runner()
    {
        using var fixture = new Fixture(); var called = false;
        var result = ReviewEnvironmentProbe.Measure("git", fixture.Root, "", (_, _) => { called = true; return ("observed", "1.2.3"); });
        Assert.Equal("unavailable", result.Status); Assert.False(called);
        Assert.Null(ReviewExecutablePolicy.Git(fixture.Root, ""));
    }

    [Fact]
    public void Executable_and_child_share_filtered_absolute_path()
    {
        using var fixture = new Fixture();
        var install = Path.Combine(fixture.Base, "installed"); Directory.CreateDirectory(install);
        var git = Path.Combine(install, OperatingSystem.IsWindows() ? "git.exe" : "git"); File.WriteAllText(git, "not executed");
        var internalDir = Path.Combine(fixture.Root, "bin"); Directory.CreateDirectory(internalDir);
        var input = string.Join(Path.PathSeparator, "", ".", internalDir, $"\"{install}\"");
        var calls = 0;
        var observed = ReviewEnvironmentProbe.Measure("git", fixture.Root, input, (exe, childPath) =>
        {
            calls++; Assert.Equal(git, exe); Assert.True(Path.IsPathFullyQualified(exe)); Assert.Equal(install, childPath);
            return ("observed", "1.2.3");
        });
        Assert.Equal(1, calls); Assert.Equal("observed", observed.Status);
        Assert.Equal(git, ReviewExecutablePolicy.Git(fixture.Root, ReviewExecutablePolicy.SearchPath(fixture.Root, input)));
    }

    [Fact]
    public void External_directory_link_cannot_select_repository_executable()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "bin"); Directory.CreateDirectory(target);
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git"; File.WriteAllText(Path.Combine(target, name), "never execute");
        var link = Path.Combine(fixture.Base, "alias");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.True(process.WaitForExit(5000)); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.True(File.Exists(Path.Combine(link, name)));
            Assert.False(ReviewExecutablePolicy.Allowed(Path.Combine(link, name), fixture.Root));
            Assert.Equal("", ReviewExecutablePolicy.SearchPath(fixture.Root, link));
        }
        finally { Directory.Delete(link); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Base { get; } = Directory.CreateTempSubdirectory("hb-environment-test-").FullName;
        public string Root { get; }
        public Fixture() { Root = Path.Combine(Base, "repository"); Directory.CreateDirectory(Root); }
        public void Dispose() => Directory.Delete(Base, true);
    }
}
