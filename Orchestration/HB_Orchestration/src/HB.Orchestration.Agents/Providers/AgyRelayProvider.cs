using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// agy(Antigravity CLI) 수동 중계: API 대신 HB_Chat에 프롬프트를 맡기고, 사용자가 곁칸 셸에서 agy에 직접 입력해 쓴 결과를 받아 온다.
/// 서버는 agy에 아무것도 입력하지 않는다 — Antigravity 약관상 앱이 agy를 감싸 쓰면 안 된다 (사람 입력만).
/// 중계 폴더 · 관문(카나리 확인한 판 · agy 설정 검사) · 한 번에 하나 규칙은 HB_Chat이 맡는다 — 이 공급자는 HB_Chat 루프백 API에 요청하고 결과를 기다리기만 한다.
/// HB_Chat은 agy 중계를 켠 방의 실행에만 주소 · 실행 토큰을 환경 변수로 넘긴다 — 없으면(HB_Chat 밖 · 끈 방) 바로 실패.
/// </summary>
public sealed class AgyRelayProvider : IChatClientProvider
{
    public const string TypeName = "agy-relay";

    /// <summary>HB_Chat이 넘기는 환경 변수: 루프백 주소 · 그 방 실행 토큰.</summary>
    public const string UrlVariable = "HB_CHAT_AGY_URL", TokenVariable = "HB_CHAT_AGY_TOKEN";

    public string Type => TypeName;

    public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent) =>
        new AgyRelayChatClient(new AgyRelaySettings(agent.Model,
            agent.TimeoutSeconds is > 0 || options.TimeoutSeconds is > 0 ? AgentSettings.ResolveTimeout(agent, options) : AgyRelaySettings.DefaultTimeout),
            () => (Environment.GetEnvironmentVariable(UrlVariable), Environment.GetEnvironmentVariable(TokenVariable)));
}

/// <param name="Timeout">사람이 agy로 끝낼 때까지 기다리는 상한 — MCP 도구 호출 900초가 끊기지 않음을 실측한 만큼 (기본 15분).</param>
public sealed record AgyRelaySettings(string Model, TimeSpan Timeout)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
}

/// <param name="endpoint">HB_Chat 주소 · 토큰 (둘 중 하나라도 없으면 쓸 수 없음).</param>
/// <param name="http">시험용 HTTP 처리기 (비우면 기본).</param>
public sealed class AgyRelayChatClient(AgyRelaySettings settings, Func<(string? Url, string? Token)> endpoint, HttpMessageHandler? http = null, TimeSpan? pollEvery = null)
    : IChatClient
{
    /// <summary>HB_Chat 앱 MCP와 같은 토큰 헤더 · 경로 (토큰 검사 · 같은 출처 예외가 그 경로에 걸려 있다).</summary>
    public const string TokenHeader = "X-HB-Token", BasePath = "/mcp/terminal/agy-relay";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Any(m => m.Role == ChatRole.Assistant))
            throw new ClaudeCliException("[agy-relay] 앞 응답을 assistant 메시지로 받지 않습니다.", ErrorKind.Client, retryable: false);
        if (list.SelectMany(m => m.Contents).Any(c => c is DataContent))
            throw new ClaudeCliException("[agy-relay] 그림은 보내지 않습니다 (에이전트 Vision=false).", ErrorKind.Client, retryable: false);
        var (url, token) = endpoint();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new ClaudeCliException("[agy-relay] HB_Chat 방에서 agy 중계를 켰을 때만 씁니다 (설정 → 연결 → agy 중계) — 다른 에이전트를 고르세요.",
                ErrorKind.Client, retryable: false);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || !baseUri.IsLoopback)
            throw new ClaudeCliException("[agy-relay] HB_Chat 주소가 루프백이 아닙니다.", ErrorKind.Client, retryable: false);

        var prompt = Prompt(options, list);
        using var client = http is null ? new HttpClient() : new HttpClient(http, disposeHandler: false);
        client.BaseAddress = baseUri;
        client.DefaultRequestHeaders.Add(TokenHeader, token);

        using var request = await Send(() => client.PostAsJsonAsync(BasePath + "/request",
            new { prompt, model = settings.Model, timeoutSeconds = (int)settings.Timeout.TotalSeconds }, Json, cancellationToken));
        var started = await Read(request, cancellationToken);
        if (started.Status != "waiting" || started.Id is not { Length: > 0 } id)
            throw new ClaudeCliException("[agy-relay] HB_Chat이 받지 않았습니다: " + (started.Error ?? started.Status), ErrorKind.Client, retryable: false);

        using var timeout = new CancellationTokenSource(settings.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                await Task.Delay(pollEvery ?? AgyRelaySettings.PollEvery, linked.Token);
                using var poll = await Send(() => client.GetAsync($"{BasePath}/result?id={Uri.EscapeDataString(id)}", linked.Token));
                var result = await Read(poll, linked.Token);
                switch (result.Status)
                {
                    case "done":
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Text ?? "")) { ModelId = settings.Model };
                    case "waiting":
                        continue;
                    default:
                        throw new ClaudeCliException("[agy-relay] " + (result.Error ?? "중계가 끝났습니다: " + result.Status), ErrorKind.Client, retryable: false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            await Cancel(client, id);
            throw new ClaudeCliException($"[agy-relay] {settings.Timeout.TotalMinutes:0}분 안에 agy 결과(output.md)가 오지 않았습니다.", ErrorKind.Timeout, retryable: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Cancel(client, id); // 사용자가 턴을 멈춤 — 중계 자리를 풀고 취소를 그대로 올린다
            throw;
        }
    }

    /// <summary>agy에 넘길 글: 시스템 지시 + 사용자 글 (첨부 코드 · input). HB_Chat이 request.md에 그대로 쓰고 꼬리를 붙인다.</summary>
    internal static string Prompt(ChatOptions? options, IReadOnlyList<ChatMessage> list)
    {
        var system = string.Join("\n\n", new[] { options?.Instructions }
            .Concat(list.Where(m => m.Role == ChatRole.System).Select(m => m.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t)));
        var user = string.Join("\n\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        return new StringBuilder().Append("# 지시\n\n").Append(system).Append("\n\n# 요청\n\n").Append(user).ToString();
    }

    /// <summary>HB_Chat이 꺼졌거나 닿지 않으면 공급자 실패로 (자리는 HB_Chat이 턴 끝 · 제한 시간으로 푼다).</summary>
    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> send)
    {
        try { return await send(); }
        catch (HttpRequestException ex)
        {
            throw new ClaudeCliException("[agy-relay] HB_Chat에 닿지 못했습니다: " + ex.Message, ErrorKind.Client, retryable: false);
        }
    }

    private static async Task Cancel(HttpClient client, string id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await client.PostAsJsonAsync(BasePath + "/cancel", new { id }, Json, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { } // 풀지 못하면 HB_Chat 설정 화면 "진행 중 비우기"
    }

    private sealed record Reply(string? Status, string? Id, string? Text, string? Error);

    private static async Task<Reply> Read(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new ClaudeCliException($"[agy-relay] HB_Chat 응답 {(int)response.StatusCode}", ErrorKind.Client, retryable: false, status: (int)response.StatusCode);
        try
        {
            return await response.Content.ReadFromJsonAsync<Reply>(Json, ct) ?? new Reply(null, null, null, "빈 응답");
        }
        catch (JsonException ex)
        {
            throw new ClaudeCliException("[agy-relay] HB_Chat 응답 모양이 올바르지 않습니다: " + ex.Message, ErrorKind.Client, retryable: false);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
