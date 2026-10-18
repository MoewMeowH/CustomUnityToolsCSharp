using System.ClientModel;
using System.ClientModel.Primitives;
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

        // SDK 자동 재시도(기본 3회)는 끈다. 재시도가 기록에 남지 않고, 402처럼 다시 보내도 소용없는 오류까지
        // 다시 보내기 때문이다. 재시도는 AgentRunner가 오류 종류를 보고 직접 하고 시도마다 기록한다.
        clientOptions.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);

        // 캐시 지시는 호출마다 다르므로 정책은 붙여 두고, 실제 방식은 AgentRunner가 놓는 PromptCacheScope로 정한다.
        if (options.PromptCache != PromptCacheMode.ProviderDefault)
            clientOptions.AddPolicy(new PromptCachePolicy(), PipelinePosition.PerCall);

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(agent.Model)
            .AsIChatClient();
    }
}
