using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents;

/// <summary>에이전트 값 → 제공사 값 → 기본값 순으로 실제 적용 값을 정한다. 우선순위를 한곳에 모아 둔다.</summary>
public static class AgentSettings
{
    /// <summary>OpenAI SDK(System.ClientModel)의 기본 네트워크 타임아웃. 설정이 없을 때 실제로 적용되는 값.</summary>
    public static readonly TimeSpan SdkDefaultTimeout = TimeSpan.FromSeconds(100);

    /// <summary>요청 하나의 네트워크 타임아웃. 0 이하 값은 설정하지 않은 것으로 본다.</summary>
    public static TimeSpan ResolveTimeout(AgentOptions agent, ProviderOptions? provider)
    {
        var seconds = Positive(agent.TimeoutSeconds) ?? Positive(provider?.TimeoutSeconds);
        return seconds is { } s ? TimeSpan.FromSeconds(s) : SdkDefaultTimeout;
    }

    /// <summary>구조화 출력 요청 방식: 에이전트 → 제공사 → Prompt.</summary>
    public static StructuredOutputMode ResolveStructuredOutput(AgentOptions agent, ProviderOptions? provider) =>
        agent.StructuredOutput ?? provider?.StructuredOutput ?? StructuredOutputMode.Prompt;

    public const int DefaultMaxAttempts = 2;
    public const int MaxAttemptsCap = 5;

    /// <summary>한 단계의 최대 시도 횟수 (첫 시도 포함). 0 이하는 설정하지 않은 것으로 보고, 상한을 넘으면 상한으로 자른다.</summary>
    public static int ResolveMaxAttempts(AgentOptions agent, ProviderOptions? provider)
    {
        var value = Positive(agent.MaxAttempts) ?? Positive(provider?.MaxAttempts) ?? DefaultMaxAttempts;
        return Math.Min(value, MaxAttemptsCap);
    }

    /// <summary>설정·덮어쓰기에 쓰는 추론 강도 이름. 기록에도 이 이름으로 남긴다.</summary>
    public static readonly IReadOnlyList<string> EffortNames = ["none", "low", "medium", "high", "xhigh"];

    /// <summary>추론 강도 이름 → MEAI 값. 비었으면 (null, null), 모르는 이름이면 오류 문구.</summary>
    public static (ReasoningEffort? Effort, string? Error) ParseEffort(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => (null, null),
            "none" => (ReasoningEffort.None, null),
            "low" => (ReasoningEffort.Low, null),
            "medium" => (ReasoningEffort.Medium, null),
            "high" => (ReasoningEffort.High, null),
            "xhigh" => (ReasoningEffort.ExtraHigh, null),
            _ => (null, $"추론 강도 '{value}'은(는) 쓸 수 없습니다. 가능: {string.Join(" · ", EffortNames)}"),
        };

    /// <summary>MEAI 값 → 설정 이름 (기록·표시용).</summary>
    public static string EffortName(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => effort.ToString().ToLowerInvariant(),
    };

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
