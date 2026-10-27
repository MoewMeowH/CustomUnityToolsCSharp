namespace HB.Orchestration.Shared;

/// <summary>실행 말풍선 종류: 실행 시작 줄 · 외부 에이전트 · API 리뷰(sub_*, claude-cli) · /self-review 기록.</summary>
public enum RunBubbleKind
{
    Start,
    External,
    ApiReview,
    SelfReview,
}

/// <summary>
/// run_mode 실행 하나의 말풍선 (대시보드 채팅방 · HB_Chat 공용, 계획서 2026-10-28_HB_Chat-2차 2-2 e).
/// Situation은 자동 스티커 상황 이름(ChatUi AutoSticker와 같은 값) — 그림으로 바꾸는 것은 화면 몫. CallId는 호출 상세 링크용.
/// </summary>
public sealed record RunBubble(
    string Key, DateTimeOffset At, RunBubbleKind Kind, string Name, string? Detail, string Markdown,
    string? Situation, bool IsError, string RunId, string CallId);

/// <summary>
/// 호출 기록(calls.jsonl)의 실행 하나 → 말풍선들. 대시보드 ChatRoomBuilder에 있던 조립을 옮긴 것 (본문 그대로):
/// 실행 시작 줄(/self-review 기록 실행은 없음) + 단계마다 마지막 시도 (재시도 · 실패 뒤 성공은 호출 상세에서 본다).
/// </summary>
public static class RunBubbles
{
    /// <summary>/self-review 기록 호출의 도구 이름 (Agents VerdictRecorder.SelfReviewTool이 이 값을 쓴다).</summary>
    public const string SelfReviewTool = "self_review";

    /// <summary>자동 스티커 상황 이름 — ChatUi AutoSticker의 같은 이름 상수와 값이 같아야 한다 (대시보드 테스트가 확인).</summary>
    public const string CallNoFindings = "CallNoFindings", CallFindings = "CallFindings", CallWarning = "CallWarning",
        CallRequests = "CallRequests", CallQuota = "CallQuota", CallRateLimit = "CallRateLimit", CallTimeout = "CallTimeout",
        CallFailed = "CallFailed";

    /// <summary>
    /// 한 실행의 호출들 → 말풍선 (시작 줄 · 단계마다 마지막 시도, 시각 순). selfReviewShown이면 /self-review 기록 호출 말풍선은 뺀다
    /// (대시보드는 같은 글을 서브에이전트 말풍선으로 이미 보인다).
    /// </summary>
    public static List<RunBubble> ForRun(string runId, IEnumerable<CallRecord> runCalls, bool selfReviewShown = false)
    {
        var ordered = runCalls.OrderBy(c => c.Timestamp).ToList();
        var bubbles = new List<RunBubble>();
        if (ordered.Count == 0) return bubbles;
        var first = ordered[0];
        var mode = ModeNames.Normalize(first.Mode ?? first.Tool) ?? first.Tool;
        if (first.Tool != SelfReviewTool)
        {
            bubbles.Add(new RunBubble(
                Key: $"r{runId}", At: first.Timestamp, Kind: RunBubbleKind.Start, Name: "실행", Detail: $"{mode} · {runId}",
                Markdown: string.IsNullOrWhiteSpace(first.Reason) ? $"`{mode}` 실행 시작" : $"`{mode}` 실행 시작 — {first.Reason}",
                Situation: null, IsError: false, RunId: runId, CallId: first.Id));
        }

        foreach (var call in ordered.GroupBy(c => c.Step ?? 1).Select(g => g.OrderBy(c => c.Attempt ?? 1).ThenBy(c => c.Timestamp).Last()))
        {
            if (call.Tool == SelfReviewTool && selfReviewShown) continue;
            bubbles.Add(FromCall(call, mode));
        }
        return bubbles;
    }

    public static RunBubble FromCall(CallRecord c, string mode)
    {
        var kind = c.Tool == SelfReviewTool ? RunBubbleKind.SelfReview
            : string.Equals(c.Provider, "claude-cli", StringComparison.OrdinalIgnoreCase) || mode.StartsWith("sub_", StringComparison.Ordinal) ? RunBubbleKind.ApiReview
            : RunBubbleKind.External;
        var detail = string.Join(" · ", new[] { mode, c.Role, c.Step is { } s ? $"{s}단계" : null, c.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string body;
        string? situation;
        if (!c.Success)
        {
            body = $"**실패** ({c.ErrorKind ?? "알 수 없음"}): {c.Error ?? "오류 문구 없음"}";
            situation = c.ErrorKind switch
            {
                "Quota" => CallQuota,
                "RateLimit" => CallRateLimit,
                "Timeout" => CallTimeout,
                "Canceled" => null,
                _ => CallFailed,
            };
        }
        else
        {
            body = c.Structured is { Parsed: true } st ? FindingRenderer.Render(st) : c.Response ?? "";
            situation = c.Structured switch
            {
                { Parsed: true, Requests.Count: > 0 } => CallRequests,
                _ when c.Warnings.Count > 0 => CallWarning,
                { Parsed: true, Findings.Count: 0 } => CallNoFindings,
                { Parsed: true } => CallFindings,
                _ => null,
            };
        }
        if (c.Warnings.Count > 0) body += "\n\n" + string.Join("\n", c.Warnings.Select(w => "> " + w));

        return new RunBubble(
            Key: $"c{c.Id}", At: c.Timestamp.AddMilliseconds(Math.Max(0, c.ElapsedMs)), Kind: kind, Name: c.Agent ?? c.Model,
            Detail: detail, Markdown: body, Situation: situation, IsError: !c.Success, RunId: c.RunId ?? "", CallId: c.Id);
    }
}
