using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>청구 대조 설정. 키 값은 두지 않고 환경 변수 이름만 둔다.</summary>
public sealed class BillingOptions
{
    /// <summary>OpenAI 조직 Admin 키(sk-admin-…, Read Only면 충분)를 담은 환경 변수 이름.</summary>
    public string AdminKeyEnv { get; set; } = "OPENAI_ADMIN_KEY";
}

/// <summary>청구 한 줄: UTC 날짜별 금액. LineItem은 "gpt-6.1-sol, cache writes"처럼 나뉘어 올 수 있다(없으면 null).</summary>
public sealed record CostLine(DateOnly Day, string? LineItem, decimal Amount, decimal? Quantity);

/// <summary>OpenAI Usage API의 한 칸 (시작 시각 UTC, 모델별).</summary>
public sealed record UsageRow(DateTimeOffset Start, string Model, long Input, long Cached, long CacheWrite, long Output, long Requests);

/// <summary>OpenAI 비용 파일(Costs API 응답 JSON 또는 대시보드에서 받는 CSV)과 Usage API 응답을 읽는다.</summary>
public static class BillingParser
{
    public static List<CostLine> ParseCosts(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        return trimmed.StartsWith('{') ? ParseCostsJson(trimmed) : ParseCostsCsv(trimmed);
    }

    private static List<CostLine> ParseCostsJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var lines = new List<CostLine>();
        foreach (var bucket in Buckets(doc.RootElement))
        {
            var day = DateOnly.FromDateTime(Unix(bucket.GetProperty("start_time").GetInt64()).UtcDateTime);
            if (!bucket.TryGetProperty("results", out var results)) continue;
            foreach (var r in results.EnumerateArray())
            {
                var amount = r.TryGetProperty("amount", out var a) && a.TryGetProperty("value", out var v) ? Decimal(v) : 0m;
                lines.Add(new CostLine(day, Str(r, "line_item"), amount, r.TryGetProperty("quantity", out var q) ? DecimalOrNull(q) : null));
            }
        }
        return lines;
    }

    private static List<CostLine> ParseCostsCsv(string csv)
    {
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if (rows.Count == 0) return [];
        var header = rows[0].Split(',');
        int Col(string name) => Array.FindIndex(header, h => h.Trim() == name);
        int start = Col("start_time"), amount = Col("amount_value"), item = Col("line_item"), qty = Col("quantity");
        if (start < 0 || amount < 0) throw new FormatException("CSV에 start_time·amount_value 열이 없습니다.");

        var lines = new List<CostLine>();
        foreach (var row in rows.Skip(1))
        {
            // line_item에 쉼표가 들어갈 수 있다 ("gpt-6.1-sol, input"). 따옴표로 감싼 칸을 나눠 읽는다.
            var cells = SplitCsv(row);
            if (cells.Count <= Math.Max(start, amount)) continue;
            var day = DateOnly.FromDateTime(Unix(long.Parse(cells[start], CultureInfo.InvariantCulture)).UtcDateTime);
            var lineItem = item >= 0 && item < cells.Count && cells[item].Length > 0 ? cells[item] : null;
            decimal? quantity = qty >= 0 && qty < cells.Count && decimal.TryParse(cells[qty], NumberStyles.Float, CultureInfo.InvariantCulture, out var qv) ? qv : null;
            lines.Add(new CostLine(day, lineItem, decimal.Parse(cells[amount], NumberStyles.Float, CultureInfo.InvariantCulture), quantity));
        }
        return lines;
    }

    public static List<UsageRow> ParseUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<UsageRow>();
        foreach (var bucket in Buckets(doc.RootElement))
        {
            var start = Unix(bucket.GetProperty("start_time").GetInt64());
            if (!bucket.TryGetProperty("results", out var results)) continue;
            foreach (var r in results.EnumerateArray())
            {
                rows.Add(new UsageRow(start, Str(r, "model") ?? "(모델 없음)",
                    Long(r, "input_tokens"), Long(r, "input_cached_tokens"), Long(r, "input_cache_write_tokens"),
                    Long(r, "output_tokens"), Long(r, "num_model_requests")));
            }
        }
        return rows;
    }

    /// <summary>다음 쪽 커서. 없으면 null.</summary>
    public static string? NextPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True ? Str(root, "next_page") : null;
    }

    /// <summary>버킷 목록. Costs·Usage 응답 구조(data 배열)가 아니면 형식 오류 — 엉뚱한 JSON을 빈 자료로 받아 기존 자료를 지우지 않게.</summary>
    private static IEnumerable<JsonElement> Buckets(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToList()
            : throw new FormatException("비용·사용량 응답 형식이 아닙니다 (data 배열 없음).");

    private static DateTimeOffset Unix(long seconds) =>
        seconds is >= 0 and <= 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(seconds)
        : throw new FormatException($"start_time {seconds}이(가) 시각 범위를 벗어났습니다.");

    private static List<string> SplitCsv(string row)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '"') { if (quoted && i + 1 < row.Length && row[i + 1] == '"') { sb.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static decimal Decimal(JsonElement v) => DecimalOrNull(v) ?? 0m;

    private static decimal? DecimalOrNull(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}

/// <summary>OpenAI 조직 Costs·Usage API (조회 전용). 오류 문구는 직접 만들어 요청 헤더(키)가 섞이지 않게 한다.</summary>
public sealed class BillingApi(HttpClient http, Func<string?> adminKey)
{
    private const string Base = "https://api.openai.com/v1/organization/";
    private const int MaxPages = 20;

    public bool HasKey => !string.IsNullOrWhiteSpace(adminKey());

    public async Task<List<CostLine>> CostsAsync(DateOnly fromUtc, int days, CancellationToken ct)
    {
        var lines = new List<CostLine>();
        foreach (var page in await PagesAsync($"costs?start_time={UnixDay(fromUtc)}&bucket_width=1d&limit={days}&group_by=line_item", ct))
            lines.AddRange(Parse(() => BillingParser.ParseCosts(page)));
        return lines;
    }

    /// <param name="bucket">"1d" 또는 "1h".</param>
    public async Task<List<UsageRow>> UsageAsync(DateTimeOffset fromUtc, string bucket, int limit, CancellationToken ct)
    {
        var rows = new List<UsageRow>();
        foreach (var page in await PagesAsync($"usage/completions?start_time={fromUtc.ToUnixTimeSeconds()}&bucket_width={bucket}&limit={limit}&group_by=model", ct))
            rows.AddRange(Parse(() => BillingParser.ParseUsage(page)));
        return rows;
    }

    private async Task<List<string>> PagesAsync(string query, CancellationToken ct)
    {
        var key = adminKey();
        if (string.IsNullOrWhiteSpace(key)) throw new BillingApiException("Admin 키가 없습니다.");

        var pages = new List<string>();
        string? cursor = null;
        for (var i = 0; i < MaxPages; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Base + query + (cursor is null ? "" : $"&page={Uri.EscapeDataString(cursor)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) { throw new BillingApiException($"연결 실패: {ex.Message}"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BillingApiException("시간 초과 (응답이 없습니다)."); }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw new BillingApiException($"HTTP {(int)response.StatusCode}: {ErrorMessage(body)}");
                pages.Add(body);
                try { cursor = BillingParser.NextPage(body); }
                catch (JsonException) { throw new BillingApiException("응답 형식을 읽지 못했습니다."); }
                if (cursor is null) return pages;
            }
        }
        return pages;
    }

    private static List<T> Parse<T>(Func<List<T>> parse)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        { throw new BillingApiException($"응답 형식을 읽지 못했습니다: {ex.Message}"); }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) return m.GetString() ?? "";
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }

    private static long UnixDay(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
}

public sealed class BillingApiException(string message) : Exception(message);

/// <summary>마지막으로 가져오거나 올린 청구·사용량 자료 (메모리에만 보관).</summary>
public sealed class BillingStore
{
    private readonly Lock _gate = new();
    private Snapshot _current = new([], null, null, [], [], null);

    public sealed record Snapshot(
        IReadOnlyList<CostLine> Costs, string? CostSource, DateTimeOffset? CostAt,
        IReadOnlyList<UsageRow> UsageDaily, IReadOnlyList<UsageRow> UsageHourly, DateTimeOffset? UsageAt);

    public Snapshot Current { get { lock (_gate) return _current; } }

    public void SetCosts(IReadOnlyList<CostLine> costs, string source) { lock (_gate) _current = _current with { Costs = costs, CostSource = source, CostAt = DateTimeOffset.Now }; }

    public void SetUsage(IReadOnlyList<UsageRow>? daily, IReadOnlyList<UsageRow>? hourly)
    {
        lock (_gate)
            _current = _current with { UsageDaily = daily ?? _current.UsageDaily, UsageHourly = hourly ?? _current.UsageHourly, UsageAt = DateTimeOffset.Now };
    }
}

/// <summary>토큰 합계 (입력·캐시 읽기·캐시 쓰기·출력·요청 수).</summary>
public sealed record TokenSum(long Input, long Cached, long CacheWrite, long Output, long Requests)
{
    public static readonly TokenSum Zero = new(0, 0, 0, 0, 0);
    public TokenSum Add(long input, long cached, long write, long output, long requests) =>
        new(Input + input, Cached + cached, CacheWrite + write, Output + output, Requests + requests);
}

/// <summary>대조 표의 한 행 (하루 또는 한 시간, UTC).</summary>
public sealed record BillingRow(
    DateTimeOffset Start, DateTimeOffset End,
    decimal? Billed, IReadOnlyList<CostLine> BilledItems,
    decimal Computed, decimal ComputedAssumed, int UnknownWriteCalls, int UnpricedCalls,
    TokenSum Ours, TokenSum? OpenAI);

/// <summary>우리 기록(openai 호출)과 OpenAI 청구·사용량을 UTC 칸별로 맞춘다. 계산식은 서버 비용 상한과 같은 ModelPrice.CostOf.</summary>
public static class BillingComparer
{
    public static List<BillingRow> Daily(IEnumerable<CallRecord> calls, Func<string, ModelPrice?> price,
        DateOnly lastDayUtc, int days, IReadOnlyList<CostLine> costs, IReadOnlyList<UsageRow> usage)
    {
        var rows = new List<BillingRow>();
        var openai = OpenAICalls(calls);
        for (var i = days - 1; i >= 0; i--)
        {
            var day = lastDayUtc.AddDays(-i);
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var items = costs.Where(c => c.Day == day).ToList();
            rows.Add(Row(start, start.AddDays(1), openai, price, items.Count > 0 ? items.Sum(c => c.Amount) : null, items, usage));
        }
        return rows;
    }

    public static List<BillingRow> Hourly(IEnumerable<CallRecord> calls, Func<string, ModelPrice?> price,
        DateTimeOffset nowUtc, int hours, IReadOnlyList<UsageRow> usage)
    {
        var rows = new List<BillingRow>();
        var openai = OpenAICalls(calls);
        var last = new DateTimeOffset(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, nowUtc.UtcDateTime.Day, nowUtc.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
        for (var i = hours - 1; i >= 0; i--)
        {
            var start = last.AddHours(-i);
            rows.Add(Row(start, start.AddHours(1), openai, price, null, [], usage));
        }
        return rows;
    }

    private static List<CallRecord> OpenAICalls(IEnumerable<CallRecord> calls) =>
        calls.Where(c => string.Equals(c.Provider, "openai", StringComparison.OrdinalIgnoreCase)).ToList();

    private static BillingRow Row(DateTimeOffset start, DateTimeOffset end, List<CallRecord> calls, Func<string, ModelPrice?> price,
        decimal? billed, IReadOnlyList<CostLine> items, IReadOnlyList<UsageRow> usage)
    {
        decimal computed = 0, assumed = 0;
        int unknown = 0, unpriced = 0;
        var ours = TokenSum.Zero;
        foreach (var c in calls.Where(c => c.Timestamp >= start && c.Timestamp < end))
        {
            var input = c.InputTokens ?? 0;
            var cached = c.CachedInputTokens ?? 0;
            ours = ours.Add(input, cached, c.CacheWriteTokens ?? 0, c.OutputTokens ?? 0, c.InputTokens is null ? 0 : 1);
            if (price(c.Model) is not { } p) { if (c.InputTokens is not null) unpriced++; continue; }
            computed += p.CostOf(c);
            if (c.CacheWriteTokens is null && c.InputTokens is not null)
            {
                // 쓰기를 기록하기 전 호출은 OpenAI 기본 동작(캐시에 안 걸린 입력 전부 쓰기)으로 가정한 값도 낸다.
                unknown++;
                assumed += p.CostOf(c with { CacheWriteTokens = Math.Max(input - cached, 0) });
            }
            else assumed += p.CostOf(c);
        }

        var inRange = usage.Where(u => u.Start >= start && u.Start < end).ToList();
        TokenSum? openai = inRange.Count == 0 ? null
            : inRange.Aggregate(TokenSum.Zero, (s, u) => s.Add(u.Input, u.Cached, u.CacheWrite, u.Output, u.Requests));

        return new BillingRow(start, end, billed, items, computed, assumed, unknown, unpriced, ours, openai);
    }
}
