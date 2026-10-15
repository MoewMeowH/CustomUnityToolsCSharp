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
}

public sealed class ProviderOptions
{
    /// <summary>클라이언트 구현 종류. 현재는 "openai-compatible" 하나로 OpenAI·Gemini·Ollama·LM Studio를 모두 처리.</summary>
    public string Type { get; set; } = "openai-compatible";

    /// <summary>API 기본 주소. 비우면 OpenAI 공식 주소.</summary>
    public string? Endpoint { get; set; }

    /// <summary>API 키를 담은 환경 변수 이름. 로컬 모델처럼 키가 필요 없으면 비운다.</summary>
    public string? ApiKeyEnv { get; set; }
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
}

public sealed class BudgetOptions
{
    /// <summary>하루(로컬 날짜 기준) 입력+출력 토큰 합계 상한. 0이면 끔. 로컬 제공사는 집계에서 제외.</summary>
    public long DailyTokenLimit { get; set; }

    /// <summary>run_mode 입력 최대 글자 수. 넘으면 실행하지 않고 줄여달라고 돌려준다. 모드별로 덮어쓸 수 있음.</summary>
    public int MaxInputChars { get; set; } = 40_000;

    /// <summary>MaxRunsPerSession을 정하지 않은 모드의 세션당 실행 한도. 0이면 무제한.</summary>
    public int DefaultMaxRunsPerSession { get; set; } = 5;

    /// <summary>예산 집계에서 뺄 제공사 (무료인 로컬 등).</summary>
    public List<string> FreeProviders { get; set; } = ["local"];
}
