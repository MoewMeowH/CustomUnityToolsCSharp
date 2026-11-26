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

    /// <summary>agy.json을 직접 바꾼다 (앱 재시작 뒤 남은 상태 흉내).</summary>
    private void WriteState(AgyRelay relay, Func<AgyRelay.State, AgyRelay.State> change) =>
        File.WriteAllText(Path.Combine(_data, "agy.json"),
            System.Text.Json.JsonSerializer.Serialize(change(relay.Read(out _)), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    private static List<string> Strings(JsonNode? node) => [.. node!.AsArray().Select(x => x!.GetValue<string>())];

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
    public void RelaySettings_ModeSandbox_DenyRequiredRecommendedAndDataWrite_AllowCurrent()
    {
        var s = JsonNode.Parse(AgyCheck.RelaySettings([@"C:\home", @"F:\"], [@"C:\ProgramData", @"c:\home\"], @"C:\relay\current", @"C:\home\data"))!;
        Assert.Equal("", s["agentMode"]!.GetValue<string>());
        Assert.True(s["enableTerminalSandbox"]!.GetValue<bool>());
        // 쓰기 deny: 필수 경로(프로필 — PowerShell 프로필 · ~/.gemini statusLine) · 데이터 폴더(이 파일 · statusLine 스크립트 · 보관함)를 중계 중인 agy가 고치지 못하게
        Assert.Equal(["read_file(C:/home)", "read_file(F:/)", "read_file(C:/ProgramData)", "write_file(C:/home)", "write_file(F:/)", "write_file(C:/home/data)"],
            Strings(s["permissions"]!["deny"]));
        Assert.Equal(["write_file(C:/relay/current)"], Strings(s["permissions"]!["allow"]));
    }

    private const string Current = @"C:\relay\current";

    [Fact]
    public void Inspect_Global_NeverBlocks_RemovesOldRelayRulesOnly_KeepsOtherKeys()
    {
        var relayJson = AgyCheck.RelaySettings([@"C:\home", @"G:\"], [], Current, @"C:\home\data");
        // 전역이 없어도 · auto여도 · 샌드박스가 꺼져도 중계를 막지 않는다 (규칙은 중계 설정 파일에)
        Assert.True(AgyCheck.Inspect(null, relayJson, Current).Ok);
        var mine = """{"agentMode":"auto","enableTerminalSandbox":false,"trustedWorkspaces":["C:\\x"],"permissions":{"deny":["read_file(D:/mine)"],"allow":["command(git)"]}}""";
        var own = AgyCheck.Inspect(mine, relayJson, Current);
        Assert.True(own.Ok);
        Assert.Empty(own.Warnings);
        Assert.False(own.OpenSuggestion);
        Assert.Equal(0, own.OldRelayRules);

        // 옛 중계 규칙(대소문자만 달라도)은 넣을 JSON에서 빼고, 사용자 항목 · 다른 키는 그대로
        var old = """{"agentMode":"","trustedWorkspaces":["C:\\x"],"permissions":{"deny":["read_file(c:/home)","read_file(G:/)","read_file(D:/mine)"],"allow":["write_file(C:/relay/current)","command(git)"]}}""";
        var r = AgyCheck.Inspect(old, relayJson, Current);
        Assert.True(r.Ok);
        Assert.True(r.OpenSuggestion);
        Assert.Equal(3, r.OldRelayRules);
        Assert.Contains(r.Warnings, w => w.Contains("옛 중계 규칙 3개"));
        var s = JsonNode.Parse(r.SuggestedJson)!;
        Assert.Equal(["read_file(D:/mine)"], Strings(s["permissions"]!["deny"]));
        Assert.Equal(["command(git)"], Strings(s["permissions"]!["allow"]));
        Assert.Equal(@"C:\x", s["trustedWorkspaces"]![0]!.GetValue<string>());
        Assert.Equal("", s["agentMode"]!.GetValue<string>()); // 모드는 사용자 몫 — 넣을 JSON이 바꾸지 않는다

        // 옛 권고(statusLine 스크립트 폴더 write_file deny)도 옛 중계 규칙
        var status = AgyCheck.Inspect("""{"permissions":{"deny":["write_file(C:/home/data/agy-status)"]}}""", relayJson, Current, @"C:\home\data\agy-status\statusline.ps1");
        Assert.Equal(1, status.OldRelayRules);

        // 깨진 파일: 막지 않고, 필요한 키만 담았다고 경고 (덮으라고 권하지 않게)
        var broken = AgyCheck.Inspect("{ not json", relayJson, Current);
        Assert.True(broken.Ok);
        Assert.Contains(broken.Warnings, w => w.Contains("기존 내용과 합쳐"));
    }

    [Fact]
    public void Inspect_Global_WarnsWideAllow_AndDenyOverRelay()
    {
        // agy가 --settings와 전역 permissions를 합치는지 모름 — 합쳐지면 중계에도 걸리는 항목은 경고
        var relayJson = AgyCheck.RelaySettings([@"C:\home"], [], Current, @"C:\home\data");
        var wide = AgyCheck.Inspect("""{"permissions":{"allow":["read_file(*)","command(*)","command(git)"]}}""", relayJson, Current);
        Assert.True(wide.Ok);
        Assert.Equal(2, wide.Warnings.Count(w => w.Contains("넓은 항목")));
        var over = AgyCheck.Inspect("""{"permissions":{"deny":["read_file(C:/)","write_file(D:/x)"]}}""", relayJson, Current);
        Assert.Single(over.Warnings, w => w.Contains("중계 폴더"));
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

        // 전역 agy 설정이 없어도 막지 않는다 — 중계 규칙은 앱이 쓰는 중계 설정 파일에
        Assert.False(File.Exists(_settings));
        Assert.Contains("카나리", await relay.WhyNotReadyAsync());
        Assert.True(File.Exists(relay.RelaySettingsPath));
        Assert.False(File.Exists(_settings)); // 전역은 고치지 않는다

        await PassCanary(relay);
        Assert.Null(await relay.WhyNotReadyAsync());
        // 전역 settings.json 방식 때 통과한 같은 판 — 막는 방식이 바뀌었으니 다시 확인
        WriteState(relay, s => s with { VerifiedScheme = null });
        Assert.Contains("막는 방식", await relay.WhyNotReadyAsync());
        WriteState(relay, s => s with { VerifiedScheme = AgyRelay.Scheme });
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
        Assert.Null(relay.CreateCanary());
        Assert.True(File.Exists(relay.CanaryFile));
        Assert.StartsWith(_data, relay.CanaryFile); // deny 구역(데이터 폴더)
        var canary = relay.Read(out _).Canary!;
        var token = canary.Token;
        var control = canary.Control!;
        Assert.True(File.Exists(Path.Combine(relay.CurrentFolder, AgyRelay.ControlFile)));
        // 할 일 파일: 셸 읽기가 먼저 (도구 읽기가 막힌 뒤에는 agy가 셸을 "우회"라며 거절했다), 두 단계 모두 대조 파일
        var task = File.ReadAllText(Path.Combine(relay.CurrentFolder, AgyRelay.CanaryTask));
        Assert.True(task.IndexOf(AgyRelay.CanaryOut2, StringComparison.Ordinal) < task.IndexOf("파일 읽기 도구", StringComparison.Ordinal));
        Assert.Contains($"type \"{relay.CanaryFile}\", {AgyRelay.ControlFile}", task);
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
        // 전역에 옛 중계 규칙이 남아 있으면 --settings가 막았는지 가를 수 없다 — 통과로 적지 않는다
        File.WriteAllText(_settings, $$$"""{"permissions":{"deny":["read_file({{{AgyCheck.RulePath(_home)}}})"]}}""");
        var old = await relay.JudgeCanaryAsync();
        Assert.False(old.Passed);
        Assert.Contains("옛 중계 규칙", old.Message);
        File.WriteAllText(_settings, "{}");
        Assert.True((await relay.JudgeCanaryAsync()).Passed);
        Assert.Equal("1.5.14", relay.Read(out _).VerifiedVersion);
        Assert.Equal(AgyRelay.Scheme, relay.Read(out _).VerifiedScheme);
        Assert.False(File.Exists(relay.CanaryFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(relay.CurrentFolder));
    }

    [Fact]
    public async Task Prepare_CopiesWorkspaceFilesOnly_Collect_ArchivesAndClears()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
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
        Assert.True(relay.Check().Ok);
        _temps = [AgyCheck.RulePath(_dir)];
        Assert.Contains(relay.Check().Problems, p => p.Contains("임시 폴더"));
        Assert.Contains("임시 폴더", await relay.WhyNotReadyAsync());
    }

    [Fact]
    public void Inspect_BadShapes_NoExceptions_JsoncAccepted()
    {
        var relayJson = AgyCheck.RelaySettings([@"C:\home"], [], @"C:\relay\current", @"C:\home\data");
        // permissions가 객체가 아니어도 예외가 아니다 (설정 화면이 500이 되지 않게)
        Assert.True(AgyCheck.Inspect("""{"permissions":[]}""", relayJson, Current).Ok);
        // 주석 · 끝 쉼표
        var jsonc = """
            {
              // 손으로 고친 줄
              "permissions": { "deny": ["read_file(C:/home)",], "allow": [] },
            }
            """;
        var r = AgyCheck.Inspect(jsonc, relayJson, Current);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("읽지 못했습니다"));
        Assert.Contains(r.Warnings, w => w.Contains("옛 중계 규칙 1개"));
    }

    [Fact]
    public void RelaySettingsFile_WrittenByCheck_RestoredWhenChanged_GlobalUntouched()
    {
        var relay = Relay();
        File.WriteAllText(_settings, """{"agentMode":"auto"}""");
        var check = relay.Check();
        Assert.True(check.Ok, string.Join(" / ", check.Problems));
        var json = File.ReadAllText(relay.RelaySettingsPath);
        Assert.Equal(check.RelayJson, json);
        Assert.StartsWith(_data, relay.RelaySettingsPath); // deny 구역(데이터 폴더)
        Assert.Contains($"read_file({AgyCheck.RulePath(_ws)})", json); // 등록된 작업 폴더
        Assert.Contains($"write_file({AgyCheck.RulePath(_data)})", json);

        File.WriteAllText(relay.RelaySettingsPath, """{"agentMode":"auto"}"""); // 바뀐 파일은 다시 맞춘다
        relay.Check();
        Assert.Equal(json, File.ReadAllText(relay.RelaySettingsPath));
        Assert.Equal("""{"agentMode":"auto"}""", File.ReadAllText(_settings));

        // 전역을 못 읽으면(잠금) 넣을 JSON을 만들지 않는다 — 필요한 키만 든 JSON으로 덮으면 사용자 설정이 사라진다
        using (new FileStream(_settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = relay.Check();
            Assert.Equal("", locked.SuggestedJson);
            Assert.False(locked.OpenSuggestion);
            Assert.Contains(locked.Warnings, w => w.Contains("읽지 못했습니다"));
        }

        // 중계 설정 파일을 못 쓰면 막고, 계산값을 '앱이 쓴 내용'으로 보이지 않는다
        File.Delete(relay.RelaySettingsPath);
        Directory.CreateDirectory(relay.RelaySettingsPath);
        var unwritable = relay.Check();
        Assert.False(unwritable.Ok);
        Assert.Null(unwritable.RelayJson);
    }

    [Fact]
    public void ShellStartup_DefinesAgyWithSettings_QuotesPaths_EncodedForPowerShell()
    {
        var relay = Relay();
        var startup = relay.ShellStartup()!;
        Assert.Contains("Remove-Item Alias:agy, Alias:agy.exe", startup); // 프로필 별칭이 함수보다 먼저 잡히지 않게
        var call = $"& '{Environment.ProcessPath}' --settings '{relay.RelaySettingsPath}' @args";
        Assert.Contains($"function global:agy {{ {call} }}", startup);
        Assert.Contains($"function global:agy.exe {{ {call} }}", startup);
        Assert.Contains("Write-Warning", startup); // 함수로 잡히지 않으면(지울 수 없는 별칭) 알린다

        var o = Options();
        o.DataDir = Path.Combine(_dir, "it's data");
        Assert.Contains("it''s data", new AgyRelay(() => o, () => _settings, _ => Task.FromResult(_version), () => _home, () => _temps).ShellStartup());
        // PowerShell은 ‘ ’ ‚ ‛도 작은따옴표로 읽는다 — 모두 두 번
        Assert.Equal("'O’’Brien ‘‘x‚‚‛‛ y''z'", AgyRelay.PowerShellQuote("O’Brien ‘x‚‛ y'z"));
        // 상대 AgyPath: 셸은 current에서 시작하므로 앱 기준 전체 경로로
        var rel = Options();
        rel.AgyPath = Path.GetRelativePath(Environment.CurrentDirectory, Environment.ProcessPath!);
        Assert.Contains($"& '{Environment.ProcessPath}' --settings", new AgyRelay(() => rel, () => _settings, _ => Task.FromResult(_version), () => _home, () => _temps).ShellStartup());

        var line = Terminals.ShellCommandLine("powershell", @"C:\Windows\System32", startup);
        Assert.StartsWith("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoExit -EncodedCommand ", line);
        Assert.Equal(startup, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(line.Split(' ')[^1])));
        Assert.Equal("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo", Terminals.ShellCommandLine("powershell", @"C:\Windows\System32"));
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
    public async Task RunMode_RequestWaitDone_SharesSlotWithPrepare_Cancel()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        Assert.NotNull((await relay.RequestRunAsync("r1", "x")).Error); // 카나리 확인 전
        await PassCanary(relay);

        var (error, id, until) = await relay.RequestRunAsync("r1", "# 지시\n\n형식\n\n# 요청\n\n코드", TimeSpan.FromMinutes(10));
        Assert.Null(error);
        Assert.InRange(until - DateTimeOffset.Now, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
        var request = File.ReadAllText(Path.Combine(relay.CurrentFolder, AgyRelay.RequestFile));
        Assert.StartsWith("# 지시", request);
        Assert.Contains("output.md", request);
        // 한 자리: 다른 요청 · 지휘자 agy_prepare · agy_collect는 건드리지 못한다
        Assert.NotNull((await relay.RequestRunAsync("r2", "y")).Error);
        Assert.Contains("run_mode", (await relay.PrepareAsync("r1", _ws, "z", null)).Error);
        Assert.NotNull(relay.Collect("r1").Error);
        Assert.Equal("gone", relay.RunResult("r2", id).Status); // 다른 방 토큰
        Assert.Equal("waiting", relay.RunResult("r1", id).Status);

        // 빈 파일 · 처음 본 크기는 아직 쓰는 중으로 본다 — 두 번 연속 같아야 받는다
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile), "");
        Assert.Equal("waiting", relay.RunResult("r1", id).Status);
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile), "{\"findings\":[]}");
        Assert.Equal("waiting", relay.RunResult("r1", id).Status);
        using (new FileStream(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("waiting", relay.RunResult("r1", id).Status); // agy가 열고 쓰는 중
        var (status, text, _) = relay.RunResult("r1", id);
        Assert.Equal(("done", "{\"findings\":[]}"), (status, text));
        Assert.True(File.Exists(Path.Combine(relay.ArchiveRoot, id!, AgyRelay.OutputFile)));
        Assert.Null(relay.Read(out _).Run);
        Assert.Equal("gone", relay.RunResult("r1", id).Status);

        var (_, second, _) = await relay.RequestRunAsync("r1", "again");
        Assert.False(relay.CancelRun("r2", second));
        Assert.True(relay.CancelRun("r1", second));
        Assert.Null(relay.Read(out _).Run);
        Assert.Empty(Directory.EnumerateFileSystemEntries(relay.CurrentFolder));
    }

    [Fact]
    public async Task RunMode_SlotReleased_OnTurnEnd_Oversize_Stale()
    {
        var relay = Relay();
        Assert.Null(relay.SetEnabled(true, true));
        await PassCanary(relay);

        // 턴 · 실행이 끝남 (서버가 cancel 없이 꺼짐): 그 방 것만 푼다
        var (_, first, _) = await relay.RequestRunAsync("r1", "a");
        Assert.False(relay.ReleaseRunFor("r2"));
        Assert.True(relay.ReleaseRunFor("r1"));
        Assert.Null(relay.Read(out _).Run);
        Assert.True(Directory.Exists(Path.Combine(relay.ArchiveRoot, first!)));

        // 받지 못한 결과(크기 초과)도 자리를 푼다 — 받아 갈 곳이 없어 막히지 않게
        var (_, big, _) = await relay.RequestRunAsync("r1", "b");
        File.WriteAllText(Path.Combine(relay.CurrentFolder, AgyRelay.OutputFile), new string('x', AgyRelay.MaxOutputBytes + 1));
        Assert.Equal("gone", relay.RunResult("r1", big).Status);
        Assert.Null(relay.Read(out _).Run);

        // 제한 시간이 지난 자리 (앱 재시작 등): 새 요청 · agy_prepare를 막지 않는다
        var stale = new AgyRelay.RunInfo("old", "r9", DateTimeOffset.Now.AddHours(-1), ForRunMode: true, Until: DateTimeOffset.Now.AddMinutes(-2));
        Assert.True(stale.Stale(DateTimeOffset.Now));
        Assert.False((stale with { Until = DateTimeOffset.Now.AddMinutes(5) }).Stale(DateTimeOffset.Now));
        WriteState(relay, s => s with { Run = stale });
        Assert.Null((await relay.RequestRunAsync("r1", "c")).Error);
        WriteState(relay, s => s with { Run = stale });
        Assert.Null((await relay.PrepareAsync("r1", _ws, "z", null)).Error);
    }

    [Fact]
    public void McpConfig_ServerEnv_AddedToBaseServerOnly()
    {
        File.WriteAllText(Path.Combine(_ws, ".mcp.json"),
            """{"mcpServers":{"hb-orchestration":{"command":"x.exe","env":{"KEEP":"1"}},"other":{"command":"y.exe"}}}""");
        var (path, missing) = McpConfigFile.WriteRoom(_ws, ["hb-orchestration", "other"], null, _data, "abcdef123456", null,
            ("hb-orchestration", new Dictionary<string, string> { [RoomHub.AgyUrlVariable] = "http://127.0.0.1:5290", [RoomHub.AgyTokenVariable] = "t" }));
        Assert.Empty(missing);
        var servers = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!;
        Assert.Equal("1", servers["hb-orchestration"]!["env"]!["KEEP"]!.GetValue<string>());
        Assert.Equal("t", servers["hb-orchestration"]!["env"]![RoomHub.AgyTokenVariable]!.GetValue<string>());
        Assert.Null(servers["other"]!["env"]);
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
