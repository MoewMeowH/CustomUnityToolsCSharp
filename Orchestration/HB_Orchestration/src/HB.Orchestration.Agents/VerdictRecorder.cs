using System.ComponentModel;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>record_verdicts의 지적 하나. MCP 스키마에 그대로 나가므로 설명은 짧게 둔다 (상세 규칙은 CLAUDE.md).</summary>
public sealed record VerdictItem(
    [property: Description("실행 안에서 지적을 구별하는 짧은 키. 같은 키로 다시 쓰면 앞 판정을 대체. 권장: 첫 지적 ID")] string FindingKey,
    [property: Description("지적 한 줄 요약. findingIds를 주면 비워도 됨")] string? Summary,
    [property: Description("adopted · partial · rejected · deferred · duplicate")] string Verdict,
    [property: Description("이 지적을 낸 단계 번호들. 비우면 Claude만 지적")] int[]? Steps = null,
    [property: Description("판정 근거. adopted가 아니면 필수")] string? Reason = null,
    [property: Description("high · medium · low (선택)")] string? Severity = null,
    [property: Description("ownReview에도 있던 지적인지")] bool ClaudeAlsoFound = false,
    [property: Description("관련 파일 path 또는 path:line (선택)")] string? File = null,
    [property: Description("구조화 결과의 지적 ID (예: 1-F2). 주면 steps·summary·file·severity를 채운다")] string[]? FindingIds = null);

/// <summary>
/// record_verdicts: 결과 종합 뒤 지적마다 판정을 verdicts.jsonl에 남긴다 (에이전트·모드별 채택률의 원본).
/// 하나라도 틀리면 아무것도 쓰지 않는다. 실패는 예외가 아니라 "[판정 기록 불가] …" 문자열.
/// </summary>
public static class VerdictRecorder
{
    public const int MaxItems = 50;
    public const int MaxKeyChars = 80;
    public const int MaxSummaryChars = 300;

    private static readonly string[] Severities = ["high", "medium", "low"];

    /// <param name="maxReasonChars">Reason 글자 수 상한 (Records.MaxReasonChars, 0 = 무제한).</param>
    /// <param name="logPath">호출 기록 파일. null이면 기본 경로. 판정 파일은 그 옆 (<see cref="VerdictLog.PathFor"/>).</param>
    public static string Record(string? runId, IReadOnlyList<VerdictItem>? items, int maxReasonChars, string? logPath = null)
    {
        const string Fail = "[판정 기록 불가] ";
        if (string.IsNullOrWhiteSpace(runId)) return Fail + "runId가 비어 있습니다. run_mode 결과 첫 줄의 runId를 넘기세요.";
        runId = runId.Trim();
        if (items is null || items.Count == 0) return Fail + "items가 비어 있습니다.";
        if (items.Count > MaxItems) return Fail + $"한 번에 {MaxItems}건까지입니다 ({items.Count}건). 나눠서 호출하세요.";

        List<CallRecord> runCalls;
        try
        {
            runCalls = CallLog.ReadAll(logPath).Where(r => r.RunId == runId).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail + $"호출 기록을 읽지 못했습니다 ({ex.GetType().Name}: {ex.Message}).";
        }
        if (runCalls.Count == 0) return Fail + $"runId '{runId}'의 기록이 없습니다.";

        var steps = runCalls.Where(r => r.Step is not null).Select(r => r.Step!.Value).ToHashSet();

        // 구조화 출력의 지적 (단계의 대표 호출 = 성공한 마지막 시도). ID는 실행 안에서 유일하다.
        var findings = runCalls.Where(r => r.Step is not null && r.Success && r.Structured is { Parsed: true })
            .GroupBy(r => r.Step!.Value).Select(g => g.MaxBy(r => r.Attempt ?? 1)!)
            .SelectMany(r => r.Structured!.Findings)
            .ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var resolved = new VerdictItem[items.Count];
        var errors = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not { } original) { errors.Add($"{i + 1}번: 항목이 비어 있습니다 (null)."); continue; }
            var it = resolved[i] = Resolve(original, findings, errors, i + 1);
            var at = $"{i + 1}번({it.FindingKey})";
            if (string.IsNullOrWhiteSpace(it.FindingKey)) { errors.Add($"{i + 1}번: findingKey가 비어 있습니다."); continue; }
            if (it.FindingKey.Trim().Length > MaxKeyChars || it.FindingKey.Contains('\n'))
                errors.Add($"{at}: findingKey는 한 줄 {MaxKeyChars}자 이내입니다.");
            if (!keys.Add(it.FindingKey.Trim())) errors.Add($"{at}: 같은 호출 안에서 findingKey가 겹칩니다.");
            if (string.IsNullOrWhiteSpace(it.Summary)) errors.Add($"{at}: summary가 비어 있습니다.");
            else if (it.Summary.Length > MaxSummaryChars) errors.Add($"{at}: summary는 {MaxSummaryChars}자 이내입니다 ({it.Summary.Length}자).");

            var verdict = it.Verdict?.Trim().ToLowerInvariant();
            if (verdict is null || !Verdicts.All.Contains(verdict))
                errors.Add($"{at}: verdict '{it.Verdict}'는 {string.Join(" · ", Verdicts.All)} 중 하나여야 합니다.");
            else if (verdict != Verdicts.Adopted && string.IsNullOrWhiteSpace(it.Reason))
                errors.Add($"{at}: {Verdicts.Label(verdict)}({verdict})은 reason이 필수입니다.");
            if (it.Reason is not null && maxReasonChars > 0 && it.Reason.Length > maxReasonChars)
                errors.Add($"{at}: reason은 {maxReasonChars:N0}자 이내입니다 ({it.Reason.Length:N0}자).");

            if (it.Severity is { } sev && !string.IsNullOrWhiteSpace(sev) && !Severities.Contains(sev.Trim().ToLowerInvariant()))
                errors.Add($"{at}: severity '{sev}'는 high · medium · low 중 하나여야 합니다.");

            if (it.Steps?.Where(s => !steps.Contains(s)).ToList() is { Count: > 0 } missing)
                errors.Add($"{at}: 이 실행에 없는 단계 {string.Join(", ", missing)} (있는 단계: {string.Join(", ", steps.Order())}).");
        }
        if (errors.Count > 0) return Fail + "아무것도 기록하지 않았습니다.\n- " + string.Join("\n- ", errors);

        HashSet<string> existing;
        string path;
        try
        {
            path = VerdictLog.PathFor(logPath);
            existing = VerdictLog.ReadAll(path).Where(v => v.RunId == runId).Select(v => v.FindingKey).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail + $"판정 기록을 읽지 못했습니다 ({ex.GetType().Name}: {ex.Message}).";
        }

        var now = DateTimeOffset.Now;
        var records = resolved.Select(it =>
        {
            var stepList = (it.Steps ?? []).Distinct().Order().ToList();
            return new VerdictRecord
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Timestamp = now,
                RunId = runId,
                FindingKey = it.FindingKey.Trim(),
                Steps = stepList,
                Summary = it.Summary!.Trim(),
                Verdict = it.Verdict.Trim().ToLowerInvariant(),
                Reason = string.IsNullOrWhiteSpace(it.Reason) ? null : it.Reason.Trim(),
                Severity = string.IsNullOrWhiteSpace(it.Severity) ? null : it.Severity.Trim().ToLowerInvariant(),
                ClaudeAlsoFound = stepList.Count == 0 || it.ClaudeAlsoFound,
                File = string.IsNullOrWhiteSpace(it.File) ? null : it.File.Trim(),
                FindingIds = it.FindingIds is { Length: > 0 } ids ? ids : null,
            };
        }).ToList();

        try
        {
            VerdictLog.Append(records, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail + $"쓰기 실패 ({ex.GetType().Name}: {ex.Message}).";
        }

        var linked = records.Count(r => r.FindingIds is not null);
        var counts = string.Join(" · ", Verdicts.All
            .Select(v => (v, n: records.Count(r => r.Verdict == v)))
            .Where(x => x.n > 0)
            .Select(x => $"{Verdicts.Label(x.v)} {x.n}"));
        var claudeOnly = records.Count(r => r.IsClaudeOnly);
        var replaced = records.Count(r => existing.Contains(r.FindingKey));
        return $"판정 {records.Count}건 기록 — {counts}" +
               (claudeOnly > 0 ? $" (Claude만 지적 {claudeOnly})" : "") +
               (replaced > 0 ? $" · 앞 판정 대체 {replaced}" : "") +
               (linked > 0 ? $" · 지적 ID 연결 {linked}" : "") +
               $" (runId {runId})";
    }

    /// <summary>
    /// findingIds를 풀어 steps·summary·file·severity를 채운다. 지휘자가 준 값이 있으면 그대로 두고, steps는 ID와 일치해야 한다.
    /// 대표는 첫 ID (지휘자가 순서로 고른다). severity는 여러 ID 중 가장 높은 것을 high·medium·low로 바꿔 쓴다.
    /// </summary>
    private static VerdictItem Resolve(VerdictItem it, IReadOnlyDictionary<string, FindingRecord> findings, List<string> errors, int n)
    {
        if (it.FindingIds is not { Length: > 0 } ids) return it;
        var found = new List<FindingRecord>();
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) { errors.Add($"{n}번: findingIds에 빈 값이 있습니다."); continue; }
            if (findings.TryGetValue(id.Trim(), out var f)) found.Add(f);
            else errors.Add($"{n}번: 이 실행에 없는 지적 ID '{id}'" + (findings.Count == 0 ? " (이 실행에는 구조화된 결과가 없습니다)" : ""));
        }
        if (found.Count != ids.Length) return it;

        var idSteps = found.Select(f => int.Parse(f.Id.Split('-', 2)[0])).Distinct().Order().ToArray();
        if (it.Steps is { Length: > 0 } given && !given.Distinct().Order().SequenceEqual(idSteps))
            errors.Add($"{n}번: steps({string.Join(", ", given)})가 findingIds의 단계({string.Join(", ", idSteps)})와 다릅니다.");

        var first = found[0];
        return it with
        {
            Steps = idSteps,
            Summary = string.IsNullOrWhiteSpace(it.Summary) ? first.Problem[..Math.Min(first.Problem.Length, MaxSummaryChars)] : it.Summary,
            File = string.IsNullOrWhiteSpace(it.File) ? first.Location : it.File,
            Severity = string.IsNullOrWhiteSpace(it.Severity) ? found.Select(f => Level(f.Severity)).MinBy(Rank) : it.Severity,
            FindingIds = [.. found.Select(f => f.Id)],
        };

        static string Level(string severity) => severity switch
        {
            "critical" or "major" or "high" => "high",
            "minor" or "medium" => "medium",
            _ => "low",
        };
        static int Rank(string level) => level switch { "high" => 0, "medium" => 1, _ => 2 };
    }
}
