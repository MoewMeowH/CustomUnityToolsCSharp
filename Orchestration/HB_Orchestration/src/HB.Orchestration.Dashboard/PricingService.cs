using System.Globalization;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// 비용 계산과 표시. 단가·환율은 MCP appsettings.json의 Orchestration:Pricing에서 읽는다 (모드 화면의 에이전트 표에서 편집).
/// 거기에 없는 모델은 대시보드 appsettings.json의 Dashboard:Prices(예전 방식)로 보완한다.
/// 같은 파일의 Orchestration:Budget(일일 상한)도 함께 읽는다. 파일이 바뀌었을 때만 다시 읽는다.
/// </summary>
public sealed class PricingService(IOptions<DashboardOptions> options, ModesConfigService modes)
{
    private readonly Lock _gate = new();
    private string? _stamp;
    private (PricingOptions Pricing, BudgetOptions Budget) _cached = (new(), new());

    public PricingOptions Current => Load().Pricing;

    /// <summary>MCP 서버의 일일 상한 설정. 대시보드의 "오늘" 칸이 서버 검사와 같은 기준으로 세는 데 쓴다.</summary>
    public BudgetOptions Budget => Load().Budget;

    private (PricingOptions Pricing, BudgetOptions Budget) Load()
    {
        var path = modes.McpConfigDir is { } dir ? Path.Combine(dir, "appsettings.json") : null;
        var stamp = path is not null && File.Exists(path)
            ? $"{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}"
            : "";

        lock (_gate)
        {
            if (stamp == _stamp) return _cached;

            // 읽기에 성공했을 때만 Stamp를 확정한다. 실패하면 마지막 정상 값을 유지하고 다음 요청에서 다시 읽는다.
            if (Read(path) is { } loaded)
            {
                _stamp = stamp;
                _cached = loaded;
            }
            return _cached;
        }
    }

    /// <returns>읽은 설정. 파일이 없으면 빈 설정, 읽기 실패(잠김·편집 도중 깨진 JSON)면 null.</returns>
    private static (PricingOptions, BudgetOptions)? Read(string? path)
    {
        if (path is null || !File.Exists(path)) return (new PricingOptions(), new BudgetOptions());
        try
        {
            var config = new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build();
            return (config.GetSection("Orchestration:Pricing").Get<PricingOptions>() ?? new PricingOptions(),
                    config.GetSection("Orchestration:Budget").Get<BudgetOptions>() ?? new BudgetOptions());
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public ModelPrice? PriceOf(string model) =>
        Current.Models.TryGetValue(model, out var p) ? p
        : options.Value.Prices.TryGetValue(model, out var legacy) ? legacy
        : null;

    /// <summary>
    /// USD 비용. 단가가 없으면 null.
    /// 입력 토큰에는 캐시된 토큰이 포함되어 있으므로 (입력 - 캐시) × 입력 단가 + 캐시 × 캐시 단가 + 출력 × 출력 단가.
    /// </summary>
    public decimal? CostOf(CallRecord r)
    {
        if (PriceOf(r.Model) is not { } p) return null;

        var input = r.InputTokens ?? 0;
        var cached = Math.Clamp(r.CachedInputTokens ?? 0, 0, input);
        return (input - cached) / 1_000_000m * p.InputPerMillion
             + cached / 1_000_000m * (p.CachedInputPerMillion ?? p.InputPerMillion)
             + (r.OutputTokens ?? 0) / 1_000_000m * p.OutputPerMillion;
    }

    /// <summary>합계. 단가가 있는 기록이 하나도 없으면 null.</summary>
    public decimal? TotalOf(IEnumerable<CallRecord> records)
    {
        decimal sum = 0;
        var any = false;
        foreach (var r in records)
        {
            if (CostOf(r) is not { } c) continue;
            sum += c;
            any = true;
        }
        return any ? sum : null;
    }

    /// <summary>"$0.0123 (₩17)" 형식. 환율이 없으면 달러만, 금액이 없으면 "-".</summary>
    public string Format(decimal? usd, int usdDecimals = 4)
    {
        if (usd is not { } v) return "-";
        var dollars = "$" + v.ToString("F" + usdDecimals, CultureInfo.InvariantCulture);
        return Current.KrwPerUsd is { } rate and > 0
            ? $"{dollars} (₩{Math.Round(v * rate, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture)})"
            : dollars;
    }

    /// <summary>단가 표시용 숫자 (불필요한 0 없이).</summary>
    public static string Number(decimal? value) =>
        value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
}
