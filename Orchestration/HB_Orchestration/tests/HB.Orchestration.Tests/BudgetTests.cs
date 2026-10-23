using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>오늘 사용량으로 세는 토큰·비용 (대시보드 "오늘 비용·토큰" 칸). 하루 상한은 없다.</summary>
public sealed class BudgetTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static CallRecord Call(string? provider, long? input, long? output, string model = "m", long? cached = null) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Timestamp = DateTimeOffset.Now,
        Tool = "review", Agent = "a", Model = model, Prompt = "p",
        Provider = provider, InputTokens = input, OutputTokens = output, CachedInputTokens = cached,
    };

    private static PricingOptions Prices(string model = "m") => new()
    {
        Models = new(StringComparer.OrdinalIgnoreCase)
        {
            [model] = new ModelPrice { InputPerMillion = 2, CachedInputPerMillion = 0.1m, OutputPerMillion = 10 },
        },
    };

    [Fact]
    public void CountedTokens_SumsInputAndOutput_ExcludingFreeProviders()
    {
        var budget = new BudgetOptions { FreeProviders = ["local"] };
        var calls = new[]
        {
            Call("openai", 100, 10),
            Call("LOCAL", 1_000, 1_000), // 대소문자 무시
            Call(null, 5, null),          // 예전 기록(제공사 없음)은 센다
            Call("gemini", null, null),   // 실패 기록
        };

        Assert.Equal(115, budget.CountedTokens(calls));
    }

    [Fact]
    public void CountedCost_AppliesCachedPrice_ExcludesFreeAndUnpriced()
    {
        var budget = new BudgetOptions { FreeProviders = ["local"] };
        var calls = new[]
        {
            Call("openai", 1_000_000, 100_000, cached: 500_000), // 0.5M×2 + 0.5M×0.1 + 0.1M×10 = 1 + 0.05 + 1
            Call("local", 1_000_000, 1_000_000),                 // 무료
            Call("openai", 1_000_000, 0, model: "unknown"),      // 단가 없음 → 0
        };

        Assert.Equal(2.05m, budget.CountedCost(calls, Prices()));
    }

    [Fact]
    public void CostOf_AppliesCacheWritePrice_AndTreatsMissingWriteAsZero()
    {
        var price = new ModelPrice { InputPerMillion = 2, CachedInputPerMillion = 0.1m, CacheWritePerMillion = 2.5m, OutputPerMillion = 10 };

        // 1M 입력 = 읽기 0.2M + 쓰기 0.6M + 일반 0.2M: 0.2×2 + 0.2×0.1 + 0.6×2.5 + 출력 0.1M×10
        Assert.Equal(0.4m + 0.02m + 1.5m + 1m, price.CostOf(Call("openai", 1_000_000, 100_000, cached: 200_000) with { CacheWriteTokens = 600_000 }));

        // 쓰기 기록 없는 예전 호출은 쓰기 0
        Assert.Equal(2m + 1m, price.CostOf(Call("openai", 1_000_000, 100_000)));

        // 읽기 + 쓰기가 입력보다 크면 쓰기를 남은 입력까지만 센다
        Assert.Equal(0.9m * 0.1m + 0.1m * 2.5m, price.CostOf(Call("openai", 1_000_000, 0, cached: 900_000) with { CacheWriteTokens = 500_000 }));

        // 쓰기 단가가 없으면 입력 단가
        var noWrite = new ModelPrice { InputPerMillion = 2, OutputPerMillion = 10 };
        Assert.Equal(2m, noWrite.CostOf(Call("openai", 1_000_000, 0) with { CacheWriteTokens = 1_000_000 }));
    }

    // ── 날짜별 단가 변경 (계획서 2026-10-23_단가-적용-시작일) ─────────────

    private static ModelPrice Argon() => new()
    {
        InputPerMillion = 2, CachedInputPerMillion = 0.1m, OutputPerMillion = 10,
        Changes = [new PriceChange { From = "2026-12-01", InputPerMillion = 4, CachedInputPerMillion = 0.2m, OutputPerMillion = 20 }],
    };

    private static CallRecord At(string time) => Call("gemini", 1_000_000, 1_000_000) with { Timestamp = DateTimeOffset.Parse(time) };

    [Fact]
    public void CostOf_UsesPriceAtRecordTime_SwitchingAtUtcMidnight()
    {
        var p = Argon();
        Assert.Equal(12m, p.CostOf(At("2026-11-30T23:59:59Z")));
        Assert.Equal(24m, p.CostOf(At("2026-12-01T00:00:00Z")));
        // KST 12-01 08:59 = UTC 11-30 → 이전 단가
        Assert.Equal(12m, p.CostOf(At("2026-12-01T08:59:00+09:00")));
        Assert.Equal(24m, p.CostOf(At("2026-12-01T09:00:00+09:00")));
    }

    [Fact]
    public void PriceAt_OverlaysOnlyGivenFields_InDateOrder()
    {
        var p = new ModelPrice
        {
            InputPerMillion = 1, CachedInputPerMillion = 0.1m, CacheWritePerMillion = 1.25m, OutputPerMillion = 5, WebSearchPerThousand = 10,
            Changes =
            [
                new PriceChange { From = "2027-02-01", OutputPerMillion = 9 },  // 목록 순서와 무관하게 날짜 순
                new PriceChange { From = "2027-01-01", InputPerMillion = 2, OutputPerMillion = 7 },
            ],
        };

        var jan = p.PriceAt(DateTimeOffset.Parse("2027-01-15T00:00:00Z"));
        Assert.Equal((2m, 0.1m, 1.25m, 7m, 10m), (jan.InputPerMillion, jan.CachedInputPerMillion, jan.CacheWritePerMillion, jan.OutputPerMillion, jan.WebSearchPerThousand));

        var feb = p.PriceAt(DateTimeOffset.Parse("2027-02-01T00:00:00Z"));
        Assert.Equal((2m, 9m), (feb.InputPerMillion, feb.OutputPerMillion));

        Assert.Same(p, p.PriceAt(DateTimeOffset.Parse("2026-12-31T23:59:59Z")));
    }

    [Fact]
    public void ChangeProblems_WarnsBadOrDuplicateDates_AndBadDateIsNotApplied()
    {
        var p = new ModelPrice
        {
            InputPerMillion = 1, OutputPerMillion = 1,
            Changes =
            [
                new PriceChange { From = "2026/12/01", InputPerMillion = 100 },
                new PriceChange { From = "2026-12-01", InputPerMillion = 2 },
                new PriceChange { From = "2026-12-01", InputPerMillion = 3 },
            ],
        };

        var problems = p.ChangeProblems().ToList();
        Assert.Equal(2, problems.Count);
        Assert.Contains("읽지 못해", problems[0]);
        Assert.Contains("중복", problems[1]);

        // 읽지 못한 변경은 무시, 같은 날짜는 목록 뒤의 값
        Assert.Equal(3m, p.PriceAt(DateTimeOffset.Parse("2027-01-01T00:00:00Z")).InputPerMillion);
        Assert.Empty(Argon().ChangeProblems());
    }

    [Fact]
    public void ChangeProblems_WarnsEmptyOrNegativeChange_NullChangesIsSafe()
    {
        var p = new ModelPrice
        {
            InputPerMillion = 1, OutputPerMillion = 1,
            Changes =
            [
                new PriceChange { From = "2026-12-01" },                          // 키 오타 등으로 칸이 비면
                new PriceChange { From = "2027-01-01", OutputPerMillion = -1 },
            ],
        };
        var problems = p.ChangeProblems().ToList();
        Assert.Equal(2, problems.Count);
        Assert.Contains("단가 칸이 없습니다", problems[0]);
        Assert.Contains("음수", problems[1]);

        var nullChanges = new ModelPrice { InputPerMillion = 2, OutputPerMillion = 10, Changes = null! };
        Assert.Equal(12m, nullChanges.CostOf(At("2026-12-01T00:00:00Z")));
        Assert.Empty(nullChanges.ChangeProblems());
    }

    [Fact]
    public void Changes_BindFromConfiguration_WithDateAsUtc()
    {
        var config = Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder(),
            new Dictionary<string, string?>
            {
                ["Pricing:Models:m:InputPerMillion"] = "2",
                ["Pricing:Models:m:OutputPerMillion"] = "10",
                ["Pricing:Models:m:Changes:0:From"] = "2026-12-01",
                ["Pricing:Models:m:Changes:0:InputPerMillion"] = "4",
            })
            .Build();
        var options = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<OrchestrationOptions>(config)!;

        var price = options.Pricing.Models["m"];
        Assert.Equal(2m, price.PriceAt(DateTimeOffset.Parse("2026-11-30T23:59:59Z")).InputPerMillion);
        Assert.Equal(4m, price.PriceAt(DateTimeOffset.Parse("2026-12-01T00:00:00Z")).InputPerMillion);
        Assert.Equal(10m, price.PriceAt(DateTimeOffset.Parse("2026-12-01T00:00:00Z")).OutputPerMillion);
    }

    [Fact]
    public void UnpricedCalls_CountsRecordsWithTokensButNoPrice_ExcludingFree()
    {
        var budget = new BudgetOptions { FreeProviders = ["local"] };
        var calls = new[]
        {
            Call("openai", 100, 10),                    // 단가 있음
            Call("openai", 100, 10, model: "unknown"),  // 단가 없음
            Call("local", 100, 10, model: "unknown"),   // 무료 제공사
            Call("openai", null, null, model: "unknown"), // 실패 기록 (토큰 없음)
        };

        Assert.Equal(1, budget.UnpricedCalls(calls, Prices()));
    }

    // 하루 토큰·비용 상한은 없앴다 (계획서 2026-10-20_하루상한-제거): 오늘 많이 썼어도, 단가 없는 모델이어도 실행한다.

    [Fact]
    public async Task HeavyUsageToday_StillRuns()
    {
        _h.Options.Pricing = Prices();
        await CallLog.AppendAsync(Call("fake", 100_000_000, 10_000_000), _h.LogPath); // $300, 1.1억 토큰

        await _h.Run("seq");

        Assert.Equal(1, _h.Client.Calls);
    }

    [Fact]
    public async Task UnpricedModel_Runs()
    {
        await _h.Run("seq"); // 가짜 하네스의 모델 m은 단가 없음

        Assert.Equal(1, _h.Client.Calls);
    }
}
