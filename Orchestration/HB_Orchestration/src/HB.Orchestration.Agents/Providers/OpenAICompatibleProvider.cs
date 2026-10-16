using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// OpenAI Chat Completions 호환 API면 전부 이걸로 붙는다.
/// - OpenAI:    Endpoint 비움
/// - Gemini:    https://generativelanguage.googleapis.com/v1beta/openai/
/// - Ollama:    http://localhost:11434/v1
/// - LM Studio: http://localhost:1234/v1
/// </summary>
public sealed class OpenAICompatibleProvider : IChatClientProvider
{
    public string Type => "openai-compatible";

    public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent)
    {
        string apiKey;
        if (string.IsNullOrWhiteSpace(options.ApiKeyEnv))
        {
            // 로컬 서버는 키를 검사하지 않지만 SDK가 빈 값을 허용하지 않아 더미 값을 넣는다.
            apiKey = "local";
        }
        else
        {
            apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnv) is { Length: > 0 } k
                ? k
                : throw new ProviderConfigurationException(
                    $"제공사 '{providerName}': 환경 변수 {options.ApiKeyEnv} 이(가) 비어 있습니다.");
        }

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);

        // 에이전트 → 제공사 → SDK 기본값(100초). 기본값과 같아도 명시해 두면 실제 적용 값이 코드에서 보인다.
        clientOptions.NetworkTimeout = AgentSettings.ResolveTimeout(agent, options);

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(agent.Model)
            .AsIChatClient();
    }
}
