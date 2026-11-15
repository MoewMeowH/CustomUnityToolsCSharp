using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>순차 · 동시 흐름의 프롬프트 글자 · 실패 때 멈춤 · 반환 범위를 고정한다 (실행기가 바뀌어도 같아야 한다).</summary>
public sealed class ModeFlowBehaviorTests
{
    private const string Step = """{ "Role": "R", "Agent": "a1" }""";

    private static string Modes(string third = "") => $$"""
        {
          "Roles": { "R": { "Prompt": "역할 R" } },
          "Modes": {
            "seq3":    { "Steps": [ {{Step}}, {{Step}}, { "Role": "R", "Agent": "a1"{{third}} } ] },
            "seq3all": { "Return": "All", "Steps": [ {{Step}}, {{Step}}, {{Step}} ] },
            "conc":    { "Flow": "Concurrent", "Steps": [ {{Step}}, {{Step}} ] }
          }
        }
        """;

    private static ModeRunnerHarness Harness(string third = "")
    {
        var h = new ModeRunnerHarness(Modes(third));
        h.Client.Script = [ScriptedChatClient.Reply("하나"), ScriptedChatClient.Reply("둘"), ScriptedChatClient.Reply("셋")];
        return h;
    }

    private static List<string> Prompts(ModeRunnerHarness h) =>
        [.. h.Records().OrderBy(r => r.Step).Select(r => r.Prompt.ReplaceLineEndings("\n"))];

    [Theory]
    [InlineData("", "## 요청\n요청\n\n## 앞 단계 결과\n### 2. R\n둘")] // 기본값 = InputAndPrevious
    [InlineData(""", "Sees": "Input" """, "## 요청\n요청")]
    [InlineData(""", "Sees": "Previous" """, "## 앞 단계 결과\n### 2. R\n둘")]
    [InlineData(""", "Sees": "InputAndPrevious" """, "## 요청\n요청\n\n## 앞 단계 결과\n### 2. R\n둘")]
    [InlineData(""", "Sees": "All" """, "## 요청\n요청\n\n## 앞 단계 결과\n### 1. R\n하나\n\n### 2. R\n둘")]
    public async Task Sequential_prompt_follows_sees(string third, string expectedThird)
    {
        using var h = Harness(third);

        await h.Run("seq3");

        var prompts = Prompts(h);
        Assert.Equal(3, prompts.Count);
        Assert.Equal("## 요청\n요청", prompts[0]); // 첫 단계는 항상 요청만
        Assert.Equal("## 요청\n요청\n\n## 앞 단계 결과\n### 1. R\n하나", prompts[1]);
        Assert.Equal(expectedThird, prompts[2]);
    }

    [Fact]
    public async Task Sequential_returns_only_the_last_step_by_default()
    {
        using var h = Harness();

        var result = await h.Run("seq3");

        Assert.Contains("### 3. R", result);
        Assert.DoesNotContain("### 1. R", result);
        Assert.DoesNotContain("### 2. R", result);
    }

    [Fact]
    public async Task Sequential_returns_every_step_when_return_is_all()
    {
        using var h = Harness();

        var result = await h.Run("seq3all");

        Assert.True(result.IndexOf("### 1. R", StringComparison.Ordinal) < result.IndexOf("### 2. R", StringComparison.Ordinal));
        Assert.True(result.IndexOf("### 2. R", StringComparison.Ordinal) < result.IndexOf("### 3. R", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sequential_stops_after_a_failed_step_and_shows_only_that_step()
    {
        using var h = Harness();
        h.Client.Script = [ScriptedChatClient.Reply("하나"), ScriptedChatClient.Throw(FakeResponse.Error(500)), ScriptedChatClient.Reply("셋")];

        var result = await h.Run("seq3");

        Assert.Equal(2, h.Client.Calls);
        Assert.Contains("### 2. R", result);
        Assert.DoesNotContain("### 1. R", result);
        Assert.DoesNotContain("### 3. R", result);
        Assert.Contains("(단계 2에서 실패해 이후 1단계는 실행하지 않음)", result);
    }

    [Fact]
    public async Task Concurrent_steps_see_only_the_request_and_all_results_return()
    {
        using var h = Harness();

        var result = await h.Run("conc");

        Assert.Equal(["## 요청\n요청", "## 요청\n요청"], Prompts(h));
        Assert.Equal([1, 2], h.Records().Select(r => r.Step!.Value).Order());
        Assert.Contains("### 1. R", result);
        Assert.Contains("### 2. R", result);
    }

    [Fact]
    public async Task Concurrent_shows_every_step_even_when_one_fails()
    {
        using var h = Harness();
        h.Client.Script = [ScriptedChatClient.Throw(FakeResponse.Error(500)), ScriptedChatClient.Reply("둘")];

        var result = await h.Run("conc");

        Assert.Equal(2, h.Client.Calls);
        Assert.Contains("### 1. R", result);
        Assert.Contains("### 2. R", result);
    }
}
