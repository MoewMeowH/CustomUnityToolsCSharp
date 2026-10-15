using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

public sealed class DashboardOptions
{
    /// <summary>비우면 MCP 서버와 같은 기본 경로(CallLog.DefaultPath)를 쓴다.</summary>
    public string? LogPath { get; set; }

    /// <summary>
    /// 파일 링크 템플릿. {path}=절대 경로(URL 인코딩), {line}=줄 번호.
    /// 기본값은 JetBrains IDE 내장 서버로 Rider에서 바로 연다.
    /// </summary>
    public string FileLinkTemplate { get; set; } = "http://localhost:63342/api/file?file={path}&line={line}";

    /// <summary>모델별 1M 토큰당 가격(USD). 비워두면 비용 칸은 '-'.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ResolvedLogPath => string.IsNullOrWhiteSpace(LogPath) ? CallLog.DefaultPath : LogPath;

    public decimal? CostOf(CallRecord r)
    {
        if (!Prices.TryGetValue(r.Model, out var p)) return null;
        return (r.InputTokens ?? 0) / 1_000_000m * p.InputPerMillion
             + (r.OutputTokens ?? 0) / 1_000_000m * p.OutputPerMillion;
    }
}

public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}
