using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>모드 이름 통일 · 세션 실행 한도 제거 (계획서 2026-10-20_모드이름-통일-세션한도-제거).</summary>
public sealed class ModeNamesTests : IDisposable
{
    // 예전 키 MaxRunsPerSession이 남은 설정: 무시되어야 한다.
    private const string Modes = """
        {
          "Roles": { "R": { "Prompt": "역할 R" } },
          "Modes": {
            "review_main": { "MaxRunsPerSession": 1, "Steps": [ { "Role": "R", "Agent": "a1" } ] }
          }
        }
        """;

    private readonly ModeRunnerHarness _h = new(Modes);

    public void Dispose() => _h.Dispose();

    private static CallRecord Call(string tool, string? mode) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Timestamp = DateTimeOffset.Now,
        Tool = tool, Mode = mode, Agent = "a", Model = "m", Prompt = "p",
    };

    [Fact]
    public async Task OldName_IsRejectedWithNewName_WithoutCalling()
    {
        var result = await _h.Run("review");

        Assert.StartsWith("[알 수 없는 모드 'review']", result);
        Assert.Contains("'review_main'", result);
        Assert.Equal(0, _h.Client.Calls);
    }

    [Fact]
    public async Task UnknownName_ListsAvailableModes()
    {
        var result = await _h.Run("nope");

        Assert.Contains("사용 가능: review_main", result);
    }

    [Fact]
    public async Task NoSessionLimit_LeftoverMaxRunsPerSessionIsIgnored()
    {
        for (var i = 0; i < 3; i++)
            Assert.DoesNotContain("한도", await _h.Run("review_main"));

        Assert.Equal(3, _h.Client.Calls);
        Assert.DoesNotContain("남은 실행", _h.Runner.Describe());
    }

    [Fact]
    public async Task CallLog_ReadsOldNamesAsNewNames()
    {
        await CallLog.AppendAsync(Call("review", "review"), _h.LogPath);
        await CallLog.AppendAsync(Call("panel", "panel"), _h.LogPath);
        await CallLog.AppendAsync(Call("crash_review", "crash_review"), _h.LogPath);
        await CallLog.AppendAsync(Call("perf_astra", "perf_astra"), _h.LogPath); // 없어진 모드는 그대로
        await CallLog.AppendAsync(Call(VerdictRecorder.SelfReviewTool, VerdictRecorder.SelfReviewTool), _h.LogPath);
        await CallLog.AppendAsync(Call("save_result", null), _h.LogPath);

        var all = CallLog.ReadAll(_h.LogPath);
        Assert.Equal(["save_result", "self_review", "perf_astra", "crash_check", "review_high", "review_main"], all.Select(r => r.Tool));
        Assert.Equal([null, "self_review", "perf_astra", "crash_check", "review_high", "review_main"], all.Select(r => r.Mode));

        var since = CallLog.ReadSince(DateTimeOffset.Now.AddHours(-1), _h.LogPath);
        Assert.Equal(all.Select(r => r.Mode), since.Select(r => r.Mode));
    }

    [Fact]
    public void GoldenCase_OldModeNameIsReadAsNewName()
    {
        Assert.Equal("review_main", new GoldenCaseInfo().Mode);
        Assert.Equal("crash_main", new GoldenCaseInfo { Mode = "crash" }.Mode);
        Assert.Equal("perf_high", new GoldenCaseInfo { Mode = "perf_high" }.Mode);
    }

    [Fact]
    public async Task StaleOldKeyInModesJson_IsWarnedAndExplained()
    {
        using var h = new ModeRunnerHarness("""
            { "Roles": { "R": { "Prompt": "역할 R" } },
              "Modes": { "review": { "Steps": [ { "Role": "R", "Agent": "a1" } ] } } }
            """);

        Assert.Contains("[주의] modes.json에 예전 모드 이름이 남아 있습니다: review→review_main", h.Runner.Describe());
        var result = await h.Run("review_main");
        Assert.Contains("예전 이름 'review'로 남아 있습니다", result);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public void Renamed_TargetsAreNotOldNames()
    {
        // 새 이름이 다시 예전 이름 표에 걸리면 Normalize가 두 번 바꾸거나 거절 안내가 꼬인다.
        Assert.All(ModeNames.Renamed.Values, v => Assert.False(ModeNames.Renamed.ContainsKey(v), v));
    }
}
