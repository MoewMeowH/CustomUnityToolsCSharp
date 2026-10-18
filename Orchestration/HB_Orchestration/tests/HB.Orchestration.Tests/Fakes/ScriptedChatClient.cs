using Microsoft.Extensions.AI;

namespace HB.Orchestration.Tests.Fakes;

/// <summary>호출마다 정해 둔 동작(예외 또는 응답 텍스트)을 차례로 수행하는 채팅 클라이언트. 받은 메시지·옵션을 남긴다.</summary>
public sealed class ScriptedChatClient(params Func<CancellationToken, ChatResponse>[] script) : IChatClient
{
    private readonly object _gate = new();

    public int Calls { get; private set; }

    /// <summary>호출마다 수행할 동작. 하네스처럼 클라이언트를 먼저 만든 경우 테스트에서 바꿔 끼운다.</summary>
    public Func<CancellationToken, ChatResponse>[] Script { get; set; } = script;

    /// <summary>호출마다 받은 메시지와 옵션 (호출 순서대로).</summary>
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Received { get; } = [];

    public static Func<CancellationToken, ChatResponse> Reply(string text, ChatFinishReason? finishReason = null) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 },
            FinishReason = finishReason ?? ChatFinishReason.Stop,
        };

    public static Func<CancellationToken, ChatResponse> Throw(Exception ex) => _ => throw ex;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, ChatResponse> step;
        lock (_gate) // Concurrent 모드에서 동시에 불린다
        {
            Received.Add((messages.ToList(), options?.Clone()));
            step = Script[Math.Min(Calls, Script.Length - 1)];
            Calls++;
        }
        return Task.FromResult(step(cancellationToken));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    /// <summary>
    /// 에이전트가 IChatClient에 넘긴 시스템 지시. MAF는 ChatOptions.Instructions 또는 system 메시지로 넘기므로 둘 다 모은다.
    /// </summary>
    public static string SystemText((List<ChatMessage> Messages, ChatOptions? Options) received) =>
        string.Concat(new[] { received.Options?.Instructions ?? "" }
            .Concat(received.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));

    /// <summary>시스템이 아닌 메시지 본문 전체 (에이전트에게 간 입력).</summary>
    public static string UserText((List<ChatMessage> Messages, ChatOptions? Options) received) =>
        string.Join("\n", received.Messages.Where(m => m.Role != ChatRole.System).Select(m => m.Text));
}
