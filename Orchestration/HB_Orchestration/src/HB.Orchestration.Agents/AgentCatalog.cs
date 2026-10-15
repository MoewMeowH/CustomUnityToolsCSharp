using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HB.Orchestration.Agents.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HB.Orchestration.Agents;

/// <summary>설정의 에이전트 하나 = 제공사·모델이 정해진 채팅 클라이언트 하나. 역할(프롬프트)은 실행 시점에 붙는다.</summary>
public sealed record AgentEntry(AgentOptions Options, IChatClient Client)
{
    public string Name => Options.Name;
    public string Provider => Options.Provider;
    public string Model => Options.Model;
}

/// <summary>
/// appsettings의 Agents 목록으로 채팅 클라이언트를 만든다.
/// 한 에이전트 설정이 잘못돼도(키 누락 등) 서버 전체가 죽지 않도록 그 에이전트만 건너뛴다.
/// </summary>
public sealed partial class AgentCatalog
{
    private readonly Dictionary<string, AgentEntry> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _skipped = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AgentEntry> Agents => _agents.Values;

    /// <summary>비활성화되었거나 설정 오류로 건너뛴 에이전트와 그 이유.</summary>
    public IReadOnlyDictionary<string, string> Skipped => _skipped;

    public AgentCatalog(OrchestrationOptions options, IEnumerable<IChatClientProvider> providers, ILogger<AgentCatalog> logger)
    {
        var byType = providers.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);

        foreach (var a in options.Agents)
        {
            if (!a.Enabled)
            {
                _skipped[a.Name] = "Enabled=false";
                continue;
            }

            try
            {
                if (!NameRegex().IsMatch(a.Name))
                    throw new ProviderConfigurationException($"에이전트 이름 '{a.Name}'은(는) 영문·숫자·_·- 만 사용할 수 있습니다.");
                if (_agents.ContainsKey(a.Name))
                    throw new ProviderConfigurationException($"에이전트 이름 '{a.Name}'이(가) 중복됩니다.");
                if (!options.Providers.TryGetValue(a.Provider, out var providerOptions))
                    throw new ProviderConfigurationException($"Providers에 '{a.Provider}'이(가) 없습니다.");
                if (!byType.TryGetValue(providerOptions.Type, out var provider))
                    throw new ProviderConfigurationException($"지원하지 않는 제공사 Type '{providerOptions.Type}'.");

                var client = provider.Create(a.Provider, providerOptions, a.Model);

                // 출력 상한: 모든 호출에 MaxOutputTokens를 기본값으로 건다.
                if (a.MaxOutputTokens is { } max)
                    client = client.AsBuilder().ConfigureOptions(o => o.MaxOutputTokens ??= max).Build();

                _agents[a.Name] = new AgentEntry(a, client);
                logger.LogInformation("에이전트 등록: {Name} ({Provider}/{Model})", a.Name, a.Provider, a.Model);
            }
            catch (Exception ex) when (ex is ProviderConfigurationException or UriFormatException)
            {
                _skipped[a.Name] = ex.Message;
                logger.LogWarning("에이전트 '{Name}' 건너뜀: {Reason}", a.Name, ex.Message);
            }
        }

        if (_agents.Count == 0)
            logger.LogWarning("사용 가능한 에이전트가 없습니다. appsettings.json과 API 키 환경 변수를 확인하세요.");
    }

    public bool TryGet(string name, [MaybeNullWhen(false)] out AgentEntry entry) => _agents.TryGetValue(name, out entry);

    /// <summary>에이전트를 쓸 수 없는 이유. 없는 이름이면 "정의되지 않음".</summary>
    public string WhyUnavailable(string name) =>
        _skipped.TryGetValue(name, out var reason) ? reason : "appsettings.json에 정의되지 않음";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NameRegex();
}
