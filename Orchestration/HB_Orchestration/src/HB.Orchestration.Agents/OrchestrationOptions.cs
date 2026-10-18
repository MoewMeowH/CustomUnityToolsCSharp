using HB.Orchestration.Shared;

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

    /// <summary>호출 기록 보강(지침·설정 해시, 원문 저장) 설정.</summary>
    public RecordsOptions Records { get; set; } = new();

    /// <summary>/work-order 명령이 작업 지시서를 남길 폴더. 비우면 list_modes 출력이 이전과 같다.</summary>
    public WorkOrdersOptions WorkOrders { get; set; } = new();
}

/// <summary>
/// appsettings.json의 "Orchestration:WorkOrders". 서버는 파일을 쓰지 않는다 — list_modes에 폴더를 보여 줄 뿐이고,
/// 지시서는 지휘자(Claude)가 직접 쓴다 (save_result는 에이전트 응답 전용).
/// </summary>
public sealed class WorkOrdersOptions
{
    /// <summary>
    /// 프로젝트 루트 기준 상대 경로. 비우면 list_modes에 줄이 없고 명령은 기본 폴더(docs/work-orders)를 쓴다.
    /// 잘못된 값(절대 경로·루트 밖·'@')은 list_modes에 [주의]로만 보이고, 명령은 기본 폴더를 쓴다.
    /// </summary>
    public string? Folder { get; set; }

    // Split('/', '\\', options)로 쓰면 '\\'가 int(count)로 바뀌어 '/'로만 나뉜다. 배열로 넘긴다.
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>폴더 값의 문제. 정상이면 null. 비어 있는 값은 호출하지 않는다 (비어 있으면 기존 동작).</summary>
    public static string? FolderProblem(string folder)
    {
        var value = folder.Trim();
        if (value.StartsWith('@')) return "추가 루트(@이름)는 읽기 전용이라 쓸 수 없습니다.";
        if (Path.IsPathRooted(value) || value.Contains(':')) return "프로젝트 루트 기준 상대 경로여야 합니다.";
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "경로에 쓸 수 없는 문자가 있습니다.";
        var segments = value.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.All(s => s == ".")) return "폴더 이름이 없습니다.";
        if (segments.Any(s => s == "..")) return "프로젝트 루트 밖입니다 ('..' 사용 불가).";
        return null;
    }

    /// <summary>표시용: 구분자를 '/'로, '.' 구간과 끝 구분자를 뺀다.</summary>
    public static string Normalize(string folder) =>
        string.Join('/', folder.Trim().Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "."));
}

/// <summary>appsettings.json의 "Orchestration:Records".</summary>
public sealed class RecordsOptions
{
    /// <summary>
    /// 시스템 프롬프트·지침·modes.json 원문을 해시 이름으로 저장할 폴더. 비우면 호출 기록 폴더의 blobs/,
    /// 상대 경로면 호출 기록 폴더 기준. 저장에 실패해도 호출은 진행하고 기록의 StoreErrors에 남는다.
    /// </summary>
    public string? BlobFolder { get; set; }

    /// <summary>
    /// 서버 설정(appsettings.json) 원문도 저장할지. 기본 false = 해시와 키 패턴 검사 결과만 남긴다.
    /// true여도 키 패턴 검사에 걸리면 저장하지 않는다.
    /// </summary>
    public bool StoreSettingsText { get; set; }

    /// <summary>run_mode reason(부른 이유) 최대 글자 수. 넘으면 잘라 쓰지 않고 거절한다 (실행 횟수 차감 없음). 0이면 무제한.</summary>
    public int MaxReasonChars { get; set; } = 500;

    /// <summary>run_mode ownReview(결과를 보기 전의 자체 검토) 최대 글자 수. 넘으면 거절. 0이면 무제한.</summary>
    public int MaxOwnReviewChars { get; set; } = 30_000;
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

    /// <summary>
    /// 프롬프트 캐시에 새로 쓴 입력 토큰 단가. 비우면 InputPerMillion과 같다고 본다.
    /// OpenAI GPT-5.6 이후는 입력 단가의 1.25배 (2026-10-17 UTC 하루 청구액으로 확인).
    /// </summary>
    public decimal? CacheWritePerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    /// <summary>
    /// 한 기록의 USD 비용. 입력 토큰에는 캐시 읽기·쓰기 토큰이 포함되어 있으므로
    /// (입력 - 읽기 - 쓰기) × 입력 단가 + 읽기 × 캐시 단가 + 쓰기 × 캐시 쓰기 단가 + 출력 × 출력 단가.
    /// 쓰기 토큰을 기록하기 전의 호출(CacheWriteTokens null)은 쓰기 0으로 계산한다.
    /// </summary>
    public decimal CostOf(CallRecord r)
    {
        var input = r.InputTokens ?? 0;
        var cached = Math.Clamp(r.CachedInputTokens ?? 0, 0, input);
        var written = Math.Clamp(r.CacheWriteTokens ?? 0, 0, input - cached);
        return (input - cached - written) / 1_000_000m * InputPerMillion
             + cached / 1_000_000m * (CachedInputPerMillion ?? InputPerMillion)
             + written / 1_000_000m * (CacheWritePerMillion ?? InputPerMillion)
             + (r.OutputTokens ?? 0) / 1_000_000m * OutputPerMillion;
    }
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

    /// <summary>
    /// 프로젝트 루트 밖에서 context로 읽을 수 있는 추가 루트 (이름 → 절대 경로). 기본은 비어 있음 = 기존 동작.
    /// context에서 "@이름/상대경로"로 지정한다. 루트마다 같은 검사(루트 밖·링크·DenyPatterns)를 적용하고, 읽기 전용이다
    /// (save_result는 추가 루트에 쓰지 않음). 이름은 대소문자를 구분하지 않는다 — 설정 시스템이 키를 대소문자 무시로 다루므로.
    /// 잘못된 항목은 list_modes에 경고로만 보이고, 그 루트를 참조한 호출만 거절된다.
    /// </summary>
    public Dictionary<string, string> ExtraRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidExtraRootName(string name) =>
        name.Length is >= 2 and <= 32 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>이름으로 추가 루트를 찾는다 (대소문자 무시). key는 설정에 적힌 이름.</summary>
    public bool TryGetExtraRoot(string name, out string key, out string path)
    {
        foreach (var (k, v) in ExtraRoots)
        {
            if (!string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) continue;
            key = k;
            path = v;
            return true;
        }
        key = path = "";
        return false;
    }
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
    /// 하루(로컬 날짜 기준) 비용 상한 (USD, Pricing 단가로 계산). 0이면 끔. 토큰 상한과 함께 쓰면 먼저 닿는 쪽에서 거절한다.
    /// 켜져 있으면 단가가 없는 모델은 실행하지 않는다 (FreeProviders 제외) — 비용이 집계에서 새지 않게.
    /// </summary>
    public decimal DailyCostLimitUsd { get; set; }

    /// <summary>
    /// run_mode 입력 최대 글자 수 (input + context로 붙인 코드 합계). 넘으면 실행하지 않고 줄여달라고 돌려준다.
    /// 모드별로 덮어쓸 수 있음. Debate는 토론자가 매 턴 요청 전체를 다시 받으므로 실제 비용은 턴 수만큼 곱해진다.
    /// </summary>
    public int MaxInputChars { get; set; } = 350_000;

    /// <summary>MaxRunsPerSession을 정하지 않은 모드의 세션당 실행 한도. 0이면 무제한.</summary>
    public int DefaultMaxRunsPerSession { get; set; } = 5;

    /// <summary>예산 집계에서 뺄 제공사 (무료인 로컬 등).</summary>
    public List<string> FreeProviders { get; set; } = ["local"];

    /// <summary>상한에 세는 토큰 (입력+출력, FreeProviders 제외). 기간은 호출하는 쪽이 고른다. 서버 검사와 대시보드가 같은 식을 쓴다.</summary>
    public long CountedTokens(IEnumerable<CallRecord> calls) =>
        Counted(calls).Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0));

    /// <summary>상한에 세는 비용 (USD, FreeProviders 제외). 단가가 없는 모델의 기록은 0으로 센다 (상한이 켜져 있으면 애초에 실행되지 않는다).</summary>
    public decimal CountedCost(IEnumerable<CallRecord> calls, PricingOptions pricing) =>
        Counted(calls).Sum(c => pricing.Models.TryGetValue(c.Model, out var p) ? p.CostOf(c) : 0m);

    public bool IsFree(string? provider) =>
        provider is not null && FreeProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<CallRecord> Counted(IEnumerable<CallRecord> calls) => calls.Where(c => !IsFree(c.Provider));
}
