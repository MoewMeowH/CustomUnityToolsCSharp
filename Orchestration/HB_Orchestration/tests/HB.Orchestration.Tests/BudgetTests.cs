using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>일일 상한에 세는 토큰 (서버 검사와 대시보드 "오늘 상한 사용" 칸이 같은 식을 쓴다).</summary>
public sealed class BudgetTests
{
    private static CallRecord Call(string? provider, long? input, long? output) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Timestamp = DateTimeOffset.Now,
        Tool = "review", Agent = "a", Model = "m", Prompt = "p",
        Provider = provider, InputTokens = input, OutputTokens = output,
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
}
