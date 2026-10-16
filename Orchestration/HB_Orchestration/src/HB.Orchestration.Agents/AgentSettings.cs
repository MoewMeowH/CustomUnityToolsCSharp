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

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
