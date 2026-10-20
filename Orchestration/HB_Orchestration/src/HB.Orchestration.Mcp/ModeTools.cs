using System.ComponentModel;
using HB.Orchestration.Agents;
using ModelContextProtocol.Server;

namespace HB.Orchestration.Mcp;

/// <summary>
/// MCP 도구는 list_modes, run_mode, save_result, record_verdicts와 확인용 server_info 다섯뿐이다. 모드를 추가·수정해도 도구 목록이 바뀌지 않으므로
/// modes.json만 고치면 재시작 없이 반영되고, Claude 컨텍스트에 올라가는 도구 설명도 최소로 유지된다.
/// save_result는 설명이 컨텍스트에 늘 올라가는 비용을 감수하고 추가했다: 지휘자가 응답을 다시 출력해 옮겨 적는
/// 토큰과 옮겨 적기 오류를 없애고, 저장 위치를 기록의 프로젝트 폴더로 고정한다.
/// record_verdicts도 같은 비용을 감수했다: 지적마다 채택·기각을 남겨야 에이전트·모드의 가치를 데이터로 볼 수 있다 (대시보드 판정 화면).
/// /self-review 판정도 새 도구 없이 record_verdicts의 selfReview 인자로 남긴다 (도구 설명 비용을 늘리지 않게).
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
        [Description("에이전트에게 보낼 코드 범위 (선택). 서버가 디스크에서 읽어 줄 번호와 함께 input 앞에 둔다(같은 자료로 다시 물을 때 프롬프트 캐시에 걸리도록). path는 프로젝트 루트 기준 상대 경로, startLine~endLine은 1부터이고 끝 줄 포함. 판단에 필요한 주변 코드(해제부, 호출부 등)까지 범위에 넣을 것")] ContextRange[]? context = null,
        [Description("에이전트에게 보낼 이미지 경로 (선택, png·jpeg). 프로젝트 루트 기준 상대 경로. 사용자가 고른 뒤 image_prep.py shrink로 줄인 _send/ 파일만 넣을 것. 서버는 줄이지 않고 큰 이미지는 거절한다")] string[]? images = null,
        // 필수 여부는 서버가 검사한다: 스키마에서 필수로 두면 누락이 '['로 시작하는 안내 대신 도구 오류로 나간다.
        [Description("이 모드를 부른 이유 한 줄. 필수. 기록용이며 에이전트에게 전달되지 않음.")] string? reason = null,
        [Description("결과를 보기 전에 완성한 자체 검토 원문. [ownReview 필수] 모드에서 필수. 기록용이며 에이전트에게 전달되지 않음.")] string? ownReview = null,
        [Description("자료 보충 재질문 (선택). 앞 결과의 '자료 요청 1-N1'에 대해 찾은 코드를 context로, 메모를 input으로 같은 에이전트에게 1회 다시 묻는다. 실행마다 1회.")] FollowUpRequest? followUp = null,
        CancellationToken cancellationToken = default)
    {
        // followUp은 원래 단계의 에이전트로 보내고 이미지·ownReview를 받지 않는다 — 같이 오면 말없이 버리지 않고 거절한다.
        var result = followUp is not null && (agents is { Length: > 0 } || images is { Length: > 0 } || !string.IsNullOrWhiteSpace(ownReview))
            ? "[followUp 불가] followUp에는 agents · images · ownReview를 함께 쓸 수 없습니다 (원래 단계의 에이전트로 보내고, 이미지는 보충하지 않습니다)."
            : followUp is not null
            ? await modes.RunFollowUpAsync(mode, followUp, input, files, context, reason, cancellationToken)
            : await modes.RunAsync(mode, input, files, agents, context, images, reason, ownReview, cancellationToken);
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

    [McpServerTool(Name = "record_verdicts")]
    [Description("결과 종합 뒤 지적마다 판정(채택·부분 채택·기각·보류·중복)과 근거를 기록한다. Claude만 지적한 것은 steps 없이.")]
    public async Task<string> RecordVerdicts(
        // 셋 다 스키마에서 선택으로 둔다 (기본값이 있어야 선택): 누락은 도구 오류 대신 "[판정 기록 불가] …" 안내로 돌려준다.
        [Description("지적별 판정")] VerdictItem[]? items = null,
        [Description("run_mode 결과 첫 줄의 runId. /self-review는 빼고 selfReview를 준다")] string? runId = null,
        [Description("/self-review 전용: self-reviewer 실행을 새로 기록 (runId 없이). self-reviewer 지적은 steps [1]")] SelfReviewInfo? selfReview = null)
    {
        if (selfReview is not null && !string.IsNullOrWhiteSpace(runId))
            return "[판정 기록 불가] runId와 selfReview는 함께 쓸 수 없습니다 (selfReview는 새 실행을 만든다).";
        return selfReview is not null
            ? await modes.RecordSelfReviewAsync(selfReview, items)
            : modes.RecordVerdicts(runId ?? "", items);
    }

    [McpServerTool(Name = "server_info")]
    [Description("이 MCP 서버의 빌드 시각·PID·설정 로드 시각을 본다. 사용자가 서버 상태나 publish 반영 여부를 물을 때만 호출.")]
    public string ServerInfo() => modes.ServerInfo();
}
