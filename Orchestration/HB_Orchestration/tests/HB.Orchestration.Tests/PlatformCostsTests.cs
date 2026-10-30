using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Dashboard.Pages;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>첫 화면 플랫폼별 비용: 같은 키 묶기 · 구독 제외 · 이번 달 · 크레딧 주기.</summary>
public sealed class PlatformCostsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-platform-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.FromHours(9));

    private static readonly Dictionary<string, ProviderOptions> Providers = new()
    {
        ["openai"] = new ProviderOptions { ApiKeyEnv = "OPENAI_API_KEY" },
        ["openai-web"] = new ProviderOptions { ApiKeyEnv = "OPENAI_API_KEY" },
        ["anthropic-cli"] = new ProviderOptions { ApiKeyEnv = "HB_REVIEWER_API_KEY", MonthlyCreditUsd = 100, CreditResetDay = 19 },
        ["codex"] = new ProviderOptions(),
    };

    private static readonly BudgetOptions Budget = new() { FreeProviders = ["local", "codex"] };

    private static CallRecord Call(string provider, int? inputTokens, DateTimeOffset at, string model = "m") => new()
    {
        Id = Guid.NewGuid().ToString("N"), Timestamp = at, Tool = "review", Model = model, Prompt = "p", Success = true, Provider = provider, InputTokens = inputTokens,
    };

    /// <summary>입력 토큰 1개 = $1, 모델 "free"는 단가 없음.</summary>
    private static decimal? Cost(CallRecord c) => c.Model == "free" ? null : c.InputTokens ?? 0;

    private static IReadOnlyList<PlatformCost> Build(params CallRecord[] calls) =>
        PlatformCosts.Build(calls, calls, c => c.Provider ?? "openai", Cost, Providers, Budget, Now);

    [Fact]
    public void Providers_with_the_same_key_are_one_platform()
    {
        var rows = Build(Call("openai", 3, Now), Call("openai-web", 4, Now));

        var row = Assert.Single(rows);
        Assert.Equal("openai", row.Name);
        Assert.Equal(7m, row.PeriodUsd);
        Assert.Equal(2, row.PeriodCalls);
    }

    [Fact]
    public void Subscription_and_unconfigured_tokenless_providers_are_marked_no_charge_and_sort_last()
    {
        var rows = Build(Call("codex", 9, Now), Call("claude-subagent", null, Now, "free"), Call("openai", 1, Now));

        Assert.Equal("openai", rows[0].Name);
        Assert.False(rows[0].NoCharge);
        Assert.True(rows.Single(r => r.Name == "codex").NoCharge);
        Assert.Equal(9m, rows.Single(r => r.Name == "codex").PeriodUsd);          // 환산액은 칸 제목용으로 남는다
        Assert.True(rows.Single(r => r.Name == "claude-subagent").NoCharge);
    }

    [Fact]
    public void A_removed_provider_with_tokens_is_still_charged_even_without_a_price()
    {
        Assert.False(Assert.Single(Build(Call("옛제공사", 5, Now))).NoCharge);

        var unpriced = Assert.Single(Build(Call("옛제공사", 5, Now, "free")));
        Assert.False(unpriced.NoCharge);
        Assert.Equal(1, unpriced.UnpricedCalls);
    }

    [Fact]
    public void A_free_provider_sharing_a_key_is_left_out_of_the_paid_amount()
    {
        var providers = new Dictionary<string, ProviderOptions>(Providers) { ["local"] = new ProviderOptions { ApiKeyEnv = "OPENAI_API_KEY" } };

        var row = Assert.Single(PlatformCosts.Build([Call("openai", 3, Now), Call("local", 8, Now)], [Call("openai", 3, Now), Call("local", 8, Now)],
            c => c.Provider!, Cost, providers, Budget, Now));

        Assert.False(row.NoCharge);
        Assert.Equal((3m, 3m, 1), (row.PeriodUsd, row.MonthUsd, row.PeriodCalls));
    }

    [Fact]
    public void Credit_says_when_calls_are_missing_a_price()
    {
        var credit = Assert.Single(Build(Call("anthropic-cli", 92, Now), Call("anthropic-cli", 5, Now, "free"))).Credit!;

        Assert.Equal((92m, 1), (credit.UsedUsd, credit.UnpricedCalls));
    }

    [Fact]
    public void Month_uses_the_local_calendar_and_period_only_counts_the_given_records()
    {
        var old = Call("openai", 10, new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.FromHours(9)));
        var month = Call("openai", 5, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(9)));
        var today = Call("openai", 1, Now);

        var row = Assert.Single(PlatformCosts.Build([old, month, today], [today], c => c.Provider!, Cost, Providers, Budget, Now));

        Assert.Equal(1m, row.PeriodUsd);
        Assert.Equal(6m, row.MonthUsd);
        Assert.Equal(1, row.PeriodCalls);
    }

    [Fact]
    public void Credit_counts_from_the_last_reset_day()
    {
        var before = Call("anthropic-cli", 50, new DateTimeOffset(2026, 10, 18, 23, 59, 0, TimeSpan.FromHours(9)));
        var after = Call("anthropic-cli", 92, new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(9)));

        var credit = Assert.Single(Build(before, after)).Credit!;

        Assert.Equal((92m, 100m, 92, true, new DateOnly(2026, 11, 19)), (credit.UsedUsd, credit.LimitUsd, credit.Percent, credit.NearLimit, credit.Renews));
    }

    [Theory]
    [InlineData(2026, 10, 18, 19, "2026-09-19", "2026-10-19")]
    [InlineData(2026, 10, 19, 19, "2026-10-19", "2026-11-19")]
    [InlineData(2026, 1, 5, 19, "2025-12-19", "2026-01-19")]
    [InlineData(2026, 2, 28, 31, "2026-02-28", "2026-03-28")]   // 없는 날은 28일로
    public void Credit_cycle_rolls_over_months(int year, int month, int day, int resetDay, string start, string renews)
    {
        var (from, next) = PlatformCosts.CreditCycle(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.FromHours(9)), resetDay);

        Assert.Equal(start, from.ToString("yyyy-MM-dd"));
        Assert.Equal(DateOnly.Parse(renews), next);
    }

    [Fact]
    public void Calls_without_a_price_are_counted_and_do_not_become_zero_cost()
    {
        var row = Assert.Single(Build(Call("openai", 3, Now, "free")));

        Assert.Null(row.PeriodUsd);
        Assert.Equal(1, row.UnpricedCalls);
    }

    [Fact]
    public async Task Total_card_leaves_out_subscriptions_and_the_table_ignores_the_provider_filter()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            {
              "Orchestration": {
                "Pricing": { "Models": { "m": { "InputPerMillion": 1000000, "OutputPerMillion": 0 } } },
                "Budget": { "FreeProviders": [ "local", "codex" ] },
                "Providers": {
                  "openai": { "ApiKeyEnv": "OPENAI_API_KEY" },
                  "codex": { "Type": "codex-cli" }
                }
              }
            }
            """);
        var log = Path.Combine(_dir, "calls.jsonl");
        await CallLog.AppendAsync(Call("openai", 2, DateTimeOffset.Now), log);
        await CallLog.AppendAsync(Call("codex", 7, DateTimeOffset.Now), log);
        var options = Options.Create(new DashboardOptions { LogPath = log, McpConfigDir = _dir, ClaudeProjectsDir = Path.Combine(_dir, "projects") });
        var modes = new ModesConfigService(options, _ => null);
        IndexModel Page(string? provider)
        {
            var model = new IndexModel(options, new PricingService(options, modes), new CallLogCache(options, NullLogger<CallLogCache>.Instance), new VerdictLogCache(options),
                new SubagentResultsService(options, new ClaudeSessionsService(options, NullLogger<ClaudeSessionsService>.Instance), NullLogger<SubagentResultsService>.Instance), modes)
            {
                Provider = provider,
            };
            model.OnGet();
            return model;
        }

        var all = Page(null);
        Assert.Equal(2m, all.TotalCost);                                            // Codex 7달러(환산)는 빠진다
        Assert.Equal(2, all.Platforms.Count);
        Assert.Equal(2, Page("openai").Platforms.Count);                            // 제공사 필터와 상관없이 전부
        Assert.Equal(0m, Page("codex").TotalCost);                                  // 청구가 없으면 "모름"이 아니라 0

        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), "{ 깨진 내용");
        Assert.Empty(Page(null).Platforms);                                         // 설정을 못 읽으면 표가 바뀌어 보이지 않게 그리지 않는다
    }
}
