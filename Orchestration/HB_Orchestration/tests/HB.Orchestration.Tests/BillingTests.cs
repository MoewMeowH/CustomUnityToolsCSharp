using System.Net;
using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>청구 대조: 비용 파일(JSON·CSV)·Usage 응답 파싱, UTC 칸별 대조 계산, Costs·Usage API 호출(쪽 넘김·오류).</summary>
public sealed class BillingTests
{
    // 2026-10-18 사용자가 받은 비용 파일과 같은 모양 (UTC 10-17·10-18 두 버킷)
    private const string CostJson = """
        {"object":"list","data":[
          {"object":"bucket","start_time":1792195200,"end_time":1792281600,"results":[{"object":"organization.costs.result","amount":{"value":"1.349377675000000000000000000","currency":"usd"},"line_item":null}]},
          {"object":"bucket","start_time":1792281600,"end_time":1792368000,"results":[{"object":"organization.costs.result","amount":{"value":"0.5403879000000000000000000000","currency":"usd"},"line_item":null}]}]}
        """;

    private const string CostCsv = """
        start_time,end_time,start_time_iso,end_time_iso,amount_value,amount_currency,quantity,quantity_unit,line_item,user_id,api_key_id,project_id,organization_id,project_name,organization_name,user_email,api_source
        1792195200,1792281600,2026-10-17T00:00:00,2026-10-18T00:00:00,1.349377675000000000000000000,usd,,,,,,,org-x,,Personal,,
        1792281600,1792368000,2026-10-18T00:00:00,2026-10-19T00:00:00,0.7910262000000000000000000000,usd,,,,,,,org-x,,Personal,,
        """;

    private static readonly DateOnly D17 = new(2026, 10, 17), D18 = new(2026, 10, 18);

    [Fact]
    public void ParseCosts_Json()
    {
        var lines = BillingParser.ParseCosts(CostJson);
        Assert.Equal([(D17, 1.349377675m), (D18, 0.5403879m)], lines.Select(l => (l.Day, l.Amount)));
        Assert.All(lines, l => Assert.Null(l.LineItem));
    }

    [Fact]
    public void ParseCosts_Csv()
    {
        var lines = BillingParser.ParseCosts("﻿" + CostCsv);
        Assert.Equal([(D17, 1.349377675m), (D18, 0.7910262m)], lines.Select(l => (l.Day, l.Amount)));
    }

    [Fact]
    public void ParseCosts_CsvQuotedLineItemWithComma()
    {
        const string csv = "start_time,amount_value,quantity,line_item\n1792281600,0.39,155301,\"gpt-6.1-sol, cache writes\"\n";
        var line = Assert.Single(BillingParser.ParseCosts(csv));
        Assert.Equal("gpt-6.1-sol, cache writes", line.LineItem);
        Assert.Equal(155301m, line.Quantity);
    }

    [Fact]
    public void ParseCosts_JsonNumberAmountsAndLineItems()
    {
        const string json = """
            {"object":"page","data":[{"start_time":1792281600,"results":[
              {"amount":{"value":0.3882525,"currency":"usd"},"line_item":"gpt-6.1-sol, cache writes","quantity":155301.0},
              {"amount":{"value":0.39238,"currency":"usd"},"line_item":"gpt-6.1-sol, output","quantity":39238.0}]}],"has_more":false,"next_page":null}
            """;
        var lines = BillingParser.ParseCosts(json);
        Assert.Equal(0.7806325m, lines.Sum(l => l.Amount));
        Assert.Equal(["gpt-6.1-sol, cache writes", "gpt-6.1-sol, output"], lines.Select(l => l.LineItem));
    }

    [Theory]
    [InlineData("{깨진")]
    [InlineData("a,b\n1,2")]
    public void ParseCosts_Broken_Throws(string text) =>
        Assert.ThrowsAny<Exception>(() => BillingParser.ParseCosts(text));

    [Fact]
    public void ParseUsage_ReadsCacheWriteTokens()
    {
        const string json = """
            {"object":"page","data":[{"start_time":1792310400,"results":[{"model":"gpt-6.1-sol","num_model_requests":9,
              "input_tokens":149355,"output_tokens":38362,"input_cached_tokens":5144,"input_cache_write_tokens":140904}]}]}
            """;
        var row = Assert.Single(BillingParser.ParseUsage(json));
        Assert.Equal(new UsageRow(DateTimeOffset.FromUnixTimeSeconds(1792310400), "gpt-6.1-sol", 149355, 5144, 140904, 38362, 9), row);
    }

    // ── 대조 계산 ───────────────────────────────────────────────────────

    private static readonly ModelPrice Sol = new() { InputPerMillion = 2, CachedInputPerMillion = 0.1m, CacheWritePerMillion = 2.5m, OutputPerMillion = 10 };

    private static CallRecord Call(string kst, long input, long output, long? write, string provider = "openai", long cached = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = DateTimeOffset.Parse(kst),
        Tool = "review", Agent = "a", Model = "gpt-6.1-sol", Prompt = "p", Provider = provider,
        InputTokens = input, OutputTokens = output, CachedInputTokens = cached, CacheWriteTokens = write,
    };

    [Fact]
    public void Daily_SplitsAtUtcMidnight_AssumesWritesForUnrecordedCalls_ExcludesOtherProviders()
    {
        var calls = new[]
        {
            Call("2026-10-18T08:59:00+09:00", 1_000_000, 0, null),    // UTC 10-17, 쓰기 미기록
            Call("2026-10-18T09:00:00+09:00", 1_000_000, 0, 1_000_000), // UTC 10-18, 전부 쓰기
            Call("2026-10-18T10:00:00+09:00", 1_000_000, 0, null, provider: "gemini"),
        };
        var costs = new List<CostLine> { new(D18, "a", 2.0m, null), new(D18, "b", 0.5m, null) };

        var rows = BillingComparer.Daily(calls, _ => Sol, D18, 2, costs, []);

        Assert.Equal([D17, D18], rows.Select(r => DateOnly.FromDateTime(r.Start.UtcDateTime)));
        var (r17, r18) = (rows[0], rows[1]);
        Assert.Null(r17.Billed);
        Assert.Equal(2m, r17.Computed);          // 쓰기 0으로 계산
        Assert.Equal(2.5m, r17.ComputedAssumed); // 전부 쓰기 가정
        Assert.Equal(1, r17.UnknownWriteCalls);
        Assert.Equal(2.5m, r18.Billed);
        Assert.Equal(2.5m, r18.Computed);
        Assert.Equal(0, r18.UnknownWriteCalls);
        Assert.Equal(1, r18.Ours.Requests);      // gemini 제외
    }

    [Fact]
    public void Hourly_BucketsCallsAndUsage()
    {
        var now = DateTimeOffset.Parse("2026-10-18T09:30:00Z");
        var calls = new[] { Call("2026-10-18T18:19:00+09:00", 4525, 246, 4491), Call("2026-10-18T17:40:00+09:00", 5147, 256, 0, cached: 5144) };
        var usage = new List<UsageRow> { new(DateTimeOffset.Parse("2026-10-18T09:00:00Z"), "gpt-6.1-sol", 4525, 0, 4491, 246, 1) };

        var rows = BillingComparer.Hourly(calls, _ => Sol, now, 2, usage);

        Assert.Equal([DateTimeOffset.Parse("2026-10-18T08:00:00Z"), DateTimeOffset.Parse("2026-10-18T09:00:00Z")], rows.Select(r => r.Start));
        Assert.Equal(5144, rows[0].Ours.Cached);
        Assert.Null(rows[0].OpenAI);
        Assert.Equal(new TokenSum(4525, 0, 4491, 246, 1), rows[1].OpenAI);
        Assert.Equal(rows[1].OpenAI, rows[1].Ours);
    }

    // ── API 호출 ────────────────────────────────────────────────────────

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private int _i;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = replies[Math.Min(_i++, replies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Costs_FollowsNextPage_AndSendsKey()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, """{"data":[{"start_time":1792195200,"results":[{"amount":{"value":"1.0"}}]}],"has_more":true,"next_page":"p2"}"""),
            (HttpStatusCode.OK, """{"data":[{"start_time":1792281600,"results":[{"amount":{"value":"0.5"}}]}],"has_more":false,"next_page":null}"""));
        var api = new BillingApi(new HttpClient(handler), () => "sk-admin-test");

        var lines = await api.CostsAsync(D17, 2, CancellationToken.None);

        Assert.Equal(1.5m, lines.Sum(l => l.Amount));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=p2", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("group_by=line_item", handler.Requests[0].RequestUri!.Query);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer sk-admin-test", r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task Error_UsesOpenAIMessage_WithoutKey()
    {
        var handler = new ScriptedHandler((HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid admin key"}}"""));
        var api = new BillingApi(new HttpClient(handler), () => "sk-admin-secret");

        var ex = await Assert.ThrowsAsync<BillingApiException>(() => api.UsageAsync(DateTimeOffset.UnixEpoch, "1h", 24, CancellationToken.None));

        Assert.Equal("HTTP 401: Invalid admin key", ex.Message);
        Assert.DoesNotContain("sk-admin-secret", ex.Message);
    }

    [Fact]
    public async Task NoKey_DoesNotCall()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, "{}"));
        var api = new BillingApi(new HttpClient(handler), () => null);

        Assert.False(api.HasKey);
        await Assert.ThrowsAsync<BillingApiException>(() => api.CostsAsync(D17, 1, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("start_time,amount_value\n253402300800,1.0\n")]
    public void ParseCosts_WrongShapeOrOutOfRangeTime_IsFormatError(string text) =>
        Assert.Throws<FormatException>(() => BillingParser.ParseCosts(text));

    [Fact]
    public void Daily_CountsUnpricedCalls()
    {
        var rows = BillingComparer.Daily([Call("2026-10-18T10:00:00+09:00", 1000, 10, 0)], _ => null, D18, 1, [], []);
        Assert.Equal(1, rows[0].UnpricedCalls);
        Assert.Equal(0m, rows[0].Computed);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Timeout_BecomesBillingApiException()
    {
        var api = new BillingApi(new HttpClient(new SlowHandler()) { Timeout = TimeSpan.FromMilliseconds(50) }, () => "sk-admin-test");
        var ex = await Assert.ThrowsAsync<BillingApiException>(() => api.CostsAsync(D17, 1, CancellationToken.None));
        Assert.StartsWith("시간 초과", ex.Message);
    }

    [Fact]
    public async Task SuccessWithNonJsonBody_BecomesBillingApiException()
    {
        var api = new BillingApi(new HttpClient(new ScriptedHandler((HttpStatusCode.OK, "<html>"))), () => "sk-admin-test");
        await Assert.ThrowsAsync<BillingApiException>(() => api.CostsAsync(D17, 1, CancellationToken.None));
    }
}
