using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// 실행 하나의 입력. Resume이면 --resume, 아니면 --session-id로 새 세션을 앱이 정한 ID로 만든다.
/// McpConfigPath는 읽기 · 편집 단계에 필요하다 (hb-orchestration 항목만 담은 파일, <see cref="McpConfigFile"/>).
/// </summary>
public sealed record RunSpec(
    PermissionLevel Level,
    string Model,
    string SessionId,
    bool Resume,
    string? McpConfigPath,
    string McpServer,
    string? AppendSystemPrompt);

/// <summary>
/// 권한 단계 → claude 인자 (계획서 4번 표 + 0번 실측: 읽기 · 편집도 --strict-mcp-config로 claude.ai 커넥터 · 사용자 MCP를 막는다).
/// 인자는 이 표에서만 만든다. 위험 우회 인자(dangerously · bypass)가 생기면 예외 (CodePet assertSafeArgv와 같은 장치).
/// </summary>
public static partial class ClaudeArgs
{
    /// <summary>
    /// 편집 단계에서 Edit · Write를 막는 경로 (작업 폴더 기준 gitignore 모양, /self-review security b19234c862 C1).
    /// 고치면 다음 실행에 명령 실행 · 권한 확장 · 다른 세션 지시 변경으로 이어지는 파일들: 이 앱 자신(설정 · 코드), MCP 서버 게시 폴더(설정 · modes.json),
    /// 모든 appsettings, 지휘자 지시 파일, 스크립트. `.claude/` · `.mcp.json`은 CLI가 보호 경로로 따로 막지만(문서) 겹쳐 둔다.
    /// Edit(…) 규칙은 Write 도구에도 걸린다 (2026-10-28 실측, 2.1.311).
    /// </summary>
    public static readonly IReadOnlyList<string> EditDenied =
    [
        "Orchestration/HB_Chat/**", "Orchestration/Publish_Output/**", "**/appsettings*.json",
        "**/.claude/**", "**/.mcp*.json",
        "**/CLAUDE.md", "**/CLAUDE.local.md", "**/PROJECT.md", "**/MISTAKES.md", "**/REVIEW_GUIDELINES.md",
        "**/*.bat", "**/*.cmd", "**/*.ps1",
    ];

    /// <summary>모델 · MCP 서버 이름에 쓸 수 있는 모양인지 (인자 · 파일 이름에 들어간다).</summary>
    public static bool IsSafeName(string? value) => value is not null && SafeOption().IsMatch(value);

    public static IReadOnlyList<string> Build(RunSpec spec, IReadOnlyCollection<string> allowedModels)
    {
        if (!allowedModels.Contains(spec.Model, StringComparer.Ordinal) || !IsSafeName(spec.Model))
            throw new ArgumentException($"허용 목록에 없는 모델: {spec.Model}");
        if (!Guid.TryParseExact(spec.SessionId, "D", out _))
            throw new ArgumentException($"세션 ID가 uuid 모양이 아니다: {spec.SessionId}");
        if (!IsSafeName(spec.McpServer))
            throw new ArgumentException($"MCP 서버 이름 형식이 올바르지 않다: {spec.McpServer}");

        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--include-partial-messages", "--verbose",
            "--model", spec.Model,
        };
        args.AddRange(spec.Resume ? ["--resume", spec.SessionId] : ["--session-id", spec.SessionId]);
        // 시스템 프롬프트는 첫 요청 때 세션에 기록되고 압축 전까지는 resume해도 기록된 값이 쓰인다. 그 뒤로는 그 실행에 준 값이 쓰인다 (문서)
        // → 방 규칙은 매 말 같은 값을 넣는다. 안 넣으면 압축 뒤 규칙이 빠진다 (/self-review 0a3b1d5155 C2).
        if (!string.IsNullOrWhiteSpace(spec.AppendSystemPrompt))
            args.AddRange(["--append-system-prompt", spec.AppendSystemPrompt]);

        switch (spec.Level)
        {
            case PermissionLevel.Chat:
                // --tools ""여도 MCP 도구는 남는다 (0번 실측 C) → MCP는 --strict-mcp-config로 하나도 안 붙인다.
                args.AddRange(["--tools", "", "--strict-mcp-config"]);
                break;
            case PermissionLevel.Read:
                args.AddRange(["--tools", "Read,Grep,Glob"]);
                AddMcp(args, spec);
                break;
            case PermissionLevel.Edit:
                args.AddRange(["--tools", "Read,Grep,Glob,Edit,Write", "--permission-mode", "acceptEdits"]);
                args.Add("--disallowedTools");
                args.AddRange(EditDenied.Select(p => $"Edit({p})"));
                AddMcp(args, spec);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.Level, "알 수 없는 권한 단계");
        }
        // 승인 창이 없다 (2차). 승인이 필요한 호출은 묻지 않고 거절하고, Claude에게 다시 시도하지 말라고 알린다.
        args.AddRange(["--permission-prompts", "none"]);

        foreach (var a in args)
            if (DangerousArg().IsMatch(a)) throw new InvalidOperationException($"금지된 인자가 만들어졌다: {a}");
        return args;
    }

    private static void AddMcp(List<string> args, RunSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.McpConfigPath))
            throw new ArgumentException($"{spec.Level} 단계에는 MCP 설정 파일이 필요하다");
        args.AddRange(["--strict-mcp-config", "--mcp-config", spec.McpConfigPath, "--allowedTools", $"mcp__{spec.McpServer}"]);
    }

    /// <summary>모델 · 서버 이름: 영숫자로 시작, 영숫자 . _ : / - 만 64자까지.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,63}$")]
    private static partial Regex SafeOption();

    [GeneratedRegex("dangerously|bypass", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousArg();
}
