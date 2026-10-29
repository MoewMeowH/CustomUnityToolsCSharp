using System.Text.Json.Serialization;

namespace HB.Orchestration.Shared;

/// <summary>
/// 이 호출의 에이전트가 어떤 지침·설정으로 실행됐는지. 해시는 SHA-256(소문자 hex), 원문은 <see cref="BlobFolder"/>에
/// 해시 이름으로 한 번만 저장한다 (서버 설정 제외). 원문 저장이 실패해도 호출은 진행하고 <see cref="StoreErrors"/>에 남긴다.
/// </summary>
public sealed record CallProvenance
{
    /// <summary>에이전트에 넘긴 시스템 프롬프트(Instructions) 전체의 UTF-8(BOM 없음) 바이트 해시.</summary>
    public string? InstructionsSha256 { get; init; }
    public long? InstructionsBytes { get; init; }

    public GuidelinesProvenance? Guidelines { get; init; }

    /// <summary>
    /// 이 단계의 모델별 덧붙임 파일 (prompts/&lt;역할&gt;/&lt;에이전트&gt;.md, 원본 바이트 해시). 붙이지 않았으면 null
    ///.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileProvenance? Append { get; init; }

    /// <summary>실행 시점 modes.json 원본 바이트.</summary>
    public FileProvenance? Modes { get; init; }

    /// <summary>실행 시점 서버 설정(appsettings.json) 원본 바이트. 기본은 해시만 남긴다.</summary>
    public SettingsProvenance? Settings { get; init; }

    /// <summary>원문 저장 폴더 (절대 경로).</summary>
    public string? BlobFolder { get; init; }

    /// <summary>해시 계산·원문 저장 실패. 없으면 필드를 남기지 않는다.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StoreErrors { get; init; }
}

/// <summary>리뷰 지침 파일. 붙이지 않았으면 Attached=false와 Reason.</summary>
public sealed record GuidelinesProvenance
{
    public const string ReasonRoleDisabled = "역할 UseGuidelines=false";
    public const string ReasonNoFile = "지침 파일 없음";
    public const string ReasonGoldenSkipped = "골든 --no-guidelines";

    public bool Attached { get; init; }
    public string? Path { get; init; }

    /// <summary>지침 파일 원본 바이트 해시 (sha256sum과 대조 가능).</summary>
    public string? Sha256 { get; init; }
    public long? Bytes { get; init; }
    public string? Reason { get; init; }
}

public sealed record FileProvenance
{
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public long? Bytes { get; init; }
}

public sealed record SettingsProvenance
{
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public long? Bytes { get; init; }

    /// <summary>원문을 저장했는지. 기본 false (Records.StoreSettingsText).</summary>
    public bool Stored { get; init; }

    /// <summary>키 패턴 검사 결과: "통과" 또는 "의심 n건". 읽지 못했으면 null.</summary>
    public string? SecretScan { get; init; }

    /// <summary>appsettings.json 다시 읽기가 실패해 이전 설정으로 동작 중이었다면 그 이유 (파일 해시와 실제 적용 값이 다를 수 있음).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReloadError { get; init; }
}
