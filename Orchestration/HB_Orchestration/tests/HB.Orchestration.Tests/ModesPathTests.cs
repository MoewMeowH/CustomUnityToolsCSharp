using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>modes.json 찾는 순서(서버·대시보드 공용)와 대시보드의 "최근 호출이 쓴 modes.json".</summary>
public sealed class ModesPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-modespath-" + Guid.NewGuid().ToString("N")[..8]);

    public ModesPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_(string relative, string text = "{}")
    {
        var path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // ── 공용 찾는 순서 ────────────────────────────────────────────────────────

    [Fact]
    public void Resolver_follows_order_when_specified_paths_exist()
    {
        var configured = File_("cfg/modes.json");
        var env = File_("env/modes.json");
        var cwd = Path.Combine(_dir, "proj");
        File_("proj/hb-modes.json");
        var server = Path.Combine(_dir, "server");
        File_("server/modes.json");

        static (string?, ModesPathResolver.Source, string?) T(ModesPathResolver.Resolution r) => (r.Path, r.Source, r.Error);
        Assert.Equal((configured, ModesPathResolver.Source.Configured, null), T(ModesPathResolver.Resolve(configured, env, cwd, server)));
        Assert.Equal((env, ModesPathResolver.Source.Environment, null), T(ModesPathResolver.Resolve(" ", env, cwd, server)));
        Assert.Equal((Path.Combine(cwd, "hb-modes.json"), ModesPathResolver.Source.ProjectFile, null), T(ModesPathResolver.Resolve(null, "", cwd, server)));
        Assert.Equal((Path.Combine(server, "modes.json"), ModesPathResolver.Source.ServerFolder, null), T(ModesPathResolver.Resolve(null, null, null, server)));
        Assert.NotNull(ModesPathResolver.Resolve(null, null, null, Path.Combine(_dir, "empty")).Error);
    }

    [Theory]
    [InlineData(true)]   // 설정 Orchestration:ModesPath
    [InlineData(false)]  // HB_MODES_PATH
    public void Resolver_specified_path_missing_is_error_not_fallback(bool configured)
    {
        var cwd = Path.Combine(_dir, "proj");
        File_("proj/hb-modes.json");
        var server = Path.Combine(_dir, "server");
        File_("server/modes.json");
        var typo = Path.Combine(_dir, "typo", "modes.jsn");

        var r = configured ? ModesPathResolver.Resolve(typo, null, cwd, server) : ModesPathResolver.Resolve(null, typo, cwd, server);

        Assert.Null(r.Path);
        Assert.Equal(configured ? ModesPathResolver.Source.Configured : ModesPathResolver.Source.Environment, r.Source);
        Assert.Contains("지정한 modes.json이 없습니다", r.Error);
        Assert.Contains(Path.GetFullPath(typo), r.Error);
    }

    [Fact]
    public void Resolver_relative_configured_path_uses_server_folder_not_session_folder()
    {
        var cwd = Path.Combine(_dir, "proj");
        File_("proj/custom.json");                       // 세션 폴더에도 같은 이름이 있어도
        var server = Path.Combine(_dir, "server");
        var expected = File_("server/custom.json");

        var r = ModesPathResolver.Resolve("custom.json", null, cwd, server);

        Assert.Equal((expected, ModesPathResolver.Source.Configured), (r.Path, r.Source));
        // 서버 폴더에 없으면 세션 폴더 파일로 넘어가지 않고 오류
        File.Delete(expected);
        Assert.Contains(Path.Combine(server, "custom.json"), ModesPathResolver.Resolve("custom.json", null, cwd, server).Error);
    }

    [Fact]
    public async Task Server_run_fails_when_configured_modes_file_is_missing()
    {
        using var h = new ModeRunnerHarness();
        File.Delete(h.ModesPath);

        var result = await h.Run("seq");

        Assert.Contains("지정한 modes.json이 없습니다", result);
        Assert.Contains(h.ModesPath, result);
    }

    [Fact]
    public async Task Server_provenance_records_full_modes_path()
    {
        using var h = new ModeRunnerHarness();

        await h.Run("seq");

        Assert.Equal(Path.GetFullPath(h.ModesPath), h.Records().Single().Provenance!.Modes!.Path);
    }

    // ── 대시보드 편집 대상 ──────────────────────────────────────────────────

    private ModesConfigService Service(string? dashboardModesPath = null, string? env = null) =>
        new(Options.Create(new DashboardOptions { McpConfigDir = Path.Combine(_dir, "publish"), ModesPath = dashboardModesPath }),
            name => name == ModesPathResolver.EnvVar ? env : null);

    [Fact]
    public void Dashboard_uses_server_folder_modes_by_default()
    {
        var path = File_("publish/modes.json");

        var snap = Service().Load();

        Assert.Equal(path, snap.ModesPath);
        Assert.Equal(ModesPathResolver.Label(ModesPathResolver.Source.ServerFolder), snap.ModesPathSource);
        Assert.Equal(BlobStore.Sha256Hex(File.ReadAllBytes(path)), snap.ModesSha256);
    }

    [Fact]
    public void Dashboard_follows_own_HB_MODES_PATH_like_the_server()
    {
        File_("publish/modes.json");
        var temp = File_("harness/modes.base.json");

        var snap = Service(env: temp).Load();

        Assert.Equal(temp, snap.ModesPath);
        Assert.Equal(ModesPathResolver.EnvVar, snap.ModesPathSource);
    }

    [Fact]
    public void Dashboard_appsettings_ModesPath_wins_over_env_and_missing_file_is_error()
    {
        File_("publish/modes.json");
        var env = File_("harness/modes.base.json");
        var configured = File_("publish/custom.json");
        File_("publish/appsettings.json", """{ "Orchestration": { "ModesPath": "custom.json" } }""");

        Assert.Equal(configured, Service(env: env).Load().ModesPath);

        // 서버처럼 지정 경로의 파일이 없으면 오류 (다음 후보로 넘어가지 않음)
        File.Delete(configured);
        var ex = Assert.Throws<ModesConfigException>(() => Service(env: env).Load());
        Assert.Contains("지정한 modes.json이 없습니다", ex.Message);
    }

    [Fact]
    public void Dashboard_missing_HB_MODES_PATH_file_is_error()
    {
        File_("publish/modes.json");

        var ex = Assert.Throws<ModesConfigException>(() => Service(env: Path.Combine(_dir, "harness", "typo.json")).Load());
        Assert.Contains(ModesPathResolver.EnvVar, ex.Message);
    }

    [Fact]
    public void Dashboard_direct_option_wins_and_missing_direct_file_is_an_error()
    {
        File_("publish/modes.json");
        var direct = File_("direct.json");

        var snap = Service(dashboardModesPath: direct, env: File_("harness/x.json")).Load();
        Assert.Equal((direct, ModesConfigService.SourceDashboardOption), (snap.ModesPath, snap.ModesPathSource));

        Assert.Throws<ModesConfigException>(() => Service(dashboardModesPath: Path.Combine(_dir, "none.json")).Load());
    }

    // ── 최근 호출이 쓴 modes.json ──────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(9));

    private static CallRecord Call(string? modesPath, string? sha, double daysAgo, string? runId) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = Now.AddDays(-daysAgo), Tool = "run_mode", Agent = "a", Model = "m", Prompt = "p",
        RunId = runId,
        Provenance = modesPath is null ? null : new CallProvenance { Modes = new FileProvenance { Path = modesPath, Sha256 = sha } },
    };

    [Fact]
    public void Recent_groups_by_path_and_sha_within_window()
    {
        var main = Path.Combine(_dir, "publish", "modes.json");
        var temp = Path.Combine(_dir, "harness", "modes.base.json");
        var uses = ModesUsage.Recent(
        [
            Call(main, "aa", 1, "r1"), Call(main, "aa", 1, "r1"), Call(main.ToUpperInvariant(), "AA", 2, "r2"),
            Call(main, "bb", 3, "r3"),
            Call(temp, "cc", 0.5, "r4"),
            Call(temp, "cc", 8, "old"),          // 7일 밖
            Call(null, null, 0.1, "noprov"),     // provenance 없음
        ], Now, ModesUsage.Window);

        Assert.Equal(3, uses.Count);
        Assert.Equal((temp, 1, 1), (uses[0].Path, uses[0].Calls, uses[0].Runs)); // 가장 최근
        var aa = uses.Single(u => u.Sha256 == "aa");
        Assert.Equal((3, 2), (aa.Calls, aa.Runs));
    }

    [Fact]
    public void Recent_keeps_old_relative_records_unresolved()
    {
        var main = Path.Combine(_dir, "publish", "modes.json");
        var uses = ModesUsage.Recent([Call("review_tmp/harness/modes.base.json", "aa", 1, "r1"), Call(@"review_tmp\harness\modes.base.json", "aa", 1, "r2")],
            Now, ModesUsage.Window);

        var u = Assert.Single(uses);                     // 구분자만 다른 같은 기록은 한 묶음
        Assert.Equal(("review_tmp/harness/modes.base.json", true, 2), (u.Path, u.Relative, u.Runs));
        Assert.Single(ModesUsage.OtherFiles(uses, main));  // 대시보드 폴더로 풀지 않고 "다른 파일"로 보인다
    }

    [Fact]
    public void SameFile_splits_current_and_earlier_content()
    {
        var main = Path.Combine(_dir, "publish", "modes.json");
        var temp = Path.Combine(_dir, "harness", "modes.base.json");
        var uses = ModesUsage.Recent(
            [Call(main, "now", 1, "r1"), Call(main, "old1", 2, "r2"), Call(main, "old2", 3, "r3"), Call(temp, "cc", 1, "r4")], Now, ModesUsage.Window);

        var (current, earlier) = ModesUsage.SameFile(uses, main, "NOW");

        Assert.Equal("now", Assert.Single(current).Sha256);
        Assert.Equal(["old1", "old2"], earlier.Select(u => u.Sha256));   // 최근 사용 순, 다른 파일은 빠짐
    }

    [Fact]
    public void OtherFiles_and_ModifiedSince()
    {
        var main = Path.Combine(_dir, "publish", "modes.json");
        var temp = Path.Combine(_dir, "harness", "modes.base.json");
        var uses = ModesUsage.Recent([Call(main, "old", 1, "r1"), Call(temp, "cc", 1, "r2")], Now, ModesUsage.Window);

        var other = Assert.Single(ModesUsage.OtherFiles(uses, main));
        Assert.Equal(temp, other.Path);

        var same = uses.Single(u => u.Path == main);
        Assert.True(ModesUsage.ModifiedSince(same, main, "new"));
        Assert.False(ModesUsage.ModifiedSince(same, main, "OLD"));
        Assert.False(ModesUsage.ModifiedSince(other, main, "new")); // 다른 경로는 "수정됨"이 아니라 경고 대상
    }
}
