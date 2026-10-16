using Microsoft.Extensions.AI;

namespace HB.Orchestration.Tests.Fakes;

/// <summary>호출마다 정해 둔 동작(예외 또는 응답 텍스트)을 차례로 수행하는 채팅 클라이언트.</summary>
public sealed class ScriptedChatClient(params Func<CancellationToken, ChatResponse>[] script) : IChatClient
{
    public int Calls { get; private set; }

    public static Func<CancellationToken, ChatResponse> Reply(string text, ChatFinishReason? finishReason = null) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 },
            FinishReason = finishReason ?? ChatFinishReason.Stop,
        };

    public static Func<CancellationToken, ChatResponse> Throw(Exception ex) => _ => throw ex;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var step = script[Math.Min(Calls, script.Length - 1)];
        Calls++;
        return Task.FromResult(step(cancellationToken));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
