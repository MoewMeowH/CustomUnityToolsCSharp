using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>크레딧 한 주기의 사용량 (호출 기록 기준 — 실제 잔액은 콘솔 값이 기준이다).</summary>
/// <param name="UnpricedCalls">이 주기에 단가가 없어 사용량에 넣지 못한 호출 — 있으면 실제보다 낮게 보인다.</param>
public sealed record PlatformCredit(decimal LimitUsd, decimal UsedUsd, DateOnly Renews, int UnpricedCalls)
{
    public int Percent => LimitUsd > 0 ? (int)Math.Round(UsedUsd * 100 / LimitUsd, MidpointRounding.AwayFromZero) : 0;

    /// <summary>화면이 경고 색으로 보이는 기준.</summary>
    public bool NearLimit => Percent >= 90;
}

/// <summary>
/// 플랫폼 한 줄. NoCharge면 구독 · 로컬이라 따로 청구되지 않는다 — 금액은 API 단가로 환산한 값이고 합계에 넣지 않는다.
/// </summary>
/// <param name="PeriodUsd">화면 기간의 비용. 단가가 있는 기록이 없으면 null.</param>
/// <param name="MonthUsd">이번 달(로컬 달력)의 비용. 단가가 있는 기록이 없으면 null.</param>
public sealed record PlatformCost(string Name, bool NoCharge, decimal? PeriodUsd, decimal? MonthUsd, int PeriodCalls, int UnpricedCalls, PlatformCredit? Credit);

/// <summary>호출 기록을 플랫폼별 비용으로 묶는다. 같은 API 키를 쓰는 제공사는 청구서가 하나라 한 줄이다 (openai · openai-web).</summary>
public static class PlatformCosts
{
    public static IReadOnlyList<PlatformCost> Build(IReadOnlyCollection<CallRecord> all, IReadOnlyCollection<CallRecord> period, Func<CallRecord, string> providerOf,
        Func<CallRecord, decimal?> costOf, IReadOnlyDictionary<string, ProviderOptions> providers, BudgetOptions budget, DateTimeOffset now)
    {
        string Key(CallRecord c)
        {
            var provider = providerOf(c);
            return providers.TryGetValue(provider, out var o) && !string.IsNullOrWhiteSpace(o.ApiKeyEnv) ? "key:" + o.ApiKeyEnv : "name:" + provider;
        }

        decimal? Sum(IEnumerable<CallRecord> records)
        {
            decimal sum = 0;
            var any = false;
            foreach (var r in records)
            {
                if (costOf(r) is not { } c) continue;
                sum += c;
                any = true;
            }
            return any ? sum : null;
        }

        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var periodByKey = period.ToLookup(Key);
        var rows = new List<PlatformCost>();
        foreach (var group in all.GroupBy(Key))
        {
            var members = group.Select(providerOf).Distinct().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToList();
            // 설정에 없고 토큰 기록도 없는 제공사(claude-subagent: 구독 서브에이전트)도 청구가 없다. 토큰이 있으면 단가가 없어도 청구로 본다
            var noCharge = members.All(budget.IsFree) || (!members.Any(providers.ContainsKey) && group.All(c => c.InputTokens is null));
            // 같은 키에 무료 제공사가 섞여도 금액에는 넣지 않는다 (카드 합계와 같은 기준)
            bool Counted(CallRecord c) => noCharge || !budget.IsFree(providerOf(c));
            var inPeriod = periodByKey[group.Key].Where(Counted).ToList();
            var counted = group.Where(Counted).ToList();

            PlatformCredit? credit = null;
            var options = members.Select(p => providers.GetValueOrDefault(p)).FirstOrDefault(o => o?.MonthlyCreditUsd > 0);
            if (options?.MonthlyCreditUsd is { } limit)
            {
                var (start, renews) = CreditCycle(now, options.CreditResetDay ?? 1);
                var cycle = counted.Where(c => c.Timestamp >= start).ToList();
                credit = new PlatformCredit(limit, Sum(cycle) ?? 0m, renews, cycle.Count(c => c.InputTokens is not null && costOf(c) is null));
            }

            rows.Add(new PlatformCost(members[0], noCharge, Sum(inPeriod), Sum(counted.Where(c => c.Timestamp >= monthStart)), inPeriod.Count,
                inPeriod.Count(c => c.InputTokens is not null && costOf(c) is null), credit));
        }
        return [.. rows.OrderBy(r => r.NoCharge).ThenByDescending(r => r.MonthUsd ?? 0m).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>지금이 속한 크레딧 주기: 시작 시각과 다음 갱신일. 갱신일은 모든 달에 있도록 1~28로 줄인다.</summary>
    public static (DateTimeOffset Start, DateOnly Renews) CreditCycle(DateTimeOffset now, int resetDay)
    {
        var thisMonth = new DateTimeOffset(now.Year, now.Month, Math.Clamp(resetDay, 1, 28), 0, 0, 0, now.Offset);
        var start = now >= thisMonth ? thisMonth : thisMonth.AddMonths(-1);
        return (start, DateOnly.FromDateTime(start.AddMonths(1).DateTime));
    }
}
