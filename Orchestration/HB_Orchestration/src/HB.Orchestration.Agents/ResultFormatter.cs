using System.Globalization;
using System.Text;

namespace HB.Orchestration.Agents;

/// <summary>
/// run_mode 결과 조립. 맨 위에 runId(save_result에 넘길 값), 단계마다 제목 줄에 에이전트·토큰·시간,
/// 그 아래에 재시도 알림과 검사기 경고를 한 줄씩 붙인다.
/// </summary>
public static class ResultFormatter
{
    /// <param name="returnAll">false면 마지막 단계만 (실패가 있으면 첫 실패 단계만) 보여준다 (Claude 쪽 토큰 절약).</param>
    /// <param name="note">끝에 붙일 안내 (조기 종료, 에이전트 교체 등).</param>
    /// <param name="header">runId 다음 줄에 붙일 한 줄 (첨부 여유 등). 첫 줄은 늘 runId만 — 세션 기록이 첫 줄로 runId를 읽는다.</param>
    public static string Format(string runId, IReadOnlyList<StepResult> results, bool returnAll, string? note, string? header = null)
    {
        var failed = results.FirstOrDefault(r => !r.Success);
        var shown = returnAll || results.Count == 0 ? results : [failed ?? results[^1]];

        // 출력에서 빠지는 단계(Last 모드의 앞 단계 등)의 재시도·경고는 끝에 모은다 (보이는 단계는 제목 아래).
        var hidden = results.Except(shown).SelectMany(r => r.Notices.Select(n => $"단계 {r.Step} {n}")).ToList();
        if (hidden.Count > 0)
            note = note is null ? string.Join("\n", hidden) : $"{string.Join("\n", hidden)}\n{note}";

        var sb = new StringBuilder();
        sb.Append("runId: ").Append(runId).Append('\n');
        if (header is not null) sb.Append(header).Append('\n');
        foreach (var r in shown)
        {
            sb.Append(Title(r)).Append('\n');
            foreach (var notice in r.Notices) sb.Append(notice).Append('\n');
            sb.Append(r.Text).Append("\n\n");
        }

        if (note is not null) sb.Append(note).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>"### 1. Cross_P3_A · gpt_main_sol (입력 15,551 / 출력 7,502, 144.0초)"</summary>
    public static string Title(StepResult r) =>
        $"### {r.Step}. {r.Role} · {r.Agent} (입력 {Tokens(r.InputTokens)} / 출력 {Tokens(r.OutputTokens)}, " +
        $"{(r.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}초)";

    /// <summary>
    /// "첨부 12,000자 + input 3,000자 = 15,000자 / 상한 350,000자 (4%)" — 지휘자가 다음 호출에서 첨부를 넉넉히 붙여도 되는지 보게
    /// (계획서 2026-10-27_외부-첨부-범위-규칙: 리뷰 입력은 상한의 1/3 안팎인데 자료 요청이 35% 실행에서 나왔다). 상한이 없으면 null.
    /// </summary>
    public static string? AttachmentHeadroom(int attachedChars, int inputChars, int maxChars)
    {
        if (maxChars <= 0) return null;
        var total = attachedChars + inputChars;
        // 내림 — 상한 직전(99.5%)이 100%로 보여 막힌 것처럼 읽히지 않게 (sub_self 11b9c52eba C8)
        var percent = (int)Math.Floor(total * 100.0 / maxChars);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"첨부 {attachedChars:N0}자 + input {inputChars:N0}자 = {total:N0}자 / 상한 {maxChars:N0}자 ({percent}%)");
        // followUp은 원래 프롬프트 + 응답 + 보충이 같은 상한 안에 들어가야 한다 — 절반을 넘으면 보충 여유가 적다 (C3)
        return total * 2 > maxChars ? line + " · followUp 여유 적음 (원래 프롬프트 + 응답 + 보충이 같은 상한)" : line;
    }

    private static string Tokens(long? n) => n?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
}
