using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace HB.Orchestration.Tests;

/// <summary>
/// 웹검색: 설정 검사, /responses 본문 정책(store=false, max_tool_calls, include, blocked_domains, search_context_size),
/// 실제 SDK 경로의 요청·기록(검색어·출처·인용·사용량), 출처 대조(본문 URL이 검색 결과에 없으면 경고), 비용, 모드 검사.
/// </summary>
public sealed class WebSearchTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ── 설정 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, null, null, "MaxCalls")]
    [InlineData(16, null, null, "MaxCalls")]
    [InlineData(5, "https://a.com", null, "도메인만")]
    [InlineData(5, "a.com/x", null, "도메인만")]
    [InlineData(5, null, "huge", "ContextSize")]
    public void Settings_Problem(int maxCalls, string? domain, string? size, string expected)
    {
        var s = new WebSearchSettings { MaxCalls = maxCalls, ContextSize = size, BlockedDomains = domain is null ? [] : [domain] };
        Assert.Contains(expected, s.Problem());
    }

    [Fact]
    public void Settings_Valid() =>
        Assert.Null(new WebSearchSettings { MaxCalls = 12, BlockedDomains = ["spam.example"], ContextSize = "low" }.Problem());

    // ── 본문 정책 ───────────────────────────────────────────────────────

    private static JsonObject Rewrite(string json, WebSearchSettings? s) =>
        JsonNode.Parse((WebSearchPolicy.Rewrite(BinaryData.FromString(json), s) ?? BinaryData.FromString(json)).ToString())!.AsObject();

    [Fact]
    public void Policy_AlwaysStoreFalse_NoSearchFieldsWithoutTool()
    {
        var body = Rewrite("""{"model":"m","input":[]}""", new WebSearchSettings());

        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Null(body["max_tool_calls"]);
        Assert.Null(body["include"]);
    }

    [Fact]
    public void Policy_WithTool_AddsLimitsFiltersAndSources()
    {
        var body = Rewrite("""{"model":"m","tools":[{"type":"web_search"}],"include":["x"]}""",
            new WebSearchSettings { MaxCalls = 8, BlockedDomains = ["spam.example"], ContextSize = "low" });

        Assert.Equal(8, body["max_tool_calls"]!.GetValue<int>());
        Assert.Equal(["x", WebSearchPolicy.SourcesInclude], body["include"]!.AsArray().Select(n => n!.GetValue<string>()));
        var tool = body["tools"]![0]!;
        Assert.Equal("spam.example", tool["filters"]!["blocked_domains"]![0]!.GetValue<string>());
        Assert.Equal("low", tool["search_context_size"]!.GetValue<string>());
    }

    [Fact]
    public void Policy_ToolWithoutScope_OnlyStore()
    {
        var body = Rewrite("""{"tools":[{"type":"web_search"}]}""", null);

        Assert.Null(body["max_tool_calls"]);
        Assert.False(body["store"]!.GetValue<bool>());
    }

    [Fact]
    public void Policy_KeepsExplicitStoreAndBrokenBodyUntouched()
    {
        Assert.Null(WebSearchPolicy.Rewrite(BinaryData.FromString("""{"store":true}"""), null));
        Assert.Null(WebSearchPolicy.Rewrite(BinaryData.FromString("{깨진"), new WebSearchSettings()));
    }

    // ── 출처 대조 ───────────────────────────────────────────────────────

    [Fact]
    public void Unverified_IgnoresSchemeQueryFragmentSlashWww()
    {
        var known = new[] { "https://www.Issuetracker.unity3d.com/issues/a/?utm_source=openai", "https://github.com/x/y" };
        const string text = """
            - 근거: http://issuetracker.unity3d.com/issues/a#c3, (https://github.com/x/y).
            - 다른 곳: https://example.com/made-up-thread/ 와 [링크](https://example.org/p?q=1)
            """;

        Assert.Equal(["https://example.com/made-up-thread/", "https://example.org/p?q=1"], WebSearchExtractor.Unverified(text, known));
    }

    [Fact]
    public void Unverified_KeepsBalancedParenthesesInPath()
    {
        Assert.Empty(WebSearchExtractor.Unverified("참고: https://en.wikipedia.org/wiki/Foo_(bar)", ["https://en.wikipedia.org/wiki/Foo_(bar)"]));
    }

    // ── 실제 SDK 경로 (가짜 HTTP) ──────────────────────────────────────

    private sealed class CaptureHandler(string reply) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private const string SearchReply = """
        {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"m","output":[
         {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"il2cpp abort","queries":["il2cpp abort","unity crash libil2cpp"],
           "sources":[{"type":"url","url":"https://issuetracker.unity3d.com/issues/a"},{"type":"url","url":"https://discussions.unity.com/t/b"}]}},
         {"type":"web_search_call","id":"ws_2","status":"completed","action":{"type":"open_page","url":"https://github.com/mirror/commit/abc"}},
         {"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text",
           "text":"유력: https://issuetracker.unity3d.com/issues/a 수정: https://github.com/mirror/commit/abc 의심: https://example.com/fake",
           "annotations":[{"type":"url_citation","start_index":4,"end_index":44,"url":"https://issuetracker.unity3d.com/issues/a?utm_source=openai","title":"A"}]}]}],
         "usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":0},"output_tokens":50,"output_tokens_details":{"reasoning_tokens":10},"total_tokens":1050},
         "parallel_tool_calls":true,"tool_choice":"auto","tools":[],"text":{"format":{"type":"text"}}}
        """;

    /// <summary>search 2 · open_page 1 · find_in_page 1 (같은 페이지).</summary>
    private const string MixedActionsReply = """
        {"id":"resp_2","object":"response","created_at":1,"status":"completed","model":"m","output":[
         {"type":"web_search_call","id":"ws_1","status":"completed","action":{"type":"search","query":"a","sources":[{"type":"url","url":"https://a.example/1"}]}},
         {"type":"web_search_call","id":"ws_2","status":"completed","action":{"type":"open_page","url":"https://a.example/1"}},
         {"type":"web_search_call","id":"ws_3","status":"completed","action":{"type":"find_in_page","url":"https://a.example/1","pattern":"fix"}},
         {"type":"web_search_call","id":"ws_4","status":"completed","action":{"type":"search","query":"b"}},
         {"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"끝","annotations":[]}]}],
         "usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":0},"output_tokens":5,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":105},
         "parallel_tool_calls":true,"tool_choice":"auto","tools":[],"text":{"format":{"type":"text"}}}
        """;

    private (AgentEntry Agent, CaptureHandler Handler) ResponsesAgent(string reply = SearchReply)
    {
        var handler = new CaptureHandler(reply);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        options.AddPolicy(new WebSearchPolicy(), PipelinePosition.PerCall);
#pragma warning disable OPENAI001
        var client = new OpenAIClient(new ApiKeyCredential("k"), options).GetResponsesClient().AsIChatClient("m");
#pragma warning restore OPENAI001
        var agent = new AgentEntry(new AgentOptions { Name = "luna_web", Provider = "openai-web", Model = "m", MaxAttempts = 1 }, client)
        {
            ProviderOptions = new ProviderOptions { Type = OpenAIResponsesProvider.TypeName },
        };
        return (agent, handler);
    }

    private async Task<(StepResult Result, CallRecord Record, CaptureHandler Handler)> RunSearchStep(WebSearchSettings? search, string reply = SearchReply)
    {
        var (agent, handler) = ResponsesAgent(reply);
        var call = new StepCall("r000000001", "crash", 1, "CrashSearch", agent, "역할", "## 요청\n찾아", new CallMeta(null, [])) { WebSearch = search };
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, _h.LogPath);
        var result = await runner.RunStepAsync(call, CancellationToken.None);
        return (result, CallLog.ReadAll(_h.LogPath).Single(), handler);
    }

    [Fact]
    public async Task Sdk_SendsWebSearchToolWithLimits()
    {
        var (_, _, handler) = await RunSearchStep(new WebSearchSettings { MaxCalls = 8, BlockedDomains = ["spam.example"] });

        Assert.EndsWith("/responses", handler.Url);
        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("web_search", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("spam.example", body["tools"]![0]!["filters"]!["blocked_domains"]![0]!.GetValue<string>());
        Assert.Equal(8, body["max_tool_calls"]!.GetValue<int>());
        Assert.Contains(WebSearchPolicy.SourcesInclude, body["include"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(body["store"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sdk_RecordsSearchAndUsage_WarnsUnverifiedUrl()
    {
        var (result, record, _) = await RunSearchStep(new WebSearchSettings());

        var ws = record.WebSearch!;
        Assert.Equal(2, ws.Calls);
        Assert.Equal(1, ws.SearchActions); // open_page는 청구 횟수에서 뺀다
        Assert.Equal(["il2cpp abort", "unity crash libil2cpp"], ws.Queries);
        Assert.Equal(["https://issuetracker.unity3d.com/issues/a", "https://discussions.unity.com/t/b"], ws.Sources);
        Assert.Single(ws.Citations);
        Assert.Equal(["https://github.com/mirror/commit/abc"], ws.Opened); // 연 페이지는 대조 목록에 들어가 경고하지 않는다
        Assert.Equal(["https://example.com/fake"], ws.Unverified);
        Assert.Equal(1000, record.InputTokens);
        Assert.Contains("\"input_tokens\"", record.UsageRaw);
        Assert.Contains(result.Warnings, w => w.StartsWith("[경고] 검색 결과에 없는 URL 1개") && w.Contains("https://example.com/fake"));
    }

    [Fact]
    public async Task Sdk_SearchActions_ExcludeOpenAndFindInPage()
    {
        var (_, record, _) = await RunSearchStep(new WebSearchSettings(), MixedActionsReply);

        var ws = record.WebSearch!;
        Assert.Equal(4, ws.Calls);
        Assert.Equal(2, ws.SearchActions);
        Assert.Equal(2, ws.BilledCalls);
        Assert.Equal(["https://a.example/1"], ws.Opened);
    }

    [Fact]
    public async Task Sdk_UnknownActionType_CountedAsSearch()
    {
        var reply = MixedActionsReply.Replace("""{"type":"search","query":"b"}""", """{"type":"future_action","x":1}""");

        var (_, record, _) = await RunSearchStep(new WebSearchSettings(), reply);

        Assert.Equal(2, record.WebSearch!.SearchActions); // 새 동작 타입도 적게 세지 않는다
    }

    [Fact]
    public void From_RawWithoutSearchItems_ButMessagesHaveSearch_FallsBackToCalls()
    {
        // 원본이 응답 일부만 담은 경우(web_search_call 0개)에 검색 요금을 0으로 세지 않는다.
#pragma warning disable OPENAI001
        var raw = System.ClientModel.Primitives.ModelReaderWriter.Read<OpenAI.Responses.ResponseResult>(BinaryData.FromString("""
            {"id":"resp_3","object":"response","created_at":1,"status":"completed","model":"m","output":[
             {"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"끝","annotations":[]}]}],
             "parallel_tool_calls":true,"tool_choice":"auto","tools":[],"text":{"format":{"type":"text"}}}
            """))!;
#pragma warning restore OPENAI001
        var messages = new[] { new ChatMessage(ChatRole.Assistant, [new WebSearchToolCallContent("ws_1")]) };

        var wrapped = new ChatResponse(messages) { RawRepresentation = raw };
        Assert.Equal((0, 0), (WebSearchExtractor.RawActions(wrapped).SearchActions, WebSearchExtractor.RawActions(wrapped).RawCalls)); // 원본은 읽힘

        var ws = WebSearchExtractor.From(messages, "끝", wrapped);

        Assert.Equal(1, ws.Calls);
        Assert.Null(ws.SearchActions);
        Assert.Equal(1, ws.BilledCalls);
    }

    [Fact]
    public void RawActions_NoRawResponse_IsNull()
    {
        Assert.Null(WebSearchExtractor.RawActions(null).SearchActions);
        Assert.Null(WebSearchExtractor.RawActions("not a response").SearchActions);
    }

    [Fact]
    public void Record_SearchActionsNull_NotWrittenAndOldRecordsRead()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new WebSearchRecord { Calls = 3 });
        Assert.DoesNotContain("searchActions", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("billedCalls", json, StringComparison.OrdinalIgnoreCase);

        var old = System.Text.Json.JsonSerializer.Deserialize<WebSearchRecord>("""{"calls":3}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Null(old.SearchActions);
        Assert.Equal(3, old.BilledCalls);
    }

    [Fact]
    public async Task Sdk_NoSearchSettings_NoToolAndNoRecordField()
    {
        var (_, record, handler) = await RunSearchStep(null);

        Assert.Null(JsonNode.Parse(handler.Body!)!["tools"]);
        Assert.Null(record.WebSearch);
    }

    // ── 비용 ────────────────────────────────────────────────────────────

    [Fact]
    public void Cost_IncludesSearchCalls()
    {
        var price = new ModelPrice { InputPerMillion = 1, OutputPerMillion = 1, WebSearchPerThousand = 10 };
        var record = new CallRecord
        {
            Id = "a", Timestamp = DateTimeOffset.Now, Tool = "t", Model = "m", Prompt = "p",
            InputTokens = 1_000_000, OutputTokens = 0, WebSearch = new WebSearchRecord { Calls = 3 },
        };

        Assert.Equal(1m + 0.03m, price.CostOf(record));
        Assert.Equal(1m, new ModelPrice { InputPerMillion = 1 }.CostOf(record)); // 검색 단가가 없으면 0
        // search 동작 수가 있으면 그것으로 (open_page·find_in_page 제외), 없으면(예전 기록) 모든 동작 수
        Assert.Equal(1m + 0.01m, price.CostOf(record with { WebSearch = new WebSearchRecord { Calls = 3, SearchActions = 1 } }));
    }

    // ── 모드 검사 ───────────────────────────────────────────────────────

    private void WriteModes(string stepExtra) =>
        File.WriteAllText(_h.ModesPath, ModeRunnerHarness.DefaultModes.Replace(
            "\"seq\":  { \"Steps\": [ { \"Role\": \"R\", \"Agent\": \"a1\" } ] }",
            "\"seq\":  { \"Steps\": [ { \"Role\": \"R\", \"Agent\": \"a1\"" + stepExtra + " } ] }"));

    [Fact]
    public async Task Mode_WebSearchOnChatCompletionsAgent_RejectedBeforeCall()
    {
        WriteModes(", \"WebSearch\": { \"MaxCalls\": 3 }");

        var result = await _h.Run("seq");

        Assert.Contains("openai-responses", result);
        Assert.Empty(_h.Client.Received);
    }

    [Fact]
    public async Task Mode_BadSearchSettings_Rejected()
    {
        WriteModes(", \"WebSearch\": { \"MaxCalls\": 50 }");

        var result = await _h.Run("seq");

        Assert.Contains("MaxCalls", result);
        Assert.Empty(_h.Client.Received);
    }

    [Fact]
    public async Task Mode_SearchStep_GetsRulesAndTool()
    {
        WriteModes(", \"WebSearch\": { \"MaxCalls\": 3 }");
        // 제공사 Type이 openai-responses인 가짜 하네스 (요청은 가짜 클라이언트가 받는다).
        var harness = new ModeRunnerHarness(providerType: OpenAIResponsesProvider.TypeName);
        try
        {
            File.WriteAllText(harness.ModesPath, File.ReadAllText(_h.ModesPath));

            await harness.Run("seq");

            var received = Assert.Single(harness.Client.Received);
            Assert.Contains("## 웹검색 규칙", ScriptedChatClient.SystemText(received));
            Assert.Contains(received.Options!.Tools!, t => t is HostedWebSearchTool);
            Assert.NotNull(harness.Records().Single().WebSearch);
        }
        finally { harness.Dispose(); }
    }
}
