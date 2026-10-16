namespace HB.Orchestration.Agents;

/// <summary>appsettings.json의 "Orchestration" 섹션 — "누가"(제공사·모델)와 안전장치. 서버 시작 시 1회 로드.</summary>
public sealed class OrchestrationOptions
{
    /// <summary>키 = 제공사 이름 (openai, gemini, local ...). 에이전트가 이 이름으로 참조한다.</summary>
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AgentOptions> Agents { get; set; } = [];

    /// <summary>
    /// 모드 정의 파일. 비우면 HB_MODES_PATH 환경 변수 → Unity 프로젝트 루트의 hb-modes.json → 서버 폴더의 modes.json 순.
    /// 실행할 때마다 다시 읽으므로 고치면 바로 반영된다.
    /// </summary>
    public string? ModesPath { get; set; }

    /// <summary>
    /// 공통 리뷰 지침 파일. 비우면 REVIEW_GUIDELINES_PATH 환경 변수 → 프로젝트 루트의 REVIEW_GUIDELINES.md 순.
    /// </summary>
    public string? GuidelinesPath { get; set; }

    public BudgetOptions Budget { get; set; } = new();

    public ContextOptions Context { get; set; } = new();

    /// <summary>모델 단가(USD)와 원화 환율. 서버는 쓰지 않고 대시보드가 비용 표시에 쓴다 (같은 파일에서 함께 편집).</summary>
    public PricingOptions Pricing { get; set; } = new();

    /// <summary>응답 검사기(빈 응답·잘림·언어 이탈) 설정.</summary>
    public ResponseChecksOptions ResponseChecks { get; set; } = new();

    /// <summary>조사용 디버그 설정. 평소에는 모두 끔.</summary>
    public DebugOptions Debug { get; set; } = new();
}

/// <summary>appsettings.json의 "Orchestration:Debug".</summary>
public sealed class DebugOptions
{
    /// <summary>
    /// true면 run_mode가 반환하는 문자열을 그대로 %LOCALAPPDATA%/HB_Orchestration/dumps/에 남긴다.
    /// 결과가 지휘자에게 가는 길(MCP 직렬화·표시)에서 바뀌는지 가르는 용도 (4장: '&lt;' 조사). 조사가 끝나면 끈다.
    /// </summary>
    public bool DumpToolResults { get; set; }
}

/// <summary>appsettings.json의 "Orchestration:ResponseChecks". 경고는 응답을 막지 않고 기록·결과에 한 줄씩 붙는다.</summary>
public sealed class ResponseChecksOptions
{
    public const double DefaultForeignScriptRatio = 0.02;

    /// <summary>
    /// 기대하는 응답 언어. 지금은 "ko"만 지원 (코드 블록을 뺀 본문의 일본어 가나 비율을 본다).
    /// 키가 없으면 "ko"(검사 켬), ""이면 검사 끔. "비우면 기존 동작" 원칙의 의도적 예외다:
    /// 경고는 실패가 아니라 기존 실행 결과를 바꾸지 않고, 3장처럼 사람이 대시보드를 보기 전엔 모르는 이탈을 기본으로 잡기 위해서다.
    /// </summary>
    public string? ExpectedLanguage { get; set; } = "ko";

    /// <summary>본문 글자 중 가나 비율이 이 값 이상이면 경고 (0~1, 기본 0.02 = 2%). 0 이하나 1 초과면 기본값.</summary>
    public double ForeignScriptRatio { get; set; } = DefaultForeignScriptRatio;

    public double EffectiveForeignScriptRatio =>
        ForeignScriptRatio is > 0 and <= 1 ? ForeignScriptRatio : DefaultForeignScriptRatio;
}

/// <summary>appsettings.json의 "Orchestration:Pricing".</summary>
public sealed class PricingOptions
{
    /// <summary>1달러당 원. 비우면 대시보드는 달러만 표시한다.</summary>
    public decimal? KrwPerUsd { get; set; }

    /// <summary>
    /// 키 = 모델 ID (호출 기록의 Model과 같은 값). 에이전트 이름이 아니라 모델로 두는 이유:
    /// 기록에는 모델이 남고 에이전트 이름은 나중에 바뀔 수 있으며, 같은 모델을 쓰는 에이전트는 단가가 같다.
    /// 지금 에이전트가 쓰지 않는 예전 모델도 남겨 두면 예전 기록의 비용이 계속 계산된다.
    /// </summary>
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>1M 토큰당 가격 (USD).</summary>
public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }

    /// <summary>프롬프트 캐시에 걸린 입력 토큰 단가. 비우면 InputPerMillion과 같다고 본다.</summary>
    public decimal? CachedInputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}

/// <summary>run_mode의 context(서버가 읽어 붙이는 코드 범위) 제한.</summary>
public sealed class ContextOptions
{
    /// <summary>한 번에 받을 범위 개수 상한. 0이면 무제한.</summary>
    public int MaxRanges { get; set; } = 30;

    /// <summary>읽기 전에 확인하는 파일 크기 상한(바이트). 0이면 무제한.</summary>
    public long MaxFileBytes { get; set; } = 2_000_000;

    /// <summary>
    /// 외부 모델에 보내지 않을 파일을 추가한다 (프로젝트 루트 기준 glob). '/'가 없으면 어느 폴더든 파일 이름으로 매칭.
    /// <see cref="BuiltInDenyPatterns"/>는 항상 적용된다 (설정 바인더는 기본값이 든 List에 덧붙이므로 대체 방식은 쓰지 않음).
    /// </summary>
    public List<string> DenyPatterns { get; set; } = [];

    public static readonly IReadOnlyList<string> BuiltInDenyPatterns =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.p8", "*.p12", "*.pfx", "*.keystore", "*.jks",
        "*.mobileprovision", "*.provisionprofile", "credentials*", "secrets*",
        ".git/**", "**/.git/**",
    ];

    public IEnumerable<string> AllDenyPatterns => BuiltInDenyPatterns.Concat(DenyPatterns);
}

public sealed class ProviderOptions
{
    /// <summary>클라이언트 구현 종류. 현재는 "openai-compatible" 하나로 OpenAI·Gemini·Ollama·LM Studio를 모두 처리.</summary>
    public string Type { get; set; } = "openai-compatible";

    /// <summary>API 기본 주소. 비우면 OpenAI 공식 주소.</summary>
    public string? Endpoint { get; set; }

    /// <summary>API 키를 담은 환경 변수 이름. 로컬 모델처럼 키가 필요 없으면 비운다.</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>요청 하나의 네트워크 타임아웃(초). 에이전트에 값이 없을 때 쓴다. 비우면 SDK 기본값 100초.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>한 단계의 최대 시도 횟수 (첫 시도 포함). 에이전트에 값이 없을 때 쓴다. 비우면 2.</summary>
    public int? MaxAttempts { get; set; }
}

public sealed class AgentOptions
{
    /// <summary>모드의 Steps에서 이 이름으로 참조한다.</summary>
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 응답 최대 토큰. 비우면 제한 없음.
    /// 추론(reasoning) 모델은 생각하는 토큰도 여기에 포함되므로 너무 낮게 잡으면 빈 응답이 올 수 있다.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// 요청 하나의 네트워크 타임아웃(초). 비우면 제공사 값 → SDK 기본값 100초 순.
    /// 같은 제공사라도 빠른 모델과 긴 응답을 쓰는 모델의 필요 시간이 달라 에이전트 단위로 둔다.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// 한 단계의 최대 시도 횟수 (첫 시도 포함, 1~5). 비우면 제공사 값 → 2 (= 최대 1회 재시도).
    /// 타임아웃·429·5xx·네트워크 오류만 재시도한다. 최악의 대기 = 타임아웃 × 시도 횟수 (+ 시도 사이 대기).
    /// </summary>
    public int? MaxAttempts { get; set; }
}

public sealed class BudgetOptions
{
    /// <summary>하루(로컬 날짜 기준) 입력+출력 토큰 합계 상한. 0이면 끔. 로컬 제공사는 집계에서 제외.</summary>
    public long DailyTokenLimit { get; set; }

    /// <summary>
    /// run_mode 입력 최대 글자 수 (input + context로 붙인 코드 합계). 넘으면 실행하지 않고 줄여달라고 돌려준다.
    /// 모드별로 덮어쓸 수 있음. Debate는 토론자가 매 턴 요청 전체를 다시 받으므로 실제 비용은 턴 수만큼 곱해진다.
    /// </summary>
    public int MaxInputChars { get; set; } = 100_000;

    /// <summary>MaxRunsPerSession을 정하지 않은 모드의 세션당 실행 한도. 0이면 무제한.</summary>
    public int DefaultMaxRunsPerSession { get; set; } = 5;

    /// <summary>예산 집계에서 뺄 제공사 (무료인 로컬 등).</summary>
    public List<string> FreeProviders { get; set; } = ["local"];
}
