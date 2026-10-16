using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HB.Orchestration.Agents.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HB.Orchestration.Agents;

/// <summary>설정의 에이전트 하나 = 제공사·모델이 정해진 채팅 클라이언트 하나. 역할(프롬프트)은 실행 시점에 붙는다.</summary>
public sealed record AgentEntry(AgentOptions Options, IChatClient Client)
{
    /// <summary>이 에이전트가 쓰는 제공사 설정. 타임아웃·시도 횟수의 기본값을 정할 때 쓴다.</summary>
    public ProviderOptions? ProviderOptions { get; init; }

    public string Name => Options.Name;
    public string Provider => Options.Provider;
    public string Model => Options.Model;
}

/// <summary>
/// appsettings.json의 "Orchestration" 섹션을 읽어 오는 방법.
/// Load는 설정을 파일에서 다시 읽어 최신 값을 돌려줘야 한다. SettingsPath의 수정 시각이 바뀌면 다시 호출된다.
/// </summary>
public sealed record OrchestrationSource(Func<OrchestrationOptions> Load, string? SettingsPath);

/// <summary>
/// 설정과 에이전트 목록.
/// appsettings.json이 바뀌면 다음 호출 때 자동으로 다시 만든다 → 모델·에이전트 변경에 서버 재시작이 필요 없다.
/// (API 키 환경 변수만은 프로세스 시작 때 정해지므로 바뀌어도 반영되지 않는다.)
/// 한 에이전트 설정이 잘못돼도(키 누락 등) 그 에이전트만 건너뛴다.
/// </summary>
public sealed partial class AgentCatalog
{
    /// <param name="SettingsBytes">이 스냅샷을 만들 때 읽은 설정 파일 바이트 (호출 기록의 설정 해시용). 못 읽었으면 null.</param>
    private sealed record Snapshot(
        OrchestrationOptions Options,
        Dictionary<string, AgentEntry> Agents,
        Dictionary<string, string> Skipped,
        DateTimeOffset LoadedAt,
        byte[]? SettingsBytes);

    private readonly OrchestrationSource _source;
    private readonly Dictionary<string, IChatClientProvider> _providers;
    private readonly ILogger<AgentCatalog> _logger;
    private readonly object _gate = new();

    private Snapshot _snapshot;
    private DateTime _settingsWriteUtc;

    public AgentCatalog(OrchestrationSource source, IEnumerable<IChatClientProvider> providers, ILogger<AgentCatalog> logger)
    {
        _source = source;
        _providers = providers.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _settingsWriteUtc = SettingsWriteUtc();
        var bytes = ReadSettingsBytes();
        _snapshot = Build(_source.Load(), bytes);
    }

    /// <summary>현재 설정. 파일이 바뀌었으면 다시 읽은 값.</summary>
    public OrchestrationOptions Options => Current.Options;

    public IReadOnlyCollection<AgentEntry> Agents => Current.Agents.Values;

    /// <summary>비활성화되었거나 설정 오류로 건너뛴 에이전트와 그 이유.</summary>
    public IReadOnlyDictionary<string, string> Skipped => Current.Skipped;

    /// <summary>마지막으로 설정을 읽은 시각.</summary>
    public DateTimeOffset LoadedAt => Current.LoadedAt;

    /// <summary>서버 설정 파일 경로 (호출 기록의 설정 해시용). 없으면 null.</summary>
    public string? SettingsPath => _source.SettingsPath;

    /// <summary>
    /// 현재 적용 중인 설정을 읽을 때 함께 읽은 파일 바이트. 실행 시점에 파일을 다시 읽지 않으므로
    /// 그 사이 파일이 바뀌어도 기록의 해시는 실제 적용 중인 설정을 가리킨다. 다시 읽기에 실패하면 이전 스냅샷의 바이트가 남는다.
    /// </summary>
    public byte[]? SettingsBytes => Current.SettingsBytes;

    /// <summary>마지막 재로드가 실패했다면 그 이유 (이전 설정으로 계속 동작 중).</summary>
    public string? ReloadError { get; private set; }

    public bool TryGet(string name, [MaybeNullWhen(false)] out AgentEntry entry) => Current.Agents.TryGetValue(name, out entry);

    /// <summary>에이전트를 쓸 수 없는 이유. 없는 이름이면 "정의되지 않음".</summary>
    public string WhyUnavailable(string name) =>
        Current.Skipped.TryGetValue(name, out var reason) ? reason : "appsettings.json에 정의되지 않음";

    private Snapshot Current
    {
        get
        {
            var write = SettingsWriteUtc();
            if (write == _settingsWriteUtc) return _snapshot;

            lock (_gate)
            {
                if (write != _settingsWriteUtc)
                {
                    _settingsWriteUtc = write;
                    try
                    {
                        // 설정 시스템이 같은 파일을 읽기 직전의 바이트. 둘 사이에 또 바뀌면 수정 시각이 달라져 다음 접근 때 다시 읽힌다.
                        var bytes = ReadSettingsBytes();
                        _snapshot = Build(_source.Load(), bytes);
                        ReloadError = null;
                        _logger.LogInformation("appsettings.json 변경 감지 → 설정·에이전트 다시 읽음 ({Count}개)", _snapshot.Agents.Count);
                    }
                    catch (Exception ex)
                    {
                        // JSON이 깨졌거나 쓰는 도중이면 이전 설정으로 계속 동작한다.
                        ReloadError = ex.Message;
                        _logger.LogWarning(ex, "appsettings.json 다시 읽기 실패 — 이전 설정 유지");
                    }
                }
                return _snapshot;
            }
        }
    }

    private byte[]? ReadSettingsBytes()
    {
        try
        {
            return _source.SettingsPath is { } p && File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "설정 파일 바이트 읽기 실패 (호출 기록의 설정 해시가 비게 됨)");
            return null;
        }
    }

    private DateTime SettingsWriteUtc()
    {
        try
        {
            return _source.SettingsPath is { } p && File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return _settingsWriteUtc;
        }
    }

    private Snapshot Build(OrchestrationOptions options, byte[]? settingsBytes)
    {
        var agents = new Dictionary<string, AgentEntry>(StringComparer.OrdinalIgnoreCase);
        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in options.Agents)
        {
            if (!a.Enabled)
            {
                skipped[a.Name] = "Enabled=false";
                continue;
            }

            try
            {
                if (!NameRegex().IsMatch(a.Name))
                    throw new ProviderConfigurationException($"에이전트 이름 '{a.Name}'은(는) 영문·숫자·_·- 만 사용할 수 있습니다.");
                if (agents.ContainsKey(a.Name))
                    throw new ProviderConfigurationException($"에이전트 이름 '{a.Name}'이(가) 중복됩니다.");
                if (!options.Providers.TryGetValue(a.Provider, out var providerOptions))
                    throw new ProviderConfigurationException($"Providers에 '{a.Provider}'이(가) 없습니다.");
                if (!_providers.TryGetValue(providerOptions.Type, out var provider))
                    throw new ProviderConfigurationException($"지원하지 않는 제공사 Type '{providerOptions.Type}'.");

                var client = provider.Create(a.Provider, providerOptions, a);

                // 출력 상한: 모든 호출에 MaxOutputTokens를 기본값으로 건다.
                if (a.MaxOutputTokens is { } max)
                    client = client.AsBuilder().ConfigureOptions(o => o.MaxOutputTokens ??= max).Build();

                agents[a.Name] = new AgentEntry(a, client) { ProviderOptions = providerOptions };
                _logger.LogInformation("에이전트 등록: {Name} ({Provider}/{Model})", a.Name, a.Provider, a.Model);
            }
            catch (Exception ex) when (ex is ProviderConfigurationException or UriFormatException)
            {
                skipped[a.Name] = ex.Message;
                _logger.LogWarning("에이전트 '{Name}' 건너뜀: {Reason}", a.Name, ex.Message);
            }
        }

        if (agents.Count == 0)
            _logger.LogWarning("사용 가능한 에이전트가 없습니다. appsettings.json과 API 키 환경 변수를 확인하세요.");

        return new Snapshot(options, agents, skipped, DateTimeOffset.Now, settingsBytes);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NameRegex();
}
