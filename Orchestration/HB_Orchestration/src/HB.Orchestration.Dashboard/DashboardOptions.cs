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

    public string ResolvedLogPath => string.IsNullOrWhiteSpace(LogPath) ? CallLog.DefaultPath : LogPath;
}
