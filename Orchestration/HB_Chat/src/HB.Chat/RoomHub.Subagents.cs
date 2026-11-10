namespace HB.Chat;

/// <summary>서브에이전트 말풍선의 글 한 덩이 — Narration이면 서버가 요약한 중간 글.</summary>
public sealed record SubagentSaid(string Text, bool Narration);

/// <summary>
/// 진행 칸의 서브에이전트 말풍선 하나 (Agent 호출 = CLI 작업 하나). 바꿀 때마다 새 값으로 갈아 끼운다 —
/// snapshot · 목록 알림이 잠금 밖에서 직렬화돼도 같은 값을 본다.
/// </summary>
public sealed record SubagentBubble(string TaskId, string? ToolUseId, string? Agent, string? Title)
{
    public IReadOnlyList<SubagentSaid> Said { get; init; } = [];
    public int? ToolUses { get; init; }
    public bool Done { get; init; }
    public string? Status { get; init; }

    /// <summary>끝 알림의 summary — 그 서브에이전트의 최종 답.</summary>
    public string? Summary { get; init; }

    /// <summary>SubagentHandback으로 넘긴 최종 답 (있으면 끝에 Summary가 된다). 화면에는 보내지 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Handback { get; init; }
}

/// <summary>
/// 서브에이전트 말 (Claude 앱처럼): 도는 동안 중간 글을 서브에이전트 말풍선에 쌓고, 끝나면 최종 답을 보인다.
/// 끝난 말풍선은 화면 턴이 끝날 때 빼고 — 그 답은 세션 기록(ClaudeChat Subagent 항목)에서 제자리에 다시 그려진다.
/// 도는 말풍선은 턴 사이에도 남겨, 턴 시작 · 끝(화면이 진행 칸을 비울 때) · 다시 붙기(snapshot)에 목록으로 되살린다.
/// </summary>
public sealed partial class RoomHub
{
    /// <summary>방 하나에 둘 말풍선 수 · 말풍선 하나에 둘 글 덩이 수 · 글 합 (글자) — 넘으면 오래된 것부터 뺀다.</summary>
    internal const int MaxSubagentBubbles = 20, MaxSubagentSaid = 30, MaxSubagentChars = 60_000;

    /// <summary>작업 알림 (잠금 안에서): 서브에이전트(local_agent) 시작이면 말풍선을 열고, 진행 · 끝은 연 말풍선에만 반영한다.</summary>
    private static void TrackSubagent(Live live, TaskEvent t)
    {
        var at = live.Subagents.FindIndex(b => b.TaskId == t.TaskId);
        if (at < 0)
        {
            if (t.Phase != "started" || t.Kind != AgentKind) return; // 백그라운드 명령 · 시작을 못 본 작업 (지난 실행에서 정리된 것)
            if (live.Subagents.Count >= MaxSubagentBubbles)
            {
                // 끝난 것부터 뺀다 (화면에 남아도 턴 끝에 정리된다). 도는 것만 차 있으면 새 말풍선을 열지 않는다 — 도는 것을 지우면 화면과 어긋난다
                var old = live.Subagents.FindIndex(b => b.Done);
                if (old < 0) return;
                live.Subagents.RemoveAt(old);
            }
            live.Subagents.Add(new SubagentBubble(t.TaskId, t.ToolUseId, t.Agent, t.Description));
            return;
        }
        var b = live.Subagents[at];
        live.Subagents[at] = t.Phase switch
        {
            // SubagentHandback으로 넘긴 답이 있으면 그것이 최종 답 — 그때 summary는 "delivered as a message" 안내뿐이다
            "done" => b with { Done = true, Status = t.Status, Summary = b.Handback ?? t.Summary ?? b.Summary, ToolUses = t.ToolUses ?? b.ToolUses },
            _ => b with { ToolUses = t.ToolUses ?? b.ToolUses },
        };
    }

    /// <summary>작업 알림을 화면에 보낼 모양 (잠금 안에서, TrackSubagent 뒤): 서브에이전트 말풍선이 있으면 끝 summary를 그 말풍선의 최종 답으로.</summary>
    private static TaskEvent ShownTask(Live live, TaskEvent t) =>
        t.Phase == "done" && live.Subagents.Find(b => b.TaskId == t.TaskId) is { Summary: { } summary } && summary != t.Summary ? t with { Summary = summary } : t;

    /// <summary>
    /// 서브에이전트 글 (잠금 안에서): 그 Agent 호출의 말풍선에 쌓고, 덩이마다 subagent-said 알림. 모르는 호출(시작을 못 봄)의 글은 버린다.
    /// </summary>
    private void SubagentSaidLines(Live live, SubagentToolsEvent ev)
    {
        if (ev.ParentId is not { } parent) return;
        var at = live.Subagents.FindIndex(b => b.ToolUseId == parent);
        if (at < 0) return;
        var b = live.Subagents[at];
        if (ev.Handback is { } handback) b = b with { Handback = handback };
        var said = b.Said.ToList();
        foreach (var (text, narration) in ev.Narrations.Select(n => (n, true)).Concat(ev.Texts.Select(t => (t, false))))
        {
            said.Add(new SubagentSaid(text, narration));
            Broadcast(live, new LiveEvent("subagent-said") { TaskId = b.TaskId, Text = text, Narration = narration });
        }
        while (said.Count > MaxSubagentSaid || (said.Count > 1 && said.Sum(s => s.Text.Length) > MaxSubagentChars)) said.RemoveAt(0);
        live.Subagents[at] = b with { Said = said };
    }

    /// <summary>
    /// 쉬는 실행의 화면 턴이 끝남 (잠금 안에서): 지금 실행의 백그라운드 작업 목록에 남은(도는) 말풍선만 둔다.
    /// 끝난 것은 기록 쪽 말풍선이 대신하고, 목록에 없는 것은 전경(턴보다 오래 못 산다 — 끝 알림 없이 끊긴 것 포함)이거나
    /// 도중에 바뀐 옛 프로세스와 함께 끝난 것이다 (/self-review ad101bcdb6 R-C1 — 남기면 "진행 중" 유령으로 남았다).
    /// </summary>
    private static void KeepRunningSubagents(Live live)
    {
        var running = live.BgTasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        live.Subagents.RemoveAll(b => b.Done || !running.Contains(b.TaskId));
    }

    /// <summary>말풍선 목록 알림 (잠금 안에서, 화면이 진행 칸을 비운 뒤) — 남은 것이 있을 때만.</summary>
    private void SubagentLines(Live live)
    {
        if (live.Subagents.Count > 0) Broadcast(live, SubagentsEvent(live));
    }

    private static LiveEvent SubagentsEvent(Live live) => new("subagents") { Subagents = [.. live.Subagents] };

    /// <summary>
    /// snapshot용: 도는 말풍선만. snapshot은 화면이 기록을 먼저 다시 받으므로, 끝난 것(전경 답은 이미 기록에 있음)을 같이 보내면
    /// 기록 · 진행 칸에 두 번 보인다.
    /// </summary>
    private static IReadOnlyList<SubagentBubble>? SnapshotSubagents(Live live) =>
        live.Subagents.Any(b => !b.Done) ? [.. live.Subagents.Where(b => !b.Done)] : null;
}
