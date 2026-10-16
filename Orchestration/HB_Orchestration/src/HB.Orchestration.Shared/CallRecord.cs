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

    /// <summary>MCP 서버의 작업 디렉터리 (= Claude Code를 실행한 프로젝트 루트).</summary>
    public string? ProjectDir { get; init; }

    public required string Prompt { get; init; }
    public string? Response { get; init; }

    /// <summary>입력 토큰 합계 (캐시에 걸린 토큰 포함).</summary>
    public long? InputTokens { get; init; }

    /// <summary>InputTokens 중 제공사 프롬프트 캐시에 걸린 토큰. 제공사가 알려주지 않거나 예전 기록이면 null.</summary>
    public long? CachedInputTokens { get; init; }

    public long? OutputTokens { get; init; }
    public long ElapsedMs { get; init; }

    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>같은 단계 안에서 몇 번째 시도인지 (1부터). 같은 RunId·Step의 시도는 각각 한 줄씩 남는다. 예전 기록은 null(= 1).</summary>
    public int? Attempt { get; init; }

    /// <summary>실패 종류: Timeout · RateLimit · Server · Client · Network, 사용자 취소는 Canceled. 성공이거나 분류할 수 없는 실패면 null.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>응답 검사기 경고 ("[경고] 응답 언어 이탈 감지 …" 등). 경고는 실패가 아니라 응답은 그대로 전달됐다.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>관련 파일. "Assets/Scripts/Player.cs" 또는 "Assets/Scripts/Player.cs:42" 형식.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>서버가 읽어 프롬프트에 첨부한 코드 범위. "Assets/Scripts/Player.cs:40-80" 형식. 내용은 Prompt에 있다.</summary>
    public IReadOnlyList<string> Context { get; init; } = [];
}
