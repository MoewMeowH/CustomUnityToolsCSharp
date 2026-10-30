using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// claude CLI 판 기록 · 카드 자가 시험: 처음 본 판은 확인한 판으로(결정), 판이 바뀌면 확인 필요, 자가 시험 통과면 확인한 판으로.
/// 자가 시험은 가짜 claude 'hb-selftest' 모드로 — 통과 · 명령이 다름(실패) · 질문이 안 옴.
/// </summary>
public sealed class CliCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Versions_FirstSeenIsChecked_ChangeNeedsCheck_PassClears()
    {
        var cli = new CliVersions(() => _dir);
        Assert.False(cli.Read().NeedsCheck); // 아직 본 판 없음
        Assert.True(cli.Note("2.1.312 (Claude Code)"));
        Assert.Equal(("2.1.312", "2.1.312"), (cli.Read().Seen, cli.Read().Checked)); // 결정 "지금 판을 확인함으로"
        Assert.False(cli.Read().NeedsCheck);
        Assert.False(cli.Note("2.1.312"));
        Assert.False(cli.Note("not a version"));

        Assert.True(cli.Note("2.1.320"));
        Assert.True(cli.Read().NeedsCheck);
        Assert.Equal("2.1.312", cli.Read().Checked);

        cli.Save(new CliVersions.TestResult(DateTimeOffset.Now, "2.1.320", false, [], "x"));
        Assert.True(cli.Read().NeedsCheck);
        Assert.Equal("2.1.312", cli.Read().Checked);
        cli.Save(new CliVersions.TestResult(DateTimeOffset.Now, "2.1.320", true, [], null));
        Assert.False(cli.Read().NeedsCheck);
        Assert.Equal("2.1.320", cli.Read().Checked);

        // 확인한 판에서 다시 실패하면 그 판도 확인 필요 (띠)
        cli.Save(new CliVersions.TestResult(DateTimeOffset.Now, "2.1.320", false, [], "x"));
        Assert.True(cli.Read().NeedsCheck);
    }

    /// <summary>
    /// 갈래: 통과 · 모델이 다른 명령(안 옴) · 질문 안 옴 · 허용 답이 듣지 않음(폴더 없음 — 실패, /self-review 5ccb0f709a C1) · CLI가 묻지 않고 허용(안 옴, C4) ·
    /// 정해 둔 명령 뒤 다른 Bash를 더 부름(통과 유지, C3) · 질문 답이 거절로 처리됨(실패, C1).
    /// </summary>
    [Theory]
    [InlineData("", true, "통과", "통과", "통과", "통과", null)]
    [InlineData("badcmd", false, "안 옴", "통과", "통과", "통과", "다른 명령")]
    [InlineData("noask", false, "통과", "안 옴", "통과", "통과", null)]
    [InlineData("ignoreallow", false, "실패", "통과", "통과", "통과", "듣지 않")]
    [InlineData("preallowed", false, "안 옴", "통과", "통과", "통과", "묻지 않고")]
    [InlineData("extra", true, "통과", "통과", "통과", "통과", null)]
    [InlineData("denyanswer", false, "통과", "실패", "통과", "통과", null)]
    [InlineData("noweb", false, "통과", "통과", "안 옴", "통과", null)] // CLI가 웹 가져오기를 묻지 않음 (내장 문서 사이트 목록이 바뀜 등)
    public async Task SelfTest_FakeClaude(string variant, bool passed, string bash, string ask, string web, string stream, string? bashDetail)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        var result = await CliSelfTest.RunAsync(_dir, "haiku", "hb-orchestration", (spec, workspace) =>
        {
            Assert.Equal(PermissionLevel.Command, spec.Level);
            Assert.StartsWith(Path.Combine(_dir, "selftest"), workspace, StringComparison.OrdinalIgnoreCase);
            // 가짜 claude는 TasksRoot 자리를 시험 갈래 이름으로 쓴다 (자가 시험은 할 일 폴더를 쓰지 않음)
            return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId,
                variant.Length == 0 ? "none" : variant, Path.Combine(_dir, "t.jsonl")], workspace);
        });
        Assert.Equal("0.0.0-fake", result.Version);
        Assert.Equal(passed, result.Passed);
        Assert.Equal([bash, ask, web, stream], result.Steps.Select(s => s.State));
        if (bashDetail is not null) Assert.Contains(bashDetail, result.Steps[0].Detail);
        if (variant == "badcmd") Assert.Contains("something-else", result.Steps[0].Detail);
    }

    /// <summary>부른 쪽이 취소하면(설정 화면을 떠남) 실패가 아니라 AbortedError — 설정 처리기가 기록하지 않는다 (C2). 이미 취소된 토큰도 500 없이.</summary>
    [Fact]
    public async Task SelfTest_CallerCancelled_IsAborted()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await CliSelfTest.RunAsync(_dir, "haiku", "hb-orchestration", (spec, workspace) =>
            new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId, "none",
                Path.Combine(_dir, "t.jsonl")], workspace), cts.Token);
        Assert.Equal(CliSelfTest.AbortedError, result.Error);
        Assert.False(CliSelfTest.Running);
    }

    /// <summary>claude를 찾지 못하면 그 까닭이 맨 위 오류로 (C5).</summary>
    [Fact]
    public async Task SelfTest_MissingClaude_ErrorOnTop()
    {
        var result = await CliSelfTest.RunAsync(_dir, "haiku", "hb-orchestration", (spec, workspace) =>
            new ProcessSpec(Path.Combine(_dir, "no-claude.exe"), [], workspace));
        Assert.False(result.Passed);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("init", result.Error);
    }
}
