using System.Text.RegularExpressions;
using HB.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>가짜 CLI 실행기: 돌린 명령을 적고, 버전 · 최신 확인 · 올리기에 정해 둔 답을 준다. 올리기가 성공하면 그 도구의 설치 버전이 최신으로 바뀐다.</summary>
public sealed class FakeCli
{
    public static readonly CliTools.Paths Paths = new(
        @"C:\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe",
        @"C:\fake\claude.exe", @"C:\fake\unity.exe", @"C:\fake\node.exe", @"C:\fake\node_modules\npm\bin\npm-cli.js", @"C:\npm\node_modules",
        @"C:\fake\agy\bin\agy.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");

    public List<CliTools.Command> Commands { get; } = [];
    public Dictionary<string, string> Installed { get; } = new() { [CliTools.Codex] = "0.165.0", [CliTools.Claude] = "2.1.325", [CliTools.Unity] = "1.0.0", [CliTools.Agy] = "1.5.14" };
    public Dictionary<string, string> Latest { get; } = new() { [CliTools.Codex] = "0.170.0", [CliTools.Claude] = "2.1.327", [CliTools.Unity] = "1.0.1", [CliTools.Agy] = "1.5.15" };
    /// <summary>올리기 답 (기본: 성공). ExitCode null이면 시간 제한 · 실행 실패.</summary>
    public Func<CliTools.Command, (int? ExitCode, string Output)>? OnUpdate { get; set; }
    /// <summary>올리기가 이 신호를 기다린다 (동시에 두 번 시험).</summary>
    public TaskCompletionSource? Hold { get; set; }
    /// <summary>읽은 agy manifest 주소 (네트워크 대신).</summary>
    public List<string> Fetched { get; } = [];
    /// <summary>설치 명령이 성공하면 이 자리를 돌려준다 (설치 뒤 다시 찾기 흉내).</summary>
    public CliTools.Paths? AfterInstall { get; set; }
    /// <summary>실패 종료 코드여도 깔린다 (스크립트 마지막 단계만 실패).</summary>
    public bool InstallsAnyway { get; set; }

    public CliTools Tools(CliTools.Paths? paths = null) => new(() => (_installed ? AfterInstall : null) ?? paths ?? Paths, RunAsync, FetchAsync);

    private bool _installed;

    private Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        lock (Fetched) Fetched.Add(url);
        return Task.FromResult<string?>($$"""{"version":"{{Latest[CliTools.Agy]}}","url":"https://example.invalid/agy.exe","sha512":"00"}""");
    }

    private static string ToolOf(CliTools.Command c) =>
        c.FileName.Contains("unity", StringComparison.Ordinal) || c.Args.Any(a => a.Contains("unity.com", StringComparison.Ordinal)) ? CliTools.Unity
        : c.FileName.Contains("agy", StringComparison.Ordinal) || c.Args.Any(a => a.Contains("antigravity", StringComparison.Ordinal)) ? CliTools.Agy
        : c.FileName.Contains("claude", StringComparison.Ordinal) || c.Args.Any(a => a.Contains("claude", StringComparison.Ordinal)) ? CliTools.Claude
        : CliTools.Codex;

    private async Task<(int? ExitCode, string Output)> RunAsync(CliTools.Command command, TimeSpan timeout, CancellationToken ct)
    {
        lock (Commands) Commands.Add(command);
        var tool = ToolOf(command);
        if (command.Args is ["--version"]) return (0, tool == CliTools.Codex ? "codex-cli " + Installed[tool] : Installed[tool] + (tool == CliTools.Claude ? " (Claude Code)" : ""));
        if (command.Args.Contains("view")) return (0, Latest[tool] + "\n");
        if (command.Args.Contains("--check")) return (0, $$"""{ "success": true, "data": { "currentVersion": "{{Installed[tool]}}", "latestVersion": "{{Latest[tool]}}" } }""");
        if (Hold is { } hold) await hold.Task;
        var result = OnUpdate?.Invoke(command) ?? (0, "changed 2 packages in 3s");
        if (result.ExitCode == 0 || InstallsAnyway)
        {
            Installed[tool] = Latest[tool];
            if (command.FileName.EndsWith("powershell.exe", StringComparison.Ordinal) || command.Args.Contains("-g")) _installed = AfterInstall is not null;
        }
        return result;
    }
}

/// <summary>설정 화면 CLI 도구: 정해 둔 명령 줄 · 올릴 수 없는 경우 · 최신 버전 읽기 · 확인과 올리기 결과.</summary>
public sealed class CliToolsTests
{
    private static readonly CliTools.Paths P = FakeCli.Paths;

    [Fact]
    public void Commands_AreFixedLines()
    {
        Assert.Equal([P.NpmCli!, "view", "@openai/codex", "version"], CliTools.LatestCommand(CliTools.Codex, P)!.Args);
        Assert.Equal(P.Node, CliTools.LatestCommand(CliTools.Codex, P)!.FileName);
        Assert.Equal([P.NpmCli!, "view", "@anthropic-ai/claude-code", "version"], CliTools.LatestCommand(CliTools.Claude, P)!.Args);
        Assert.Equal(["self-update", "--check", "--json", "--no-banner", "--non-interactive"], CliTools.LatestCommand(CliTools.Unity, P)!.Args);

        var (codex, _) = CliTools.UpdateCommand(CliTools.Codex, P);
        Assert.Equal(P.Node, codex!.FileName);
        Assert.Equal([P.NpmCli!, "install", "-g", "@openai/codex@latest"], codex.Args);
        var (claude, _) = CliTools.UpdateCommand(CliTools.Claude, P);
        Assert.Equal(P.Claude, claude!.FileName);
        Assert.Equal(["update"], claude.Args);
        var (unity, _) = CliTools.UpdateCommand(CliTools.Unity, P);
        Assert.Equal(P.Unity, unity!.FileName);
        Assert.Equal(["self-update", "--yes", "--no-banner", "--non-interactive"], unity.Args);

        Assert.Equal(P.Codex, CliTools.VersionCommand(CliTools.Codex, P)!.FileName);
        Assert.Equal(["--version"], CliTools.VersionCommand(CliTools.Codex, P)!.Args);

        // agy: 판은 --version, 올리기는 update (옵션 없음), 최신은 명령이 아니라 manifest
        Assert.Equal(P.Agy, CliTools.VersionCommand(CliTools.Agy, P)!.FileName);
        var (agy, _) = CliTools.UpdateCommand(CliTools.Agy, P);
        Assert.Equal(P.Agy, agy!.FileName);
        Assert.Equal(["update"], agy.Args);
        Assert.Null(CliTools.LatestCommand(CliTools.Agy, P));
        Assert.EndsWith("/manifests/windows_amd64.json", CliTools.AgyManifestUrl(System.Runtime.InteropServices.Architecture.X64));
        Assert.EndsWith("/manifests/windows_arm64.json", CliTools.AgyManifestUrl(System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Fact]
    public void Install_OnlyWhenMissing_FixedOfficialCommands()
    {
        Assert.All(CliTools.Tools, t => Assert.False(CliTools.Installs(t.Name, P)));
        Assert.False(CliTools.Installs("npm", P with { Codex = null }));

        var (codex, _) = CliTools.UpdateCommand(CliTools.Codex, P with { Codex = null });
        Assert.Equal(P.Node, codex!.FileName);
        Assert.Equal([P.NpmCli!, "install", "-g", "--prefix", @"C:\npm", "@openai/codex@latest"], codex.Args); // 앱이 찾는 npm 전역 자리에
        foreach (var (tool, missing, url) in new[]
                 {
                     (CliTools.Claude, P with { Claude = null }, "https://claude.ai/install.ps1"),
                     (CliTools.Unity, P with { Unity = null }, "https://unity.com/install.ps1"),
                     (CliTools.Agy, P with { Agy = null }, "https://antigravity.google/cli/install.ps1"),
                 })
        {
            Assert.True(CliTools.Installs(tool, missing));
            var (command, why) = CliTools.UpdateCommand(tool, missing);
            Assert.Null(why);
            Assert.Equal(P.PowerShell, command!.FileName); // 절대 경로 — PATH의 다른 powershell을 쓰지 않는다
            Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"$ProgressPreference='SilentlyContinue'; irm {url} | iex"], command.Args);
            Assert.Contains($"irm {url} | iex\"", command.Display);
        }
        Assert.NotNull(CliTools.InstallNote(CliTools.Unity));
        Assert.Null(CliTools.InstallNote(CliTools.Agy));
    }

    [Fact]
    public void Install_NotWhenSettingPointsToMissingFile()
    {
        var bad = P with { Agy = null, Codex = null, BadSetting = new Dictionary<string, string> { [CliTools.Agy] = "Chat:AgyPath", [CliTools.Codex] = "Chat:CodexPath" } };
        Assert.False(CliTools.Installs(CliTools.Agy, bad));
        Assert.False(CliTools.Installs(CliTools.Codex, bad));
        var (command, why) = CliTools.UpdateCommand(CliTools.Agy, bad);
        Assert.Null(command);
        Assert.Equal("설정 Chat:AgyPath의 파일이 없습니다 — 경로를 고치거나 비우세요.", why);
        Assert.Contains("Chat:CodexPath", CliTools.UpdateCommand(CliTools.Codex, bad).WhyNot);
    }

    [Fact]
    public async Task Update_RefusedWhenScreenAndStateDisagree()
    {
        var fake = new FakeCli();
        var tools = fake.Tools(FakeCli.Paths with { Claude = null });
        Assert.Equal(CliTools.ChangedError, await tools.UpdateAsync(CliTools.Claude)); // 화면은 '올리기'였는데 지금은 없음 — 확인 창 없이 설치하지 않는다
        Assert.Equal(CliTools.ChangedError, await tools.UpdateAsync(CliTools.Unity, install: true)); // 화면은 '설치'였는데 지금은 있음
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task Install_ExitCodeFailsButFileAppears_CountsAsInstalled()
    {
        // 종료 코드 1 · 아무것도 안 깔림 → 실패
        var fake = new FakeCli { AfterInstall = FakeCli.Paths, OnUpdate = _ => (1, "내려받기 실패") };
        var tools = fake.Tools(FakeCli.Paths with { Unity = null });
        Assert.Null(await tools.UpdateAsync(CliTools.Unity, install: true));
        Assert.False(tools.LastUpdate!.Ok);
        // 종료 코드 1이지만 실행 파일이 생김 (마지막 단계만 실패) → 설치됨
        fake.InstallsAnyway = true;
        Assert.Null(await tools.UpdateAsync(CliTools.Unity, install: true));
        Assert.True(tools.LastUpdate!.Ok);
        Assert.Equal("1.0.1", tools.LastUpdate.After);
    }

    [Fact]
    public async Task Install_RecordsInstall_AndFindsTheNewPlace()
    {
        var fake = new FakeCli { AfterInstall = FakeCli.Paths };
        var tools = fake.Tools(FakeCli.Paths with { Agy = null });
        Assert.Null(await tools.UpdateAsync(CliTools.Agy, install: true));
        var u = tools.LastUpdate!;
        Assert.True(u.Install);
        Assert.Equal((null, "1.5.15", true), (u.Before, u.After, u.Ok)); // 설치 뒤 다시 찾은 자리의 판
        Assert.Contains(fake.Commands, c => c.Args.Any(a => a.EndsWith("irm https://antigravity.google/cli/install.ps1 | iex", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("npm")]
    [InlineData("codex; calc")]
    [InlineData("CODEX")]
    public void UpdateCommand_UnknownTool_Refused(string? tool)
    {
        var (command, why) = CliTools.UpdateCommand(tool, P);
        Assert.Null(command);
        Assert.Equal("알 수 없는 도구입니다.", why);
    }

    [Fact]
    public void UpdateCommand_WhenNotPossible_SaysWhy()
    {
        // npm 전역 설치 폴더 밖의 codex (Chat:CodexPath) — npm으로 올리지 않는다
        var elsewhere = P with { Codex = @"D:\tools\codex.exe" };
        Assert.Contains("npm 전역 설치가 아닙니다", CliTools.UpdateCommand(CliTools.Codex, elsewhere).WhyNot);
        // 이름만 비슷한 폴더는 그 아래가 아니다
        Assert.Contains("npm 전역 설치가 아닙니다", CliTools.UpdateCommand(CliTools.Codex, P with { Codex = @"C:\npm\node_modules\@openai\codex-other\codex.exe" }).WhyNot);
        Assert.Contains("node", CliTools.UpdateCommand(CliTools.Codex, P with { Node = null }).WhyNot);
        Assert.Contains("node", CliTools.UpdateCommand(CliTools.Codex, P with { NpmCli = null }).WhyNot);
        // 없으면 설치 — 설치할 수 없을 때만 까닭
        Assert.Contains("Node.js", CliTools.UpdateCommand(CliTools.Codex, P with { Codex = null, Node = null }).WhyNot);
        Assert.Contains("powershell.exe", CliTools.UpdateCommand(CliTools.Claude, P with { Claude = null, PowerShell = null }).WhyNot);
        Assert.Contains("powershell.exe", CliTools.UpdateCommand(CliTools.Agy, P with { Agy = null, PowerShell = null }).WhyNot);
        // node가 없으면 npm 쪽 최신 확인도 없다 — unity는 스스로 묻는다
        Assert.Null(CliTools.LatestCommand(CliTools.Codex, P with { Node = null }));
        Assert.Null(CliTools.LatestCommand(CliTools.Claude, P with { Node = null }));
        Assert.NotNull(CliTools.LatestCommand(CliTools.Unity, P with { Node = null }));
        Assert.Null(CliTools.VersionCommand(CliTools.Unity, P with { Unity = null }));
    }

    [Fact]
    public void ParseLatest_NpmLine_AndUnityJson()
    {
        Assert.Equal("0.170.0", CliTools.ParseLatest(CliTools.Codex, "0.170.0\n"));
        Assert.Null(CliTools.ParseLatest(CliTools.Codex, "npm error code E404"));
        const string json = """
            { "success": true, "command": "self-update", "data": { "currentVersion": "1.0.0", "latestVersion": "1.0.1", "platform": "win32-x64" }, "errors": [] }
            """;
        Assert.Equal("1.0.1", CliTools.ParseLatest(CliTools.Unity, json));
        Assert.Equal("1.0.1", CliTools.ParseLatest(CliTools.Unity, "배너 한 줄\n" + json)); // 앞에 다른 글이 붙어도
        Assert.Null(CliTools.ParseLatest(CliTools.Unity, "{ \"data\": { \"currentVersion\": \"1.0.0\" } }"));
        Assert.Null(CliTools.ParseLatest(CliTools.Unity, "{ \"data\": 3 }"));
        Assert.Null(CliTools.ParseLatest(CliTools.Unity, "{ broken"));
        Assert.Null(CliTools.ParseLatest(CliTools.Unity, "1.0.1")); // JSON이 아니면 읽지 않는다
        // agy manifest (설치 스크립트가 읽는 것)
        Assert.Equal("1.5.15", CliTools.ParseLatest(CliTools.Agy, """{"version":"1.5.15","url":"https://x/agy.exe","sha512":"ab"}"""));
        Assert.Null(CliTools.ParseLatest(CliTools.Agy, """{"version":3}"""));
        Assert.Null(CliTools.ParseLatest(CliTools.Agy, "[1]"));
        Assert.Null(CliTools.ParseLatest(CliTools.Agy, "<html>404</html>"));
    }

    [Fact]
    public void Tail_KeepsLastLines_StripsColor()
    {
        var output = string.Join("\r\n", Enumerable.Range(1, 100).Select(i => $"\u001b[32mline {i}\u001b[0m")) + "\r\n\r\n";
        var tail = CliTools.Tail(output).Split('\n');
        Assert.Equal(CliTools.OutputLines, tail.Length);
        Assert.Equal("line 61", tail[0]);
        Assert.Equal("line 100", tail[^1]);
        Assert.Equal("", CliTools.Tail(""));
    }

    [Fact]
    public async Task Check_ReadsInstalledAndLatest_ForAllTools()
    {
        var fake = new FakeCli();
        var tools = fake.Tools();
        Assert.Null(tools.LastCheck);
        Assert.Null(await tools.CheckAsync(CancellationToken.None));
        Assert.Equal(new CliTools.Versions("0.165.0", "0.170.0"), tools.LastCheck!.Tools[CliTools.Codex]);
        Assert.Equal(new CliTools.Versions("2.1.325", "2.1.327"), tools.LastCheck.Tools[CliTools.Claude]);
        Assert.Equal(new CliTools.Versions("1.0.0", "1.0.1"), tools.LastCheck.Tools[CliTools.Unity]);
        Assert.Equal(new CliTools.Versions("1.5.14", "1.5.15"), tools.LastCheck.Tools[CliTools.Agy]);
        Assert.Equal(7, fake.Commands.Count); // 판 넷 + 최신 셋 (agy 최신은 manifest)
        Assert.Single(fake.Fetched);
        Assert.DoesNotContain(fake.Commands, c => c.Args.Contains("install") || c.Args.Contains("update") || c.Args.Contains("--yes"));
        Assert.False(tools.Busy);
    }

    [Fact]
    public async Task Check_MissingTool_ShowsNothingForIt()
    {
        var fake = new FakeCli();
        var tools = fake.Tools(FakeCli.Paths with { Unity = null, Node = null });
        Assert.Null(await tools.CheckAsync(CancellationToken.None));
        Assert.Equal(new CliTools.Versions(null, null), tools.LastCheck!.Tools[CliTools.Unity]);
        Assert.Equal(new CliTools.Versions("0.165.0", null), tools.LastCheck.Tools[CliTools.Codex]);
    }

    [Fact]
    public async Task Update_Success_RecordsBeforeAfter_AndRefreshesCheck()
    {
        var fake = new FakeCli();
        var tools = fake.Tools();
        await tools.CheckAsync(CancellationToken.None);
        Assert.Null(await tools.UpdateAsync(CliTools.Codex));
        var u = tools.LastUpdate!;
        Assert.Equal((CliTools.Codex, "0.165.0", "0.170.0", true), (u.Tool, u.Before, u.After, u.Ok));
        Assert.Equal("changed 2 packages in 3s", u.Output);
        Assert.Equal(new CliTools.Versions("0.170.0", "0.170.0"), tools.LastCheck!.Tools[CliTools.Codex]);
        Assert.Equal("2.1.325", tools.LastCheck.Tools[CliTools.Claude].Installed); // 다른 도구는 그대로
        Assert.Single(fake.Commands, c => c.Args.Contains("install"));
    }

    [Fact]
    public async Task Update_FailureOrTimeout_IsRecordedAsFailed()
    {
        var fake = new FakeCli { OnUpdate = _ => (1, "npm error code EPERM") };
        var tools = fake.Tools();
        Assert.Null(await tools.UpdateAsync(CliTools.Codex));
        Assert.Equal((false, "0.165.0", "0.165.0", "npm error code EPERM"), (tools.LastUpdate!.Ok, tools.LastUpdate.Before, tools.LastUpdate.After, tools.LastUpdate.Output));

        fake.OnUpdate = _ => (null, "시간 제한(300초) 안에 끝나지 않아 끝냈습니다.");
        Assert.Null(await tools.UpdateAsync(CliTools.Unity));
        Assert.False(tools.LastUpdate!.Ok);
        Assert.Contains("시간 제한", tools.LastUpdate.Output);
    }

    [Fact]
    public async Task Update_NotStarted_ReturnsWhy_AndRunsNothing()
    {
        var fake = new FakeCli();
        var tools = fake.Tools(FakeCli.Paths with { Codex = @"D:\tools\codex.exe" });
        Assert.Equal("알 수 없는 도구입니다.", await tools.UpdateAsync("npm"));
        Assert.Contains("npm 전역 설치가 아닙니다", await tools.UpdateAsync(CliTools.Codex));
        Assert.Empty(fake.Commands);
        Assert.Null(tools.LastUpdate);
    }

    [Fact]
    public async Task OnlyOneAtATime()
    {
        var fake = new FakeCli { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var tools = fake.Tools();
        var first = tools.UpdateAsync(CliTools.Unity);
        Assert.True(tools.Busy);
        Assert.Equal(CliTools.BusyError, await tools.UpdateAsync(CliTools.Codex));
        Assert.Equal(CliTools.BusyError, await tools.CheckAsync(CancellationToken.None));
        fake.Hold.SetResult();
        Assert.Null(await first);
        Assert.False(tools.Busy);
        Assert.Equal(CliTools.Unity, tools.LastUpdate!.Tool);
        Assert.DoesNotContain(fake.Commands, c => c.Args.Contains("install"));
    }
}

/// <summary>설정 화면의 CLI 도구 칸: 버전 확인 · 올리기 단추(POST), 도는 방이 있으면 거절, claude를 올리면 본 판이 바뀐다.</summary>
[Collection(SelfTestCollection)]
public sealed class CliToolsPageTests(ChatApp app) : IClassFixture<ChatApp>
{
    /// <summary>카드 자가 시험을 돌리는 시험과 같은 묶음 — 자가 시험이 도는 동안은 올리기가 거절된다 (CliSelfTest.Running은 전역).</summary>
    public const string SelfTestCollection = "cli-selftest";

    private async Task<(HttpClient Client, string Token)> OpenAsync()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = app.BaseAddress };
        var page = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings"));
        return (client, Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
    }

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string handler, string? tool = null)
    {
        List<KeyValuePair<string, string>> form = [new("__RequestVerificationToken", token)];
        if (tool is not null) form.Add(new("tool", tool));
        using var req = new HttpRequestMessage(HttpMethod.Post, "/Settings?handler=" + handler) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("Origin", app.BaseAddress.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Page_CheckThenUpdate()
    {
        var (client, token) = await OpenAsync();
        using (client)
        {
            var before = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings"));
            Assert.Contains(">CLI 도구</h3>", before);
            Assert.Contains("handler=CliCheck", before);
            Assert.Contains("handler=CliUpdate", before);
            Assert.Empty(app.Cli.Commands); // 화면을 여는 것만으로는 아무것도 돌리지 않는다

            using (var check = await PostAsync(client, token, "CliCheck"))
                Assert.Equal("/Settings?done=cli-checked#cli-tools", check.Headers.Location?.OriginalString);
            var checkedPage = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings?done=cli-checked"));
            Assert.Contains("data-active=\"connect\"", checkedPage);
            Assert.Contains("<td>0.165.0</td>", checkedPage);
            Assert.Contains("<td>0.170.0</td>", checkedPage);
            Assert.Contains("<td>2.1.327 (npm 기준)</td>", checkedPage);

            // claude 올리기 — 본 판이 새 버전으로 바뀐다 (확인 필요 띠로 이어짐)
            var versions = app.Services.GetRequiredService<CliVersions>();
            versions.Note("2.1.325");
            using (var update = await PostAsync(client, token, "CliUpdate", CliTools.Claude))
                Assert.Equal("/Settings?done=cli-updated#cli-tools", update.Headers.Location?.OriginalString);
            Assert.Equal("2.1.327", versions.Read().Seen);
            Assert.True(versions.Read().NeedsCheck);
            var updated = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings?done=cli-updated"));
            Assert.Contains("claude CLI: 2.1.325 → 2.1.327로 올렸습니다.", updated);
            Assert.Contains("마지막 올리기", updated);

            // 알 수 없는 도구 이름은 아무것도 돌리지 않는다
            var ran = app.Cli.Commands.Count;
            using (var bad = await PostAsync(client, token, "CliUpdate", "npm"))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, bad.StatusCode);
                Assert.Contains("알 수 없는 도구입니다.", System.Net.WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
            }
            Assert.Equal(ran, app.Cli.Commands.Count);
        }
    }

    /// <summary>도는 방이 있으면 올리기를 시작하지 않는다 — 방이 말을 받자마자(Busy) 센다.</summary>
    [Fact]
    public async Task Update_RefusedWhileARoomRuns()
    {
        var room = app.Room("도는 방", ChatApp.User("안녕", 0));
        var hub = app.Services.GetRequiredService<RoomHub>();
        var (client, token) = await OpenAsync();
        using (client)
        {
            var ran = app.Cli.Commands.Count;
            Assert.Null(hub.Send(room.Id, "say 1"));
            Assert.Null(hub.TryHoldStarts(out var active)); // 보낸 직후 — 실행이 끝나기 전
            Assert.Equal(1, active);
            if (hub.IsBusy(room.Id))
            {
                using var res = await PostAsync(client, token, "CliUpdate", CliTools.Unity);
                var html = System.Net.WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
                // 요청이 닿기 전에 가짜 실행이 끝났으면 올리기가 돌았을 수 있다 — 그때는 거절 문구를 볼 수 없다
                if (app.Cli.Commands.Count == ran) Assert.Contains("방 1개가 도는 중입니다 (백그라운드 작업 포함) — 끝난 뒤 올리세요.", html);
            }
            for (var i = 0; i < 200 && hub.IsBusy(room.Id); i++) await Task.Delay(50);
        }
    }

    /// <summary>올리는 동안(새 실행 막힘) 방은 새 말을 받지 않고, 풀리면 다시 받는다. 막힌 동안 두 번째 올리기도 시작하지 않는다.</summary>
    [Fact]
    public async Task WhileHeld_RoomsRefuseNewMessages_AndSecondUpdateIsRefused()
    {
        var room = app.Room("막힌 방", ChatApp.User("안녕", 0));
        var hub = app.Services.GetRequiredService<RoomHub>();
        for (var i = 0; i < 200; i++) // 다른 시험의 실행이 끝나기를 기다림
        {
            if (hub.TryHoldStarts(out _) is { } free)
            {
                free.Dispose();
                break;
            }
            await Task.Delay(50);
        }
        using (var hold = hub.TryHoldStarts(out var active))
        {
            Assert.NotNull(hold);
            Assert.Equal(0, active);
            Assert.Equal(RoomHub.StartsHeldError, hub.Send(room.Id, "say 1"));
            Assert.False(hub.IsBusy(room.Id));
            Assert.Null(hub.TryHoldStarts(out _)); // 이미 막혀 있음

            var (client, token) = await OpenAsync();
            using (client)
            {
                var ran = app.Cli.Commands.Count;
                using var res = await PostAsync(client, token, "CliUpdate", CliTools.Unity);
                Assert.Contains(CliTools.BusyError, System.Net.WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
                Assert.Equal(ran, app.Cli.Commands.Count);
            }
        }
        Assert.Null(hub.Send(room.Id, "say 1")); // 풀린 뒤에는 받는다
        for (var i = 0; i < 200 && hub.IsBusy(room.Id); i++) await Task.Delay(50);
    }
}

/// <summary>실행기: 명령이 남긴 자식이 출력 파이프를 물고 있어도 프로세스가 끝나면 곧 돌아온다.</summary>
public sealed class CliToolsProcessTests
{
    [Fact]
    public async Task Run_ReturnsSoonAfterExit_EvenIfAChildKeepsThePipes()
    {
        // cmd가 뒤에 남기는 ping(약 20초)이 표준 출력 · 오류 손잡이를 물려받는다
        var command = new CliTools.Command("cmd.exe", ["/c", "echo done& start /b ping -n 20 127.0.0.1"]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (exit, output) = await CliTools.RunProcessAsync(command, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"걸린 시간 {watch.Elapsed}");
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("PING")) // 남긴 ping 정리 (이 시험이 띄운 것만 — 방금 시작한 것)
            using (p)
                try { if (p.StartTime > DateTime.Now.AddSeconds(-30)) p.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _ = output;
    }

    [Fact]
    public async Task Run_Timeout_AndMissingFile_ReturnNoExitCode()
    {
        var (exit, output) = await CliTools.RunProcessAsync(new CliTools.Command("cmd.exe", ["/c", "ping -n 30 127.0.0.1"]), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Null(exit);
        Assert.Contains("시간 제한", output);
        var (exit2, output2) = await CliTools.RunProcessAsync(new CliTools.Command(@"C:\no-such-dir\no-such.exe", []), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Null(exit2);
        Assert.Contains("실행하지 못했습니다", output2);
    }
}
