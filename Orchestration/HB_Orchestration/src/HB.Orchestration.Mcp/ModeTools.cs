using System.ComponentModel;
using HB.Orchestration.Agents;
using ModelContextProtocol.Server;

namespace HB.Orchestration.Mcp;

/// <summary>
/// MCP 도구는 이 두 개뿐이다. 모드를 추가·수정해도 도구 목록이 바뀌지 않으므로
/// modes.json만 고치면 재시작 없이 반영되고, Claude 컨텍스트에 올라가는 도구 설명도 최소로 유지된다.
/// </summary>
[McpServerToolType]
public sealed class ModeTools(ModeRunner modes)
{
    [McpServerTool(Name = "list_modes")]
    [Description("사용 가능한 모드 목록과 구성(역할@에이전트)을 본다. run_mode 전에 모드 이름을 모를 때만 호출.")]
    public string ListModes() => modes.Describe();

    [McpServerTool(Name = "run_mode")]
    [Description("외부 에이전트(GPT/Gemini/로컬)로 모드를 실행한다. 입력은 필요한 최소한만. Claude 자신의 결론은 넣지 말 것(독립 검증 유지).")]
    public Task<string> RunMode(
        [Description("모드 이름 (list_modes 참고)")] string mode,
        [Description("요청 본문: 목적/의도 + diff나 설계안 등 필요한 최소 자료")] string input,
        [Description("관련 파일 경로 (프로젝트 루트 기준, 'path:line' 가능). 기록·대시보드 링크용이며 에이전트에게는 전달되지 않음.")] string[]? files = null,
        [Description("이번 실행만 에이전트 교체 (선택). 'from=to' 또는 '*=to'. 사용자가 다른 모델을 요청했을 때만 사용. 이름은 list_modes 참고.")] string[]? agents = null,
        CancellationToken cancellationToken = default)
        => modes.RunAsync(mode, input, files, agents, cancellationToken);
}
