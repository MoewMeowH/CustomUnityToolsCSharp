namespace HB.Orchestration.Agents;

/// <summary>modes.json 전체 — "무엇을"(역할)과 "어떤 순서로"(모드).</summary>
public sealed class ModesFile
{
    /// <summary>키 = 역할 이름. 코드에 내장된 기본 역할(Prompts.BuiltInRoles)을 같은 이름으로 덮어쓸 수 있다.</summary>
    public Dictionary<string, RoleDefinition> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>키 = 모드 이름 (run_mode의 mode 인자).</summary>
    public Dictionary<string, ModeDefinition> Modes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RoleDefinition
{
    /// <summary>시스템 프롬프트 본문. PromptFile과 둘 중 하나.</summary>
    public string? Prompt { get; set; }

    /// <summary>프롬프트 파일 경로 (modes.json 기준 상대 경로 가능).</summary>
    public string? PromptFile { get; set; }

    /// <summary>
    /// 프로젝트 리뷰 지침(REVIEW_GUIDELINES.md)을 붙일지. 코드와 무관한 역할(탐색·실효성 등)은 false로 두면
    /// 매 호출 지침 분량만큼 입력 토큰이 줄어든다.
    /// </summary>
    public bool UseGuidelines { get; set; } = true;

    /// <summary>
    /// 구조화 출력 형식 (findings · perf_findings). 지정하면 서버가 형식 설명을 시스템 프롬프트 끝에 붙이고, 응답을 해석해
    /// 지적 ID를 매긴다. 역할 프롬프트에는 출력 형식을 따로 적지 않는다. 비우면 자유 텍스트 (지금 동작).
    /// </summary>
    public string? Output { get; set; }

    /// <summary>
    /// 자유 텍스트 출력 형식의 절 개수 (예: preflight P3는 7). 있으면 응답 검사기가 1..N 절 제목이 모두 있는지 보고, 빠지면 경고한다
    /// (모델이 상한 전에 스스로 멈춘 응답 — 계획서 2026-10-27_한국어-응답-지시-강화). 비우면 검사하지 않는다.
    /// </summary>
    public int? OutputSections { get; set; }
}

public sealed class ModeDefinition
{
    /// <summary>list_modes에서 Claude에게 보이는 한 줄 설명. 짧을수록 토큰 절약.</summary>
    public string Description { get; set; } = "";

    public ModeFlow Flow { get; set; } = ModeFlow.Sequential;

    public List<StepDefinition> Steps { get; set; } = [];

    /// <summary>Claude에게 돌려줄 범위. Last면 마지막 단계 결과만 (Claude 쪽 토큰 절약).</summary>
    public ModeReturn Return { get; set; } = ModeReturn.Last;

    /// <summary>이 모드의 입력 최대 글자 수. 비우면 Budget.MaxInputChars.</summary>
    public int? MaxInputChars { get; set; }

    /// <summary>
    /// true면 run_mode의 ownReview(지휘자가 결과를 보기 전에 완성한 자체 검토)가 필수다 (review 계열).
    /// 키가 없으면 false = 필수 아님. list_modes에 "[ownReview 필수]"로 보인다.
    /// </summary>
    public bool RequireOwnReview { get; set; }

    /// <summary>
    /// 이 모드의 프롬프트 캐시 방식. 비우면 제공사 설정(Providers.*.PromptCache).
    /// 제공사가 ProviderDefault(캐시 정책 없음)면 무시한다. 같은 앞부분을 30분 안에 다시 보내지 않는 모드는 Off.
    /// </summary>
    public PromptCacheMode? PromptCache { get; set; }

    /// <summary>Debate 전용: 최대 회차 (1회차 = 두 토론자가 한 번씩). 기본 2, 서버 상한 MaxRoundsHardCap.</summary>
    public int? MaxRounds { get; set; }

    /// <summary>Debate 전용: 각 턴에 넘길 직전 발언 개수. 작을수록 저렴. 기본 2 (= 양쪽 직전 발언 하나씩).</summary>
    public int HistoryWindow { get; set; } = 2;

    /// <summary>설정 실수로 토론이 길어지는 것을 막는 서버 측 절대 상한.</summary>
    public const int MaxRoundsHardCap = 5;
}

public sealed class StepDefinition
{
    public string Role { get; set; } = "";
    public string Agent { get; set; } = "";

    /// <summary>
    /// 이 단계 에이전트가 Quota(구독 한도 · 크레딧 소진)로 실패하거나 최근 Quota 기록으로 쉬는 중이면 같은 입력으로 대신 부를 에이전트.
    /// 비우면 대체 없이 실패한다. run_mode agents로 바꾼 실행에는 쓰지 않는다 (계획서 2026-10-29_Codex-외부-리뷰어 결정).
    /// </summary>
    public string? FallbackAgent { get; set; }

    /// <summary>Sequential에서 이 단계가 볼 내용. Concurrent에서는 항상 Input.</summary>
    public StepSees Sees { get; set; } = StepSees.InputAndPrevious;

    /// <summary>
    /// 웹검색을 켠다 (없으면 끔). 에이전트 제공사가 openai-responses여야 한다 — 일반 모델의 web_search 도구는 Responses API에만 있다.
    /// </summary>
    public WebSearchSettings? WebSearch { get; set; }
}

/// <summary>단계의 웹검색 설정. 요청 본문에는 WebSearchPolicy가 넣는다 (max_tool_calls, filters.blocked_domains, search_context_size).</summary>
public sealed class WebSearchSettings
{
    /// <summary>한 호출의 검색 도구 호출 상한 (max_tool_calls). 1~<see cref="MaxCallsHardCap"/>.</summary>
    public int MaxCalls { get; set; } = 5;

    /// <summary>검색에서 뺄 도메인 (프로토콜 없이, 하위 도메인 포함, 최대 100개). 비우면 제한 없음.</summary>
    public List<string> BlockedDomains { get; set; } = [];

    /// <summary>검색 결과 분량: low · medium · high. 비우면 제공사 기본값(medium).</summary>
    public string? ContextSize { get; set; }

    /// <summary>설정 실수로 검색이 늘어나는 것을 막는 서버 측 상한.</summary>
    public const int MaxCallsHardCap = 15;

    /// <summary>설정 문제. 정상이면 null.</summary>
    public string? Problem()
    {
        if (MaxCalls is < 1 or > MaxCallsHardCap) return $"WebSearch.MaxCalls는 1~{MaxCallsHardCap}입니다 (지금 {MaxCalls}).";
        if (BlockedDomains.Count > 100) return "WebSearch.BlockedDomains는 최대 100개입니다.";
        if (BlockedDomains.Any(d => string.IsNullOrWhiteSpace(d) || d.Contains("://", StringComparison.Ordinal) || d.Contains('/')))
            return "WebSearch.BlockedDomains에는 프로토콜·경로 없이 도메인만 씁니다 (예: example.com).";
        if (ContextSize is { Length: > 0 } size && size is not ("low" or "medium" or "high"))
            return $"WebSearch.ContextSize는 low · medium · high입니다 (지금 '{size}').";
        return null;
    }
}

public enum ModeFlow
{
    /// <summary>단계를 순서대로. 앞 단계 결과가 다음 단계로 넘어간다.</summary>
    Sequential,
    /// <summary>모든 단계를 동시에. 서로의 결과를 보지 않는 독립 응답.</summary>
    Concurrent,
    /// <summary>
    /// 다회차 토론. Steps[0]과 Steps[1]이 번갈아 발언(Steps[0]부터)하고, Steps[2]가 있으면 마지막에 판정.
    /// 어느 쪽이든 마지막 줄에 [합의]를 쓰면 남은 회차를 건너뛰고 판정으로 간다.
    /// </summary>
    Debate,
}

public enum ModeReturn
{
    Last,
    All,
}

public enum StepSees
{
    /// <summary>원래 요청만.</summary>
    Input,
    /// <summary>바로 앞 단계 결과만 (요청 원문 없이 — 가장 저렴).</summary>
    Previous,
    /// <summary>원래 요청 + 바로 앞 단계 결과. 기본값.</summary>
    InputAndPrevious,
    /// <summary>원래 요청 + 앞 단계 전부 (가장 비쌈).</summary>
    All,
}
