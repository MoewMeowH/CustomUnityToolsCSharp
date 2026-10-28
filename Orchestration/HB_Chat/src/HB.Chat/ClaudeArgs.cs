using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// 실행 하나의 입력. Resume이면 --resume, 아니면 --session-id로 새 세션을 앱이 정한 ID로 만든다.
/// McpConfigPath는 읽기 · 편집 · 명령 단계에 필요하다 (hb-orchestration 항목만 담은 파일, <see cref="McpConfigFile"/>).
/// AllowRules는 명령 단계에만 쓴다: "이 방에서 항상 허용" 규칙.
/// </summary>
public sealed record RunSpec(
    PermissionLevel Level,
    string Model,
    string SessionId,
    bool Resume,
    string? McpConfigPath,
    string McpServer,
    string? AppendSystemPrompt,
    IReadOnlyList<string>? AllowRules = null)
{
    /// <summary>갈래 방의 첫 실행: 이어받을 원래 세션 ID (Resume이 아닐 때만 쓴다, 계획서 2-2 c).</summary>
    public string? ForkFrom { get; init; }

    /// <summary>그 방 첨부 폴더 (절대 경로, 계획서 2-2 d). 대화 단계가 아니면 --add-dir로 더해 Read로 열 수 있게 한다.</summary>
    public string? AttachDir { get; init; }
}

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
            // 입력도 stream-json (SDK식): 사용자 말 · 승인 답(control_response)을 stdin으로 보낸다 (3차 a, <see cref="ClaudeRunner"/>)
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--include-partial-messages", "--verbose",
            "--model", spec.Model,
        };
        // 첨부 폴더: 작업 폴더 밖이라 없으면 Read가 거절된다 (2-2 0번 실측). 대화 단계는 도구가 없어 넣지 않는다 (사용자 결정 2).
        // --add-dir는 여러 값을 받으므로 바로 뒤에 다른 플래그가 온다.
        string? attachDeny = null;
        if (spec.AttachDir is { } attach && spec.Level != PermissionLevel.Chat)
        {
            if (!Path.IsPathFullyQualified(attach)) throw new ArgumentException($"첨부 폴더는 절대 경로여야 한다: {attach}");
            args.AddRange(["--add-dir", attach]);
            // 편집 · 명령 방은 그 폴더 편집을 막는다: 추가 폴더의 .claude/skills를 CLI가 불러와, 써 두면 다음 턴부터 실린다
            // (/self-review 0c3b7e3514 C2 실측). 편집 금지 목록 끝에 붙는다.
            attachDeny = $"Edit({AbsoluteRulePath(attach)}/**)";
        }
        if (spec.Resume) args.AddRange(["--resume", spec.SessionId]);
        else if (spec.ForkFrom is { } source)
        {
            // 갈래: 원래 세션을 이어받아 이 방의 ID로 새 세션을 만든다 (2-2 0번 실측 — init · result 모두 그 ID).
            if (!Guid.TryParseExact(source, "D", out _) || string.Equals(source, spec.SessionId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"갈래의 원래 세션 ID가 올바르지 않다: {source}");
            args.AddRange(["--resume", source, "--fork-session", "--session-id", spec.SessionId]);
        }
        else args.AddRange(["--session-id", spec.SessionId]);
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
                args.AddRange(["--tools", "Read,Grep,Glob," + HostTools]);
                AddMcp(args, spec);
                break;
            case PermissionLevel.Edit:
                args.AddRange(["--tools", "Read,Grep,Glob,Edit,Write," + HostTools, "--permission-mode", "acceptEdits"]);
                args.Add("--disallowedTools");
                args.AddRange(EditDenied.Select(p => $"Edit({p})"));
                if (attachDeny is not null) args.Add(attachDeny);
                AddMcp(args, spec);
                break;
            case PermissionLevel.Command:
                // 편집 단계 + 명령 도구. 명령은 stdio 호스트(앱)의 승인 카드로 묻는다 (3차 a — 그 전에는 PermissionRequest HTTP 훅).
                // acceptEdits는 쓰지 않는다: 그 모드는 작업 폴더 안 cp · mv 같은 파일 명령을 묻지 않고 허용해, 편집 금지 규칙(Edit 도구에만 걸림)과
                // 카드를 둘 다 지나치게 한다 (/self-review 36e4e20e04 C1). 대신 작업 폴더 안 편집만 `Edit(**)`로 미리 허용한다 —
                // 실측(2026-10-28): 작업 폴더 안 Write는 묻지 않음, 밖 Write는 거절, Bash cp는 승인 요청으로 옴.
                args.AddRange(["--tools", "Read,Grep,Glob,Edit,Write,Bash,PowerShell," + HostTools]);
                args.Add("--disallowedTools");
                args.AddRange(EditDenied.Select(p => $"Edit({p})"));
                if (attachDeny is not null) args.Add(attachDeny);
                AddMcp(args, spec);
                // --allowedTools 가변 인자(AddMcp의 MCP 규칙 뒤)에 작업 폴더 안 편집과 "항상 허용" 규칙을 이어 붙인다. 규칙 모양은 저장할 때 검사했지만 다시 거른다.
                args.Add(CommandEditAllow);
                args.AddRange((spec.AllowRules ?? []).Where(ApprovalRules.IsAcceptable));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.Level, "알 수 없는 권한 단계");
        }
        // 승인 요청은 모두 앱(stdio 호스트)이 답한다 (3차 a): 명령 방의 명령 도구는 승인 카드, AskUserQuestion은 질문 카드, 그 밖은 바로 거절
        // (그 전의 --permission-prompts none과 같은 동작). PermissionRequest 훅은 붙이지 않는다 — 호스트가 있으면 훅의 거절을 호스트의 허용이 이긴다
        // (3차 0번 실측). 프로젝트 PreToolUse 훅(H1 등)은 호스트에 묻기 전에 그대로 막는다 (같은 실측).
        args.AddRange(["--permission-prompt-tool", "stdio"]);

        foreach (var a in args)
            if (DangerousArg().IsMatch(a)) throw new InvalidOperationException($"금지된 인자가 만들어졌다: {a}");
        return args;
    }

    /// <summary>
    /// 권한 규칙의 절대 경로 모양: <c>C:\Users\x</c> → <c>//c/Users/x</c> (드라이브 소문자 · 콜론 없음).
    /// 2-2 d 실측(2.1.311): <c>Edit(//c/…/**)</c>는 듣고 <c>Edit(//C:/…/**)</c>는 안 듣는다. 드라이브가 없는 경로는 <c>//</c> + 경로.
    /// </summary>
    public static string AbsoluteRulePath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        return full.Length >= 2 && char.IsAsciiLetter(full[0]) && full[1] == ':'
            ? "//" + char.ToLowerInvariant(full[0]) + full[2..]
            : "/" + full;
    }

    /// <summary>명령 단계에서 작업 폴더 안 편집을 묻지 않게 하는 규칙 (Edit 규칙은 Write 도구에도 걸린다 — 2026-10-28 실측).</summary>
    public const string CommandEditAllow = "Edit(**)";

    /// <summary>명령 방에서 승인 카드로 받는 도구. 그 밖의 승인 요청(작업 폴더 밖 쓰기 등)은 카드 없이 거절한다 (/self-review 36e4e20e04 C2 · ab79e61df5 C4).</summary>
    public static readonly IReadOnlySet<string> CommandTools = new HashSet<string>(StringComparer.Ordinal) { "Bash", "PowerShell" };

    /// <summary>지휘자의 객관식 질문 도구 (3차 a). SDK식 실행(stdio 호스트)에서만 도구 목록에 들어온다 (3차 0번 실측 A).</summary>
    public const string AskTool = "AskUserQuestion";

    /// <summary>
    /// 질문 + 할 일 도구 (3차 b — 진행 단계). 2.1.312에는 TodoWrite가 없고 Task* 넷이 승인 없이 돈다 (0번 실측 B). 상태는 ~/.claude/tasks에만 쓴다.
    /// 읽기 · 편집 · 명령 단계의 --tools 끝에 붙는다.
    /// </summary>
    public const string HostTools = AskTool + ",TaskCreate,TaskGet,TaskList,TaskUpdate";

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
