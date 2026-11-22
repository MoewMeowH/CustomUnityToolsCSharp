using System.Net;
using System.Text;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Tests;

/// <summary>agy 수동 중계 공급자: HB_Chat 루프백 API에 요청 → 결과 기다리기 (환경 없음 · 거절 · waiting→done · 제한 시간 · 취소).</summary>
public sealed class AgyRelayTests
{
    private sealed class FakeChat(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body, string? Token)> Seen = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Seen) Seen.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body,
                request.Headers.TryGetValues(AgyRelayChatClient.TokenHeader, out var t) ? t.Single() : null));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Reply(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static AgyRelayChatClient Client(FakeChat http, string? url = "http://127.0.0.1:5290", string? token = "tok", TimeSpan? timeout = null) =>
        new(new AgyRelaySettings("antigravity", timeout ?? TimeSpan.FromSeconds(10)), () => (url, token), http, TimeSpan.FromMilliseconds(10));

    private static readonly ChatMessage[] Ask = [new(ChatRole.User, "첨부\n\n코드를 봐줘")];
    private static readonly ChatOptions Options = new() { Instructions = "너는 리뷰어다." };

    [Fact]
    public async Task NoEnvironment_FailsAtOnce_NotRetryable()
    {
        var http = new FakeChat((_, _) => Reply(new { }));
        var ex = await Assert.ThrowsAsync<ClaudeCliException>(() => Client(http, url: null).GetResponseAsync(Ask, Options));
        Assert.False(ex.Retryable);
        Assert.Contains("agy 중계를 켰을 때만", ex.Message);
        Assert.Empty(http.Seen);
        // 루프백이 아닌 주소는 받지 않는다 (프롬프트 · 코드를 밖으로 보내지 않게)
        await Assert.ThrowsAsync<ClaudeCliException>(() => Client(http, url: "http://example.com").GetResponseAsync(Ask, Options));
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task WaitingThenDone_ReturnsText_SendsPromptAndToken()
    {
        var polls = 0;
        var http = new FakeChat((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/request")
            ? Reply(new { status = "waiting", id = "r1" })
            : ++polls < 3 ? Reply(new { status = "waiting" }) : Reply(new { status = "done", text = "{\"findings\":[]}" }));
        var response = await Client(http).GetResponseAsync(Ask, Options);
        Assert.Equal("{\"findings\":[]}", response.Text);
        Assert.Null(response.Usage);
        var first = http.Seen[0];
        Assert.Equal(("POST", AgyRelayChatClient.BasePath + "/request", "tok"), (first.Method, first.Path, first.Token));
        var body = JsonDocument.Parse(first.Body).RootElement;
        Assert.Equal(10, body.GetProperty("timeoutSeconds").GetInt32()); // HB_Chat 대기 띠 · 낡은 자리 판정
        var prompt = body.GetProperty("prompt").GetString();
        Assert.Contains("너는 리뷰어다.", prompt);
        Assert.Contains("코드를 봐줘", prompt);
        Assert.All(http.Seen.Skip(1), s => Assert.Equal(AgyRelayChatClient.BasePath + "/result?id=r1", s.Path));
    }

    [Fact]
    public async Task Refused_FailsWithReason()
    {
        var http = new FakeChat((_, _) => Reply(new { status = "refused", error = "agy 1.5.15는 아직 카나리 확인을 하지 않았습니다" }));
        var ex = await Assert.ThrowsAsync<ClaudeCliException>(() => Client(http).GetResponseAsync(Ask, Options));
        Assert.Contains("카나리", ex.Message);
        Assert.Single(http.Seen);
    }

    [Fact]
    public async Task Timeout_CancelsRelay_TimeoutKind()
    {
        var http = new FakeChat((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/request") ? Reply(new { status = "waiting", id = "r1" }) : Reply(new { status = "waiting" }));
        var ex = await Assert.ThrowsAsync<ClaudeCliException>(() => Client(http, timeout: TimeSpan.FromMilliseconds(100)).GetResponseAsync(Ask, Options));
        Assert.Equal(ErrorKind.Timeout, ex.Kind);
        Assert.Contains(http.Seen, s => s.Path == AgyRelayChatClient.BasePath + "/cancel" && s.Body.Contains("r1"));
    }

    [Fact]
    public async Task UserCancel_CancelsRelay_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeChat((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/request")) return Reply(new { status = "waiting", id = "r1" });
            cts.Cancel();
            return Reply(new { status = "waiting" });
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GetResponseAsync(Ask, Options, cts.Token));
        Assert.Contains(http.Seen, s => s.Path == AgyRelayChatClient.BasePath + "/cancel");
    }

    [Fact]
    public async Task ChatUnreachable_ProviderFailure()
    {
        var http = new FakeChat((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/request")
            ? Reply(new { status = "waiting", id = "r1" })
            : throw new HttpRequestException("연결 거부"));
        var ex = await Assert.ThrowsAsync<ClaudeCliException>(() => Client(http).GetResponseAsync(Ask, Options));
        Assert.Contains("HB_Chat에 닿지 못했습니다", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Images_Refused()
    {
        var http = new FakeChat((_, _) => Reply(new { }));
        ChatMessage[] withImage = [new(ChatRole.User, [new TextContent("봐줘"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])];
        await Assert.ThrowsAsync<ClaudeCliException>(() => Client(http).GetResponseAsync(withImage, Options));
        Assert.Empty(http.Seen);
    }
}
