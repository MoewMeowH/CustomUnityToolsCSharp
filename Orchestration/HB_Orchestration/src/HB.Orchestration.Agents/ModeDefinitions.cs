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
    /// Claude Code 세션 하나에서 이 모드를 실행할 수 있는 최대 횟수 (MCP 서버 프로세스 = 세션).
    /// 비우면 Budget.DefaultMaxRunsPerSession, 0이면 무제한.
    /// </summary>
    public int? MaxRunsPerSession { get; set; }

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

    /// <summary>Sequential에서 이 단계가 볼 내용. Concurrent에서는 항상 Input.</summary>
    public StepSees Sees { get; set; } = StepSees.InputAndPrevious;
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
