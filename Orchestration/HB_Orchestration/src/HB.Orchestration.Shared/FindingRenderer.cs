using System.Text;

namespace HB.Orchestration.Shared;

// 대시보드 채팅방 · HB_Chat 실행 말풍선이 같이 쓰도록 Agents에서 옮김 (본문 그대로).
/// <summary>해석된 구조화 출력을 지휘자에게 보낼 짧은 마크다운으로. 모든 필드를 보낸다 (빼면 판단 근거가 사라짐).</summary>
public static class FindingRenderer
{
    /// <summary>성능 지적 형식 이름 (Agents FindingFormats.PerfFindings가 이 값을 쓴다).</summary>
    public const string PerfFormatName = "perf_findings";

    public static string Render(StructuredOutputRecord s)
    {
        var perf = s.Format.StartsWith(PerfFormatName + "@", StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var f in s.Findings)
        {
            sb.Append("- ").Append(f.Id).Append(" [").Append(f.Severity).Append("] ")
              .Append(f.Location ?? "위치 없음").Append(" — ").Append(OneLine(f.Problem));
            if (f.Suggestion is { } sug) sb.Append(" / ").Append(OneLine(sug));
            sb.Append(" / 확신도(").Append(f.Confidence).Append(")\n");
            if (f.Detail is { } d) sb.Append("  상세: ").Append(OneLine(d)).Append('\n');
            if (perf && (f.Frequency ?? f.Scaling ?? f.Measurement) is not null)
                sb.Append("  빈도: ").Append(f.Frequency ?? "-").Append(" · 증가: ").Append(f.Scaling ?? "-")
                  .Append(" · 측정: ").Append(f.Measurement ?? "-").Append('\n');
        }

        // 자료 요청은 ID와 함께 (지휘자가 followUp으로 보충하거나 record_verdicts에 처리를 남긴다). 예전 기록(Requests 없음)은 문장만.
        if (s.Requests.Count > 0)
        {
            foreach (var r in s.Requests)
            {
                sb.Append("자료 요청 ").Append(r.Id).Append(": ").Append(OneLine(r.What));
                var where = string.Join(" · ", new[] { r.Path is null ? null : $"path {r.Path}", r.Symbol is null ? null : $"symbol {r.Symbol}" }.Where(x => x is not null));
                if (where.Length > 0) sb.Append(" (").Append(where).Append(')');
                if (r.Why is { } why) sb.Append(" — ").Append(OneLine(why));
                sb.Append('\n');
            }
        }
        else foreach (var n in s.NeedsInfo) sb.Append("자료 부족: ").Append(OneLine(n)).Append('\n');
        Section(sb, "사양 의존", s.SpecDependent);
        Section(sb, "수정 방향", s.FixDirection);
        Section(sb, "측정 우선순위", s.MeasurePriority);

        sb.Append(Summary(s, perf));
        return sb.ToString();
    }

    /// <summary>마지막 줄. "문제 없음"은 완료 상태에서 지적이 0건일 때만 (자료 부족·잘림이면 무결점으로 보이지 않게).</summary>
    public static string Summary(StructuredOutputRecord s, bool perf)
    {
        if (s.Findings.Count == 0 && s.State == OutputStates.Complete) return "문제 없음";

        var count = perf
            ? $"총 {s.Findings.Count}건(높음 {N("high")} / 중간 {N("medium")} / 낮음 {N("low")})"
            : $"총 {s.Findings.Count}건(critical {N("critical")} / major {N("major")} / minor {N("minor")} / nit {N("nit")})";
        return s.State switch
        {
            OutputStates.NeedsInfo => $"{count} · 검토 불완전: 자료 부족 {s.NeedsInfo.Count}건",
            OutputStates.Incomplete => $"{count} · 검토 불완전: {s.Error} — 일부 지적만 있을 수 있음",
            _ => count,
        };

        int N(string severity) => s.Findings.Count(f => f.Severity == severity);
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<string>? items)
    {
        if (items is not { Count: > 0 }) return;
        sb.Append(title).Append(":\n");
        foreach (var i in items) sb.Append("  - ").Append(OneLine(i)).Append('\n');
    }

    /// <summary>줄바꿈을 공백으로 (지적 한 항목이 여러 줄로 흩어지지 않게). 코드 펜스가 든 제안은 그대로 둔다.</summary>
    private static string OneLine(string s) =>
        s.Contains("```", StringComparison.Ordinal) ? "\n" + s.Trim() : string.Join(' ', s.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
