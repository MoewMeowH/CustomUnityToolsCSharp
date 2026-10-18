using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;
using OpenAI;

namespace HB.Orchestration.Tests;

/// <summary>
/// 프롬프트 캐시: 첨부 자료가 프롬프트 맨 앞에 오고, Context면 첨부/나머지를 text 블록 둘로 나눠 보내며,
/// HTTP 정책이 explicit 모드와 첨부 블록 저장 지점을 요청 본문에 넣는다.
/// </summary>
public sealed class PromptCacheTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ── 본문 변환 ───────────────────────────────────────────────────────

    private static JsonObject Parse(BinaryData data) => JsonNode.Parse(data.ToString())!.AsObject();

    private const string TwoBlockBody = """
        {"model":"m","messages":[
          {"role":"system","content":"sys"},
          {"role":"user","content":[{"type":"text","text":"자료"},{"type":"text","text":"질문"}]}]}
        """;

    [Fact]
    public void Rewrite_Off_AddsExplicitOptionsOnly()
    {
        var body = Parse(PromptCachePolicy.Rewrite(BinaryData.FromString(TwoBlockBody), PromptCacheMode.Off)!);

        Assert.Equal("explicit", body["prompt_cache_options"]!["mode"]!.GetValue<string>());
        Assert.Null(body["messages"]![1]!["content"]![0]!["prompt_cache_breakpoint"]);
    }

    [Fact]
    public void Rewrite_Context_AddsBreakpointToFirstBlockOfLastUserMessage()
    {
        var body = Parse(PromptCachePolicy.Rewrite(BinaryData.FromString(TwoBlockBody), PromptCacheMode.Context)!);

        Assert.Equal("explicit", body["prompt_cache_options"]!["mode"]!.GetValue<string>());
        var content = body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("explicit", content[0]!["prompt_cache_breakpoint"]!["mode"]!.GetValue<string>());
        Assert.Null(content[1]!["prompt_cache_breakpoint"]);
    }

    [Fact]
    public void Rewrite_Context_SingleStringContent_OnlyOptions()
    {
        var body = Parse(PromptCachePolicy.Rewrite(BinaryData.FromString("""{"messages":[{"role":"user","content":"질문"}]}"""), PromptCacheMode.Context)!);

        Assert.NotNull(body["prompt_cache_options"]);
        Assert.Equal("질문", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Rewrite_KeepsExistingOptions_NothingToChangeIsNull() =>
        Assert.Null(PromptCachePolicy.Rewrite(BinaryData.FromString("""{"prompt_cache_options":{"mode":"implicit"},"messages":[]}"""), PromptCacheMode.Off));

    [Theory]
    [InlineData("{깨진", PromptCacheMode.Off)]
    [InlineData("[1,2]", PromptCacheMode.Off)]
    [InlineData("""{"messages":[]}""", PromptCacheMode.ProviderDefault)]
    public void Rewrite_UnexpectedBodyOrProviderDefault_IsNull(string json, PromptCacheMode mode) =>
        Assert.Null(PromptCachePolicy.Rewrite(BinaryData.FromString(json), mode));

    // ── 실제 HTTP 전송 본문 (OpenAI SDK + MEAI 경로) ─────────────────────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string reply = """
                {"id":"c","object":"chat.completion","created":1,"model":"m",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],
                 "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private static (IChatClient Client, CaptureHandler Handler) OpenAIClientWithPolicy()
    {
        var handler = new CaptureHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        options.AddPolicy(new PromptCachePolicy(), PipelinePosition.PerCall);
        return (new OpenAIClient(new ApiKeyCredential("k"), options).GetChatClient("m").AsIChatClient(), handler);
    }

    [Fact]
    public async Task Http_ContextScope_SendsTwoBlocksWithBreakpoint()
    {
        var (client, handler) = OpenAIClientWithPolicy();

        using (PromptCacheScope.Enter(PromptCacheMode.Context))
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, [new TextContent("자료"), new TextContent("질문")])]);

        var body = Parse(BinaryData.FromString(handler.Body!));
        Assert.Equal("explicit", body["prompt_cache_options"]!["mode"]!.GetValue<string>());
        var content = body["messages"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("자료", content[0]!["text"]!.GetValue<string>());
        Assert.NotNull(content[0]!["prompt_cache_breakpoint"]);
    }

    [Fact]
    public async Task Http_NoScope_LeavesBodyUntouched()
    {
        var (client, handler) = OpenAIClientWithPolicy();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "질문")]);

        Assert.DoesNotContain("prompt_cache", handler.Body);
    }

    // ── ModeRunner 프롬프트 순서와 블록 분리 ─────────────────────────────────

    private string WriteAttachment(int lines)
    {
        var path = Path.Combine(_h.Root, "big.cs");
        File.WriteAllLines(path, Enumerable.Range(1, lines).Select(i => $"// 첨부 자료 줄 {i} — 프롬프트 캐시 테스트용 고정 내용"));
        _h.Options.Context.ExtraRoots["tc"] = _h.Root;
        return "@tc/big.cs";
    }

    private Task<string> RunWithContext(string path, int lines) =>
        _h.Runner.RunAsync("seq", "질문", null, null, [new ContextRange(path, 1, lines)], ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

    [Fact]
    public async Task Prompt_PutsAttachmentBeforeRequest()
    {
        await RunWithContext(WriteAttachment(3), 3);

        var prompt = Assert.Single(_h.Records()).Prompt;
        Assert.StartsWith("## 첨부 코드", prompt);
        Assert.True(prompt.IndexOf("## 첨부 코드", StringComparison.Ordinal) < prompt.IndexOf("## 요청", StringComparison.Ordinal));
        Assert.EndsWith("## 요청\n질문".ReplaceLineEndings(), prompt);
    }

    [Fact]
    public async Task ContextProvider_LongAttachment_SplitsBlocks()
    {
        _h.Options.Providers["fake"].PromptCache = PromptCacheMode.Context;

        await RunWithContext(WriteAttachment(200), 200);

        var user = _h.Client.Received.Single().Messages.Single(m => m.Role == ChatRole.User);
        var texts = user.Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, texts.Count);
        Assert.StartsWith("## 첨부 코드", texts[0].Text);
        Assert.StartsWith("## 요청", texts[1].Text);
        Assert.Equal(_h.Records().Single().Prompt, texts[0].Text + texts[1].Text);
    }

    [Fact]
    public async Task Runner_SetsScopeDuringCallAndRestoresAfter()
    {
        PromptCacheMode? seen = null;
        var client = new ScriptedChatClient(ct => { seen = PromptCacheScope.Current; return ScriptedChatClient.Reply("ok")(ct); });
        var agent = new AgentEntry(new AgentOptions { Name = "a", Provider = "p", Model = "m", MaxAttempts = 1 }, client)
        {
            ProviderOptions = new ProviderOptions { PromptCache = PromptCacheMode.Context },
        };
        var prefix = new string('x', StepCall.MinContextCacheChars);
        var call = new StepCall("r000000001", "seq", 1, "R", agent, "i", prefix + "## 요청\n질문", new CallMeta(null, [])) { ContextPrefix = prefix };
        var runner = new AgentRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, _h.LogPath);

        await runner.RunStepAsync(call, CancellationToken.None);

        Assert.Equal(PromptCacheMode.Context, seen);
        Assert.Null(PromptCacheScope.Current);
    }

    [Fact]
    public async Task ContextProvider_ShortAttachment_SendsSingleBlock()
    {
        _h.Options.Providers["fake"].PromptCache = PromptCacheMode.Context;

        await RunWithContext(WriteAttachment(3), 3);

        var user = _h.Client.Received.Single().Messages.Single(m => m.Role == ChatRole.User);
        Assert.Single(user.Contents.OfType<TextContent>());
    }

    [Fact]
    public void CacheMode_ContextWithShortOrMissingPrefix_IsOff()
    {
        var agent = new AgentEntry(new AgentOptions { Name = "a", Provider = "p", Model = "m" }, _h.Client)
        {
            ProviderOptions = new ProviderOptions { PromptCache = PromptCacheMode.Context },
        };
        StepCall Call(string? prefix) => new("r", "m", 1, "R", agent, "i", (prefix ?? "") + "## 요청", new CallMeta(null, [])) { ContextPrefix = prefix };

        Assert.Equal(PromptCacheMode.Off, Call(null).CacheMode);
        Assert.Equal(PromptCacheMode.Off, Call("짧음").CacheMode);
        Assert.Equal(PromptCacheMode.Context, Call(new string('x', StepCall.MinContextCacheChars)).CacheMode);

        // Prompt가 ContextPrefix로 시작하지 않으면 나누지 않는다
        var mismatched = Call(new string('x', StepCall.MinContextCacheChars)) with { Prompt = "다른 프롬프트" };
        Assert.Equal(PromptCacheMode.Off, mismatched.CacheMode);
    }
}
