using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace HB.Orchestration.Tests;

/// <summary>에이전트별 추론 강도 (계획서 2026-10-20_추론강도-설정).</summary>
public sealed class ReasoningEffortTests
{
    // ── 설정 값 해석 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("Low", ReasoningEffort.Low)]
    [InlineData(" medium ", ReasoningEffort.Medium)]
    [InlineData("HIGH", ReasoningEffort.High)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    public void Effort_names_parse_case_insensitively_and_round_trip(string value, ReasoningEffort expected)
    {
        var (effort, error) = AgentSettings.ParseEffort(value);
        Assert.Null(error);
        Assert.Equal(expected, effort);
        Assert.Equal(value.Trim().ToLowerInvariant(), AgentSettings.EffortName(effort!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_effort_means_not_sent(string? value) =>
        Assert.Equal((null, null), AgentSettings.ParseEffort(value));

    [Theory]
    [InlineData("max")]
    [InlineData("minimal")]
    [InlineData("8000")]
    public void Unsupported_effort_is_an_error(string value)
    {
        var (effort, error) = AgentSettings.ParseEffort(value);
        Assert.Null(effort);
        Assert.Contains($"'{value}'", error);
    }

    [Fact]
    public void Catalog_skips_agent_with_unknown_effort_and_keeps_valid_one()
    {
        var options = new OrchestrationOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase) { ["fake"] = new ProviderOptions { Type = "fake" } },
            Agents =
            [
                new AgentOptions { Name = "ok", Provider = "fake", Model = "m", ReasoningEffort = "Medium" },
                new AgentOptions { Name = "bad", Provider = "fake", Model = "m", ReasoningEffort = "max" },
            ],
        };
        var catalog = new AgentCatalog(new OrchestrationSource(() => options, null), [new FakeProvider()], NullLogger<AgentCatalog>.Instance);

        Assert.True(catalog.TryGet("ok", out var ok));
        Assert.Equal(ReasoningEffort.Medium, ok.Reasoning);
        Assert.False(catalog.TryGet("bad", out _));
        Assert.Contains("'max'", catalog.WhyUnavailable("bad"));
    }

    private sealed class FakeProvider : IChatClientProvider
    {
        public string Type => "fake";
        public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent) => new ScriptedChatClient(ScriptedChatClient.Reply("응답"));
    }

    // ── 실제 HTTP 본문: reasoning_effort가 실린다 (일반 · Schema · 없음) ───────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string reply = """
                {"id":"c","object":"chat.completion","created":1,"model":"m",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"{\"findings\":[],\"needsInfo\":[]}"}}],
                 "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static (AgentEntry Agent, CaptureHandler Handler) OpenAIAgent(ReasoningEffort? configured, StructuredOutputMode? output = null)
    {
        var handler = new CaptureHandler();
        var client = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).GetChatClient("m").AsIChatClient();
        var options = new AgentOptions { Name = "a", Provider = "openai", Model = "m", MaxAttempts = 1, StructuredOutput = output };
        return (new AgentEntry(options, client) { Reasoning = configured }, handler);
    }

    private static async Task<(JsonDocument Body, CallRecord Record)> Send(AgentEntry agent, CaptureHandler handler, ReasoningEffort? overrideEffort = null,
        FindingFormat? output = null)
    {
        using var h = new ModeRunnerHarness();
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, h.LogPath);
        var call = new StepCall("r000000001", "m", 1, "R", agent, "역할", "질문", new CallMeta(null, [])) { ReasoningOverride = overrideEffort, Output = output };
        var result = await runner.RunStepAsync(call, CancellationToken.None);
        Assert.True(result.Success, result.Text);
        return (JsonDocument.Parse(handler.Body!), h.Records().Single());
    }

    [Fact]
    public async Task Configured_effort_is_sent_and_recorded()
    {
        var (agent, handler) = OpenAIAgent(ReasoningEffort.High);
        var (body, record) = await Send(agent, handler);

        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("high", record.ReasoningEffort);
    }

    [Fact]
    public async Task Override_wins_and_survives_schema_mode_options()
    {
        var (agent, handler) = OpenAIAgent(ReasoningEffort.Low, StructuredOutputMode.Schema);
        var (body, record) = await Send(agent, handler, ReasoningEffort.ExtraHigh, FindingFormat.Findings);

        Assert.Equal("xhigh", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString()); // 응답 형식이 지워지지 않음
        Assert.Equal("xhigh", record.ReasoningEffort);
    }

    [Fact]
    public async Task No_effort_sends_no_field()
    {
        var (agent, handler) = OpenAIAgent(null);
        var (body, record) = await Send(agent, handler);

        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.Null(record.ReasoningEffort);
    }

    // ── 실행마다 덮어쓰기 (run_mode reasoning) ─────────────────────────────

    /// <summary>하네스는 만들 때 설정을 읽는다 → 수정 시각을 바꿔 다시 읽게 한다 (추론 강도는 다시 읽을 때 해석됨).</summary>
    private static void Reload(ModeRunnerHarness h) => File.SetLastWriteTimeUtc(h.SettingsPath, DateTime.UtcNow.AddMinutes(1));

    [Fact]
    public async Task Run_override_applies_to_named_agent_and_is_noted()
    {
        using var h = new ModeRunnerHarness();
        h.Options.Agents[0].ReasoningEffort = "medium";
        Reload(h);

        var result = await h.Runner.RunAsync("seq", "요청", null, null, null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None,
            reasoningOverrides: ["a1=low"]);

        Assert.Equal(ReasoningEffort.Low, h.Client.Received.Single().Options?.Reasoning?.Effort);
        Assert.Equal("low", h.Records().Single().ReasoningEffort);
        Assert.Contains("(추론 강도: a1=low)", result);
    }

    [Fact]
    public async Task Configured_effort_is_used_without_override()
    {
        using var h = new ModeRunnerHarness();
        h.Options.Agents[0].ReasoningEffort = "medium";
        Reload(h);

        await h.Run("seq");

        Assert.Equal(ReasoningEffort.Medium, h.Client.Received.Single().Options?.Reasoning?.Effort);
        Assert.Equal("medium", h.Records().Single().ReasoningEffort);
    }

    [Theory]
    [InlineData("a1", "형식 오류")]
    [InlineData("zz=low", "'zz'을(를) 쓰지 않습니다")]
    [InlineData("a1=max", "'max'")]
    public async Task Bad_override_is_rejected_before_calling(string raw, string expected)
    {
        using var h = new ModeRunnerHarness();

        var result = await h.Runner.RunAsync("seq", "요청", null, null, null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None,
            reasoningOverrides: [raw]);

        Assert.StartsWith("[모드 'seq']", result);
        Assert.Contains(expected, result);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public void List_modes_shows_configured_effort()
    {
        using var h = new ModeRunnerHarness();
        h.Options.Agents[0].ReasoningEffort = "high";
        Reload(h);

        Assert.Contains("a1(m, 추론 high)", h.Runner.Describe());
    }

    [Fact]
    public async Task Duplicate_override_key_is_rejected()
    {
        using var h = new ModeRunnerHarness();

        var result = await h.Runner.RunAsync("seq", "요청", null, null, null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None,
            reasoningOverrides: ["a1=low", "A1=high"]);

        Assert.Contains("두 번 왔습니다", result);
        Assert.Equal(0, h.Client.Calls);
    }

    // ── 리뷰 반영 (/cross-review a5acd75c8a): none · Responses+웹검색 본문, followUp 조건 ──

    [Fact]
    public async Task None_effort_is_sent_as_none()
    {
        var (agent, handler) = OpenAIAgent(ReasoningEffort.None);
        var (body, record) = await Send(agent, handler);

        Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("none", record.ReasoningEffort);
    }

    private const string ResponsesReply = """
        {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"m","output":[
         {"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"끝","annotations":[]}]}],
         "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":0},"output_tokens":5,"output_tokens_details":{"reasoning_tokens":1},"total_tokens":15},
         "parallel_tool_calls":true,"tool_choice":"auto","tools":[],"text":{"format":{"type":"text"}}}
        """;

    private sealed class ResponsesCapture : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponsesReply, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Responses_web_search_step_sends_reasoning_effort_and_keeps_search_tool()
    {
        var handler = new ResponsesCapture();
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
            Reasoning = ReasoningEffort.Low,
        };

        using var h = new ModeRunnerHarness();
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, h.LogPath);
        var call = new StepCall("r000000001", "crash", 1, "CrashSearch", agent, "역할", "찾아", new CallMeta(null, [])) { WebSearch = new WebSearchSettings() };
        var result = await runner.RunStepAsync(call, CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Contains(body.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("type").GetString()!.StartsWith("web_search"));
        Assert.Equal("low", h.Records().Single().ReasoningEffort);
    }

    private const string FollowUpModes = """
        {
          "Roles": { "Rev": { "Prompt": "역할", "Output": "findings" } },
          "Modes": { "r": { "Steps": [ { "Role": "Rev", "Agent": "a1" } ] } }
        }
        """;

    private const string AnswerWithRequest = """
        {"findings":[],"needsInfo":[{"what":"수집부","path":"src/Reader.cs","symbol":null,"why":"확인"}]}
        """;

    private static async Task<(ModeRunnerHarness H, string RunId)> FirstRunForFollowUp(string[]? reasoning)
    {
        var h = new ModeRunnerHarness(FollowUpModes);
        h.Client.Script = [ScriptedChatClient.Reply(AnswerWithRequest), ScriptedChatClient.Reply("""{"findings":[],"needsInfo":[]}""")];
        var result = await h.Runner.RunAsync("r", "요청", null, null, null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None,
            reasoningOverrides: reasoning);
        return (h, result.Split('\n')[0]["runId: ".Length..].Trim());
    }

    [Fact]
    public async Task FollowUp_omits_effort_when_original_sent_none_even_if_now_configured()
    {
        var (h, runId) = await FirstRunForFollowUp(null);
        using var _ = h;
        h.Options.Agents[0].ReasoningEffort = "medium"; // 첫 실행 뒤 설정에 강도를 넣음
        Reload(h);

        await h.Runner.RunFollowUpAsync("r", new FollowUpRequest(runId, 1, ["1-N1"]), "메모", null, null, "보충", CancellationToken.None);

        Assert.Null(h.Client.Received[1].Options?.Reasoning);
        Assert.Null(h.Records().Last().ReasoningEffort);
    }

    [Fact]
    public async Task FollowUp_reuses_original_effort_over_current_setting()
    {
        var (h, runId) = await FirstRunForFollowUp(["a1=high"]);
        using var _ = h;
        h.Options.Agents[0].ReasoningEffort = "low";
        Reload(h);

        await h.Runner.RunFollowUpAsync("r", new FollowUpRequest(runId, 1, ["1-N1"]), "메모", null, null, "보충", CancellationToken.None);

        Assert.Equal(ReasoningEffort.High, h.Client.Received[1].Options?.Reasoning?.Effort);
        Assert.Equal("high", h.Records().Last().ReasoningEffort);
    }

    // ── 골든 CLI ──────────────────────────────────────────────────────────

    [Fact]
    public void Golden_parses_reasoning_and_score_rejects_it()
    {
        var (run, runError) = GoldenCli.Parse(["--golden", "run", "c1", "--reasoning", "a=low, b=high"]);
        Assert.Null(runError);
        Assert.Equal(["a=low", "b=high"], run!.Reasoning);

        var (_, scoreError) = GoldenCli.Parse(["--golden", "score", "c1", "--from", "x.json", "--reasoning", "a=low"]);
        Assert.Contains("--reasoning", scoreError);
    }
}
