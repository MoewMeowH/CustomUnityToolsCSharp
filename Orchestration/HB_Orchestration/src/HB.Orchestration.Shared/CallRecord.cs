using System.Text.Json.Serialization;

namespace HB.Orchestration.Shared;

/// <summary>에이전트 호출 1건의 기록. MCP 서버가 쓰고 대시보드가 읽는다.</summary>
public sealed record CallRecord
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>호출된 MCP 도구 또는 모드 이름 (예: debate, review).</summary>
    public required string Tool { get; init; }

    /// <summary>같은 모드 실행에 속한 단계들을 묶는 ID.</summary>
    public string? RunId { get; init; }

    /// <summary>실행된 모드 이름.</summary>
    public string? Mode { get; init; }

    /// <summary>이 단계에서 에이전트가 맡은 역할 (예: Rebuttal, LogicCheck).</summary>
    public string? Role { get; init; }

    /// <summary>모드 안에서의 단계 번호 (1부터).</summary>
    public int? Step { get; init; }

    /// <summary>실제로 응답한 에이전트 이름.</summary>
    public string? Agent { get; init; }

    /// <summary>appsettings의 Providers 키 (예: openai, gemini, local).</summary>
    public string? Provider { get; init; }

    public required string Model { get; init; }

    /// <summary>이 호출에 보낸 추론 강도 (none · low · medium · high · xhigh). 보내지 않았거나(제공사 기본값) 예전 기록이면 null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    /// <summary>MCP 서버의 작업 디렉터리 (= Claude Code를 실행한 프로젝트 루트).</summary>
    public string? ProjectDir { get; init; }

    public required string Prompt { get; init; }
    public string? Response { get; init; }

    /// <summary>입력 토큰 합계 (캐시에 걸린 토큰 포함).</summary>
    public long? InputTokens { get; init; }

    /// <summary>InputTokens 중 제공사 프롬프트 캐시에 걸린 토큰. 제공사가 알려주지 않거나 예전 기록이면 null.</summary>
    public long? CachedInputTokens { get; init; }

    /// <summary>InputTokens 중 제공사 프롬프트 캐시에 새로 쓴 토큰. usage 원문에 없거나 예전 기록이면 null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheWriteTokens { get; init; }

    public long? OutputTokens { get; init; }

    /// <summary>OutputTokens 중 추론(생각) 토큰. 제공사가 알려주지 않거나 예전 기록이면 null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ReasoningTokens { get; init; }

    /// <summary>제공사 usage JSON 원문 (Chat Completions). SDK가 모르는 필드 확인용. 꺼내지 못했거나 예전 기록이면 null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UsageRaw { get; init; }

    public long ElapsedMs { get; init; }

    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>같은 단계 안에서 몇 번째 시도인지 (1부터). 같은 RunId·Step의 시도는 각각 한 줄씩 남는다. 예전 기록은 null(= 1).</summary>
    public int? Attempt { get; init; }

    /// <summary>실패 종류: Timeout · RateLimit · Server · Client · Network · Quota(크레딧·한도 소진, 재시도 안 함), 사용자 취소는 Canceled. 성공이거나 분류할 수 없는 실패면 null.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>응답 검사기 경고 ("[경고] 응답 언어 이탈 감지 …" 등). 경고는 실패가 아니라 응답은 그대로 전달됐다.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>관련 파일. "Assets/Scripts/Player.cs" 또는 "Assets/Scripts/Player.cs:42" 형식.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>서버가 읽어 프롬프트에 첨부한 코드 범위. "Assets/Scripts/Player.cs:40-80" 형식. 내용은 Prompt에 있다.</summary>
    public IReadOnlyList<string> Context { get; init; } = [];

    /// <summary>
    /// Context·Images 중 "@이름/…" 항목이 읽은 추가 루트 (설정의 이름 → 그때의 절대 경로). 나중에 설정을 비워도 출처를 알 수 있다.
    /// 추가 루트를 쓰지 않은 호출에는 필드 자체를 남기지 않는다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ContextRoots { get; init; }

    /// <summary>
    /// 이 단계에 보낸 이미지 (run_mode images). "경로 (WxH, 바이트, sha256:앞 12자)" 형식, 보낸 순서대로. 원문은 남기지 않는다.
    /// 이미지를 보내지 않은 호출에는 필드 자체를 남기지 않는다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Images { get; init; }

    /// <summary>
    /// 이 호출에서 에이전트가 한 웹검색 (modes.json 단계의 WebSearch). 검색 도구를 주지 않은 호출에는 필드 자체를 남기지 않는다.
    /// 도구를 줬지만 검색하지 않았으면 Calls = 0.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebSearchRecord? WebSearch { get; init; }

    /// <summary>에이전트가 받은 지침·설정의 해시. 모든 호출에 남긴다. 5장 이전 기록은 null(대시보드 "기록 없음").</summary>
    public CallProvenance? Provenance { get; init; }

    /// <summary>지휘자가 이 모드를 부른 이유 (run_mode reason). 에이전트에게는 보내지 않았다. 5장 이전 기록은 null.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 지휘자가 결과를 보기 전에 완성한 자체 검토 (run_mode ownReview). 에이전트에게는 보내지 않았다.
    /// 같은 실행의 모든 줄(단계·시도)에 같은 값이 남는다. 넣지 않았으면 null.
    /// </summary>
    public string? OwnReview { get; init; }

    /// <summary>
    /// 역할에 구조화 출력(Output)을 지정한 호출의 해석 결과 (지적 ID 포함). Response는 원문(JSON) 그대로다.
    /// 구조화 출력을 쓰지 않은 호출과 예전 기록에는 필드 자체를 남기지 않는다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StructuredOutputRecord? Structured { get; init; }

    /// <summary>
    /// 자료 보충 재질문(run_mode followUp)이면 보충한 원래 단계 번호. 이 호출은 같은 RunId의 새 단계 번호로 남는다
    /// (save_result·지적 ID가 원래 응답과 섞이지 않게). 보통 호출은 null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FollowUpOf { get; init; }

    /// <summary>followUp에서 보충한 요청 ID (예: 1-N1). 보충하지 않은 요청은 FollowUpSkipped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FollowUpRequests { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FollowUpSkipped { get; init; }
}

/// <summary>
/// 웹검색 기록. Calls = 검색 도구 호출 수 (search·open_page·find_in_page 동작 모두).
/// Queries = 검색어, Sources = 참고한 URL 전체(web_search_call.action.sources), Citations = 응답 본문에 인용된 URL. 모두 중복 제거, 나온 순서.
/// </summary>
public sealed record WebSearchRecord
{
    public int Calls { get; init; }

    /// <summary>
    /// search 동작 수 = 청구 단위 (추정: 2026-10-19 기록 71 대 청구 50). 응답 원본을 못 읽었거나 예전 기록이면 null — 비용은 Calls로 계산한다.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchActions { get; init; }

    /// <summary>비용 계산에 쓰는 검색 횟수.</summary>
    [JsonIgnore]
    public int BilledCalls => SearchActions ?? Calls;

    public IReadOnlyList<string> Queries { get; init; } = [];
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> Citations { get; init; } = [];

    /// <summary>open_page·find_in_page 동작으로 직접 연 페이지 URL. 예전 기록은 빈 목록.</summary>
    public IReadOnlyList<string> Opened { get; init; } = [];

    /// <summary>응답 본문에 적혔지만 Sources·Citations·Opened에 없는 URL (지어냈을 수 있음). 비교는 호스트 + 경로, 쿼리·조각 무시.</summary>
    public IReadOnlyList<string> Unverified { get; init; } = [];
}
