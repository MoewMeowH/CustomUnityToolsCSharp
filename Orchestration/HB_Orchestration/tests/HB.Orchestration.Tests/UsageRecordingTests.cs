using System.ClientModel.Primitives;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ChatCompletion = OpenAI.Chat.ChatCompletion;

namespace HB.Orchestration.Tests;

/// <summary>usage 원문 기록: SDK가 모르는 캐시 쓰기 필드도 원문에 남고, 추론 토큰·캐시 쓰기 토큰이 기록된다. 원문이 없으면 null.</summary>
public sealed class UsageRecordingTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"hb-orch-test-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_log)) File.Delete(_log);
    }

    private const string CompletionJson = """
        {
          "id": "c1", "object": "chat.completion", "created": 1, "model": "m",
          "choices": [ { "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "ok" } } ],
          "usage": {
            "prompt_tokens": 2000, "completion_tokens": 300, "total_tokens": 2300,
            "prompt_tokens_details": { "cached_tokens": 0, "cache_write_tokens": 1920 },
            "completion_tokens_details": { "reasoning_tokens": 250 }
          }
        }
        """;

    private static ChatCompletion Completion(string json) => ModelReaderWriter.Read<ChatCompletion>(BinaryData.FromString(json))!;

    [Fact]
    public void RawUsageJson_KeepsUnknownCacheWriteField()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { RawRepresentation = Completion(CompletionJson) };

        var raw = UsageExtractor.RawUsageJson(response);

        Assert.NotNull(raw);
        Assert.Contains("cache_write_tokens", raw);
        Assert.Equal(1920, UsageExtractor.CacheWriteTokens(raw));
        Assert.Equal(0, UsageExtractor.CachedTokens(raw));
    }

    [Fact]
    public void RawUsageJson_UnknownOrMissingRaw_IsNull()
    {
        Assert.Null(UsageExtractor.RawUsageJson(null));
        Assert.Null(UsageExtractor.RawUsageJson("문자열"));
        Assert.Null(UsageExtractor.RawUsageJson(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{깨진")]
    [InlineData("""{"prompt_tokens": 10}""")]
    [InlineData("""{"prompt_tokens_details": {"cache_write_tokens": "12"}}""")]
    public void CacheWriteTokens_MissingOrBroken_IsNull(string? json) => Assert.Null(UsageExtractor.CacheWriteTokens(json));

    [Fact]
    public void CachedTokens_ReadsGeminiStyleKey() =>
        Assert.Equal(4096, UsageExtractor.CachedTokens("""{"prompt_tokens": 5000, "cached_content_token_count": 4096}"""));

    [Fact]
    public async Task Runner_RecordsReasoningCacheWriteAndRaw()
    {
        var client = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 2000, OutputTokenCount = 300, CachedInputTokenCount = 0, ReasoningTokenCount = 250 },
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = Completion(CompletionJson),
        });

        var result = await Runner().RunStepAsync(Call(client), CancellationToken.None);

        Assert.True(result.Success);
        var record = Assert.Single(CallLog.ReadAll(_log));
        Assert.Equal(250, record.ReasoningTokens);
        Assert.Equal(1920, record.CacheWriteTokens);
        Assert.Equal(0, record.CachedInputTokens);
        Assert.Contains("cache_write_tokens", record.UsageRaw);
    }

    [Fact]
    public async Task Runner_NoRaw_LeavesNewFieldsNullAndOmitsThem()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Reply("ok"));

        await Runner().RunStepAsync(Call(client), CancellationToken.None);

        var record = Assert.Single(CallLog.ReadAll(_log));
        Assert.Null(record.UsageRaw);
        Assert.Null(record.CacheWriteTokens);
        Assert.Null(record.ReasoningTokens);
        var line = File.ReadAllText(_log);
        Assert.DoesNotContain("usageRaw", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cacheWriteTokens", line, StringComparison.OrdinalIgnoreCase);
    }

    // ── Gemini 호환: 추론 토큰이 total에만 있음 (2026-10-21 실측 모양) ────────────────

    private const string GeminiHiddenReasoningJson = """
        {
          "id": "c2", "object": "chat.completion", "created": 1, "model": "gemini-4-argon",
          "choices": [ { "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "ok" } } ],
          "usage": { "prompt_tokens": 12026, "completion_tokens": 1230, "total_tokens": 30404 }
        }
        """;

    [Theory]
    [InlineData("""{"prompt_tokens": 12026, "completion_tokens": 1230, "total_tokens": 30404}""", 17148L)]                                       // 추론 칸 없음 + 차이 있음
    [InlineData("""{"prompt_tokens": 12026, "completion_tokens": 1240, "total_tokens": 13266}""", null)]                                        // 추론 칸 없음 + 차이 없음
    [InlineData("""{"prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 400, "completion_tokens_details": {"reasoning_tokens": 30}}""", null)] // 추론 칸 있음 + 차이 있음
    [InlineData("""{"prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 150, "completion_tokens_details": {"reasoning_tokens": 30}}""", null)] // 추론 칸 있음 + 차이 없음
    [InlineData("""{"prompt_tokens": 100, "completion_tokens": 50, "total_tokens": 120}""", null)]                                              // total이 더 작음
    [InlineData("""{"prompt_tokens": 100, "completion_tokens": 50}""", null)]                                                                   // total 없음
    [InlineData("{깨진", null)]
    [InlineData(null, null)]
    public void HiddenReasoningTokens_OnlyWhenNoReasoningFieldAndTotalExceeds(string? json, long? expected) =>
        Assert.Equal(expected, UsageExtractor.HiddenReasoningTokens(json));

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", true)]
    [InlineData("https://api.fireworks.ai/inference/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void IsGeminiEndpoint(string? endpoint, bool expected) => Assert.Equal(expected, UsageExtractor.IsGeminiEndpoint(endpoint));

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", 17148L, 1230L + 17148L)]  // Gemini: 차이를 추론으로, 출력에도 더함
    [InlineData("https://api.fireworks.ai/inference/v1", null, 1230L)]                              // 다른 제공사: 보정하지 않음
    public async Task Runner_CorrectsHiddenReasoning_OnlyForGemini(string endpoint, long? reasoning, long output)
    {
        var client = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 12026, OutputTokenCount = 1230 },
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = Completion(GeminiHiddenReasoningJson),
        });
        var call = Call(client) with
        {
            Agent = new AgentEntry(new AgentOptions { Name = "argon", Provider = "p", Model = "gemini-4-argon" }, client)
            {
                ProviderOptions = new ProviderOptions { Endpoint = endpoint },
            },
        };

        await Runner().RunStepAsync(call, CancellationToken.None);

        var record = Assert.Single(CallLog.ReadAll(_log));
        Assert.Equal(reasoning, record.ReasoningTokens);
        Assert.Equal(output, record.OutputTokens);
        Assert.Equal(12026, record.InputTokens);
    }

    [Fact]
    public async Task Runner_GeminiWithReportedReasoning_IsNotChanged()
    {
        var client = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 2000, OutputTokenCount = 300, ReasoningTokenCount = 250 },
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = Completion(CompletionJson),
        });
        var call = Call(client) with
        {
            Agent = new AgentEntry(new AgentOptions { Name = "argon", Provider = "p", Model = "m" }, client)
            {
                ProviderOptions = new ProviderOptions { Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/" },
            },
        };

        await Runner().RunStepAsync(call, CancellationToken.None);

        var record = Assert.Single(CallLog.ReadAll(_log));
        Assert.Equal((250L, 300L), (record.ReasoningTokens, record.OutputTokens));
    }

    private AgentRunner Runner() => new(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, _log);

    private static StepCall Call(ScriptedChatClient client) => new(
        RunId: "run0000001", Mode: "review", Step: 1, Role: "Reviewer",
        Agent: new AgentEntry(new AgentOptions { Name = "fake", Provider = "openai", Model = "m" }, client),
        Instructions: "x", Prompt: "p", Meta: new CallMeta(null, []));
}
