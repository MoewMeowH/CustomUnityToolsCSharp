using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

public sealed class IndexModel(IOptions<DashboardOptions> options, PricingService pricing, CallLogCache log, VerdictLogCache verdicts,
    SubagentResultsService subagents, ModesConfigService modes) : PageModel
{
    public DashboardOptions Opt => options.Value;
    public PricingService Pricing => pricing;

    /// <summary>최근 N일. 0이면 전체.</summary>
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 7;
    [BindProperty(SupportsGet = true)] public string? Tool { get; set; }
    [BindProperty(SupportsGet = true)] public string? Provider { get; set; }
    /// <summary>이 실행(RunId)의 기록만. 앞부분만 넣어도 된다.</summary>
    [BindProperty(SupportsGet = true)] public string? Run { get; set; }
    /// <summary>검색어. Id·RunId는 앞부분 일치, 나머지(이유·역할·에이전트·모델·파일·응답·오류)는 부분 일치.</summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>true면 10초마다 자동 새로고침.</summary>
    [BindProperty(SupportsGet = true)] public bool Live { get; set; }

    /// <summary>표의 쪽 (1부터). 합계 · 비용 · 일별 그래프는 걸러진 전체 기준이고 표만 나눈다.</summary>
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    /// <summary>한 쪽의 행 수 (사용자 결정 2026-10-23: 200).</summary>
    public const int PageSize = 200;

    /// <summary>걸러진 전체 기록 (합계 · 그래프 기준).</summary>
    public List<CallRecord> Calls { get; private set; } = [];

    /// <summary>이 쪽에 그릴 기록.</summary>
    public List<CallRecord> PageCalls { get; private set; } = [];

    public int PageCount => Math.Max(1, (Calls.Count + PageSize - 1) / PageSize);

    /// <summary>필터를 유지한 채 쪽만 바꾼 주소.</summary>
    public string PageUrl(int page)
    {
        var query = new List<string> { $"Days={Days}", $"P={page}" };
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) query.Add($"{key}={Uri.EscapeDataString(value)}"); }
        Add(nameof(Tool), Tool);
        Add(nameof(Provider), Provider);
        Add(nameof(Run), Run);
        Add(nameof(Q), Q);
        if (Live) query.Add("Live=true");
        return "?" + string.Join('&', query);
    }
    public List<string> ToolNames { get; private set; } = [];
    public List<string> ProviderNames { get; private set; } = [];
    public List<(DateOnly Day, long Tokens, int Count)> Daily { get; private set; } = [];

    public long TotalIn => Calls.Sum(c => c.InputTokens ?? 0);
    public long TotalCached => Calls.Sum(c => c.CachedInputTokens ?? 0);

    /// <summary>입력 토큰은 있는데 캐시 값이 없는 기록 수. 0(미적중)과 다르다: 제공사가 값을 주지 않았거나(Gemini 미적중 등) 예전 기록.</summary>
    public int CacheUnknown => Calls.Count(c => c.InputTokens is not null && c.CachedInputTokens is null && c.Estimate is null);
    /// <summary>캐시 쓰기 토큰 합계. 비용은 CacheWritePerMillion 단가로 계산된다 (ModelPrice.CostOf).</summary>
    public long TotalCacheWrite => Calls.Sum(c => c.CacheWriteTokens ?? 0);
    public long TotalOut => Calls.Sum(c => c.OutputTokens ?? 0);
    public long TotalReasoning => Calls.Sum(c => c.ReasoningTokens ?? 0);
    public int Failures => Calls.Count(c => !c.Success);
    public double AvgSeconds => Calls.Count == 0 ? 0 : Calls.Average(c => c.ElapsedMs) / 1000.0;

    /// <summary>걸러진 기록 중 따로 청구되는 것 (FreeProviders 제외 — 구독 Codex를 API 단가로 합치면 실제로 나가지 않는 돈이 섞인다).</summary>
    public List<CallRecord> PaidCalls { get; private set; } = [];

    /// <summary>청구되는 기록이 없으면 0 ("값 없음"과 "낼 돈 없음"을 가른다). 단가를 모르면 null.</summary>
    public decimal? TotalCost => PaidCalls.Count == 0 && Calls.Count > 0 ? 0m : pricing.TotalOf(PaidCalls);

    /// <summary>플랫폼별 비용. 표끼리 비교하려는 것이라 기간만 따르고 모드 · 제공사 · 검색 필터는 따르지 않는다.</summary>
    public IReadOnlyList<PlatformCost> Platforms { get; private set; } = [];

    /// <summary>
    /// 오늘(로컬 0시부터) 사용 토큰 (FreeProviders 제외). 화면 필터와 상관없이 전체 기록에서 센다 (하루 상한은 없음).
    /// 위 기간 필터의 "최근 24시간"은 지금부터 24시간 전까지라 이 값과 다르다.
    /// </summary>
    public long TodayCounted { get; private set; }

    /// <summary>오늘 비용 (USD). MCP appsettings의 단가만 쓴다 (대시보드 예전 단가 보완 없음).</summary>
    public decimal TodayCost { get; private set; }

    /// <summary>오늘 비용에 섞인 끊긴 호출 추정분 안내 (없으면 빈 문자열).</summary>
    public string TodayEstimateNote { get; private set; } = "";

    /// <summary>오늘 기록 중 단가가 없어 비용에 넣지 못한 건수.</summary>
    public int TodayUnpriced { get; private set; }

    /// <summary>최근 7일 검토형 서브에이전트 결과 중 서버 기록에 없는 수.</summary>
    public int UnrecordedSubagents { get; private set; }

    /// <summary>API 리뷰(claude-cli 제공사) 이번 달(로컬) 실행 수 · 비용. 월 크레딧을 보며 쓰려고 (차 설계 2c).</summary>
    public int SubReviewMonthRuns { get; private set; }
    public decimal SubReviewMonthCost { get; private set; }

    /// <summary>이번 달 API 리뷰 기록 중 단가가 없어 비용에 넣지 못한 건수.</summary>
    public int SubReviewMonthUnpriced { get; private set; }

    /// <summary>API 리뷰 기록 중 가장 최근의 판정 가능한 기록(성공 또는 Quota)이 Quota면 그 시각 — 크레딧 소진, 서브에이전트로 대체 중.</summary>
    public DateTimeOffset? SubReviewQuotaAt { get; private set; }

    /// <summary>Quota 뒤 서버가 호출을 쉬는 시간(ModeRunner.ClaudeCliQuotaHold)이 지났다 — 다음 호출에서 다시 시도한다.</summary>
    public bool SubReviewQuotaHoldOver => SubReviewQuotaAt is { } at && DateTimeOffset.Now - at >= Agents.ModeRunner.ClaudeCliQuotaHold;

    /// <summary>배지 링크: claude-cli 제공사가 하나면 그 제공사 · 이번 달 기간으로 거른 목록.</summary>
    public string SubReviewLink { get; private set; } = "/";

    private HashSet<string> _cliProviders = [];

    /// <summary>API 리뷰 기록: 제공사 Type이 claude-cli (서버의 Quota 판정과 같은 기준). 설정을 못 읽으면 모드 이름 sub_*로 본다.</summary>
    public bool IsSubReview(CallRecord c) => _cliProviders.Count > 0
        ? c.Provider is { } p && _cliProviders.Contains(p)
        : (c.Mode ?? c.Tool).StartsWith("sub_", StringComparison.Ordinal);

    /// <summary>RunId·Step별 시도 수. 2 이상이면 그 단계에 재시도가 있었다 (시도마다 기록이 한 줄씩 남는다).</summary>
    private Dictionary<(string RunId, int Step), int> _attempts = [];

    /// <summary>재시도가 있었던 단계의 기록이면 (이 시도 번호, 전체 시도 수), 아니면 null.</summary>
    public (int Attempt, int Total)? AttemptOf(CallRecord c) =>
        c.RunId is { } run && c.Step is { } step && _attempts.TryGetValue((run, step), out var total) && total > 1
            ? (c.Attempt ?? 1, total)
            : null;

    /// <summary>화면에 보이는 기록 중 각 실행의 첫 줄(가장 최근 단계). 이유는 이 줄에만 보이고 나머지 줄은 "같은 실행"으로 줄인다.</summary>
    private HashSet<string> _runHeads = [];

    public bool IsRunHead(CallRecord c) => c.RunId is null || _runHeads.Contains(c.Id);

    private Dictionary<string, int> _verdictCounts = [];

    /// <summary>이 실행의 판정 수 (record_verdicts, 대체된 판정 제외).</summary>
    public int VerdictCount(string runId) => _verdictCounts.GetValueOrDefault(runId);

    public void OnGet()
    {
        var all = log.All;
        ToolNames = all.Select(c => c.Tool).Distinct().Order().ToList();
        ProviderNames = all.Select(ProviderOf).Distinct().Order().ToList();

        var budget = pricing.Budget;
        var today = new DateTimeOffset(DateTime.Today);
        var todayCalls = all.Where(c => c.Timestamp >= today).ToList();
        TodayCounted = budget.CountedTokens(todayCalls);
        TodayCost = budget.CountedCost(todayCalls, pricing.Current);
        TodayUnpriced = budget.UnpricedCalls(todayCalls, pricing.Current);
        // 오늘 비용과 같은 기준(MCP 단가 · 무료 제공사 제외)으로 추정분을 센다.
        TodayEstimateNote = pricing.EstimateNote(todayCalls, 2, estimated => budget.CountedCost(estimated, pricing.Current));

        var since = DateTimeOffset.Now.AddDays(-Days);
        var inDays = all.Where(c => Days <= 0 || c.Timestamp >= since).ToList();
        Calls = inDays
            .Where(c => string.IsNullOrEmpty(Tool) || c.Tool == Tool)
            .Where(c => string.IsNullOrEmpty(Provider) || ProviderOf(c) == Provider)
            .Where(c => string.IsNullOrWhiteSpace(Run) || (c.RunId?.StartsWith(Run.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(c => string.IsNullOrWhiteSpace(Q) || Matches(c, Q.Trim()))
            .ToList();
        PaidCalls = Calls.Where(c => !budget.IsFree(ProviderOf(c))).ToList();

        P = Math.Clamp(P, 1, PageCount);
        PageCalls = Calls.Skip((P - 1) * PageSize).Take(PageSize).ToList();

        // 실행의 첫 줄(이유 표시)은 이 쪽 안에서 정한다 — 앞 쪽에서 이어진 실행도 이 쪽의 첫 줄에 이유가 보이게
        _runHeads = PageCalls.Where(c => c.RunId is not null)
            .GroupBy(c => c.RunId!)
            .Select(g => g.First().Id)
            .ToHashSet();

        _verdictCounts = verdicts.Latest.GroupBy(v => v.RunId).ToDictionary(g => g.Key, g => g.Count());
        UnrecordedSubagents = subagents.UnrecordedRecent(all);

        var providers = modes.McpOptionsOrDefault().Providers;
        _cliProviders = providers
            .Where(p => string.Equals(p.Value.Type, Agents.Providers.ClaudeCliProvider.TypeName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        // 설정을 잠깐 못 읽으면(빈 설정) 키로 묶지 못해 표가 달라 보인다 — 그때는 표를 그리지 않는다
        Platforms = providers.Count > 0 ? PlatformCosts.Build(all, inDays, ProviderOf, pricing.CostOf, providers, budget, DateTimeOffset.Now) : [];
        var monthStart = new DateTimeOffset(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        var subMonth = all.Where(c => IsSubReview(c) && c.Timestamp >= monthStart).ToList();
        SubReviewMonthRuns = subMonth.Select(c => c.RunId ?? c.Id).Distinct().Count();
        SubReviewMonthCost = subMonth.Sum(c => pricing.CostOf(c) ?? 0m);
        SubReviewMonthUnpriced = subMonth.Count(c => pricing.CostOf(c) is null);
        SubReviewLink = _cliProviders.Count == 1
            ? $"?Provider={Uri.EscapeDataString(_cliProviders.First())}&Days={DateTime.Today.Day}"
            : $"?Q=sub_&Days={DateTime.Today.Day}";
        // all은 최신순이다.
        SubReviewQuotaAt = all.Where(IsSubReview).FirstOrDefault(c => c.Success || c.ErrorKind == "Quota") is { Success: false } q ? q.Timestamp : null;

        _attempts = all
            .Where(c => c.RunId is not null && c.Step is not null)
            .GroupBy(c => (c.RunId!, c.Step!.Value))
            .ToDictionary(g => g.Key, g => g.Count());

        Daily = Calls
            .GroupBy(c => DateOnly.FromDateTime(c.Timestamp.LocalDateTime))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0)), g.Count()))
            .ToList();
    }

    public static bool Matches(CallRecord c, string q)
    {
        if (c.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (c.RunId?.StartsWith(q, StringComparison.OrdinalIgnoreCase) ?? false) return true;

        bool Has(string? text) => text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false;
        return Has(c.Reason) || Has(c.Role) || Has(c.Agent) || Has(c.Model) || Has(c.Tool)
            || c.Files.Any(Has) || c.Context.Any(Has)
            || Has(c.Response) || Has(c.Error);
    }

    /// <summary>예전(GptAgents 시절) 기록은 Provider가 없으므로 openai로 본다.</summary>
    public static string ProviderOf(CallRecord c) => c.Provider ?? "openai";

    public static string ProjectName(string? dir) =>
        string.IsNullOrEmpty(dir) ? "-" : Path.GetFileName(dir.TrimEnd('/', '\\'));

    public static string Preview(string? text, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var oneLine = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => !l.StartsWith('#') && l != "```"));
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
