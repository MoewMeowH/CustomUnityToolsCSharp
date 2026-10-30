using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>방마다 고르는 권한 단계 (인자 표).</summary>
public enum PermissionLevel
{
    /// <summary>작업 폴더 읽기 · 검색 + hb-orchestration MCP.</summary>
    Read,
    /// <summary>읽기 + 작업 폴더 파일 편집 + 명령 실행(Bash · PowerShell). 명령은 실행 전에 방의 카드로 승인한다 (2차).</summary>
    Command,
}

/// <summary>
/// 방 · 단톡방 기록의 단계. 없앤 대화 · 편집 단계는 읽기로 읽는다 — 저장 파일을 그대로 열면서 권한이 말없이 오르지 않게.
/// 모르는 값은 오류로 둔다 (읽지 못한 색인은 덮어쓰지 않는다).
/// </summary>
public sealed class PermissionLevelConverter : System.Text.Json.Serialization.JsonConverter<PermissionLevel>
{
    public override PermissionLevel Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() switch
        {
            "Chat" or "Edit" or nameof(PermissionLevel.Read) => PermissionLevel.Read,
            nameof(PermissionLevel.Command) => PermissionLevel.Command,
            var other => throw new System.Text.Json.JsonException($"알 수 없는 권한 단계: {other}"),
        } : throw new System.Text.Json.JsonException("권한 단계는 글자여야 한다");

    public override void Write(System.Text.Json.Utf8JsonWriter writer, PermissionLevel value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// appsettings.json의 Chat 절. 개인 값은 appsettings.local.json(git 제외)에 둔다.
/// </summary>
public sealed class ChatOptions
{
    /// <summary>claude 실행 파일. 비우면 PATH에서 claude.exe를 찾는다.</summary>
    public string? ClaudePath { get; set; }

    /// <summary>
    /// Codex CLI 실행 파일 (단톡방 Codex 구성원, ②). 비우면 npm 전역 설치의 codex.exe(%APPDATA%\npm\…\vendor\…\codex.exe),
    /// 그다음 PATH의 codex.exe. 못 찾으면 null — Codex 구성원이 "Codex CLI를 찾지 못함"으로 실패한다.
    /// </summary>
    public string? CodexPath { get; set; }

    /// <summary>Codex 홈 폴더 (세션 기록 sessions/ 가 있는 곳). 비우면 CODEX_HOME, 그다음 ~/.codex. 스레드 유실 확인에 쓴다.</summary>
    public string? CodexHome { get; set; }

    public string CodexHomeDir() =>
        !string.IsNullOrWhiteSpace(CodexHome) ? CodexHome.Trim()
        : Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } env ? env
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public string? ResolveCodex()
    {
        if (!string.IsNullOrWhiteSpace(CodexPath)) return CodexPath.Trim();
        // x64 · ARM64 판 (59063d4106 C11). npm이 PATH에 두는 codex.cmd는 cmd를 거쳐야 해서 쓰지 않는다
        foreach (var (package, triple) in new[] { ("codex-win32-x64", "x86_64-pc-windows-msvc"), ("codex-win32-arm64", "aarch64-pc-windows-msvc") })
        {
            var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex",
                "node_modules", "@openai", package, "vendor", triple, "bin", "codex.exe");
            if (File.Exists(npm)) return npm;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>방 색인 · 만든 MCP 설정을 두는 폴더. 비우면 %LOCALAPPDATA%/HB_Chat.</summary>
    public string? DataDir { get; set; }

    /// <summary>Claude 세션 기록 폴더. 비우면 ~/.claude/projects (CLAUDE_CONFIG_DIR가 있으면 그 아래). 지휘자 할 일 폴더는 이 폴더 옆의 tasks로 본다 (3차 b).</summary>
    public string? ClaudeProjectsDir { get; set; }

    /// <summary>작업 폴더 허용 목록 (이름 → 절대 경로). 방은 이 이름으로만 고른다.</summary>
    public Dictionary<string, string> Workspaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>고를 수 있는 모델 (claude --model 값). 설정 바인딩이 목록에 덧붙이므로 기본값은 비워 두고 <see cref="ModelList"/>가 채운다.</summary>
    public List<string> Models { get; set; } = [];

    public string DefaultModel { get; set; } = "opus";

    /// <summary>읽기 · 명령 단계에 붙이는 MCP 서버 이름 (작업 폴더 .mcp.json의 항목).</summary>
    public string McpServer { get; set; } = "hb-orchestration";

    /// <summary>대시보드 주소 (방 화면의 "대시보드에서 보기" 링크). 비우면 링크 없음.</summary>
    public string? DashboardUrl { get; set; }

    /// <summary>
    /// 지휘자 브라우저가 로컬이어도 열지 못하는 포트 — 이 앱 · 대시보드 화면을 열면 같은 출처라 자기 승인 카드를 누를 수 있다
    /// (/self-review security 8b11177bd5 C1). 이 앱이 듣는 포트 · DashboardUrl 포트는 따로 더한다. 기본은 사용자 앱 5290(시험 서버에서도 막히게).
    /// </summary>
    public int[] BrowserBlockedPorts { get; set; } = [5290];

    /// <summary>스티커 설정을 읽을 대시보드 폴더 (appsettings.json · appsettings.local.json의 Dashboard:Stickers). 비우면 글자 배지.</summary>
    public string? StickerSettingsDir { get; set; }

    /// <summary>
    /// 승인 · 질문 카드가 답을 기다리는 시간(초). 넘으면 거절(질문은 답 없음)로 끝낸다. 3차 a부터 승인은 stdio 호스트라 claude 쪽 제한이 없다
    /// (130초 늦은 답도 받음 — 3차 0번 실측) — 이 값만 대기를 정한다. 기본 570초는 2-1의 훅 제한(600초)에 맞췄던 값을 그대로 둔 것.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 570;

    /// <summary>run_mode 호출 기록 (외부 에이전트 말풍선). 비우면 HB_ORCH_LOG_PATH 또는 %LOCALAPPDATA%/HB_Orchestration/calls.jsonl. 대시보드 Dashboard:LogPath를 바꿨으면 같은 값으로.</summary>
    public string? CallLogPath { get; set; }

    public string CallLogFile => string.IsNullOrWhiteSpace(CallLogPath) ? CallLog.DefaultPath : CallLogPath.Trim();

    /// <summary>지휘자 표정 → 스티커 태그 (상태 이름 Idle · Thinking · Writing · Tool · Approval · Question · Done · Failed · Stopped · Sleep). 없는 상태는 기본값 (<see cref="FaceCatalog"/>).</summary>
    public Dictionary<string, string> Expressions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ModelList => Models.Count > 0 ? Models : ["opus", "sonnet", "haiku"];

    public string DataFolder =>
        string.IsNullOrWhiteSpace(DataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HB_Chat")
            : DataDir.Trim();

    public string ProjectsRoot =>
        string.IsNullOrWhiteSpace(ClaudeProjectsDir) ? ClaudeTranscriptReader.DefaultProjectsRoot() : ClaudeProjectsDir.Trim();

    /// <summary>
    /// 지휘자 할 일 폴더 (세션마다 <c>&lt;세션 ID&gt;/*.json</c>, 3차 b). 세션 기록 폴더(projects) 옆의 tasks — 기본 ~/.claude/tasks
    /// (CLAUDE_CONFIG_DIR를 쓰면 그 아래, ClaudeProjectsDir를 정하면 그 옆). 위치를 정하지 못하면 null — 단계 표시만 빠진다 (/self-review ea28ba9185 C4).
    /// </summary>
    public string? TasksRoot
    {
        get
        {
            try
            {
                return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectsRoot))) is { Length: > 0 } parent
                    ? Path.Combine(parent, "tasks") : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 앱에서 더한 작업 폴더 (3차 d, 데이터 폴더 workspaces.json). 설정에서 바인딩하지 않고 켤 때 Program이 채운다 — 없으면(테스트) 설정 파일 것만.
    /// </summary>
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("__app_workspaces_not_bound__")]
    public WorkspaceStore? AppWorkspaces { get; set; }

    /// <summary>작업 폴더 이름 → 경로: 설정 파일(Chat:Workspaces) 먼저, 그다음 앱에서 더한 것. 목록에 없으면 null.</summary>
    public string? WorkspacePath(string? name) =>
        name is null ? null
        : Workspaces.TryGetValue(name, out var path) && !string.IsNullOrWhiteSpace(path) ? path.Trim()
        : AppWorkspaces?.PathOf(name);

    /// <summary>새 방 화면 · 설정 화면에 보일 작업 폴더 전체 (설정 파일 것 먼저).</summary>
    public IReadOnlyList<WorkspaceEntry> AllWorkspaces =>
        AppWorkspaces?.All() ?? [.. Workspaces.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new WorkspaceEntry(p.Key, p.Value.Trim(), true))];

    /// <summary>앱에서 더한 MCP 서버 (데이터 폴더 mcp-servers.json). 켤 때 Program이 채운다.</summary>
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("__app_mcp_not_bound__")]
    public AppMcpStore? AppMcp { get; set; }

    // ---- Unity 프로젝트 ----

    /// <summary>설정 파일의 Unity 프로젝트 이름 → 경로 (작업 폴더와 같은 모양). 앱에서 더한 것은 <see cref="AppUnityProjects"/>.</summary>
    public Dictionary<string, string> UnityProjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>앱에서 더한 Unity 프로젝트 (데이터 폴더 unity-projects.json, 작업 폴더와 같은 검사). 켤 때 Program이 채운다.</summary>
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("__app_unity_projects_not_bound__")]
    public WorkspaceStore? AppUnityProjects { get; set; }

    /// <summary>Unity CLI 실행 파일 (`unity mcp`를 띄운다). 비우면 %LOCALAPPDATA%\Unity\bin\unity.exe, 없으면 PATH의 unity.</summary>
    public string? UnityCliPath { get; set; }

    /// <summary>Unity 프로젝트 이름 → 경로 (설정 파일 먼저). 없으면 null.</summary>
    public string? UnityProjectPath(string? name) =>
        name is null ? null
        : UnityProjects.TryGetValue(name, out var path) && !string.IsNullOrWhiteSpace(path) ? path.Trim()
        : AppUnityProjects?.PathOf(name);

    public IReadOnlyList<WorkspaceEntry> AllUnityProjects =>
        AppUnityProjects?.All() ?? [.. UnityProjects.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new WorkspaceEntry(p.Key, p.Value.Trim(), true))];

    /// <summary>unity.exe 경로. 못 찾으면 null — 그 방 실행에 Unity 연결을 붙이지 않는다.</summary>
    public string? ResolveUnityCli()
    {
        if (!string.IsNullOrWhiteSpace(UnityCliPath)) return File.Exists(UnityCliPath.Trim()) ? UnityCliPath.Trim() : null;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "bin", "unity.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "unity.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>claude 실행 파일 경로. 설정이 없고 PATH에서도 못 찾으면 "claude" (실행할 때 실패로 보인다).</summary>
    public string ResolveClaude()
    {
        if (!string.IsNullOrWhiteSpace(ClaudePath)) return ClaudePath.Trim();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "claude.exe" : "claude");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return "claude";
    }
}
