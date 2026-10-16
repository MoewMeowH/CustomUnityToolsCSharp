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
    public static string Format(string runId, IReadOnlyList<StepResult> results, bool returnAll, string? note)
    {
        var failed = results.FirstOrDefault(r => !r.Success);
        var shown = returnAll || results.Count == 0 ? results : [failed ?? results[^1]];

        // 출력에서 빠지는 단계(Last 모드의 앞 단계 등)의 재시도·경고는 끝에 모은다 (보이는 단계는 제목 아래).
        var hidden = results.Except(shown).SelectMany(r => r.Notices.Select(n => $"단계 {r.Step} {n}")).ToList();
        if (hidden.Count > 0)
            note = note is null ? string.Join("\n", hidden) : $"{string.Join("\n", hidden)}\n{note}";

        var sb = new StringBuilder();
        sb.Append("runId: ").Append(runId).Append('\n');
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

    private static string Tokens(long? n) => n?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
}
