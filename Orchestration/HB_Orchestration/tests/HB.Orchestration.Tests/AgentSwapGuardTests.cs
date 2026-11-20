using HB.Orchestration.Agents;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>에이전트 교체 '*=…' 거절 (서로 다른 에이전트를 비교하는 모드) · API 리뷰 결과의 읽은 파일 경로 경고.</summary>
public class AgentSwapGuardTests
{
    private const string Modes = """
        {
          "Roles": { "R": { "Prompt": "역할 R" } },
          "Modes": {
            "seq":  { "Steps": [ { "Role": "R", "Agent": "a1" } ] },
            "same": { "Flow": "Concurrent", "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a1" } ] },
            "pair": { "Flow": "Concurrent", "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a2" } ] }
          }
        }
        """;

    private static Task<string> Run(ModeRunnerHarness h, string mode, params string[] agents) =>
        h.Runner.RunAsync(mode, "요청", null, agents, null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

    [Fact]
    public async Task Star_swap_on_mode_with_different_agents_is_rejected_before_calling()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);

        var result = await Run(h, "pair", "*=a1");

        Assert.StartsWith("[모드 'pair']", result);
        Assert.Contains("다른 에이전트(2개)", result);
        Assert.Equal(0, h.Client.Calls);
        Assert.Equal(0, h.FallbackClient.Calls);
    }

    [Fact]
    public async Task Star_swap_with_named_swap_on_mode_with_different_agents_is_still_rejected()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);

        var result = await Run(h, "pair", "a1=a2", "*=a1");

        Assert.Contains("다른 에이전트(2개)", result);
        Assert.Equal(0, h.Client.Calls + h.FallbackClient.Calls);
    }

    [Theory]
    [InlineData("seq")]
    [InlineData("same")]
    public async Task Star_swap_on_single_agent_mode_runs(string mode)
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);

        await Run(h, mode, "*=a2");

        Assert.Equal(0, h.Client.Calls);
        Assert.True(h.FallbackClient.Calls > 0);
    }

    [Fact]
    public async Task Named_swap_on_mode_with_different_agents_runs()
    {
        using var h = new ModeRunnerHarness(Modes, withFallbackAgent: true);

        await Run(h, "pair", "a2=a1");

        Assert.Equal(2, h.Client.Calls);
        Assert.Equal(0, h.FallbackClient.Calls);
    }

    [Theory]
    [InlineData("- subagent_in/x.cs:1-9")]
    [InlineData("- src/a.cs:1-9 (설명)")]
    [InlineData("- ./src/a.cs")]
    [InlineData("- Grep: \"Foo\" in src/")]
    [InlineData("- Glob: **/*.cs")]
    public void Relative_read_files_have_no_warning(string line)
    {
        Assert.Null(ResponseChecker.ReadFilesWarning($"## 지적\n문제 없음\n\n## 읽은 파일\n{line}\n\n## 비고\n- C:/x.cs 는 비고라 보지 않음\n"));
    }

    [Theory]
    [InlineData("- F:/AI/Proj/src/a.cs:1-9")]
    [InlineData("- C:\\Users\\x\\a.cs")]
    [InlineData("- /etc/passwd")]
    [InlineData("- \\\\server\\share\\a.cs")]
    [InlineData("- ~/a.cs")]
    [InlineData("- src/../../secret.txt")]
    [InlineData("- `F:/AI/a.cs`")]
    [InlineData("- Grep: \"Foo\" in F:/AI/Proj/")]
    [InlineData("* F:/AI/a.cs")]
    [InlineData("1. /etc/x")]
    [InlineData("F:/AI/a.cs")]
    public void Absolute_or_escaping_read_files_are_warned(string line)
    {
        var warning = ResponseChecker.ReadFilesWarning($"## 지적\n- C1 x\n\n### 읽은 파일\n- src/ok.cs\n{line}\n");
        Assert.NotNull(warning);
        Assert.StartsWith("[경고] 읽은 파일에 작업 폴더 기준 상대 경로가 아닌 줄 1개", warning);
    }

    [Fact]
    public void Text_without_read_files_section_has_no_warning()
    {
        Assert.Null(ResponseChecker.ReadFilesWarning("## 지적\n- F:/AI/a.cs:3 문제\n"));
    }
}
