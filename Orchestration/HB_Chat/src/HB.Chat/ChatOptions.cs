using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>방마다 고르는 권한 단계 (계획서 4번 인자 표).</summary>
public enum PermissionLevel
{
    /// <summary>도구 없이 대화만. MCP도 없음.</summary>
    Chat,
    /// <summary>작업 폴더 읽기 · 검색 + hb-orchestration MCP.</summary>
    Read,
    /// <summary>읽기 + 작업 폴더 파일 편집(acceptEdits). 명령 실행은 없음.</summary>
    Edit,
}

/// <summary>
/// appsettings.json의 Chat 절. 개인 값은 appsettings.local.json(git 제외)에 둔다.
/// </summary>
public sealed class ChatOptions
{
    /// <summary>claude 실행 파일. 비우면 PATH에서 claude.exe를 찾는다.</summary>
    public string? ClaudePath { get; set; }

    /// <summary>방 색인 · 만든 MCP 설정을 두는 폴더. 비우면 %LOCALAPPDATA%/HB_Chat.</summary>
    public string? DataDir { get; set; }

    /// <summary>Claude 세션 기록 폴더. 비우면 ~/.claude/projects.</summary>
    public string? ClaudeProjectsDir { get; set; }

    /// <summary>작업 폴더 허용 목록 (이름 → 절대 경로). 방은 이 이름으로만 고른다.</summary>
    public Dictionary<string, string> Workspaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>고를 수 있는 모델 (claude --model 값). 설정 바인딩이 목록에 덧붙이므로 기본값은 비워 두고 <see cref="ModelList"/>가 채운다.</summary>
    public List<string> Models { get; set; } = [];

    public string DefaultModel { get; set; } = "opus";

    /// <summary>읽기 · 편집 단계에 붙이는 MCP 서버 이름 (작업 폴더 .mcp.json의 항목).</summary>
    public string McpServer { get; set; } = "hb-orchestration";

    /// <summary>대시보드 주소 (방 화면의 "대시보드에서 보기" 링크). 비우면 링크 없음.</summary>
    public string? DashboardUrl { get; set; }

    /// <summary>스티커 설정을 읽을 대시보드 폴더 (appsettings.json · appsettings.local.json의 Dashboard:Stickers). 비우면 글자 배지.</summary>
    public string? StickerSettingsDir { get; set; }

    public IReadOnlyList<string> ModelList => Models.Count > 0 ? Models : ["opus", "sonnet", "haiku"];

    public string DataFolder =>
        string.IsNullOrWhiteSpace(DataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HB_Chat")
            : DataDir.Trim();

    public string ProjectsRoot =>
        string.IsNullOrWhiteSpace(ClaudeProjectsDir) ? ClaudeTranscriptReader.DefaultProjectsRoot() : ClaudeProjectsDir.Trim();

    /// <summary>작업 폴더 이름 → 경로. 목록에 없으면 null.</summary>
    public string? WorkspacePath(string? name) =>
        name is not null && Workspaces.TryGetValue(name, out var path) && !string.IsNullOrWhiteSpace(path) ? path.Trim() : null;

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
