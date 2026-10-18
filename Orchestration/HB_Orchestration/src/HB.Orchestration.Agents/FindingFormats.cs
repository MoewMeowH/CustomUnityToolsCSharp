using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Orchestration.Agents;

/// <summary>
/// 역할 Output으로 고르는 구조화 출력 형식 (계획서 2026-10-19_리뷰-구조화-출력). 스키마는 코드에 둔다:
/// strict 스키마 규칙(모든 필드 required, additionalProperties false)과 서버의 해석·그리기 코드가 어긋나지 않도록.
/// 스키마를 바꾸면 Version을 올린다 (기록의 Format에 남아 예전 기록과 구별된다).
/// </summary>
public sealed record FindingFormat(string Name, int Version, IReadOnlyList<string> Severities, bool Perf)
{
    public string Tag => $"{Name}@{Version}";

    /// <summary>Reviewer: 일반 코드 리뷰.</summary>
    public static readonly FindingFormat Findings = new("findings", 1, ["critical", "major", "minor", "nit"], Perf: false);

    /// <summary>PerfReviewer: 성능 검토 (빈도·증가·측정과 끝 절 3개).</summary>
    public static readonly FindingFormat PerfFindings = new("perf_findings", 1, ["high", "medium", "low"], Perf: true);

    public static readonly IReadOnlyList<string> Confidences = ["high", "medium", "low"];

    private static readonly IReadOnlyDictionary<string, FindingFormat> ByName =
        new Dictionary<string, FindingFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [Findings.Name] = Findings,
            [PerfFindings.Name] = PerfFindings,
        };

    public static FindingFormat? Find(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim(), out var f) ? f : null;

    public static string KnownNames => string.Join(", ", ByName.Keys);

    /// <summary>시스템 프롬프트 끝에 붙는 출력 형식 설명. Schema·Prompt 방식 모두 붙인다 (강제가 안 되는 제공사도 따르도록).</summary>
    public string PromptText => Perf ? PerfPrompt : FindingsPrompt;

    private const string FindingsPrompt = """

        ## 출력 형식 (JSON)
        JSON 객체 하나만 출력한다. 코드 펜스, 앞뒤 설명 문장을 붙이지 않는다.
        - findings: 지적 배열. 없으면 [].
          - severity: critical · major · minor · nit 중 하나. nit(스타일 취향)은 최대 3개.
          - file: 첨부 코드의 경로 그대로. 모르면 null.
          - line: 시작 줄 번호 (정수). 모르면 null.
          - problem: 문제 한 줄.
          - detail: 발생 조건 · 원인 · 영향. problem을 반복하지 않는다.
          - suggestion: 제안. 없으면 null.
          - confidence: high · medium · low 중 하나.
        - needsInfo: 자료가 부족해 판단하지 못한 것마다 필요한 코드나 정보를 한 줄로. 없으면 [].
        - 총 건수 요약은 쓰지 않는다 (서버가 계산한다).
        """;

    private const string PerfPrompt = """

        ## 출력 형식 (JSON)
        JSON 객체 하나만 출력한다. 코드 펜스, 앞뒤 설명 문장을 붙이지 않는다.
        - findings: 지적 배열. 없으면 [].
          - severity: high · medium · low 중 하나 ('빈도 × 한 번의 비용'으로 정한다).
          - file: 첨부 코드의 경로 그대로. 모르면 null.
          - line: 시작 줄 번호 (정수). 모르면 null.
          - problem: 문제 한 줄.
          - detail: 원인과 영향. problem을 반복하지 않는다.
          - suggestion: 수정 방향. 없으면 null.
          - confidence: high · medium · low 중 하나.
          - frequency: 매 프레임 · 매 요청 · 매 호출 · 초기화 1회 등.
          - scaling: 데이터 규모(N)가 커질 때 어떻게 늘어나는지.
          - measurement: 무엇을 어떻게 측정하면 확인되는지.
        - needsInfo: 자료가 부족해 판단하지 못한 것마다 필요한 코드나 정보를 한 줄로. 없으면 [].
        - specDependent: 사양에 따라 달라지는 항목 (예: 데이터 규모, 대상 플랫폼). 없으면 [].
        - fixDirection: 수정 방향 (핵심 변경만). 없으면 [].
        - measurePriority: 무엇부터 재야 하는지 최대 3개. 없으면 [].
        - 총 건수 요약은 쓰지 않는다 (서버가 계산한다).
        """;

    /// <summary>OpenAI strict json_schema 규칙을 따르는 스키마 (모든 속성 required, 선택 값은 null 허용 타입).</summary>
    public JsonElement Schema()
    {
        static JsonObject Str() => new() { ["type"] = "string" };
        static JsonObject NullableStr() => new() { ["type"] = new JsonArray("string", "null") };
        static JsonObject Enum(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };
        static JsonObject StrArray() => new() { ["type"] = "array", ["items"] = Str() };
        static JsonObject Obj(JsonObject props) => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray([.. props.Select(p => (JsonNode)p.Key)]),
            ["properties"] = props,
        };

        var item = new JsonObject
        {
            ["severity"] = Enum(Severities),
            ["file"] = NullableStr(),
            ["line"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
            ["problem"] = Str(),
            ["detail"] = Str(),
            ["suggestion"] = NullableStr(),
            ["confidence"] = Enum(Confidences),
        };
        if (Perf)
        {
            item["frequency"] = Str();
            item["scaling"] = Str();
            item["measurement"] = Str();
        }

        var root = new JsonObject
        {
            ["findings"] = new JsonObject { ["type"] = "array", ["items"] = Obj(item) },
            ["needsInfo"] = StrArray(),
        };
        if (Perf)
        {
            root["specDependent"] = StrArray();
            root["fixDirection"] = StrArray();
            root["measurePriority"] = StrArray();
        }
        return JsonSerializer.SerializeToElement(Obj(root));
    }
}
