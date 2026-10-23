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
/// 파일 확인은 <see cref="RecheckInterval"/>에 한 번만 한다 — 기록마다 stat 세 번이 비용 계산 시간의 94%였다
/// (계획서 2026-10-23_대시보드-비용계산-성능: 767건 6.3ms · 10배 60ms). 모드 화면에서 저장하면 간격과 상관없이 바로 다시 본다.
/// 파일을 못 읽으면(안 보임 · 빈 파일 · 깨진 내용 · 잠김) 마지막 정상 값을 쓴다.
/// </summary>
/// <param name="time">시계 (테스트에서 바꾼다). null이면 TimeProvider.System.</param>
public sealed class PricingService(IOptions<DashboardOptions> options, ModesConfigService modes, TimeProvider? time = null)
{
    /// <summary>파일을 다시 확인하는 최소 간격. 밖(메모장 등)에서 고친 단가는 이만큼 늦게 보일 수 있다.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _stamp;       // 마지막으로 읽기에 성공한 파일 판 (시각:크기)
    private string? _brokenStamp; // 내용이 깨져 읽지 못한 판 — 같은 판은 다시 파싱하지 않는다
    private long? _checkedAt;     // 마지막으로 파일을 확인한 시각 (TimeProvider 타임스탬프)
    private int _seenWrites = -1;
    private (PricingOptions Pricing, BudgetOptions Budget) _cached = (new(), new());

    public PricingOptions Current => Load().Pricing;

    /// <summary>MCP 서버의 일일 상한 설정. 대시보드의 "오늘" 칸이 서버 검사와 같은 기준으로 세는 데 쓴다.</summary>
    public BudgetOptions Budget => Load().Budget;

    private (PricingOptions Pricing, BudgetOptions Budget) Load()
    {
        var now = _time.GetTimestamp();
        var writes = modes.WriteCount;
        lock (_gate)
        {
            if (writes == _seenWrites && _checkedAt is { } at && _time.GetElapsedTime(at, now) < RecheckInterval) return _cached;
        }

        var path = modes.McpConfigDir is { } dir ? Path.Combine(dir, "appsettings.json") : null;
        var info = path is null ? null : new FileInfo(path); // Exists · 시각 · 크기를 한 번의 조회로

        lock (_gate)
        {
            _seenWrites = writes;
            if (info is null) // 설정 폴더를 모른다: 빈 설정 (이전과 같음)
            {
                (_stamp, _checkedAt) = (null, now);
                _cached = (new PricingOptions(), new BudgetOptions());
                return _cached;
            }
            // 파일이 잠깐 안 보이거나(편집기의 이름 바꾸기 저장) 막 잘린 빈 파일: 빈 단가로 바꾸지 않고 마지막 정상 값을 유지한다
            // (/self-review eb025da67b C1 — 이전에는 기록 한 건만 빈 값이었으나 간격을 두면 250ms 동안 이어진다).
            if (!info.Exists || info.Length == 0)
            {
                _checkedAt = now;
                return _cached;
            }

            var stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
            if (stamp == _stamp || stamp == _brokenStamp)
            {
                _checkedAt = now;
                return _cached;
            }

            // 읽기에 성공했을 때만 판을 확정한다. 실패하면 마지막 정상 값을 유지한다: 내용이 깨졌으면 그 판을 기억해 다시 파싱하지 않고
            // (C2 — 기록마다 lock 아래 다시 파싱하던 것), 잠김 · 읽는 도중 사라짐이면 다음 요청에서 간격 없이 다시 본다.
            if (Read(path!, out var broken) is { } loaded)
            {
                (_stamp, _brokenStamp, _cached, _checkedAt) = (stamp, null, loaded, now);
            }
            else if (broken)
            {
                (_brokenStamp, _checkedAt) = (stamp, now);
            }
            else _checkedAt = null;
            return _cached;
        }
    }

    /// <returns>읽은 설정, 실패하면 null. broken은 내용이 깨진 경우(같은 판을 다시 읽어도 같다), 아니면 잠김 · 사라짐 같은 일시적 실패.</returns>
    private static (PricingOptions, BudgetOptions)? Read(string path, out bool broken)
    {
        broken = false;
        try
        {
            // optional: false — 확인과 읽기 사이에 파일이 사라지면 빈 설정이 아니라 FileNotFoundException(IOException)
            var config = new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();
            return (config.GetSection("Orchestration:Pricing").Get<PricingOptions>() ?? new PricingOptions(),
                    config.GetSection("Orchestration:Budget").Get<BudgetOptions>() ?? new BudgetOptions());
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException)
        {
            broken = true;
            return null;
        }
    }

    public ModelPrice? PriceOf(string model) =>
        Current.Models.TryGetValue(model, out var p) ? p
        : options.Value.Prices.TryGetValue(model, out var legacy) ? legacy
        : null;

    /// <summary>USD 비용 (계산식은 서버와 같은 <see cref="ModelPrice.CostOf"/>). 단가가 없으면 null.</summary>
    public decimal? CostOf(CallRecord r) => PriceOf(r.Model)?.CostOf(r);

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
