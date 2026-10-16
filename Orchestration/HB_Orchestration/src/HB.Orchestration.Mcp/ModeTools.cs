using System.ComponentModel;
using HB.Orchestration.Agents;
using ModelContextProtocol.Server;

namespace HB.Orchestration.Mcp;

/// <summary>
/// MCP 도구는 list_modes, run_mode, save_result와 확인용 server_info 넷뿐이다. 모드를 추가·수정해도 도구 목록이 바뀌지 않으므로
/// modes.json만 고치면 재시작 없이 반영되고, Claude 컨텍스트에 올라가는 도구 설명도 최소로 유지된다.
/// save_result는 설명이 컨텍스트에 늘 올라가는 비용을 감수하고 추가했다: 지휘자가 응답을 다시 출력해 옮겨 적는
/// 토큰과 옮겨 적기 오류를 없애고, 저장 위치를 기록의 프로젝트 폴더로 고정한다.
/// </summary>
[McpServerToolType]
public sealed class ModeTools(ModeRunner modes)
{
    [McpServerTool(Name = "list_modes")]
    [Description("사용 가능한 모드 목록과 구성(역할@에이전트)을 본다. run_mode 전에 모드 이름을 모를 때만 호출.")]
    public string ListModes() => modes.Describe();

    [McpServerTool(Name = "run_mode")]
    [Description("외부 에이전트(GPT/Gemini/로컬)로 모드를 실행한다. 입력은 필요한 최소한만. Claude 자신의 결론은 넣지 말 것(독립 검증 유지).")]
    public async Task<string> RunMode(
        [Description("모드 이름 (list_modes 참고)")] string mode,
        [Description("요청 본문: 목적/의도/제약 + 설계안이나 diff 등. 디스크에 있는 코드는 여기에 다시 쓰지 말고 context로 넘길 것")] string input,
        [Description("관련 파일 경로 (프로젝트 루트 기준, 'path:line' 가능). 기록·대시보드 링크용이며 에이전트에게는 전달되지 않음.")] string[]? files = null,
        [Description("이번 실행만 에이전트 교체 (선택). 'from=to' 또는 '*=to'. 사용자가 다른 모델을 요청했을 때만 사용. 이름은 list_modes 참고.")] string[]? agents = null,
        [Description("에이전트에게 보낼 코드 범위 (선택). 서버가 디스크에서 읽어 줄 번호와 함께 input 뒤에 붙인다. path는 프로젝트 루트 기준 상대 경로, startLine~endLine은 1부터이고 끝 줄 포함. 판단에 필요한 주변 코드(해제부, 호출부 등)까지 범위에 넣을 것")] ContextRange[]? context = null,
        CancellationToken cancellationToken = default)
    {
        var result = await modes.RunAsync(mode, input, files, agents, context, cancellationToken);
        modes.DumpToolResult("run_mode", mode, result); // Debug.DumpToolResults가 켜졌을 때만 (반환 직전 문자열 그대로)
        return result;
    }

    [McpServerTool(Name = "save_result")]
    [Description("run_mode 결과의 한 단계 응답 원문을 파일로 저장한다. 응답을 직접 다시 쓰지 말고 이걸 쓸 것.")]
    public string SaveResult(
        [Description("run_mode 결과 첫 줄의 runId")] string runId,
        [Description("단계 번호 (결과의 '### N.')")] int step,
        [Description("저장 경로 (프로젝트 루트 기준 상대 경로)")] string path,
        [Description("N번째 코드 블록만 저장 (1부터, 선택)")] int? codeBlock = null,
        [Description("이미 있으면 덮어쓰기 (기본 false)")] bool overwrite = false)
        => modes.SaveResult(runId, step, path, codeBlock, overwrite);

    [McpServerTool(Name = "server_info")]
    [Description("이 MCP 서버의 빌드 시각·PID·설정 로드 시각을 본다. 사용자가 서버 상태나 publish 반영 여부를 물을 때만 호출.")]
    public string ServerInfo() => modes.ServerInfo();
}
