using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>구독 한도: Claude rate_limit_event 파싱 · 저장, Codex 세션 기록 읽기, 지난 값, Codex 구성원 토큰 합계.</summary>
public sealed class UsageLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-limits-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>2026-10-29 실측 줄 (haiku 작은 호출).</summary>
    private const string ClaudeLine = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1793278200,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.43,"resetsAt":1793278200,"status":"allowed"},"seven_day":{"utilization":0.68,"resetsAt":1793635200,"status":"allowed"}}},"uuid":"u","session_id":"s"}""";

    [Fact]
    public void Parser_ReadsRateLimitWindows()
    {
        var ev = Assert.IsType<RateLimitEvent>(StreamParser.Parse(ClaudeLine));
        Assert.Equal("allowed", ev.Status);
        Assert.False(ev.UsingOverage);
        Assert.Equal(2, ev.Windows.Count);
        Assert.Equal(new LimitWindow("five_hour", 43, DateTimeOffset.FromUnixTimeSeconds(1793278200), "allowed"), ev.Windows[0]);
        Assert.Equal(("7일", 68.0), (ev.Windows[1].Label, ev.Windows[1].Percent!.Value));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"rate_limit_event"}""")); // 모양이 다르면 버림
    }

    [Fact]
    public void Claude_SavedAndReadBack_EmptyNotSaved()
    {
        var limits = new UsageLimits(() => _dir, () => Path.Combine(_dir, "codex"));
        Assert.Null(limits.Claude());
        limits.SaveClaude(new RateLimitEvent([], "allowed", null));
        Assert.Null(limits.Claude()); // 창이 없으면 남기지 않음

        var at = new DateTimeOffset(2026, 10, 29, 20, 0, 0, TimeSpan.FromHours(9));
        limits.SaveClaude((RateLimitEvent)StreamParser.Parse(ClaudeLine)!, at);
        var read = new UsageLimits(() => _dir).Claude()!;
        Assert.Equal(("claude", at, "allowed", false), (read.Source, read.At, read.Status, read.UsingOverage));
        Assert.Equal([43.0, 68.0], read.Windows.Select(w => w.Percent!.Value));
    }

    private static string CodexLine(string time, double primary, double secondary) =>
        """{"timestamp":"TIME","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":P1,"window_minutes":300,"resets_at":1793275462},"secondary":{"used_percent":P2,"window_minutes":10080,"resets_at":1793862262},"plan_type":"plus"}}}"""
            .Replace("TIME", time).Replace("P1", primary.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("P2", secondary.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Codex_LatestRateLimitsFromRollouts()
    {
        var sessions = Path.Combine(_dir, "codex", "sessions");
        var day = Directory.CreateDirectory(Path.Combine(sessions, "2026", "10", "29")).FullName;
        var older = Path.Combine(day, "rollout-a.jsonl");
        var newer = Path.Combine(day, "rollout-b.jsonl");
        File.WriteAllText(older, CodexLine("2026-10-29T09:00:00Z", 50, 5) + "\n");
        File.WriteAllText(newer, string.Join("\n", CodexLine("2026-10-29T08:00:00Z", 1, 1), CodexLine("2026-10-29T10:00:00Z", 14, 2), """{"type":"other"}""") + "\n");

        var snap = UsageLimits.ReadCodex(sessions)!;
        Assert.Equal(("codex", "plus"), (snap.Source, snap.Plan));
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T10:00:00Z"), snap.At); // 파일들 중 가장 최근 줄
        Assert.Equal([("5시간", 14.0), ("7일", 2.0)], snap.Windows.Select(w => (w.Label, w.Percent!.Value)));
        Assert.Null(UsageLimits.ReadCodex(Path.Combine(_dir, "없음")));
        Assert.Equal(14, new UsageLimits(() => _dir, () => Path.Combine(_dir, "codex")).Codex()!.Windows[0].Percent); // CODEX_HOME 아래 sessions
    }

    [Fact]
    public void Stale_WhenOldOrAllWindowsReset()
    {
        var now = DateTimeOffset.Now;
        LimitSnapshot Snap(TimeSpan age, params DateTimeOffset?[] resets) =>
            new("claude", now - age, [.. resets.Select(r => new LimitWindow("five_hour", 10, r, null))]);
        Assert.False(Snap(TimeSpan.FromMinutes(5), now.AddHours(1)).IsStale(now));
        Assert.True(Snap(TimeSpan.FromHours(7), now.AddHours(1)).IsStale(now));       // 받은 지 오래
        Assert.True(Snap(TimeSpan.FromMinutes(5), now.AddMinutes(-1)).IsStale(now));   // 초기화 지남
        Assert.False(Snap(TimeSpan.FromMinutes(5), now.AddMinutes(-1), now.AddDays(2)).IsStale(now)); // 한 창은 아직
    }

    [Fact]
    public void Parser_OutOfRangeTimes_AndOldShape_DoNotThrow()   // b40d9efb00 C1 · 지휘자 K2
    {
        var ms = Assert.IsType<RateLimitEvent>(StreamParser.Parse(
            """{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.1,"resetsAt":1793278200000}}}}"""));
        Assert.Null(ms.Windows[0].ResetsAt);                                       // 밀리초로 온 값 → 시각 없음 (예외 아님)
        var old = Assert.IsType<RateLimitEvent>(StreamParser.Parse(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","resetsAt":1793278200}}"""));
        Assert.Equal(new LimitWindow("five_hour", null, DateTimeOffset.FromUnixTimeSeconds(1793278200), "allowed_warning"), Assert.Single(old.Windows));
    }

    [Fact]
    public void Codex_SkipsOtherLimitIds_AndBrokenLines()   // b40d9efb00 C2 · C5 · C7
    {
        var sessions = Path.Combine(_dir, "codex2", "sessions");
        var day = Directory.CreateDirectory(Path.Combine(sessions, "2026", "10", "29")).FullName;
        File.WriteAllText(Path.Combine(day, "rollout-c.jsonl"), string.Join("\n",
            CodexLine("2026-10-29T09:00:00Z", 30, 3),
            CodexLine("2026-10-29T09:30:00Z", 99, 99).Replace("\"limit_id\":\"codex\"", "\"limit_id\":\"other\""), // 다른 묶음
            """{"timestamp":5,"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":"x"}}}}""", // 모양이 다름
            """{"timestamp":"2026-10-29T09:40:00Z","payload":"rate_limits"}""") + "\n");

        var snap = UsageLimits.ReadCodex(sessions)!;
        Assert.Equal(30, snap.Windows[0].Percent);                                // 깨진 줄 · 다른 묶음은 건너뛰고 그 앞 줄
    }

    [Fact]
    public void Codex_NullRolloutsInFront_StillFindsValue()   // Codex Desktop이 rate_limits null 기록 21개를 한꺼번에 만든 경우
    {
        var sessions = Path.Combine(_dir, "codex3", "sessions");
        var day = Directory.CreateDirectory(Path.Combine(sessions, "2026", "11", "04")).FullName;
        var old = Path.Combine(day, "rollout-old.jsonl");
        File.WriteAllText(old, CodexLine("2026-11-03T06:54:00Z", 27, 56) + "\n");
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 11, 3, 6, 54, 0, DateTimeKind.Utc));
        for (var i = 0; i < 21; i++)
        {
            var f = Path.Combine(day, $"rollout-null{i:00}.jsonl");
            File.WriteAllText(f, """{"timestamp":"2026-11-03T17:25:57Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":null}}""" + "\n");
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 11, 3, 17, 25, 57, DateTimeKind.Utc).AddSeconds(i));
        }

        var snap = UsageLimits.ReadCodex(sessions)!;
        Assert.Equal((27.0, LimitSnapshot.FromRollout), (snap.Windows[0].Percent!.Value, snap.From));
    }

    [Fact]
    public void Codex_FileCache_RereadsOnlyChangedFiles()   // C2
    {
        var home = Path.Combine(_dir, "codex5");
        var day = Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "11", "04")).FullName;
        var file = Path.Combine(day, "rollout-a.jsonl");
        File.WriteAllText(file, CodexLine("2026-11-04T01:00:00Z", 10, 1) + "\n");
        var limits = new UsageLimits(() => _dir, () => home);
        var t0 = DateTimeOffset.Now;
        Assert.Equal(10, limits.Codex(t0)!.Windows[0].Percent);

        File.AppendAllText(file, CodexLine("2026-11-04T02:00:00Z", 20, 2) + "\n");      // 크기 · 시각이 바뀜 → 다시 읽음
        Assert.Equal(20, limits.Codex(t0 + UsageLimits.CodexCache + TimeSpan.FromSeconds(1))!.Windows[0].Percent);
        var stamp = File.GetLastWriteTimeUtc(file);                                        // 크기 · 시각이 같으면 내용이 바뀌어도 캐시
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"used_percent\":20", "\"used_percent\":30"));
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.Equal(20, limits.Codex(t0 + 2 * (UsageLimits.CodexCache + TimeSpan.FromSeconds(1)))!.Windows[0].Percent);

        File.Delete(file);
        Assert.Null(limits.Codex(t0 + 3 * (UsageLimits.CodexCache + TimeSpan.FromSeconds(1)))); // 사라진 파일은 캐시에서도
    }

    /// <summary>codex 0.165.0 account/rateLimits/read 실측 응답 (accountId 등 뺌).</summary>
    private const string AccountResponse = """{"id":2,"result":{"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1793747355},"secondary":{"usedPercent":57,"windowDurationMins":10080,"resetsAt":1793862262},"planType":"plus"},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":3,"windowDurationMins":300,"resetsAt":1793747355},"secondary":{"usedPercent":58,"windowDurationMins":10080,"resetsAt":1793862262},"planType":"plus"}},"rateLimitResetCredits":{"availableCount":2,"credits":null}}}""";

    private static LimitSnapshot? ParseAccount(string json) => CodexLimits.Parse(System.Text.Json.Nodes.JsonNode.Parse(json), DateTimeOffset.UnixEpoch);

    [Fact]
    public void Account_ParsesResponse()
    {
        var snap = ParseAccount(AccountResponse)!;
        Assert.Equal(("codex", "plus", 2, LimitSnapshot.FromAccount), (snap.Source, snap.Plan, snap.ResetCredits, snap.From));
        Assert.Equal([("5시간", 3.0), ("7일", 58.0)], snap.Windows.Select(w => (w.Label, w.Percent!.Value))); // rateLimitsByLimitId.codex 먼저
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793862262), snap.Windows[1].ResetsAt);

        var single = ParseAccount("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":null}}}""")!;
        Assert.Equal(("5시간", 10.0, (DateTimeOffset?)null, (int?)null), (single.Windows.Single().Label, single.Windows[0].Percent!.Value, single.Windows[0].ResetsAt, single.ResetCredits));

        Assert.Null(ParseAccount("""{"id":2,"result":{"rateLimits":{"limitId":"other","primary":{"usedPercent":10}}}}""")); // 다른 묶음
        Assert.Null(ParseAccount("""{"id":2,"error":{"code":-32600,"message":"chatgpt auth required"}}"""));         // 오류 응답
        Assert.Null(ParseAccount("""{"id":2,"result":{"rateLimits":{"primary":null,"secondary":null}}}"""));         // 창 없음
        Assert.Null(ParseAccount("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":"x"}}}}"""));          // 모양이 다름
        Assert.Null(CodexLimits.Parse(null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task Account_RefreshEveryInterval_FailureKeepsOld_SingleFlight()
    {
        var calls = 0;
        LimitSnapshot? next = null;
        var gate = new TaskCompletionSource();
        var limits = new UsageLimits(() => _dir, () => Path.Combine(_dir, "없는-codex"), async _ =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return next;
        });
        var t0 = DateTimeOffset.Now;
        Assert.Null(limits.Codex(t0));

        next = ParseAccount(AccountResponse)! with { At = t0 };
        var first = limits.RefreshCodexAsync(t0);
        Assert.Same(first, limits.RefreshCodexAsync(t0.AddMinutes(10))); // 돌고 있으면 같은 작업
        gate.SetResult();
        await first;
        Assert.Equal((1, 3.0), (calls, limits.Codex(t0)!.Windows[0].Percent!.Value));

        await limits.RefreshCodexAsync(t0.AddMinutes(4));                // 5분 안 — 부르지 않음
        Assert.Equal(1, calls);

        next = null;                                                      // 실패해도 전 값은 남는다
        await limits.RefreshCodexAsync(t0.AddMinutes(6));
        Assert.Equal((2, 3.0), (calls, limits.Codex(t0)!.Windows[0].Percent!.Value));
        await limits.RefreshCodexAsync(t0.AddMinutes(8));                // 실패 뒤에도 5분 쉼
        Assert.Equal(2, calls);

        var throwing = new UsageLimits(() => _dir, () => Path.Combine(_dir, "없는-codex"), _ => throw new InvalidOperationException("x"));
        await throwing.RefreshCodexAsync(t0);                             // 예외로 끝나지 않음
        Assert.Null(throwing.Codex(t0));
        Assert.Null(new UsageLimits(() => _dir).RefreshCodexAsync(t0).Exception); // 조회기 없으면 아무것도 안 함
    }

    [Fact]
    public async Task Account_AndRollout_NewerWins()
    {
        var home = Path.Combine(_dir, "codex4");
        var day = Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "10", "29")).FullName;
        File.WriteAllText(Path.Combine(day, "rollout-a.jsonl"), CodexLine("2026-10-29T10:00:00Z", 14, 2) + "\n");
        var account = ParseAccount(AccountResponse)!;

        var older = new UsageLimits(() => _dir, () => home, _ => Task.FromResult<LimitSnapshot?>(account with { At = DateTimeOffset.Parse("2026-10-29T09:00:00Z") }));
        await older.RefreshCodexAsync();
        var won = older.Codex()!;
        Assert.Equal((LimitSnapshot.FromRollout, 14.0, 2), (won.From, won.Windows[0].Percent!.Value, won.ResetCredits)); // 기록이 더 새로움 — 크레딧은 계정 값에서 (C1)

        var newer = new UsageLimits(() => _dir, () => home, _ => Task.FromResult<LimitSnapshot?>(account with { At = DateTimeOffset.Parse("2026-10-29T11:00:00Z") }));
        await newer.RefreshCodexAsync();
        Assert.Equal((LimitSnapshot.FromAccount, 2), (newer.Codex()!.From, newer.Codex()!.ResetCredits));
    }
}
