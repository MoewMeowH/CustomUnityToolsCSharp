using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>
/// OpenAI 청구 대조. 7일·3일·1일은 UTC 날짜별 청구액 대 기록 계산, 시간별은 Usage API 토큰 대 기록 토큰.
/// 청구·사용량은 버튼(Admin 키) 또는 비용 파일 업로드로만 가져온다 (자동 새로고침과 묶지 않음).
/// </summary>
public sealed class BillingModel(
    IOptions<DashboardOptions> options, CallLogCache log, PricingService pricing, BillingStore store, BillingApi api, ModesConfigService modes) : PageModel
{
    public static readonly string[] Periods = ["7d", "3d", "1d", "1h"];

    [BindProperty(SupportsGet = true)] public string P { get; set; } = "3d";

    public bool HourlyView => P == "1h";
    public int Days => P switch { "7d" => 7, "1d" => 1, _ => 3 };
    public const int Hours = 24;

    public string KeyEnv => options.Value.Billing.AdminKeyEnv;
    public bool HasKey => api.HasKey;
    public BillingStore.Snapshot Data { get; private set; } = null!;
    public List<BillingRow> Rows { get; private set; } = [];
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    /// <summary>오늘(UTC) 누적 청구액. 시간별 보기에서 한 줄로 따로 보여준다.</summary>
    public decimal? TodayBilled { get; private set; }

    public void OnGet() => Build();

    public async Task<IActionResult> OnPostFetchAsync(CancellationToken ct)
    {
        Normalize();
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            // 청구액은 최대 기간(7일)을 한 번에 받아 둔다. 기간을 바꿔도 다시 부르지 않게.
            store.SetCosts(await api.CostsAsync(today.AddDays(-6), 7, ct), "OpenAI Costs API");
            if (HourlyView)
            {
                var from = DateTimeOffset.UtcNow.AddHours(-(Hours - 1));
                store.SetUsage(null, await api.UsageAsync(new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, TimeSpan.Zero), "1h", Hours, ct));
            }
            else
            {
                store.SetUsage(await api.UsageAsync(new DateTimeOffset(today.AddDays(-6).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d", 7, ct), null);
            }
            Message = "OpenAI에서 새로 가져왔습니다.";
        }
        catch (BillingApiException ex)
        {
            Message = $"가져오기 실패 — {ex.Message}";
            MessageIsError = true;
        }
        Build();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, CancellationToken ct)
    {
        Normalize();
        if (file is null || file.Length == 0)
        {
            Message = "파일을 고르세요 (OpenAI 대시보드에서 받은 비용 CSV 또는 JSON).";
            MessageIsError = true;
        }
        else
        {
            try
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var lines = BillingParser.ParseCosts(await reader.ReadToEndAsync(ct));
                store.SetCosts(lines, $"파일 {Path.GetFileName(file.FileName)}");
                Message = $"비용 파일을 읽었습니다 ({lines.Count}줄). 디스크에는 저장하지 않습니다.";
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
            {
                Message = $"파일 형식을 읽지 못했습니다: {ex.Message}";
                MessageIsError = true;
            }
        }
        Build();
        return Page();
    }

    private void Normalize()
    {
        if (!Periods.Contains(P)) P = "3d";
    }

    private void Build()
    {
        Normalize();
        Data = store.Current;
        var calls = log.All;
        Func<string, Agents.ModelPrice?> price = pricing.PriceOf;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var billed = BillingComparer.OpenAIBilled(modes.McpOptionsOrDefault().Providers);
        if (HourlyView)
        {
            Rows = BillingComparer.Hourly(calls, price, DateTimeOffset.UtcNow, Hours, Data.UsageHourly, billed);
            var todayLines = Data.Costs.Where(c => c.Day == today).ToList();
            TodayBilled = todayLines.Count > 0 ? todayLines.Sum(c => c.Amount) : null;
        }
        else
        {
            Rows = BillingComparer.Daily(calls, price, today, Days, Data.Costs, Data.UsageDaily, billed);
        }
    }

    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    public static string Range(DateTimeOffset start, DateTimeOffset end, bool hourly) => hourly
        ? $"{start.UtcDateTime:MM-dd HH}시 UTC · {start.ToOffset(Kst):HH}~{end.ToOffset(Kst):HH}시 KST"
        : $"{start.UtcDateTime:MM-dd} UTC · KST {start.ToOffset(Kst):MM-dd HH:mm} ~ {end.ToOffset(Kst):MM-dd HH:mm}";

    public string Money(decimal? v) => pricing.Format(v, 4);
}
