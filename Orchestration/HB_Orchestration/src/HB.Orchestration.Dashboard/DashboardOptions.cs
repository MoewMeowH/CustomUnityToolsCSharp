using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

public sealed class DashboardOptions
{
    /// <summary>비우면 MCP 서버와 같은 기본 경로(CallLog.DefaultPath)를 쓴다.</summary>
    public string? LogPath { get; set; }

    /// <summary>
    /// MCP 서버의 publish 폴더 (appsettings.json, modes.json이 있는 곳).
    /// 모드 화면이 이 폴더의 설정을 읽고 고친다. HB_Orchestration.bat으로 실행하면 HB_Orchestration.config의 PUBLISH_DIR로 덮어쓴다.
    /// </summary>
    public string? McpConfigDir { get; set; }

    /// <summary>
    /// 편집할 modes.json을 직접 지정 (선택). 비우면 MCP appsettings의 Orchestration:ModesPath → McpConfigDir/modes.json 순.
    /// </summary>
    public string? ModesPath { get; set; }

    /// <summary>
    /// 파일 링크 템플릿. {path}=절대 경로(URL 인코딩), {line}=줄 번호.
    /// 기본값은 JetBrains IDE 내장 서버로 Rider에서 바로 연다.
    /// </summary>
    public string FileLinkTemplate { get; set; } = "http://localhost:63342/api/file?file={path}&line={line}";

    /// <summary>
    /// (예전 방식) 모델별 1M 토큰당 가격(USD). 단가는 이제 MCP appsettings.json의 Orchestration:Pricing에 두고
    /// 모드 화면에서 편집한다. 여기 값은 그쪽에 없는 모델에만 쓰인다.
    /// </summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claude Code 세션 기록 폴더 (projects). 비우면 CLAUDE_CONFIG_DIR/projects → %USERPROFILE%/.claude/projects.
    /// 'Claude 세션' 화면이 읽기만 한다.
    /// </summary>
    public string? ClaudeProjectsDir { get; set; }

    /// <summary>'Claude 세션' 화면에서 읽을 최근 세션 기록 수 (수정 시각 순). 기본 50.</summary>
    public int ClaudeSessionLimit { get; set; } = 50;

    /// <summary>'청구 대조' 화면 설정 (Admin 키 환경 변수 이름).</summary>
    public BillingOptions Billing { get; set; } = new();

    /// <summary>
    /// Admin 키 값. 프로세스 환경 변수 → (Windows) 사용자 환경 변수 순으로 읽는다.
    /// 사용자 환경 변수를 직접 읽으므로 키를 새로 넣어도 대시보드를 다시 켜지 않아도 된다. 화면·로그에 쓰지 않는다.
    /// </summary>
    public string? ReadAdminKey()
    {
        var name = Billing.AdminKeyEnv;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public string ResolvedLogPath => string.IsNullOrWhiteSpace(LogPath) ? CallLog.DefaultPath : LogPath;
}
