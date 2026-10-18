using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// OpenAI Responses API (/responses). 웹검색을 켠 단계의 에이전트만 이걸 쓴다 — 일반 모델의 web_search 도구는 Responses에만 있다.
/// 나머지 에이전트는 지금처럼 Chat Completions(<see cref="OpenAICompatibleProvider"/>).
/// 프롬프트 캐시 정책(PromptCachePolicy)은 /chat/completions 전용이라 붙이지 않는다 (제공사 기본 동작). 대신 WebSearchPolicy가
/// store=false와 검색 상한·필터를 넣는다.
/// </summary>
public sealed class OpenAIResponsesProvider : IChatClientProvider
{
    public const string TypeName = "openai-responses";

    public string Type => TypeName;

    public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKeyEnv))
            throw new ProviderConfigurationException($"제공사 '{providerName}': ApiKeyEnv가 비어 있습니다 (openai-responses는 키가 필요합니다).");
        var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnv) is { Length: > 0 } k
            ? k
            : throw new ProviderConfigurationException($"제공사 '{providerName}': 환경 변수 {options.ApiKeyEnv} 이(가) 비어 있습니다.");

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);
        clientOptions.NetworkTimeout = AgentSettings.ResolveTimeout(agent, options);

        // SDK 자동 재시도는 끈다 (AgentRunner가 오류 종류를 보고 직접 재시도하고 시도마다 기록한다).
        clientOptions.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        clientOptions.AddPolicy(new WebSearchPolicy(), PipelinePosition.PerCall);

        // Responses 클라이언트는 SDK에서 평가용(OPENAI001)으로 표시돼 있다. 웹검색이 Responses에만 있어 감수한다.
#pragma warning disable OPENAI001
        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetResponsesClient()
            .AsIChatClient(agent.Model);
#pragma warning restore OPENAI001
    }
}
