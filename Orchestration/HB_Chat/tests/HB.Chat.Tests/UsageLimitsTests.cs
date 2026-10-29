using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>구독 한도 (계획서 2026-10-29_사용량-한도-표시): Claude rate_limit_event 파싱 · 저장, Codex 세션 기록 읽기, 지난 값, Codex 구성원 토큰 합계.</summary>
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
}
