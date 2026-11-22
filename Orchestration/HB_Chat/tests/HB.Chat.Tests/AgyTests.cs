using System.Diagnostics;
using System.Text.Json.Nodes;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>agy 수동 중계: 설정 검사(읽기만) · 필수 deny 계산 · 중계 폴더 검사 · 카나리 자동 판정 · prepare/collect · 도구 허용 · 방 규칙.</summary>
public sealed class AgyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-agy-" + Guid.NewGuid().ToString("N"));
    private readonly string _home, _data, _ws, _relay, _settings;
    private string? _version = "1.5.14";

    public AgyTests()
    {
        _home = Path.Combine(_dir, "home");
        _data = Path.Combine(_dir, "data");
        _ws = Path.Combine(_dir, "ws");
        _relay = Path.Combine(_dir, "relay");
        _settings = Path.Combine(_dir, "agy-settings.json");
        foreach (var d in new[] { _home, _data, _ws }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ChatOptions Options() => new()
    {
        DataDir = _data,
        AgyRelayDir = _relay,
        AgyPath = Environment.ProcessPath, // 있는 파일이면 됨 — 판은 askVersion이 준다
        Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = _ws },
    };

    // 시험 폴더가 %TEMP% 안이라 임시 폴더 목록은 따로 준다 (기본은 실제 TEMP — 그대로면 데이터 폴더 검사에 걸림)
    private IReadOnlyList<string> _temps = [];

    private AgyRelay Relay() => new(Options, () => _settings, _ => Task.FromResult(_version), () => _home, () => _temps);

    /// <summary>통과하는 agy 설정 (필수 deny · Default · 샌드박스 · allow) — 다른 키는 남기는지 보려고 trustedWorkspaces도.</summary>
    private void WriteGoodSettings(AgyRelay relay, string agentMode = "", bool sandbox = true)
    {
        var json = new JsonObject
        {
            ["agentMode"] = agentMode,
            ["enableTerminalSandbox"] = sandbox,
            ["permissions"] = new JsonObject
            {
                ["deny"] = new JsonArray([.. relay.RequiredDeny().Select(p => (JsonNode?)JsonValue.Create($"read_file({p})"))]),
                ["allow"] = new JsonArray(JsonValue.Create($"write_file({AgyCheck.RulePath(relay.CurrentFolder)})")),
            },
            ["trustedWorkspaces"] = new JsonArray(JsonValue.Create(@"C:\x")),
        };
        File.WriteAllText(_settings, json.ToJsonString());
    }

    [Fact]
    public void RulePath_And_Covers_NeedDriveLetter_AndSegmentBoundary()
    {
        Assert.Equal("C:/Users/x", AgyCheck.RulePath(@"c:\Users\x\"));
        Assert.Equal("D:/", AgyCheck.RulePath(@"D:\"));
        Assert.True(AgyCheck.Covers("C:/Users/me", "C:/Users/me/AppData/Local"));
        Assert.True(AgyCheck.Covers(@"f:\ai", "F:/AI/ToolWithCS"));
        Assert.True(AgyCheck.Covers("F:/", "F:/AI"));
        Assert.False(AgyCheck.Covers("C:/Users/m", "C:/Users/me"));
        // 2회차 실측: 드라이브 없는 항목은 도구 읽기를 막지 못했다
        Assert.False(AgyCheck.Covers("/AI/ToolWithCS", "F:/AI/ToolWithCS"));
    }

    [Fact]
    public void Inspect_Problems_ForModeSandboxDeny_WarningsForAllow()
    {
        string[] required = [@"C:\home", @"F:\"];
        Assert.False(AgyCheck.Inspect(null, required, [], @"C:\relay\current").Ok);

        var good = """{"agentMode":"","enableTerminalSandbox":true,"permissions":{"deny":["read_file(C:/home)","read_file(F:/)"],"allow":["write_file(C:/relay/current)"]}}""";
        var ok = AgyCheck.Inspect(good, required, [], @"C:\relay\current");
        Assert.True(ok.Ok);
        Assert.Empty(ok.Warnings);

        Assert.Contains(AgyCheck.Inspect(good.Replace("\"agentMode\":\"\"", "\"agentMode\":\"auto\""), required, [], @"C:\relay\current").Problems, p => p.Contains("Default"));
        Assert.Contains(AgyCheck.Inspect(good.Replace("\"agentMode\":\"\",", ""), required, [], @"C:\relay\current").Problems, p => p.Contains("Default"));
        Assert.Contains(AgyCheck.Inspect(good.Replace("true", "false"), required, [], @"C:\relay\current").Problems, p => p.Contains("샌드박스"));

        var driveless = AgyCheck.Inspect(good.Replace("read_file(F:/)", "read_file(/AI)"), required, [], @"C:\relay\current");
        Assert.Equal(["F:/"], driveless.MissingDeny);
        Assert.Contains(driveless.Warnings, w => w.Contains("드라이브 문자가 없는"));

        var noAllow = AgyCheck.Inspect(good.Replace("\"write_file(C:/relay/current)\"", ""), required, [], @"C:\relay\current");
        Assert.True(noAllow.Ok); // 승인창이 늘 뿐 — 막지 않는다
        Assert.Contains(noAllow.Warnings, w => w.Contains("write_file(C:/relay/current)"));
    }

    [Fact]
    public void Inspect_SuggestedJson_KeepsOtherKeys_AddsMissing()
    {
        var json = """{"agentMode":"auto","trustedWorkspaces":["C:\\x"],"permissions":{"deny":["read_file(G:/)"]}}""";
        var r = AgyCheck.Inspect(json, [@"C:\home", @"G:\"], [@"C:\ProgramData"], @"C:\relay\current");
        var s = JsonNode.Parse(r.SuggestedJson)!;
        Assert.Equal("", s["agentMode"]!.GetValue<string>());
        Assert.True(s["enableTerminalSandbox"]!.GetValue<bool>());
        Assert.Equal(@"C:\x", s["trustedWorkspaces"]![0]!.GetValue<string>());
        var deny = s["permissions"]!["deny"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Equal(["read_file(G:/)", "read_file(C:/home)", "read_file(C:/ProgramData)"], deny);
        Assert.Equal(["write_file(C:/relay/current)"], s["permissions"]!["allow"]!.AsArray().Select(x => x!.GetValue<string>()));

        // 깨진 파일: 막고, 필요한 키만 담는다 (덮으라고 권하지 않게 경고)
        var broken = AgyCheck.Inspect("{ not json", [@"C:\home"], [], @"C:\relay\current");
        Assert.False(broken.Ok);
        Assert.Contains(broken.Warnings, w => w.Contains("기존 내용과 합쳐"));
    }

    [Fact]
    public void RequiredPaths_SkipRelayDrive_DropNested()
    {
        var req = AgyCheck.RequiredPaths(@"C:\Users\me", @"C:\Users\me\AppData\Local\HB_Chat", [@"F:\AI\Proj", @"D:\Other"], [@"C:\", @"D:\", @"F:\"], @"C:\agy_relay");
        Assert.Equal(["C:/Users/me", "D:/", "F:/"], req);
    }

    [Fact]
    public void WhyBadRelay_OutsideRequiredOnly_NotRoot_Absolute()
    {
        string[] req = ["C:/Users/me", "F:/"];
        Assert.Null(AgyCheck.WhyBadRelay(@"C:\agy_relay", req));
        Assert.Contains("프로필", AgyCheck.WhyBadRelay(@"C:\Users\me\relay", req));
        Assert.Contains("품습니다", AgyCheck.WhyBadRelay(@"C:\Users", req));
        Assert.Contains("드라이브 루트", AgyCheck.WhyBadRelay(@"C:\", req));
        Assert.Contains("절대 경로", AgyCheck.WhyBadRelay(@"relay", req));
    }

    [Fact]
    public void OutputFormat_NeedsBothHeadings()
    {
        Assert.Null(AgyCheck.OutputFormatProblem("## 지적\n- [high] a.cs:1 — x\n\n## 읽은 파일\n- a.cs\n"));
        Assert.Contains("읽은 파일", AgyCheck.OutputFormatProblem("## 지적\n- x\n"));
    }

    [Fact]
    public async Task Ready_NeedsEnable_Telemetry_Settings_AndCanaryVersion()
    {
        var relay = Relay();
        Assert.Contains("꺼져", await relay.WhyNotReadyAsync());
        Assert.Contains("Telemetry", relay.SetEnabled(true, telemetryOff: false));
        Assert.Null(relay.SetEnabled(true, telemetryOff: true));
        Assert.True(relay.Enabled);
        Assert.True(Directory.Exists(relay.CurrentFolder));

        Assert.Contains("설정 검사", await relay.WhyNotReadyAsync()); // 설정 파일 없음
        WriteGoodSettings(relay, agentMode: "auto");
        Assert.Contains("Default", await relay.WhyNotReadyAsync());
        WriteGoodSettings(relay);
        Assert.Contains("카나리", await relay.WhyNotReadyAsync());

        await PassCanary(relay);
        Assert.Null(await relay.WhyNotReadyAsync());
        _version = "1.5.15"; // 올라가면 다시 카나리 (1.3.3 → 1.5.14 때 기본 모드가 바뀜)
        Assert.Contains("1.5.15", await relay.WhyNotReadyAsync());
    }

    private async Task PassCanary(AgyRelay relay)
    {
        Assert.Null(relay.CreateCanary());
        var control = relay.Read(out _).Canary!.Control!;
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryOut), $"Permission denied … Matches user-configured deny rule.\n확인 코드: {control}");
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryOut2), $"type : Access is denied\n확인 코드: {control}");
        var (passed, message) = await relay.JudgeCanaryAsync();
        Assert.True(passed, message);
    }

    [Fact]
    public async Task Canary_FailsWhenTokenLeaks_UnsureWhenFilesMissing_PassClears()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        WriteGoodSettings(relay);
        Assert.Null(relay.CreateCanary());
        Assert.True(File.Exists(relay.CanaryFile));
        Assert.StartsWith(_data, relay.CanaryFile); // deny 구역(데이터 폴더)
        var canary = relay.Read(out _).Canary!;
        var token = canary.Token;
        var control = canary.Control!;
        Assert.True(File.Exists(Path.Combine(relay.CurrentFolder, AgyRelay.ControlFile)));
        // 시험임을 알리는 문구 없이 (agy가 옮겨 쓰지 않거나 가리지 않게)
        Assert.DoesNotContain("격리", File.ReadAllText(relay.CanaryFile));

        Assert.Contains("확인 못 함", (await relay.JudgeCanaryAsync()).Message);
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryOut), "denied\n" + control);
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryOut2), "# 메모\n" + token + "\n" + control);
        var (passed, message) = await relay.JudgeCanaryAsync();
        Assert.False(passed);
        Assert.Contains("셸 읽기", message);
        Assert.Null(relay.Read(out _).VerifiedVersion);

        // agy가 명령을 돌리지 않고 거절만 썼으면 (대조 글자 없음) 통과가 아니다
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryOut2), "셸 명령은 실행하지 않았습니다");
        var refused = await relay.JudgeCanaryAsync();
        Assert.False(refused.Passed);
        Assert.Contains(AgyRelay.CanaryOut2, refused.Message);

        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryOut2), "Access is denied\n" + control);
        Assert.True((await relay.JudgeCanaryAsync()).Passed);
        Assert.Equal("1.5.14", relay.Read(out _).VerifiedVersion);
        Assert.False(File.Exists(relay.CanaryFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(relay.CurrentFolder));
    }

    [Fact]
    public async Task Prepare_CopiesWorkspaceFilesOnly_Collect_ArchivesAndClears()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        WriteGoodSettings(relay);
        await PassCanary(relay);
        Directory.CreateDirectory(Path.Combine(_ws, "src"));
        File.WriteAllText(Path.Combine(_ws, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_ws, ".env"), "SECRET=1");
        Directory.CreateDirectory(Path.Combine(_ws, ".git"));
        File.WriteAllText(Path.Combine(_ws, ".git", "config"), "x");
        File.WriteAllText(Path.Combine(_dir, "outside.txt"), "x");

        foreach (var bad in new[] { "../outside.txt", ".env", ".git/config", Path.Combine(_ws, "src", "a.cs"), "src/none.cs", "src/./a.cs" })
            Assert.NotNull((await relay.PrepareAsync("r1", _ws, "봐줘", [bad])).Error);
        Assert.NotNull((await relay.PrepareAsync("r1", _ws, " ", ["src/a.cs"])).Error);

        var (error, runId, copied) = await relay.PrepareAsync("r1", _ws, "TerminalProcess를 봐줘", ["src/a.cs"]);
        Assert.Null(error);
        Assert.Equal([@"src\a.cs"], copied);
        Assert.Equal("class A {}", File.ReadAllText(Path.Combine(relay.CurrentFolder, "files", "src", "a.cs")));
        var request = File.ReadAllText(Path.Combine(relay.CurrentFolder, AgyRelay.RequestFile));
        Assert.StartsWith("TerminalProcess를 봐줘", request);
        Assert.Contains("## 읽은 파일", request);
        Assert.Contains("files/src/a.cs", request);

        Assert.Contains("다른 방", (await relay.PrepareAsync("r2", _ws, "x", null)).Error);
        Assert.Contains("없습니다", relay.Collect("r2").Error);
        var early = relay.Collect("r1");
        Assert.Equal(runId, early.RunId);
        Assert.Null(early.Text);

        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile), "## 지적\n- [low] a.cs:1 — x\n\n## 읽은 파일\n- files/src/a.cs\n");
        var got = relay.Collect("r1");
        Assert.Null(got.Error);
        Assert.Null(got.FormatProblem);
        Assert.Contains("[low]", got.Text);
        Assert.True(File.Exists(Path.Combine(relay.ArchiveRoot, runId!, AgyRelay.OutputFile)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(relay.CurrentFolder));
        Assert.Null(relay.Read(out _).Run);
        Assert.Equal(1, relay.ArchiveCount);
        Assert.Null(relay.ClearArchive());
        Assert.Equal(0, relay.ArchiveCount);
    }

    [Fact]
    public void ClearFolder_RemovesJunctionOnly_NotTarget()
    {
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        var folder = Path.Combine(_dir, "clear");
        Directory.CreateDirectory(folder);
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(folder, "link")}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(folder, "link")));
        AgyRelay.ClearFolder(folder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public async Task DataFolderInsideTemp_Blocks()
    {
        // 5291 실측: 샌드박스가 임시 폴더를 deny와 상관없이 열어 %TEMP% 안 카나리를 셸이 읽었다
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        WriteGoodSettings(relay);
        Assert.True(relay.Check().Ok);
        _temps = [AgyCheck.RulePath(_dir)];
        Assert.Contains(relay.Check().Problems, p => p.Contains("임시 폴더"));
        Assert.Contains("임시 폴더", await relay.WhyNotReadyAsync());
    }

    [Fact]
    public void Inspect_BadShapes_AreProblemsNotExceptions_JsoncAccepted_DenyOverRelay()
    {
        string[] required = [@"C:\home"];
        // permissions가 객체가 아니면 예외가 아니라 문제 (설정 화면이 500이 되지 않게)
        Assert.Contains(AgyCheck.Inspect("""{"agentMode":"","enableTerminalSandbox":true,"permissions":[]}""", required, [], @"C:\relay\current").Problems,
            p => p.Contains("permissions"));
        // 주석 · 끝 쉼표
        var jsonc = """
            {
              // 손으로 고친 줄
              "agentMode": "",
              "enableTerminalSandbox": true,
              "permissions": { "deny": ["read_file(C:/home)",], "allow": ["write_file(C:/relay/current)"] },
            }
            """;
        Assert.True(AgyCheck.Inspect(jsonc, required, [], @"C:\relay\current").Ok);
        // deny가 중계 폴더까지 덮으면 agy가 요청을 못 읽는다
        Assert.Contains(AgyCheck.Inspect("""{"agentMode":"","enableTerminalSandbox":true,"permissions":{"deny":["read_file(C:/home)","read_file(C:/)"]}}""",
            required, [], @"C:\relay\current").Problems, p => p.Contains("중계 폴더"));
    }

    [Theory]
    [InlineData(".env.local"), InlineData(".mcp.json"), InlineData(".mcp.local.json"), InlineData("appsettings.local.json"), InlineData("appsettings.Development.json"),
     InlineData("id_ed25519"), InlineData(".npmrc"), InlineData("credentials.json"), InlineData("server.pfx")]
    public void LooksSecret_Blocks(string name) => Assert.True(AgyRelay.LooksSecret(name));

    [Theory]
    [InlineData("appsettings.json"), InlineData("TerminalProcess.cs"), InlineData("README.md"), InlineData("id_rsa.pub.txt")]
    public void LooksSecret_Allows(string name) => Assert.False(AgyRelay.LooksSecret(name));

    [Fact]
    public async Task Prepare_DedupesSeparators_ArchivesUncollectedOnRePrepare_BlocksToolFolders()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        WriteGoodSettings(relay);
        await PassCanary(relay);
        Directory.CreateDirectory(Path.Combine(_ws, "src"));
        File.WriteAllText(Path.Combine(_ws, "src", "a.cs"), "class A {}");
        Directory.CreateDirectory(Path.Combine(_ws, ".claude"));
        File.WriteAllText(Path.Combine(_ws, ".claude", "settings.local.json"), "{}");
        Assert.Contains(".claude", (await relay.PrepareAsync("r1", _ws, "x", [".claude/settings.local.json"])).Error);

        var first = await relay.PrepareAsync("r1", _ws, "x", ["src/a.cs", @"src\a.cs"]);
        Assert.Null(first.Error);
        Assert.Single(first.Copied);
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile), "## 지적\n\n## 읽은 파일\n");
        var second = await relay.PrepareAsync("r1", _ws, "y", ["src/a.cs"]);
        Assert.Null(second.Error);
        // 받지 않은 앞 결과는 보관함으로
        Assert.True(File.Exists(Path.Combine(relay.ArchiveRoot, first.RunId!, AgyRelay.OutputFile)));
        Assert.False(File.Exists(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile)));
    }

    [Fact]
    public void ClearCurrent_RefusesWhenRelayPointsIntoProtectedPath()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "current"));
        File.WriteAllText(Path.Combine(_ws, "current", "keep.txt"), "x");
        ChatOptions IntoWorkspace()
        {
            var o = Options();
            o.AgyRelayDir = _ws; // 설정이 작업 폴더를 가리킴 — 검사는 막음으로 표시
            return o;
        }
        var relay = new AgyRelay(IntoWorkspace, () => _settings, _ => Task.FromResult(_version), () => _home, () => _temps);
        Assert.NotNull(relay.ClearCurrent());
        Assert.True(File.Exists(Path.Combine(_ws, "current", "keep.txt")));
    }

    [Fact]
    public void Enable_RelayRootGetsProtectedAcl_OwnerOnly()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        var acl = new DirectoryInfo(_relay).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var sids = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
        Assert.DoesNotContain(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null).Value, sids);
        Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, sids);
    }

    [Fact]
    public void Disabled_ReadError_BlocksAndDoesNotOverwrite()
    {
        File.WriteAllText(Path.Combine(_data, "agy.json"), "{ broken");
        var relay = Relay();
        Assert.False(relay.Enabled);
        Assert.NotNull(relay.SetEnabled(true, true));
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(_data, "agy.json")));
    }

    [Fact]
    public void Args_AgyToolsQuietOnlyWhenOn_ElseDisallowed_ReadAlwaysDisallowed()
    {
        var spec = new RunSpec(PermissionLevel.Command, "opus", "15eb061e-7153-4c14-83ba-083385ed6945", false, "C:/data/mcp.json", "hb-orchestration", "규칙")
            { TerminalMcpConfigPath = "C:/data/mcp-terminal-r.json" };
        string Allowed(RunSpec s)
        {
            var args = ClaudeArgs.Build(s, ["opus"]);
            var i = args.ToList().LastIndexOf("--allowedTools");
            return string.Join(" | ", args.Skip(i));
        }
        string Disallowed(RunSpec s)
        {
            var args = ClaudeArgs.Build(s, ["opus"]).ToList();
            var i = args.IndexOf("--disallowedTools");
            return string.Join(" | ", args.Skip(i).TakeWhile(a => a != "--strict-mcp-config"));
        }
        foreach (var level in new[] { PermissionLevel.Command, PermissionLevel.App })
        {
            Assert.Contains("mcp__hb-chat__agy_prepare", Allowed(spec with { Level = level, AgyRelay = true }));
            Assert.DoesNotContain("mcp__hb-chat__agy_prepare", Disallowed(spec with { Level = level, AgyRelay = true }));
            Assert.DoesNotContain("mcp__hb-chat__agy_prepare", Allowed(spec with { Level = level }));
            Assert.Contains("mcp__hb-chat__agy_collect", Disallowed(spec with { Level = level }));
        }
        Assert.Contains("mcp__hb-chat__agy_prepare", Disallowed(spec with { Level = PermissionLevel.Read, AgyRelay = true }));
    }

    [Fact]
    public void Rules_AgyOnlyWhenOn()
    {
        var on = RoomRules.For(PermissionLevel.Command, terminal: true, agy: true);
        Assert.Contains("agy_prepare", on);
        Assert.Contains("record_verdicts에 넣지 않는다", on);
        Assert.DoesNotContain("agy_prepare", RoomRules.For(PermissionLevel.Command, terminal: true));
    }
}
