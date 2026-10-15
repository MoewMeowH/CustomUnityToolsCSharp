using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// 제공사 설정 + 모델 이름으로 IChatClient를 만든다.
/// MAF 에이전트는 IChatClient만 있으면 되므로, 새 제공사 추가 = 이 인터페이스 구현 하나.
/// </summary>
public interface IChatClientProvider
{
    /// <summary>ProviderOptions.Type과 매칭되는 이름.</summary>
    string Type { get; }

    IChatClient Create(string providerName, ProviderOptions options, string model);
}

public sealed class ProviderConfigurationException(string message) : Exception(message);
