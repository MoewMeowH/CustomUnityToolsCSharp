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

    /// <summary>run_mode images(서버가 읽어 보내는 이미지) 제한.</summary>
    public ImageOptions Images { get; set; } = new();

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

    /// <summary>골든 세트(--golden) 설정. 서버(run_mode)는 쓰지 않는다.</summary>
    public GoldenOptions Golden { get; set; } = new();
}

/// <summary>appsettings.json의 "Orchestration:Golden".</summary>
public sealed class GoldenOptions
{
    /// <summary>사례 폴더 (절대 경로 권장). --dir가 없을 때 쓴다. 비우면 --dir가 필수.</summary>
    public string? Folder { get; set; }
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
    /// 기대하는 응답 언어. 지금은 "ko"만 지원 (코드 블록을 뺀 본문의 일본어 가나 비율, 가나가 적으면 한자 비율을 본다).
    /// 키가 없으면 "ko"(검사 켬), ""이면 검사 끔. "비우면 기존 동작" 원칙의 의도적 예외다:
    /// 경고는 실패가 아니라 기존 실행 결과를 바꾸지 않고, 3장처럼 사람이 대시보드를 보기 전엔 모르는 이탈을 기본으로 잡기 위해서다.
    /// </summary>
    public string? ExpectedLanguage { get; set; } = "ko";

    /// <summary>본문 글자 중 가나 비율(또는 한자 비율)이 이 값 이상이면 경고 (0~1, 기본 0.02 = 2%). 0 이하나 1 초과면 기본값.</summary>
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
    /// 웹검색 1,000회당 USD (OpenAI web_search: 10, 2026-10-19 공식 가격표). 비우면 0 — 검색을 켠 에이전트의 모델에는 넣는다.
    /// 검색으로 가져온 내용 토큰은 입력 토큰에 들어 있어 입력 단가로 계산된다.
    /// </summary>
    public decimal? WebSearchPerThousand { get; set; }

    /// <summary>
    /// 날짜별 단가 변경 (계획서 2026-10-23_단가-적용-시작일). 위의 칸은 처음 단가이고, 기록 시각이 변경의 시작일(UTC 0시) 이후면
    /// 그 변경에 적은 칸만 덮는다. 설정을 그날 바꾸면 이전 기록 비용까지 새 단가로 다시 계산되므로, 단가가 오르내릴 때는 위 칸을 고치지 말고 여기에 더한다.
    /// 날짜를 읽지 못한 변경은 적용하지 않는다 (서버가 설정을 읽을 때 경고 — <see cref="ChangeProblems"/>).
    /// </summary>
    public List<PriceChange> Changes { get; set; } = [];

    /// <summary>시각 <paramref name="at"/>에 적용되는 단가. 시작일 순으로 덮고, 같은 날짜 항목은 목록 순서대로 겹쳐 덮는다.</summary>
    public ModelPrice PriceAt(DateTimeOffset at)
    {
        var price = this;
        foreach (var (from, change) in ParsedChanges())
        {
            if (from > at) break;
            price = new ModelPrice
            {
                InputPerMillion = change.InputPerMillion ?? price.InputPerMillion,
                CachedInputPerMillion = change.CachedInputPerMillion ?? price.CachedInputPerMillion,
                CacheWritePerMillion = change.CacheWritePerMillion ?? price.CacheWritePerMillion,
                OutputPerMillion = change.OutputPerMillion ?? price.OutputPerMillion,
                WebSearchPerThousand = change.WebSearchPerThousand ?? price.WebSearchPerThousand,
            };
        }
        return price;
    }

    /// <summary>날짜를 읽은 변경을 시작일 순으로 (같은 날짜는 목록 순서 유지).</summary>
    public IEnumerable<(DateTimeOffset From, PriceChange Change)> ParsedChanges() =>
        (Changes ?? []).Select(c => (From: PriceChange.ParseFrom(c.From), Change: c))
               .Where(x => x.From is not null)
               .Select(x => (x.From!.Value, x.Change))
               .OrderBy(x => x.Item1);

    /// <summary>
    /// 설정 문제 (날짜 형식 오류 · 같은 날짜 중복 · 단가 칸 없음 · 음수 단가). 경고만 하고 거절하지 않는다 (사용자 결정 2026-10-23).
    /// 단가 칸 없음은 키 이름 오타(바인더가 조용히 무시)를 잡기 위한 것이다.
    /// </summary>
    public IEnumerable<string> ChangeProblems()
    {
        var seen = new HashSet<DateTimeOffset>();
        foreach (var c in Changes ?? [])
        {
            if (PriceChange.ParseFrom(c.From) is not { } from)
                yield return $"From '{c.From}'을(를) 날짜(yyyy-MM-dd, UTC)로 읽지 못해 이 변경은 적용하지 않습니다.";
            else if (!seen.Add(from))
                yield return $"From '{c.From}'이(가) 중복됩니다 (같은 날짜 항목은 목록 순서대로 겹쳐 적용됨).";

            decimal?[] prices = [c.InputPerMillion, c.CachedInputPerMillion, c.CacheWritePerMillion, c.OutputPerMillion, c.WebSearchPerThousand];
            if (prices.All(p => p is null))
                yield return $"From '{c.From}' 변경에 단가 칸이 없습니다 (키 이름 오타 확인 — 바뀌는 것 없음).";
            else if (prices.Any(p => p < 0))
                yield return $"From '{c.From}' 변경에 음수 단가가 있습니다.";
        }
    }

    /// <summary>
    /// 한 기록의 USD 비용. 기록 시각의 단가(<see cref="PriceAt"/>)로 계산한다. 입력 토큰에는 캐시 읽기·쓰기 토큰이 포함되어 있으므로
    /// (입력 - 읽기 - 쓰기) × 입력 단가 + 읽기 × 캐시 단가 + 쓰기 × 캐시 쓰기 단가 + 출력 × 출력 단가.
    /// 쓰기 토큰을 기록하기 전의 호출(CacheWriteTokens null)은 쓰기 0으로 계산한다.
    /// 웹검색은 search 동작 수(WebSearchRecord.BilledCalls, 없으면 모든 동작 수)로 계산한다.
    /// </summary>
    public decimal CostOf(CallRecord r) => Changes is not { Count: > 0 } ? BaseCostOf(r) : PriceAt(r.Timestamp).BaseCostOf(r);

    private decimal BaseCostOf(CallRecord r)
    {
        var input = r.InputTokens ?? 0;
        var cached = Math.Clamp(r.CachedInputTokens ?? 0, 0, input);
        var written = Math.Clamp(r.CacheWriteTokens ?? 0, 0, input - cached);
        return (input - cached - written) / 1_000_000m * InputPerMillion
             + cached / 1_000_000m * (CachedInputPerMillion ?? InputPerMillion)
             + written / 1_000_000m * (CacheWritePerMillion ?? InputPerMillion)
             + (r.OutputTokens ?? 0) / 1_000_000m * OutputPerMillion
             + (r.WebSearch?.BilledCalls ?? 0) / 1_000m * (WebSearchPerThousand ?? 0);
    }
}

/// <summary>
/// <see cref="ModelPrice.Changes"/>의 한 항목. 적은 칸만 바뀌고 비운 칸은 이전 단가를 이어받는다.
/// From은 문자열로 받는다: 설정 바인더가 날짜를 DateTimeOffset으로 바꾸면 이 PC의 지역 시간 0시가 되기 때문.
/// </summary>
public sealed class PriceChange
{
    /// <summary>적용 시작일 "yyyy-MM-dd". 그날 UTC 0시부터 적용한다.</summary>
    public string? From { get; set; }

    public string? Note { get; set; }
    public decimal? InputPerMillion { get; set; }
    public decimal? CachedInputPerMillion { get; set; }
    public decimal? CacheWritePerMillion { get; set; }
    public decimal? OutputPerMillion { get; set; }
    public decimal? WebSearchPerThousand { get; set; }

    public static DateTimeOffset? ParseFrom(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
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
    /// save_result가 쓰지 않을 파일을 추가한다 (쓰기에만 적용, 형식은 DenyPatterns와 같음).
    /// <see cref="BuiltInWriteDenyPatterns"/>와 DenyPatterns(비밀 파일)는 항상 함께 적용된다.
    /// </summary>
    public List<string> WriteDenyPatterns { get; set; } = [];

    /// <summary>
    /// 쓰기 전용 내장 금지 목록: 외부 LLM 응답이 실행·설정에 영향을 주는 파일로 들어가지 않게 한다 (계획서 2026-10-19_save_result-쓰기-보강).
    /// Claude Code·에이전트 설정, 저장소·CI, IDE 실행 설정, 빌드·패키지, 실행 파일·스크립트, Unity 컴파일 대상.
    /// 응답 코드는 docs/·review_tmp/ 등에 저장한 뒤 지휘자·사람이 합친다.
    /// </summary>
    /// 폴더 기준 패턴은 "**/"로 시작해 프로젝트 루트 아래 어느 깊이에서든 걸린다 (Unity 프로젝트가 작업 폴더의 하위 폴더일 수 있음 — /cross-review 1b661e26fe).
    public static readonly IReadOnlyList<string> BuiltInWriteDenyPatterns =
    [
        // Claude Code·에이전트 설정과 지침
        "**/.claude/**", ".mcp.json", "CLAUDE.md", "CLAUDE.local.md", "REVIEW_GUIDELINES.md", "hb-modes.json",
        "AGENTS.md", "GEMINI.md", "**/.cursor/**",
        // 모델별 프롬프트 덧붙임 (modes.json 옆 prompts/<역할>/<에이전트>.md — 계획서 2026-10-23_모델별-프롬프트-덧붙임)
        "**/prompts/**",
        // 저장소·CI·git 훅
        "**/.github/**", ".gitattributes", ".gitmodules", "**/.husky/**", "**/.githooks/**", ".pre-commit-config.yaml",
        // IDE 실행 설정
        "**/.vscode/**", "**/.idea/**",
        // 빌드·패키지
        "*.csproj", "*.sln", "*.slnx", "*.props", "*.targets", "global.json", "nuget.config", "package.json",
        "**/Packages/manifest.json", "**/Packages/packages-lock.json", "**/ProjectSettings/**",
        // 실행 파일·스크립트 (Windows Script Host 포함)
        "*.ps1", "*.psm1", "*.psd1", "*.bat", "*.cmd", "*.sh", "*.py", "*.exe", "*.dll", "*.com", "*.scr", "*.msi",
        "*.vbs", "*.vbe", "*.js", "*.jse", "*.wsf", "*.wsh", "*.hta", "*.reg", "*.lnk",
        // Unity 컴파일 대상 (csc.rsp는 컴파일러 인수)
        "**/Assets/**/*.cs", "**/Packages/**/*.cs", "**/Assets/**/*.asmdef", "**/Assets/**/*.asmref",
        "**/Packages/**/*.asmdef", "**/Packages/**/*.asmref", "*.rsp",
    ];

    public IEnumerable<string> AllWriteDenyPatterns => BuiltInWriteDenyPatterns.Concat(WriteDenyPatterns);

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

/// <summary>
/// run_mode images 제한. 서버는 이미지를 줄이지 않으므로 축소는 image_prep.py shrink가 하고, 여기서는 넘친 것을 거절만 한다.
/// 이미지는 MaxInputChars(텍스트 글자 수)에 들어가지 않는다. 토큰은 실제 사용량(InputTokens)으로 일일 한도·비용에 잡힌다.
/// </summary>
public sealed class ImageOptions
{
    /// <summary>한 번에 받을 장수 상한. 0이면 무제한.</summary>
    public int MaxCount { get; set; } = 20;

    /// <summary>장당 파일 크기 상한(바이트). 0이면 무제한.</summary>
    public long MaxBytes { get; set; } = 2_000_000;

    /// <summary>긴 변 상한(px). 넘으면 축소를 빠뜨린 것으로 보고 거절한다. 0이면 무제한.</summary>
    public int MaxDimension { get; set; } = 1024;
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

    /// <summary>
    /// 프롬프트 캐시 방식. OpenAI Chat Completions 요청 본문에만 적용한다 (다른 제공사는 모르는 필드에 오류를 낼 수 있음).
    /// 비우면 ProviderDefault = 손대지 않음.
    /// </summary>
    public PromptCacheMode PromptCache { get; set; } = PromptCacheMode.ProviderDefault;

    /// <summary>
    /// run_mode images의 이미지 부분에 실을 detail 값 (OpenAI: auto · low · high · original). 비우면 보내지 않는다 (= 제공사 기본값).
    /// 제공사 단위인 이유: Gemini OpenAI 호환 엔드포인트는 이 필드를 문서화하지 않았다.
    /// </summary>
    public string? ImageDetail { get; set; }

    /// <summary>
    /// 구조화 출력(역할 Output)을 요청하는 방식. 에이전트에 값이 없을 때 쓴다. 비우면 Prompt.
    /// Gemini OpenAI 호환 엔드포인트의 json_schema 지원은 문서에 없어(2026-10-19) 실제 호출로 확인한 뒤 Schema로 바꾼다.
    /// </summary>
    public StructuredOutputMode? StructuredOutput { get; set; }
}

/// <summary>구조화 출력 요청 방식.</summary>
public enum StructuredOutputMode
{
    /// <summary>시스템 프롬프트에만 JSON 형식을 적는다 (response_format 없음).</summary>
    Prompt,

    /// <summary>프롬프트 + response_format json_schema(strict)로 강제한다. 미지원이면 400 → 실패 (자동 재전송 없음).</summary>
    Schema,
}

/// <summary>프롬프트 캐시 방식. 호출 단위로 정해져 HTTP 정책(PromptCachePolicy)이 요청 본문에 반영한다.</summary>
public enum PromptCacheMode
{
    /// <summary>제공사 기본 동작 (OpenAI: 입력 전부를 캐시에 쓰고 1.25배 청구).</summary>
    ProviderDefault,

    /// <summary>캐시 쓰기 안 함 (explicit 모드, 저장 지점 없음). 읽기 할인도 없다.</summary>
    Off,

    /// <summary>첨부 자료 끝에만 저장 지점 (explicit 모드). 같은 자료로 30분 안에 다시 물으면 자료 부분을 읽기로 처리.</summary>
    Context,
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

    /// <summary>이미지 입력을 받는 모델인지. false인 에이전트가 낀 모드에 images가 오면 실행하지 않는다 (로컬 텍스트 모델 등).</summary>
    public bool Vision { get; set; } = true;

    /// <summary>구조화 출력 요청 방식. 비우면 제공사 값 → Prompt 순 (같은 제공사라도 모델마다 지원이 다를 수 있어 에이전트 단위로 둔다).</summary>
    public StructuredOutputMode? StructuredOutput { get; set; }

    /// <summary>
    /// 추론 강도: none · low · medium · high · xhigh (대소문자 무시). 비우면 보내지 않는다 (제공사·모델 기본값).
    /// 제공사 단위 기본값은 두지 않는다 — 같은 제공사라도 모델마다 받는 값이 다르다 (계획서 2026-10-20_추론강도-설정).
    /// 실행마다 run_mode reasoning · 골든 --reasoning으로 덮어쓸 수 있다.
    /// </summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// 입력 상한 · 사용량 집계. 하루 토큰·비용 상한(DailyTokenLimit · DailyCostLimitUsd)은 없앴다
/// (계획서 2026-10-20_하루상한-제거 — 하루에 몰아 쓰는 사용 방식, 제공사 계정 한도가 마지막 방어선).
/// 세션당 실행 한도(DefaultMaxRunsPerSession · 모드 MaxRunsPerSession)도 없앴다 (계획서 2026-10-20_모드이름-통일-세션한도-제거).
/// 예전 appsettings·modes.json에 키가 남아 있어도 무시된다.
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>
    /// run_mode 입력 최대 글자 수 (input + context로 붙인 코드 합계). 넘으면 실행하지 않고 줄여달라고 돌려준다.
    /// 모드별로 덮어쓸 수 있음. Debate는 토론자가 매 턴 요청 전체를 다시 받으므로 실제 비용은 턴 수만큼 곱해진다.
    /// </summary>
    public int MaxInputChars { get; set; } = 350_000;

    /// <summary>사용량 집계에서 뺄 제공사 (무료인 로컬 등).</summary>
    public List<string> FreeProviders { get; set; } = ["local"];

    /// <summary>사용량으로 세는 토큰 (입력+출력, FreeProviders 제외). 기간은 호출하는 쪽이 고른다 (대시보드 오늘 사용량).</summary>
    public long CountedTokens(IEnumerable<CallRecord> calls) =>
        Counted(calls).Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0));

    /// <summary>사용량으로 세는 비용 (USD, FreeProviders 제외). 단가가 없는 모델의 기록은 0으로 센다 — 그 건수는 <see cref="UnpricedCalls"/>.</summary>
    public decimal CountedCost(IEnumerable<CallRecord> calls, PricingOptions pricing) =>
        Counted(calls).Sum(c => pricing.Models.TryGetValue(c.Model, out var p) ? p.CostOf(c) : 0m);

    /// <summary>비용을 셀 수 없는 기록 수 (FreeProviders 제외, 토큰이 있는데 단가가 없는 모델). 대시보드가 "비용 미집계"로 보인다.</summary>
    public int UnpricedCalls(IEnumerable<CallRecord> calls, PricingOptions pricing) =>
        Counted(calls).Count(c => c.InputTokens is not null && !pricing.Models.ContainsKey(c.Model));

    public bool IsFree(string? provider) =>
        provider is not null && FreeProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<CallRecord> Counted(IEnumerable<CallRecord> calls) => calls.Where(c => !IsFree(c.Provider));
}
