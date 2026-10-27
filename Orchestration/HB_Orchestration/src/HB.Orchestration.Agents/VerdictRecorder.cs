using System.ComponentModel;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>record_verdicts의 지적 하나. MCP 스키마에 그대로 나가므로 설명은 짧게 둔다 (상세 규칙은 CLAUDE.md).</summary>
public sealed record VerdictItem(
    [property: Description("실행 안에서 지적을 구별하는 짧은 키. 같은 키로 다시 쓰면 앞 판정을 대체. 권장: 첫 지적 ID")] string FindingKey,
    [property: Description("지적 한 줄 요약. findingIds를 주면 비워도 됨")] string? Summary,
    [property: Description("지적: adopted · partial · rejected · deferred · duplicate. 자료 요청(findingIds에 1-N1): supplied · declined · deferred")] string Verdict,
    [property: Description("이 지적을 낸 단계 번호들. 비우면 Claude만 지적")] int[]? Steps = null,
    [property: Description("판정 근거. adopted가 아니면 필수")] string? Reason = null,
    [property: Description("high · medium · low (선택)")] string? Severity = null,
    [property: Description("ownReview에도 있던 지적인지")] bool ClaudeAlsoFound = false,
    [property: Description("관련 파일 path 또는 path:line (선택)")] string? File = null,
    [property: Description("구조화 결과의 지적 ID (예: 1-F2) 또는 자료 요청 ID (예: 1-N1). 주면 steps·summary·file·severity를 채운다. 요청 처리는 반드시 여기에 요청 ID (findingKey만으로는 지적으로 검사됨)")] string[]? FindingIds = null);

/// <summary>
/// /self-review의 self-reviewer 실행 (record_verdicts에 runId 대신 준다). 서버가 calls.jsonl에 실행 1건(단계 1 = self-reviewer)을 만든다.
/// 토큰은 InputTokens·OutputTokens에 넣지 않는다 — 대시보드 오늘 사용량은 외부 에이전트 기준 (계획서 2026-10-20_리뷰조건-self-reviewer).
/// </summary>
public sealed record SelfReviewInfo(
    [property: Description("self-reviewer 결과 원문 (## 지적 · ## 자료 부족 · ## 읽은 파일 · ## 비고)")] string Review,
    [property: Description("위임 메시지의 변경 의도·제약 (요약 가능)")] string Intent,
    [property: Description("대상 파일 (프로젝트 루트 기준, 선택)")] string[]? Files = null,
    [property: Description("서브에이전트 모델 (기본 opus)")] string? Model = null,
    [property: Description("작업 완료 알림의 subagent_tokens (선택)")] long? SubagentTokens = null,
    [property: Description("작업 완료 알림의 tool_uses (선택)")] int? ToolUses = null,
    [property: Description("작업 완료 알림의 duration_ms (선택)")] long? DurationMs = null,
    [property: Description("리뷰를 한 이유 한 줄 (선택)")] string? Reason = null,
    [property: Description("검토한 서브에이전트: self-reviewer(기본) · perf-reviewer · security-reviewer · direction-reviewer")] string? Agent = null);

/// <summary>
/// record_verdicts: 결과 종합 뒤 지적마다 판정을 verdicts.jsonl에 남긴다 (에이전트·모드별 채택률의 원본).
/// 하나라도 틀리면 아무것도 쓰지 않는다. 실패는 예외가 아니라 "[판정 기록 불가] …" 문자열.
/// </summary>
public static class VerdictRecorder
{
    public const int MaxItems = 50;
    public const int MaxKeyChars = 80;
    public const int MaxSummaryChars = 300;

    public const string SelfReviewTool = "self_review";
    public const string SelfReviewRole = "SelfReviewer";
    public const string SelfReviewAgent = "self-reviewer";

    /// <summary>
    /// selfReview.agent로 받는 검토형 서브에이전트 (계획서 2026-10-23_서브에이전트-종류-추가). 대시보드가 에이전트별 채택률을 낸다.
    /// 대시보드 '서브에이전트 결과'가 보관하는 대상과 같은 목록 (계획서 2026-10-27_서브에이전트-결과-보존).
    /// </summary>
    public static IReadOnlyList<string> SelfReviewAgents => SubagentArchive.ReviewerTypes;
    public const string SelfReviewProvider = "claude-subagent";

    /// <summary>같은 self-reviewer 원문을 중복으로 보는 기간 (일).</summary>
    public const int DuplicateWindowDays = 7;

    /// <summary>실패 결과의 머리. 세션 기록 요약이 이 머리로 실패한 호출을 거른다 (ClaudeTranscriptReader.VerdictFailPrefix).</summary>
    private const string Fail = ClaudeTranscriptReader.VerdictFailPrefix + " ";
    private static readonly string[] Severities = ["high", "medium", "low"];

    /// <param name="maxReasonChars">Reason 글자 수 상한 (Records.MaxReasonChars, 0 = 무제한).</param>
    /// <param name="logPath">호출 기록 파일. null이면 기본 경로. 판정 파일은 그 옆 (<see cref="VerdictLog.PathFor"/>).</param>
    public static string Record(string? runId, IReadOnlyList<VerdictItem>? items, int maxReasonChars, string? logPath = null)
    {
        if (string.IsNullOrWhiteSpace(runId)) return Fail + "runId가 비어 있습니다. run_mode 결과 첫 줄의 runId를 넘기세요 (/self-review는 runId 대신 selfReview).";
        runId = runId.Trim();
        if (CheckCount(items) is { } countError) return countError;

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

        var prepared = Prepare(runId, runCalls, items!, maxReasonChars, logPath);
        return prepared.Error ?? Commit(prepared, null);
    }

    /// <summary>
    /// /self-review: self-reviewer 실행 기록을 만들고 판정을 남긴다. 판정 검사를 먼저 하고, 통과할 때만 실행 기록과 판정을 쓴다
    /// (판정 없는 빈 실행이 남지 않게). self-reviewer 지적은 steps [1], 지휘자 추가는 steps 없음.
    /// </summary>
    /// <param name="maxReviewChars">review·intent 글자 수 상한 (Records.MaxOwnReviewChars — run_mode ownReview와 같은 기준, 0 = 무제한).</param>
    public static async Task<string> RecordSelfReviewAsync(SelfReviewInfo? info, IReadOnlyList<VerdictItem>? items, int maxReasonChars,
        string? logPath = null, int maxReviewChars = 0)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Review)) return Fail + "selfReview.review(self-reviewer 결과 원문)가 비어 있습니다.";
        if (SelfReviewFormatProblem(info.Review) is { } problem) return Fail + $"selfReview.review가 self-reviewer 결과 원문 형식이 아닙니다: {problem}.";
        if (string.IsNullOrWhiteSpace(info.Intent)) return Fail + "selfReview.intent(변경 의도)가 비어 있습니다.";
        if (maxReviewChars > 0 && (info.Review.Length > maxReviewChars || info.Intent.Length > maxReviewChars))
            return Fail + $"selfReview.review·intent는 각각 {maxReviewChars:N0}자 이내입니다 (review {info.Review.Length:N0}자 · intent {info.Intent.Length:N0}자).";
        if (info.Reason is { } r && maxReasonChars > 0 && r.Length > maxReasonChars)
            return Fail + $"selfReview.reason은 {maxReasonChars:N0}자 이내입니다 ({r.Length:N0}자).";
        var agentName = string.IsNullOrWhiteSpace(info.Agent) ? SelfReviewAgent : info.Agent.Trim().ToLowerInvariant();
        if (!SelfReviewAgents.Contains(agentName))
            return Fail + $"selfReview.agent '{info.Agent}'는 {string.Join(" · ", SelfReviewAgents)} 중 하나여야 합니다.";
        if (CheckCount(items) is { } countError) return countError;

        // 응답을 못 받아 같은 원문으로 다시 부르면 같은 리뷰가 두 번 세진다 → 최근 기록에 같은 원문이 있으면 그 runId로 안내.
        try
        {
            if (CallLog.ReadSince(DateTimeOffset.Now.AddDays(-DuplicateWindowDays), logPath)
                    .FirstOrDefault(c => c.Tool == SelfReviewTool && c.Response == info.Review) is { RunId: { } dup })
                return Fail + $"같은 self-reviewer 원문이 이미 기록돼 있습니다 (runId {dup}). 판정을 다시 쓰려면 selfReview 없이 runId {dup}로 부르세요.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail + $"호출 기록을 읽지 못했습니다 ({ex.GetType().Name}: {ex.Message}).";
        }

        var runId = Guid.NewGuid().ToString("N")[..10];
        var now = DateTimeOffset.Now;
        var usage = new System.Text.Json.Nodes.JsonObject
        {
            ["subagent_tokens"] = info.SubagentTokens,
            ["tool_uses"] = info.ToolUses,
        };
        var call = new CallRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            // 실행 시작 시각 ≈ 기록 시각 − 서브에이전트 실행 시간 (판정 화면의 기간 기준이 실행 시작 시각이라).
            Timestamp = info.DurationMs is > 0 and var ms ? now.AddMilliseconds(-ms) : now,
            Tool = SelfReviewTool,
            RunId = runId,
            Mode = SelfReviewTool,
            Role = SelfReviewRole,
            Step = 1,
            Attempt = 1,
            Agent = agentName,
            Provider = SelfReviewProvider,
            Model = string.IsNullOrWhiteSpace(info.Model) ? "opus" : info.Model.Trim(),
            ProjectDir = Environment.CurrentDirectory,
            Prompt = info.Intent,
            Response = info.Review,
            UsageRaw = usage.ToJsonString(),
            ElapsedMs = info.DurationMs ?? 0,
            Success = true,
            Files = info.Files?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? [],
            Reason = string.IsNullOrWhiteSpace(info.Reason) ? null : info.Reason.Trim(),
        };

        var prepared = Prepare(runId, [call], items!, maxReasonChars, logPath);
        if (prepared.Error is not null) return prepared.Error;
        try
        {
            await CallLog.AppendAsync(call, logPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail + $"실행 기록 쓰기 실패 ({ex.GetType().Name}: {ex.Message}).";
        }
        return Commit(prepared, "self-review 실행");
    }

    /// <summary>
    /// self-reviewer 결과 원문 형식 (H4 훅과 같은 기준: ## 지적 · ## 읽은 파일 절). 맞으면 null.
    /// ## 비고(설명 줄, 2026-10-22 추가)는 선택 절이라 검사하지 않는다. 경로 검사는 H4만 한다.
    /// </summary>
    internal static string? SelfReviewFormatProblem(string review)
    {
        var headings = review.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        if (!headings.Contains("## 지적")) return "'## 지적' 절이 없습니다";
        if (!headings.Contains("## 읽은 파일")) return "'## 읽은 파일' 절이 없습니다";
        return null;
    }

    private static string? CheckCount(IReadOnlyList<VerdictItem>? items) =>
        items is null || items.Count == 0 ? Fail + "items가 비어 있습니다."
        : items.Count > MaxItems ? Fail + $"한 번에 {MaxItems}건까지입니다 ({items.Count}건). 나눠서 호출하세요."
        : null;

    private sealed record Prepared(string? Error, string RunId, List<VerdictRecord> Records, HashSet<string> Existing, string Path);

    private static Prepared Prepare(string runId, List<CallRecord> runCalls, IReadOnlyList<VerdictItem> items, int maxReasonChars, string? logPath)
    {
        Prepared Failed(string error) => new(error, runId, [], [], "");
        var steps = runCalls.Where(r => r.Step is not null).Select(r => r.Step!.Value).ToHashSet();

        // 구조화 출력의 지적 (단계의 대표 호출 = 성공한 마지막 시도). ID는 실행 안에서 유일하다.
        var findings = runCalls.Where(r => r.Step is not null && r.Success && r.Structured is { Parsed: true })
            .GroupBy(r => r.Step!.Value).Select(g => g.MaxBy(r => r.Attempt ?? 1)!)
            .SelectMany(r => r.Structured!.Findings)
            .ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        // 자료 요청 (1-N1). 판정은 채택률과 따로 — 보충함 · 보내지 않음 · 보류.
        var requests = runCalls.Where(r => r.Step is not null && r.Success && r.Structured is { Parsed: true })
            .GroupBy(r => r.Step!.Value).Select(g => g.MaxBy(r => r.Attempt ?? 1)!)
            .SelectMany(r => r.Structured!.Requests)
            .ToDictionary(q => q.Id, StringComparer.OrdinalIgnoreCase);
        var resolved = new VerdictItem[items.Count];
        var kinds = new bool[items.Count];
        var errors = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not { } original) { errors.Add($"{i + 1}번: 항목이 비어 있습니다 (null)."); continue; }
            var isRequest = IsRequestItem(original, requests, errors, i + 1);
            var it = resolved[i] = isRequest ? ResolveRequest(original, requests, errors, i + 1) : Resolve(original, findings, errors, i + 1);
            kinds[i] = isRequest;
            var at = $"{i + 1}번({it.FindingKey})";
            if (string.IsNullOrWhiteSpace(it.FindingKey)) { errors.Add($"{i + 1}번: findingKey가 비어 있습니다."); continue; }
            if (it.FindingKey.Trim().Length > MaxKeyChars || it.FindingKey.Contains('\n'))
                errors.Add($"{at}: findingKey는 한 줄 {MaxKeyChars}자 이내입니다.");
            if (!keys.Add(it.FindingKey.Trim())) errors.Add($"{at}: 같은 호출 안에서 findingKey가 겹칩니다.");
            if (string.IsNullOrWhiteSpace(it.Summary)) errors.Add($"{at}: summary가 비어 있습니다.");
            else if (it.Summary.Length > MaxSummaryChars) errors.Add($"{at}: summary는 {MaxSummaryChars}자 이내입니다 ({it.Summary.Length}자).");

            var verdict = it.Verdict?.Trim().ToLowerInvariant();
            var allowed = kinds[i] ? Verdicts.ForRequests : Verdicts.All;
            var free = kinds[i] ? Verdicts.Supplied : Verdicts.Adopted; // reason 없이 쓸 수 있는 판정
            if (verdict is null || !allowed.Contains(verdict))
                errors.Add($"{at}: {(kinds[i] ? "자료 요청의 " : "")}verdict '{it.Verdict}'는 {string.Join(" · ", allowed)} 중 하나여야 합니다." +
                           (RequestHint(original, verdict, kinds[i], requests) ? " 자료 요청 처리는 findingIds에 요청 ID를 넣으세요 (예: findingIds [\"1-N1\"])." : ""));
            else if (verdict != free && string.IsNullOrWhiteSpace(it.Reason))
                errors.Add($"{at}: {Verdicts.Label(verdict)}({verdict})은 reason이 필수입니다.");
            if (it.Reason is not null && maxReasonChars > 0 && it.Reason.Length > maxReasonChars)
                errors.Add($"{at}: reason은 {maxReasonChars:N0}자 이내입니다 ({it.Reason.Length:N0}자).");

            if (it.Severity is { } sev && !string.IsNullOrWhiteSpace(sev) && !Severities.Contains(sev.Trim().ToLowerInvariant()))
                errors.Add($"{at}: severity '{sev}'는 high · medium · low 중 하나여야 합니다.");

            if (it.Steps?.Where(s => !steps.Contains(s)).ToList() is { Count: > 0 } missing)
                errors.Add($"{at}: 이 실행에 없는 단계 {string.Join(", ", missing)} (있는 단계: {string.Join(", ", steps.Order())}).");
        }
        if (errors.Count > 0) return Failed(Fail + "아무것도 기록하지 않았습니다.\n- " + string.Join("\n- ", errors));

        HashSet<string> existing;
        string path;
        try
        {
            path = VerdictLog.PathFor(logPath);
            existing = VerdictLog.ReadAll(path).Where(v => v.RunId == runId).Select(v => v.FindingKey).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failed(Fail + $"판정 기록을 읽지 못했습니다 ({ex.GetType().Name}: {ex.Message}).");
        }

        var now = DateTimeOffset.Now;
        var records = resolved.Select((it, index) =>
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
                Kind = kinds[index] ? Verdicts.RequestKind : null,
            };
        }).ToList();
        return new Prepared(null, runId, records, existing, path);
    }

    /// <param name="created">새로 만든 실행이면 결과 앞에 붙일 이름 (runId를 지휘자에게 알려 줌).</param>
    private static string Commit(Prepared p, string? created)
    {
        var (runId, records, existing) = (p.RunId, p.Records, p.Existing);
        try
        {
            VerdictLog.Append(records, p.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail + $"쓰기 실패 ({ex.GetType().Name}: {ex.Message})." + (created is null ? "" : $" {created} 기록(runId {runId})은 남았습니다 — 같은 runId로 다시 기록하세요.");
        }

        var linked = records.Count(r => r.FindingIds is not null);
        var counts = string.Join(" · ", Verdicts.All.Concat(Verdicts.ForRequests).Distinct()
            .Select(v => (v, n: records.Count(r => r.Verdict == v)))
            .Where(x => x.n > 0)
            .Select(x => $"{Verdicts.Label(x.v)} {x.n}"));
        var claudeOnly = records.Count(r => r.IsClaudeOnly && !r.IsRequest);
        var requestCount = records.Count(r => r.IsRequest);
        var replaced = records.Count(r => existing.Contains(r.FindingKey));
        return (created is null ? "" : $"{created} 기록 runId: {runId}\n") +
               $"판정 {records.Count}건 기록 — {counts}" +
               (claudeOnly > 0 ? $" (Claude만 지적 {claudeOnly})" : "") +
               (replaced > 0 ? $" · 앞 판정 대체 {replaced}" : "") +
               (linked > 0 ? $" · 지적 ID 연결 {linked}" : "") +
               (requestCount > 0 ? $" · 자료 요청 처리 {requestCount}" : "") +
               $" (runId {runId})";
    }

    /// <summary>
    /// 지적으로 검사돼 거절된 항목이 사실 자료 요청 처리였을 것 같으면 true: findingIds 없이 요청용 verdict를 썼거나 findingKey가 요청 ID.
    /// 안내만 한다 — 자동으로 요청 처리로 바꾸면 우연히 같은 키를 쓴 지적의 판정 종류가 조용히 바뀐다 (계획서 2026-10-23_record_verdicts-자료요청-안내).
    /// </summary>
    private static bool RequestHint(VerdictItem it, string? verdict, bool isRequest, IReadOnlyDictionary<string, InfoRequest> requests) =>
        !isRequest && it.FindingIds is not { Length: > 0 }
        && (verdict is Verdicts.Supplied or Verdicts.Declined || (it.FindingKey is { } key && requests.ContainsKey(key.Trim())));

    /// <summary>findingIds가 모두 자료 요청 ID면 true. 지적 ID와 섞이면 오류 (판정 종류가 다르다).</summary>
    private static bool IsRequestItem(VerdictItem it, IReadOnlyDictionary<string, InfoRequest> requests, List<string> errors, int n)
    {
        if (it.FindingIds is not { Length: > 0 } ids) return false;
        var asRequest = ids.Count(id => id is not null && requests.ContainsKey(id.Trim()));
        if (asRequest > 0 && asRequest < ids.Length)
            errors.Add($"{n}번: 지적 ID와 자료 요청 ID(…-N…)를 한 판정에 섞을 수 없습니다.");
        return asRequest > 0 && asRequest == ids.Length;
    }

    /// <summary>요청 ID를 풀어 steps·summary·file을 채운다 (severity 없음).</summary>
    private static VerdictItem ResolveRequest(VerdictItem it, IReadOnlyDictionary<string, InfoRequest> requests, List<string> errors, int n)
    {
        var found = it.FindingIds!.Select(id => requests[id.Trim()]).ToList();
        var idSteps = found.Select(q => int.Parse(q.Id.Split('-', 2)[0])).Distinct().Order().ToArray();
        if (it.Steps is { Length: > 0 } given && !given.Distinct().Order().SequenceEqual(idSteps))
            errors.Add($"{n}번: steps({string.Join(", ", given)})가 요청 ID의 단계({string.Join(", ", idSteps)})와 다릅니다.");
        var first = found[0];
        return it with
        {
            Steps = idSteps,
            Summary = string.IsNullOrWhiteSpace(it.Summary) ? first.What[..Math.Min(first.What.Length, MaxSummaryChars)] : it.Summary,
            File = string.IsNullOrWhiteSpace(it.File) ? first.Path : it.File,
            FindingIds = [.. found.Select(q => q.Id)],
            ClaudeAlsoFound = false,
        };
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
