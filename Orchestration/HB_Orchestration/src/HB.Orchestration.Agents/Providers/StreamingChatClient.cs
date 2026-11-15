using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// 스트리밍으로 받은 마지막 usage. 성공하면 응답의 RawRepresentation에, 중간에 끊기면 예외의 Data에 실린다
/// (끊긴 호출도 그때까지 쓴 토큰이 기록에 남게).
/// </summary>
/// <param name="Json">usage 원문. UsageExtractor가 기록용 usageRaw로 꺼낸다.</param>
public sealed record StreamedUsage(string Json, UsageDetails Details)
{
    private const string DataKey = "HB.StreamedUsage";

    public static StreamedUsage From(ChatTokenUsage usage) => new(ModelReaderWriter.Write(usage).ToString(), new UsageDetails
    {
        InputTokenCount = usage.InputTokenCount,
        OutputTokenCount = usage.OutputTokenCount,
        TotalTokenCount = usage.TotalTokenCount,
        CachedInputTokenCount = usage.InputTokenDetails?.CachedTokenCount,
        ReasoningTokenCount = usage.OutputTokenDetails?.ReasoningTokenCount,
    });

    public void AttachTo(Exception ex) => ex.Data[DataKey] = this;

    /// <summary>예외(안쪽 예외 포함)에 실린 사용량. 없으면 null.</summary>
    public static StreamedUsage? Find(Exception? ex)
    {
        for (var depth = 0; depth < 5 && ex is not null; depth++, ex = ex.InnerException)
            if (ex.Data[DataKey] is StreamedUsage found) return found;
        return null;
    }
}

/// <summary>
/// 한 번에 받는 호출(GetResponseAsync)을 스트리밍으로 받아 한 응답으로 합친다 (ProviderOptions.Stream).
/// Gemini OpenAI 호환 경로는 스트리밍이 아니면 carbon · argon 출력이 32,768 토큰(추론 포함)에서 끊긴다 (2026-11-15 실측).
/// </summary>
/// <param name="timeout">호출 전체 시간 상한. 스트리밍에서는 SDK의 NetworkTimeout이 호출 전체를 막는다고 볼 수 없어 여기서 건다.</param>
public sealed class StreamingChatClient(IChatClient inner, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 상한에 걸리면 호출자 토큰은 취소되지 않은 OperationCanceledException이라 RetryClassifier가 Timeout으로 분류한다.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        var updates = new List<ChatResponseUpdate>();
        // Gemini는 모든 조각에 누적 usage를 싣고 마지막 조각 값이 가장 크다 (2026-11-15 실측) → 마지막 것만 쓴다.
        ChatTokenUsage? last = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, limit.Token).ConfigureAwait(false))
            {
                updates.Add(update);
                if (update.RawRepresentation is StreamingChatCompletionUpdate { Usage: { } usage }) last = usage;
            }
        }
        catch (Exception ex) when (last is not null)
        {
            StreamedUsage.From(last).AttachTo(ex);
            throw;
        }

        var response = updates.ToChatResponse();
        // 끊긴 스트림은 예외 없이 끝날 수 있다 — 잘린 글을 성공으로 넘기지 않는다 (IOException = 네트워크 실패로 분류돼 재시도된다).
        if (response.FinishReason is null)
        {
            var cut = new IOException("스트리밍 응답이 종료 이유 없이 끝났습니다 (연결이 중간에 끊긴 것으로 봅니다).");
            if (last is not null) StreamedUsage.From(last).AttachTo(cut);
            throw cut;
        }

        // ToChatResponse는 조각의 usage를 더한다 → 마지막 조각 값으로 덮는다.
        if (last is not null)
        {
            var streamed = StreamedUsage.From(last);
            response.Usage = streamed.Details;
            response.RawRepresentation = streamed;
        }
        return response;
    }
}
