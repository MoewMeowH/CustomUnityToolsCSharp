using System.Text.Json.Serialization;

namespace HB.Orchestration.Shared;

/// <summary>구조화 출력의 해석 상태. 지휘자에게 "문제 없음"을 보이는 것은 Complete뿐이다.</summary>
public static class OutputStates
{
    /// <summary>종료 사유 정상, 검증 통과, 자료 부족 없음.</summary>
    public const string Complete = "complete";

    /// <summary>검증 통과, 자료 부족(needsInfo)이 있음 → 검토 불완전.</summary>
    public const string NeedsInfo = "needs_info";

    /// <summary>출력 상한 잘림·거절인데 JSON은 읽힘 → 일부 지적만 있을 수 있음.</summary>
    public const string Incomplete = "incomplete";

    /// <summary>JSON이 아니거나 검증 실패 → 원문을 그대로 전달.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// 구조화 출력(역할 Output) 해석 결과. 기록 시점에 한 번만 해석하고 다시 해석하지 않는다 (지적 ID가 바뀌지 않도록).
/// 원문은 CallRecord.Response에 그대로 있다.
/// </summary>
public sealed record StructuredOutputRecord
{
    /// <summary>"findings@1" · "perf_findings@1" (형식 이름 @ 스키마 버전).</summary>
    public required string Format { get; init; }

    /// <summary><see cref="OutputStates"/> 값.</summary>
    public required string State { get; init; }

    /// <summary>해석 실패·불완전의 사유. 그 밖에는 null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>요청 방식: "schema"(response_format 강제) 또는 "prompt"(프롬프트로만 요청).</summary>
    public required string RequestedBy { get; init; }

    public IReadOnlyList<FindingRecord> Findings { get; init; } = [];
    public IReadOnlyList<string> NeedsInfo { get; init; } = [];

    /// <summary>perf_findings 전용: 사양에 따라 달라지는 항목 · 수정 방향 · 측정 우선순위.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SpecDependent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FixDirection { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MeasurePriority { get; init; }

    public bool Parsed => State != OutputStates.Failed;
}

/// <summary>지적 하나. Id는 서버가 매긴 "단계-F순번" (예: 1-F2). 실행(RunId) 안에서만 유일하다.</summary>
public sealed record FindingRecord
{
    public required string Id { get; init; }

    /// <summary>findings: critical · major · minor · nit / perf_findings: high · medium · low.</summary>
    public required string Severity { get; init; }

    public string? File { get; init; }
    public int? Line { get; init; }
    public required string Problem { get; init; }
    public string? Detail { get; init; }
    public string? Suggestion { get; init; }

    /// <summary>high · medium · low.</summary>
    public required string Confidence { get; init; }

    /// <summary>perf_findings 전용: 빈도(매 프레임·매 요청 등), 규모에 따른 증가, 측정 방법.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Frequency { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scaling { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Measurement { get; init; }

    /// <summary>"path:line" 또는 "path" 또는 null.</summary>
    [JsonIgnore]
    public string? Location => File is null ? null : Line is { } l ? $"{File}:{l}" : File;
}
