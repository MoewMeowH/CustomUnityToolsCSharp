using System.Text;
using System.Text.Json;
using HB.Orchestration.Shared;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents;

/// <summary>
/// 구조화 출력 응답의 해석·검증·ID 부여. 순서: 종료 사유 확인 → JSON 읽기(코드 펜스 벗김) → 타입·값 검증 → ID 확정.
/// 항목 하나라도 틀리면 응답 전체를 해석 실패로 본다 (일부만 살리면 지휘자가 빠진 것을 모른다).
/// </summary>
public static class FindingParser
{
    public static StructuredOutputRecord Parse(FindingFormat format, int step, string text, ChatFinishReason? finishReason, string requestedBy)
    {
        var cut = finishReason == ChatFinishReason.Length ? "출력 상한에서 잘림"
                : finishReason == ChatFinishReason.ContentFilter ? "제공사 필터로 중단"
                : null;

        StructuredOutputRecord Failed(string why) => new()
        {
            Format = format.Tag, State = OutputStates.Failed, RequestedBy = requestedBy,
            Error = cut is null ? why : $"{cut} · {why}",
        };

        var (root, readError, repaired) = ReadJson(text);
        if (root is null) return Failed(readError!);
        using (root)
        {
            var errors = new List<string>();
            var findings = new List<(FindingRecord Item, int Order)>();
            var obj = root.RootElement;

            if (!obj.TryGetProperty("findings", out var list) || list.ValueKind != JsonValueKind.Array)
                return Failed("findings 배열이 없음");

            var i = 0;
            foreach (var f in list.EnumerateArray())
            {
                i++;
                if (f.ValueKind != JsonValueKind.Object) { errors.Add($"findings[{i}] 객체가 아님"); continue; }
                var severity = Lower(f, "severity");
                if (severity is null || !format.Severities.Contains(severity)) errors.Add($"findings[{i}].severity '{severity}'");
                var confidence = Lower(f, "confidence");
                if (confidence is null || !FindingFormat.Confidences.Contains(confidence)) errors.Add($"findings[{i}].confidence '{confidence}'");
                var problem = Str(f, "problem");
                if (string.IsNullOrWhiteSpace(problem)) errors.Add($"findings[{i}].problem 비어 있음");
                var (line, lineOk) = Line(f);
                if (!lineOk) errors.Add($"findings[{i}].line은 1 이상의 정수 또는 null");
                // 선택 문자열: 없음·null은 괜찮지만 다른 타입이면 오류 (조용히 버리면 판단 근거가 사라진다 — review 0888cb5f87).
                var file = Opt(f, "file", i, errors);
                var detail = Opt(f, "detail", i, errors);
                var suggestion = Opt(f, "suggestion", i, errors);
                var frequency = format.Perf ? Opt(f, "frequency", i, errors) : null;
                var scaling = format.Perf ? Opt(f, "scaling", i, errors) : null;
                var measurement = format.Perf ? Opt(f, "measurement", i, errors) : null;
                if (errors.Count > 0) continue;

                findings.Add((new FindingRecord
                {
                    Id = "",
                    Severity = severity!,
                    File = file,
                    Line = line,
                    Problem = problem!.Trim(),
                    Detail = detail,
                    Suggestion = suggestion,
                    Confidence = confidence!,
                    Frequency = frequency,
                    Scaling = scaling,
                    Measurement = measurement,
                }, i));
            }

            var requests = Requests(obj, step, errors);
            var needsInfo = requests.Select(r => r.What).ToList();
            var spec = format.Perf ? Strings(obj, "specDependent", errors) : null;
            var fix = format.Perf ? Strings(obj, "fixDirection", errors) : null;
            var measure = format.Perf ? Strings(obj, "measurePriority", errors) : null;
            if (errors.Count > 0) return Failed(string.Join(", ", errors.Take(5)) + (errors.Count > 5 ? $" 외 {errors.Count - 5}건" : ""));

            // 정렬: 심각도 → 에이전트가 낸 순서. ID는 정렬 뒤에 매긴다 (지휘자가 보는 순서 = 번호 순서).
            var rank = format.Severities.Select((s, n) => (s, n)).ToDictionary(x => x.s, x => x.n);
            var ordered = findings.OrderBy(x => rank[x.Item.Severity]).ThenBy(x => x.Order)
                .Select((x, n) => x.Item with { Id = $"{step}-F{n + 1}" }).ToList();

            return new StructuredOutputRecord
            {
                Format = format.Tag,
                State = cut is not null ? OutputStates.Incomplete : needsInfo.Count > 0 ? OutputStates.NeedsInfo : OutputStates.Complete,
                Error = cut,
                RequestedBy = requestedBy,
                Repaired = repaired,
                Findings = ordered,
                NeedsInfo = needsInfo,
                Requests = requests,
                SpecDependent = spec,
                FixDirection = fix,
                MeasurePriority = measure,
            };
        }
    }

    /// <summary>
    /// JSON 객체로 읽는다. 문법 오류일 때만(코드 펜스·앞뒤 문장) 첫 '{'부터 마지막 '}'까지로 다시 시도한다.
    /// 올바른 JSON인데 최상위가 객체가 아니면(배열 등) 그 안을 뒤지지 않고 실패다 (review 0888cb5f87).
    /// 그래도 실패하고 "findings" 앞에 '{'가 없으면 여는 '{'를 붙여 한 번 더 — DeepSeek이 추론→본문 경계에서 '{'를 빠뜨린 경우
    /// (2026-10-20 4c125d11ce, 258건 중 1건). 이때 Repaired로 표시한다 (조용히 고치지 않게).
    /// </summary>
    private static (JsonDocument? Doc, string? Error, string? Repaired) ReadJson(string text)
    {
        var t = text.Trim();
        var (direct, valid) = TryParse(t);
        if (direct is not null) return (direct, null, null);
        if (valid) return (null, "최상위가 JSON 객체가 아님", null);
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start >= 0 && end > start && TryParse(t[start..(end + 1)]) is { Doc: { } inner }) return (inner, null, null);
        var key = t.IndexOf("\"findings\"", StringComparison.Ordinal);
        if (key >= 0 && (start < 0 || start > key) && end > key && TryParse("{" + t[key..(end + 1)]) is { Doc: { } fixedDoc })
            return (fixedDoc, null, "여는 { 누락");
        return (null, "JSON 객체가 아님", null);

        static (JsonDocument? Doc, bool Valid) TryParse(string s)
        {
            try
            {
                var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) return (doc, true);
                doc.Dispose();
                return (null, true);
            }
            catch (JsonException) { return (null, false); }
        }
    }

    /// <summary>선택 문자열 필드: 없음·null·빈 문자열 → null, 문자열 → 값, 그 밖의 타입 → 오류.</summary>
    private static string? Opt(JsonElement o, string name, int index, List<string> errors)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return NullIfBlank(v.GetString());
        errors.Add($"findings[{index}].{name}이(가) 문자열이 아님");
        return null;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Lower(JsonElement o, string name) => Str(o, name)?.Trim().ToLowerInvariant();

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>line: 없음·null → null, 1 이상 정수 → 그 값, 숫자로 시작하는 문자열("42", "42-50") → 앞 숫자. 그 밖은 오류.</summary>
    private static (int? Line, bool Ok) Line(JsonElement o)
    {
        if (!o.TryGetProperty("line", out var v) || v.ValueKind == JsonValueKind.Null) return (null, true);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n >= 1 ? (n, true) : (null, false);
        if (v.ValueKind == JsonValueKind.String)
        {
            var digits = new string((v.GetString() ?? "").Trim().TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0) return string.IsNullOrWhiteSpace(v.GetString()) ? (null, true) : (null, false);
            return int.TryParse(digits, out var s) && s >= 1 ? (s, true) : (null, false);
        }
        return (null, false);
    }

    /// <summary>공통 규칙이 "'자료 부족: …'로 적는다"라서 needsInfo 항목에 접두어가 붙어 온다. 그릴 때 다시 붙이므로 벗긴다 (실제 확인 453bc0e4fe).</summary>
    private static string? StripNeedsInfoPrefix(string? s)
    {
        var t = s?.Trim();
        while (t is not null && t.StartsWith("자료 부족", StringComparison.Ordinal))
            t = t["자료 부족".Length..].TrimStart(':', ' ', '：');
        return t;
    }

    /// <summary>
    /// needsInfo → 자료 요청. 원소는 객체(@2: what · path · symbol · why)나 문자열(@1, what만) 둘 다 받는다
    /// (Prompt 방식이나 예전 응답을 다시 채점할 때). what이 비면 그 원소는 버린다. ID는 "단계-N순번".
    /// </summary>
    private static List<InfoRequest> Requests(JsonElement o, int step, List<string> errors)
    {
        var result = new List<InfoRequest>();
        if (!o.TryGetProperty("needsInfo", out var v) || v.ValueKind == JsonValueKind.Null) return result;
        if (v.ValueKind != JsonValueKind.Array) { errors.Add("needsInfo 배열이 아님"); return result; }
        var i = 0;
        foreach (var e in v.EnumerateArray())
        {
            i++;
            string? what, path = null, symbol = null, why = null;
            if (e.ValueKind == JsonValueKind.String) what = e.GetString();
            else if (e.ValueKind == JsonValueKind.Object)
            {
                // what이 없거나 비면 조용히 버리지 않는다 — 요청이 다 사라지면 '문제 없음'으로 보일 수 있다.
                what = Str(e, "what");
                if (string.IsNullOrWhiteSpace(StripNeedsInfoPrefix(what))) { errors.Add($"needsInfo[{i}].what이 비었거나 문자열이 아님"); continue; }
                path = NullIfBlank(ReqOpt(e, "path", i, errors));
                symbol = NullIfBlank(ReqOpt(e, "symbol", i, errors));
                why = NullIfBlank(ReqOpt(e, "why", i, errors));
            }
            else { errors.Add($"needsInfo[{i}]가 문자열·객체가 아님"); continue; }
            if (NullIfBlank(StripNeedsInfoPrefix(what)) is not { } w) continue;
            result.Add(new InfoRequest { Id = $"{step}-N{result.Count + 1}", What = w, Path = path, Symbol = symbol, Why = why });
        }
        return result;
    }

    private static string? ReqOpt(JsonElement o, string name, int index, List<string> errors)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        errors.Add($"needsInfo[{index}].{name}이(가) 문자열이 아님");
        return null;
    }

    /// <summary>문자열 배열. 키가 없거나 null이면 빈 목록 (Prompt 방식에서 빠뜨린 경우), 배열이 아니거나 문자열이 아닌 원소가 있으면 오류.</summary>
    private static List<string> Strings(JsonElement o, string name, List<string> errors)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (v.ValueKind != JsonValueKind.Array) { errors.Add($"{name} 배열이 아님"); return []; }
        var result = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) { errors.Add($"{name}에 문자열이 아닌 값"); return []; }
            if (NullIfBlank(StripNeedsInfoPrefix(e.GetString())) is { } s) result.Add(s);
        }
        return result;
    }
}

/// <summary>해석된 구조화 출력을 지휘자에게 보낼 짧은 마크다운으로. 모든 필드를 보낸다 (빼면 판단 근거가 사라짐).</summary>
public static class FindingRenderer
{
    public static string Render(StructuredOutputRecord s)
    {
        var perf = s.Format.StartsWith(FindingFormat.PerfFindings.Name + "@", StringComparison.Ordinal);
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

/// <summary>
/// 겹침 후보: 서로 다른 단계의 지적 중 같은 파일, 그룹 첫 줄에서 5줄 이내. 합치지 않고 후보만 낸다 (같은 문제인지는 지휘자가 판단).
/// 위치(file·line)가 없는 지적은 비교하지 않는다.
/// </summary>
public static class FindingOverlap
{
    public const int LineWindow = 5;
    public const int MaxGroups = 10;

    public sealed record Group(string File, int From, int To, IReadOnlyList<string> Ids);

    public static List<Group> Groups(IEnumerable<FindingRecord> findings)
    {
        var groups = new List<Group>();
        foreach (var byFile in findings.Where(f => f.File is not null && f.Line is not null).GroupBy(f => Normalize(f.File!)))
        {
            var sorted = byFile.OrderBy(f => f.Line).ToList();
            var current = new List<FindingRecord>();
            foreach (var f in sorted)
            {
                if (current.Count > 0 && f.Line - current[0].Line > LineWindow)
                {
                    Flush();
                    current = [];
                }
                current.Add(f);
            }
            Flush();

            void Flush()
            {
                if (current.Select(StepOf).Distinct().Count() < 2) return;
                groups.Add(new Group(current[0].File!, current[0].Line!.Value, current[^1].Line!.Value, [.. current.Select(c => c.Id)]));
            }
        }
        return groups;
    }

    /// <summary>결과 끝에 붙일 줄들. 겹침이 없으면 빈 목록. 최대 MaxGroups줄, 넘으면 "외 n그룹".</summary>
    public static List<string> Lines(IEnumerable<FindingRecord> findings)
    {
        var groups = Groups(findings);
        var lines = groups.Take(MaxGroups)
            .Select(g => $"겹침 후보: {g.File}:{g.From}{(g.To != g.From ? $"-{g.To}" : "")} — {string.Join(", ", g.Ids)}").ToList();
        if (groups.Count > MaxGroups) lines.Add($"겹침 후보 외 {groups.Count - MaxGroups}그룹 (대시보드 호출 상세)");
        return lines;
    }

    private static string StepOf(FindingRecord f) => f.Id.Split('-', 2)[0];

    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.ToLowerInvariant();
    }
}
