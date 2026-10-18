using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>일일 상한에 세는 토큰·비용 (서버 검사와 대시보드 "오늘 … 상한" 칸이 같은 식을 쓴다).</summary>
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

    [Fact]
    public async Task CostLimitReached_RejectsBeforeCall()
    {
        _h.Options.Pricing = Prices();
        _h.Options.Budget.DailyCostLimitUsd = 1;
        await CallLog.AppendAsync(Call("fake", 0, 100_000), _h.LogPath); // $1.00

        var result = await _h.Run("seq");

        Assert.StartsWith("[오늘 비용 상한 도달]", result);
        Assert.Equal(0, _h.Client.Calls);
    }

    [Fact]
    public async Task CostLimitOn_UnpricedModel_RejectsBeforeCall()
    {
        _h.Options.Budget.DailyCostLimitUsd = 5;

        var result = await _h.Run("seq");

        Assert.StartsWith("[단가 없는 모델] a1(m)", result);
        Assert.Equal(0, _h.Client.Calls);
    }

    [Fact]
    public async Task CostLimitOn_FreeProviderWithoutPrice_Runs()
    {
        _h.Options.Budget.DailyCostLimitUsd = 5;
        _h.Options.Budget.FreeProviders = ["fake"];

        await _h.Run("seq");

        Assert.Equal(1, _h.Client.Calls);
    }

    [Fact]
    public async Task UnderBothLimits_Runs()
    {
        _h.Options.Pricing = Prices();
        _h.Options.Budget.DailyCostLimitUsd = 5;
        _h.Options.Budget.DailyTokenLimit = 1_000_000;
        await CallLog.AppendAsync(Call("fake", 100_000, 10_000), _h.LogPath); // $0.30, 110,000 토큰

        await _h.Run("seq");

        Assert.Equal(1, _h.Client.Calls);
    }

    [Fact]
    public async Task TokenLimitReached_RejectsEvenWhenCostIsLow()
    {
        _h.Options.Pricing = Prices();
        _h.Options.Budget.DailyCostLimitUsd = 5;
        _h.Options.Budget.DailyTokenLimit = 1_000;
        await CallLog.AppendAsync(Call("fake", 1_000, 0), _h.LogPath);

        var result = await _h.Run("seq");

        Assert.StartsWith("[오늘 토큰 상한 도달]", result);
        Assert.Equal(0, _h.Client.Calls);
    }
}
