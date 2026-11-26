using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HB.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>AGY 한도(statusLine 스크립트 · 읽기 · agy 설정 검사)와 한도 문턱 알림(7일 창 80 · 95%, 주기마다 한 번).</summary>
public sealed class LimitAlertsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-alerts-" + Guid.NewGuid().ToString("N"));

    public LimitAlertsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>2026-11-24 실측(agy 1.5.16) quota를 스크립트가 남기는 모양으로.</summary>
    private const string MeasuredLimits = """
        {"at":"2026-11-24T22:04:45.3710000+09:00","version":"1.5.16","quota":{"3p-5h":{"remaining_fraction":1,"reset_in_seconds":18000,"reset_time":"2026-11-24T18:04:45Z"},"3p-weekly":{"remaining_fraction":1,"reset_in_seconds":604800,"reset_time":"2026-12-01T13:04:45Z"},"gemini-5h":{"remaining_fraction":0.9959165,"reset_in_seconds":17533,"reset_time":"2026-11-24T17:56:58Z"},"gemini-weekly":{"remaining_fraction":0.9733236,"reset_in_seconds":570539,"reset_time":"2026-12-01T03:33:44Z"}}}
        """;

    // ---- AGY 읽기 ----

    [Fact]
    public void Agy_ParsesMeasuredQuota()
    {
        var snap = AgyLimits.Parse(MeasuredLimits, DateTimeOffset.MinValue)!;
        Assert.Equal(("agy", "agy 1.5.16"), (snap.Source, snap.Plan));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 22, 4, 45, 371, TimeSpan.FromHours(9)), snap.At);
        Assert.Equal(["Claude·GPT 5시간", "Claude·GPT 7일", "Gemini 5시간", "Gemini 7일"], snap.Windows.Select(w => w.Label));
        var gemini = snap.Windows.Single(w => w.Name == "gemini-weekly");
        Assert.Equal(2.67, gemini.Percent!.Value, 2);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 3, 33, 44, TimeSpan.Zero), gemini.ResetsAt);
        Assert.True(gemini.IsWeekly);
        // 안 쓴 창(사용률 0)은 움직이는 초기화 시각을 버린다
        var unused = snap.Windows.Single(w => w.Name == "3p-weekly");
        Assert.Equal((0.0, (DateTimeOffset?)null), (unused.Percent!.Value, unused.ResetsAt));
        Assert.False(snap.Windows.Single(w => w.Name == "gemini-5h").IsWeekly);
    }

    [Fact]
    public void Agy_AmountDisabledAndBadShapes()
    {
        var at = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        var snap = AgyLimits.Parse("""{"quota":{"x-weekly":{"remaining_amount":12.5},"y":{"remaining_fraction":0.5,"disabled":true},"z":3}}""", at)!;
        Assert.Equal(at, snap.At); // at이 없으면 파일 시각
        Assert.Equal(((double?)null, "남음 12.5"), (snap.Windows[0].Percent, snap.Windows[0].Status));
        Assert.Equal((50.0, "꺼짐"), (snap.Windows[1].Percent!.Value, snap.Windows[1].Status));
        Assert.Equal(2, snap.Windows.Count); // 객체가 아닌 bucket은 건너뜀
        Assert.NotNull(AgyLimits.Parse((char)0xFEFF + MeasuredLimits, at)); // BOM
        Assert.Null(AgyLimits.Parse("""{"at":"x"}""", at));
        Assert.Null(AgyLimits.Parse("""{"quota":{}}""", at));
        Assert.Null(AgyLimits.Parse("{ 깨짐", at));
        Assert.Null(AgyLimits.Read(Path.Combine(_dir, "없음.json")));
        // 같은 키가 두 번 · 넘치는 시각도 예외 없이 (파일 감시 콜백에서 서버가 끝나지 않게 — /self-review sub C2)
        Assert.Null(AgyLimits.Parse("""{"quota":{"a":{"remaining_fraction":0.5}},"quota":{}}""", at));
        var far = AgyLimits.Parse("""{"at":"9999-12-31T23:59:59+00:00","quota":{"a-weekly":{"remaining_fraction":0.1,"reset_time":"9999-12-31T23:59:59Z"}}}""", at)!;
        Assert.Equal((at, (DateTimeOffset?)null), (far.At, far.Windows[0].ResetsAt));
    }

    [Fact]
    public void Agy_EnsureScript_WritesOnceWithBom_AndRestoresEdits()
    {
        AgyLimits.EnsureScript(_dir);
        var path = AgyLimits.ScriptPath(_dir);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var written = File.GetLastWriteTimeUtc(path);
        AgyLimits.EnsureScript(_dir);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path)); // 같으면 다시 쓰지 않음
        File.WriteAllText(path, "Write-Output 'evil'");
        AgyLimits.EnsureScript(_dir);
        Assert.Equal(AgyLimits.Script, File.ReadAllText(path));
        Assert.DoesNotContain("\"", AgyLimits.Command(path)); // agy는 따옴표를 글자 그대로 넘긴다 (실측)
    }

    private static (int Exit, string Output) RunScript(string script, string stdin)
    {
        var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        return (p.ExitCode, output.Trim());
    }

    [Fact]
    public void Agy_Script_KeepsOnlyQuota_SkipsMissing_NeverFails()
    {
        AgyLimits.EnsureScript(_dir);
        var script = AgyLimits.ScriptPath(_dir);
        var limits = AgyLimits.LimitsPath(_dir);

        var (exit, output) = RunScript(script, """{"email":"me@example.com","transcript_path":"C:\\x\\t.jsonl","conversation_title":"한글 제목","version":"1.5.16","quota":{"gemini-weekly":{"remaining_fraction":0.75,"reset_time":"2026-12-01T03:33:44Z"},"3p-5h":{"remaining_fraction":1}}}""");
        Assert.Equal((0, "HB gemini-weekly 25%"), (exit, output));
        var text = File.ReadAllText(limits);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("transcript", text);
        var snap = AgyLimits.Read(limits)!;
        Assert.Equal(25.0, snap.Windows.Single(w => w.Name == "gemini-weekly").Percent!.Value, 3);
        Assert.Equal("agy 1.5.16", snap.Plan);

        // quota가 없으면(모델 고르기 전) 쓰지 않는다 — 앞 값 유지
        Assert.Equal((0, "HB"), RunScript(script, """{"agent_state":"authenticating"}"""));
        Assert.Equal(text, File.ReadAllText(limits));
        // 덮어쓰기 (파일이 있을 때) · 다 안 썼으면 0%
        Assert.Equal((0, "HB 0%"), RunScript(script, """{"quota":{"gemini-weekly":{"remaining_fraction":1}}}"""));
        Assert.Equal(0.0, AgyLimits.Read(limits)!.Windows[0].Percent!.Value);
        // 깨진 입력도 0으로 끝난다 (30번 연속 실패면 agy가 상태 줄을 끈다)
        Assert.Equal((0, "HB limits error"), RunScript(script, "{ 깨짐"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(limits)!, "*.tmp"));
    }

    // ---- agy 설정 검사 statusLine ----

    private const string GoodSettings = """{"trustedWorkspaces":["C:\\x"]}""";

    private static readonly string RelayJson = AgyCheck.RelaySettings([@"C:\Users"], [], @"C:\relay\current", @"C:\Data");

    private static AgyCheck.Result Inspect(string json, string script) => AgyCheck.Inspect(json, RelayJson, @"C:\relay\current", script);

    [Fact]
    public void Check_StatusLine_SuggestsOnlyWhenMissing()
    {
        var script = @"C:\Data\agy-status\statusline.ps1";
        var command = AgyLimits.Command(script);

        var missing = Inspect(GoodSettings, script);
        Assert.True(missing.Ok); // 막지 않는다
        Assert.True(missing.OpenSuggestion); // 넣을 JSON을 펼쳐 보임
        Assert.Contains(missing.Warnings, w => w.Contains("statusLine이 없습니다"));
        var suggested = JsonNode.Parse(missing.SuggestedJson)!["statusLine"]!;
        Assert.Equal((command, true, "command"), (suggested["command"]!.GetValue<string>(), suggested["stack_with_default"]!.GetValue<bool>(), suggested["type"]!.GetValue<string>()));
        // 스크립트는 agy가 샌드박스 밖에서 돌린다 — 쓰기는 중계 설정 파일의 데이터 폴더 write_file deny가 막는다 (전역에는 넣지 않음)
        Assert.Null(JsonNode.Parse(missing.SuggestedJson)!["permissions"]);
        Assert.Contains("write_file(C:/Data)", RelayJson);

        string With(JsonObject statusLine)
        {
            var root = JsonNode.Parse(GoodSettings)!.AsObject();
            root["statusLine"] = statusLine;
            return root.ToJsonString();
        }
        var done = Inspect(With(new() { ["type"] = "command", ["command"] = command }), script);
        Assert.DoesNotContain(done.Warnings, w => w.StartsWith("AGY", StringComparison.Ordinal));
        Assert.False(done.OpenSuggestion);
        Assert.Contains(Inspect(With(new() { ["type"] = "command", ["command"] = command, ["enabled"] = false }), script).Warnings, w => w.Contains("꺼져"));

        var other = With(new() { ["type"] = "command", ["command"] = "my-line.cmd" });
        var otherResult = Inspect(other, script);
        Assert.Contains(otherResult.Warnings, w => w.Contains("다른 statusLine"));
        Assert.Equal("my-line.cmd", JsonNode.Parse(otherResult.SuggestedJson)!["statusLine"]!["command"]!.GetValue<string>()); // 덮지 않음

        var spaced = Inspect(GoodSettings, @"C:\Users\Jane Doe\AppData\Local\HB_Chat\agy-status\statusline.ps1");
        Assert.Contains(spaced.Warnings, w => w.Contains("공백"));
        Assert.Null(JsonNode.Parse(spaced.SuggestedJson)!["statusLine"]);

        Assert.DoesNotContain(AgyCheck.Inspect(GoodSettings, RelayJson, @"C:\relay\current").Warnings, w => w.StartsWith("AGY", StringComparison.Ordinal)); // 스크립트를 안 주면 보지 않음
    }

    // ---- 문턱 알림 ----

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset WeekReset = Now.AddDays(3);

    private static LimitSnapshot Claude(double weekly, double fiveHour = 10, DateTimeOffset? reset = null, DateTimeOffset? at = null) =>
        new("claude", at ?? Now, [new LimitWindow("five_hour", fiveHour, Now.AddHours(2), "allowed"), new LimitWindow("seven_day", weekly, reset ?? WeekReset, "allowed")]);

    [Fact]
    public void Alerts_OncePerLevelPerPeriod_WeeklyOnly()
    {
        var alerts = new LimitAlerts(() => _dir);
        Assert.Empty(alerts.Check([Claude(79.4, fiveHour: 99)], Now)); // 5시간 창은 알리지 않음, 79.4는 79로 보임
        var first = Assert.Single(alerts.Check([Claude(79.5)], Now)); // 반올림 80 — 사이드바 색과 같은 값
        Assert.Equal((80, "claude", "seven_day", "한도 80% 넘음 · Claude 7일"), (first.Level, first.Source, first.Window, first.Title));
        Assert.Empty(alerts.Check([Claude(90)], Now));
        var red = Assert.Single(alerts.Check([Claude(97)], Now));
        Assert.Equal(95, red.Level);
        Assert.Empty(alerts.Check([Claude(99)], Now));
        Assert.NotEqual(first.Id, red.Id);

        // 다시 만든 객체(앱 다시 켬)도 기억한다
        Assert.Empty(new LimitAlerts(() => _dir).Check([Claude(99)], Now));
        // 초기화 시각이 1시간 안으로 흔들리면 같은 주기, 넘게 바뀌면 새 주기
        Assert.Empty(alerts.Check([Claude(99, reset: WeekReset.AddMinutes(30))], Now));
        var next = alerts.Check([Claude(96, reset: WeekReset.AddDays(7), at: Now.AddDays(4))], Now.AddDays(4));
        Assert.Equal([95], next.Select(a => a.Level)); // 한 번에 두 단계를 넘으면 높은 것 하나
    }

    [Fact]
    public void Alerts_SkipStale_ResetPassed_Unknown_AndRecentActive()
    {
        var alerts = new LimitAlerts(() => _dir);
        Assert.Empty(alerts.Check([Claude(90, at: Now.AddHours(-7))], Now)); // 지난 값
        Assert.Empty(alerts.Check([Claude(90, reset: Now.AddMinutes(-1))], Now)); // 초기화 지남
        Assert.Empty(alerts.Check([new LimitSnapshot("agy", Now, [new LimitWindow("x-weekly", null, WeekReset, "남음 3")]), null], Now));

        var agy = AgyLimits.Parse(MeasuredLimits.Replace("0.9733236", "0.04"), Now)! with { At = Now };
        var codex = new LimitSnapshot("codex", Now, [new LimitWindow(UsageLimits.MinutesName(10080, "secondary"), 82, WeekReset, null)]);
        var fresh = alerts.Check([agy, codex], Now);
        Assert.Equal(["한도 95% 넘음 · AGY Gemini 7일", "한도 80% 넘음 · Codex 7일"], fresh.Select(a => a.Title));
        alerts.Check([codex with { Windows = [codex.Windows[0] with { Percent = 96 }] }], Now);

        Assert.Equal(3, alerts.Recent().Count);
        var active = alerts.Active(Now);
        Assert.Equal([95, 95], active.Select(a => a.Level)); // 창마다 가장 높은 단계 하나
        Assert.Equal(["agy"], alerts.Active(WeekReset.AddMinutes(1)).Select(a => a.Source)); // 초기화가 지난 창(Codex)은 띠에서 빠짐
        Assert.Empty(alerts.Active(Now.AddDays(8)));
    }

    [Fact]
    public void Alerts_UnknownReset_SevenDayPeriods_AndLateResetKeepsLevel()   // /self-review sub C4
    {
        var alerts = new LimitAlerts(() => _dir);
        LimitSnapshot Snap(double percent, DateTimeOffset at, DateTimeOffset? reset = null) =>
            new("codex", at, [new LimitWindow("seven_day", percent, reset, null)]);

        Assert.Single(alerts.Check([Snap(85, Now)], Now)); // 초기화 시각 모름
        Assert.Empty(alerts.Check([Snap(85, Now.AddDays(6))], Now.AddDays(6)));
        Assert.Single(alerts.Check([Snap(85, Now.AddDays(7))], Now.AddDays(7))); // 모르는 채 7일 → 새 주기
        Assert.Empty(alerts.Check([Snap(85, Now.AddDays(7), reset: Now.AddDays(10))], Now.AddDays(7))); // 시각이 생겨도 같은 주기
        // 알던 초기화 시각이 지났는데 지금은 모름 → 새 주기
        Assert.Single(alerts.Check([Snap(85, Now.AddDays(10).AddHours(1))], Now.AddDays(10).AddHours(1)));
    }

    [Fact]
    public void Alerts_KeepsRecentBounded_BrokenFileStartsOver()
    {
        var alerts = new LimitAlerts(() => _dir);
        for (var i = 0; i < LimitAlerts.Keep + 5; i++)
            alerts.Check([new LimitSnapshot("claude", Now, [new LimitWindow("seven_day", 85, WeekReset.AddDays(7 * i), null)])], Now);
        Assert.Equal(LimitAlerts.Keep, alerts.Recent().Count);
        File.WriteAllText(Path.Combine(_dir, "limit-alerts.json"), "{ 깨짐");
        Assert.Empty(alerts.Recent());
        Assert.Single(alerts.Check([Claude(85)], Now)); // 깨진 기록은 처음부터 (최악은 한 번 더)
    }

    [Fact]
    public void UsageLimits_SaveClaude_RaisesAlert()
    {
        var limits = new UsageLimits(() => _dir) { Alerts = new LimitAlerts(() => _dir) };
        var raised = new List<LimitAlert>();
        limits.AlertRaised += raised.AddRange;
        var ev = new RateLimitEvent([new LimitWindow("seven_day", 91, DateTimeOffset.Now.AddDays(2), "allowed")], "allowed", false);
        limits.SaveClaude(ev);
        limits.SaveClaude(ev);
        Assert.Equal([80], raised.Select(a => a.Level));

        // AGY 파일은 데이터 폴더 agy-status/limits.json
        Directory.CreateDirectory(Path.Combine(_dir, AgyLimits.Folder));
        File.WriteAllText(AgyLimits.LimitsPath(_dir), MeasuredLimits.Replace("2026-11-24T22:04:45.3710000+09:00", DateTimeOffset.Now.ToString("o")));
        Assert.Equal(4, limits.Agy()!.Windows.Count);
    }
}

/// <summary>실제 앱: 켤 때 statusLine 스크립트를 만들고, AGY 한도 파일이 있으면 사이드바 한도 · 문턱 띠 · 데스크톱 알림 목록에 보인다.</summary>
public sealed class LimitsPageTests(ChatApp app) : IClassFixture<ChatApp>
{
    [Fact]
    public async Task AgyLimits_ShowInSidebar_AndAlertListed()
    {
        var data = Path.Combine(app.Dir, "data");
        Assert.True(File.Exists(AgyLimits.ScriptPath(data)));
        var reset = DateTimeOffset.UtcNow.AddDays(3).ToString("o");
        File.WriteAllText(AgyLimits.LimitsPath(data),
            new JsonObject
            {
                ["at"] = DateTimeOffset.Now.ToString("o"),
                ["version"] = "1.5.16",
                ["quota"] = new JsonObject
                {
                    ["gemini-weekly"] = new JsonObject { ["remaining_fraction"] = 0.03, ["reset_time"] = reset },
                    ["3p-weekly"] = new JsonObject { ["remaining_fraction"] = 1 },
                },
            }.ToJsonString());

        using var client = new HttpClient { BaseAddress = app.BaseAddress };
        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/?handler=Limits"));
        Assert.Contains("<b>AGY</b>", html);
        Assert.Contains("Gemini 7일 97%", html); // 요약 줄은 쓴 창만
        Assert.DoesNotContain("Claude·GPT 7일 0%", html);
        Assert.Contains("한도 95% 넘음 · AGY Gemini 7일", html);
        Assert.Contains("data-dismiss-alert", html);

        var alerts = JsonNode.Parse(await client.GetStringAsync("/api/limits/alerts"))!.AsArray();
        Assert.Contains(alerts, a => a!["title"]!.GetValue<string>() == "한도 95% 넘음 · AGY Gemini 7일");
    }

    [Fact]
    public async Task ClaudeStatusText_NotShownInsteadOfPercent()   // /self-review sub C1
    {
        // 옛 모양 rate_limit_event: 사용률 없이 상태만 (StreamParser가 창 하나로)
        var ev = (RateLimitEvent)StreamParser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","resetsAt":4102444800}}""")!;
        app.Services.GetRequiredService<UsageLimits>().SaveClaude(ev);
        using var client = new HttpClient { BaseAddress = app.BaseAddress };
        var html = await client.GetStringAsync("/?handler=Limits");
        Assert.DoesNotContain("allowed_warning", html);
    }
}
