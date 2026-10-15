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

    /// <summary>관련 파일. "Assets/Scripts/Player.cs" 또는 "Assets/Scripts/Player.cs:42" 형식.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>서버가 읽어 프롬프트에 첨부한 코드 범위. "Assets/Scripts/Player.cs:40-80" 형식. 내용은 Prompt에 있다.</summary>
    public IReadOnlyList<string> Context { get; init; } = [];
}
