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

/// <summary>ProviderOptions.Stream: 스트리밍으로 받아 한 응답으로 합친다.</summary>
public sealed class StreamingChatClientTests
{
    private const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    private enum UsageShape
    {
        /// <summary>Gemini (2026-11-15 실측): 모든 조각에 누적 usage, 마지막 조각에서 prompt_tokens가 한 번 커진다.</summary>
        EveryChunk,
        /// <summary>OpenAI: choices가 빈 마지막 조각에만 usage.</summary>
        LastOnly,
        None,
    }

    /// <summary>조각: 글 "가" · "나" · 끝(종료 이유). EveryChunk의 usage는 (prompt 40, completion 1, total 141) → (40, 2, 342) → (60, 2, 562).</summary>
    private static string Sse(string finish = "stop", bool done = true, UsageShape usage = UsageShape.EveryChunk)
    {
        var sb = new StringBuilder();
        void Chunk(string choices, string? usageJson) => sb.Append(
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\"," +
            $"\"choices\":{choices}{(usageJson is null ? "" : $",\"usage\":{usageJson}")}}}\n\n");
        string Usage(int prompt, int completion, int total) =>
            $"{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{total}}}";
        string? Each(int prompt, int completion, int total) => usage == UsageShape.EveryChunk ? Usage(prompt, completion, total) : null;

        Chunk("[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"가\"},\"finish_reason\":null}]", Each(40, 1, 141));
        Chunk("[{\"index\":0,\"delta\":{\"content\":\"나\"},\"finish_reason\":null}]", Each(40, 2, 342));
        if (finish.Length > 0) Chunk($"[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{finish}\"}}]", Each(60, 2, 562));
        if (usage == UsageShape.LastOnly) Chunk("[]", Usage(60, 2, 62));
        if (done) sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private sealed class SseHandler(string? body) : HttpMessageHandler
    {
        public string? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body is null) await Task.Delay(Timeout.Infinite, cancellationToken); // 응답하지 않는 서버
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body!, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static (IChatClient Client, SseHandler Handler) Streaming(string? body, TimeSpan? timeout = null)
    {
        var handler = new SseHandler(body);
        var inner = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).GetChatClient("m").AsIChatClient();
        return (new StreamingChatClient(inner, timeout ?? TimeSpan.FromSeconds(30)), handler);
    }

    private static AgentEntry GeminiAgent(IChatClient client) =>
        new(new AgentOptions { Name = "carbon", Provider = "gemini", Model = "m", MaxAttempts = 1 }, client)
        {
            ProviderOptions = new ProviderOptions { Endpoint = GeminiEndpoint, Stream = true },
        };

    private static async Task<(StepResult Result, CallRecord Record)> Run(IChatClient client)
    {
        using var h = new ModeRunnerHarness();
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, h.LogPath);
        var result = await runner.RunStepAsync(new StepCall("r000000001", "m", 1, "R", GeminiAgent(client), "역할", "질문", new CallMeta(null, [])), CancellationToken.None);
        return (result, h.Records().Single());
    }

    [Fact]
    public async Task Merges_text_and_takes_usage_from_last_chunk()
    {
        var (client, _) = Streaming(Sse());

        var response = await client.GetResponseAsync("질문");

        Assert.Equal("가나", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        // 조각의 누적 값을 더하지 않는다 (더하면 입력 140 · 출력 5 · 합계 1,045)
        Assert.Equal((60L, 2L, 562L), (response.Usage!.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount));
        using var usage = JsonDocument.Parse(UsageExtractor.RawUsageJson(response)!);
        Assert.Equal(562, usage.RootElement.GetProperty("total_tokens").GetInt32());
        Assert.Equal(500, UsageExtractor.HiddenReasoningTokens(UsageExtractor.RawUsageJson(response)));
    }

    [Fact]
    public async Task Usage_only_in_the_last_empty_chunk_is_used()
    {
        var (client, _) = Streaming(Sse(usage: UsageShape.LastOnly));

        var response = await client.GetResponseAsync("질문");

        Assert.Equal("가나", response.Text);
        Assert.Equal((60L, 2L, 62L), (response.Usage!.InputTokenCount, response.Usage.OutputTokenCount, response.Usage.TotalTokenCount));
        Assert.NotNull(UsageExtractor.RawUsageJson(response));
    }

    [Fact]
    public async Task Stream_without_usage_has_no_usage()
    {
        var (client, _) = Streaming(Sse(usage: UsageShape.None));

        var response = await client.GetResponseAsync("질문");

        Assert.Equal("가나", response.Text);
        Assert.Null(response.Usage);
        Assert.Null(UsageExtractor.RawUsageJson(response));
    }

    [Fact]
    public async Task Length_finish_reason_is_kept()
    {
        var (client, _) = Streaming(Sse(finish: "length"));

        Assert.Equal(ChatFinishReason.Length, (await client.GetResponseAsync("질문")).FinishReason);
    }

    [Fact]
    public async Task Stream_that_ends_without_finish_reason_is_a_network_failure_carrying_usage()
    {
        var (client, _) = Streaming(Sse(finish: "", done: false));

        var ex = await Assert.ThrowsAsync<IOException>(() => client.GetResponseAsync("질문"));

        var failure = RetryClassifier.Classify(ex, CancellationToken.None);
        Assert.Equal((ErrorKind.Network, true), (failure.Kind, failure.Retryable));
        var cut = StreamedUsage.Find(ex)!;
        Assert.Equal((40L, 2L, 342L), (cut.Details.InputTokenCount, cut.Details.OutputTokenCount, cut.Details.TotalTokenCount));
    }

    [Fact]
    public async Task Whole_call_time_limit_is_a_timeout()
    {
        var (client, _) = Streaming(body: null, timeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync("질문"));

        Assert.Equal(ErrorKind.Timeout, RetryClassifier.Classify(ex, CancellationToken.None).Kind);
    }

    [Fact]
    public async Task Request_asks_for_stream_with_usage_and_keeps_options()
    {
        var (client, handler) = Streaming(Sse());

        await client.GetResponseAsync("질문", new ChatOptions
        {
            MaxOutputTokens = 64000,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
            ResponseFormat = ChatResponseFormat.Json,
        });

        using var body = JsonDocument.Parse(handler.Request!);
        var root = body.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(64000, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Provider_wraps_only_when_stream_is_on(bool stream)
    {
        var client = new OpenAICompatibleProvider().Create("gemini",
            new ProviderOptions { Endpoint = GeminiEndpoint, Stream = stream },
            new AgentOptions { Name = "a", Provider = "gemini", Model = "m" });

        Assert.Equal(stream, client is StreamingChatClient);
    }

    [Fact]
    public async Task Runner_records_hidden_reasoning_from_streamed_usage()
    {
        var (result, record) = await Run(Streaming(Sse()).Client);

        Assert.True(result.Success, result.Text);
        Assert.Equal("가나", record.Response);
        // 추론 = total − prompt − completion = 562 − 60 − 2, 출력에도 더한다
        Assert.Equal((60L, 502L, 500L), (record.InputTokens, record.OutputTokens, record.ReasoningTokens));
    }

    [Fact]
    public async Task Runner_records_usage_of_a_cut_stream()
    {
        var (result, record) = await Run(Streaming(Sse(finish: "", done: false)).Client);

        Assert.False(result.Success);
        Assert.Equal("Network", record.ErrorKind);
        // 끊기기 전 마지막 조각: prompt 40 · completion 2 · total 342 → 추론 300, 출력 302
        Assert.Equal((40L, 302L, 300L), (record.InputTokens, record.OutputTokens, record.ReasoningTokens));
        Assert.Contains("342", record.UsageRaw);
    }
}
