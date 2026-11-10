namespace HB.Chat;

/// <summary>
/// 지휘자가 새 방 열기 (room_open): 호스트가 고칠 수 있는 카드로 묻고, 허용하면 그 값의 표를 남긴다. 앱 MCP 도구(<see cref="RoomTools"/>)는 같은 방 ·
/// 같은 값의 표를 하나 써야 연다 (터미널 시작과 같은 길). 모든 단계 방에서 쓴다 — 늘 카드.
/// </summary>
public sealed partial class RoomHub
{
    /// <summary>방 기록 줄의 도구 이름 (승인 기록 tool 칸) — 요약 칸 = 새 방 id, 결정 칸 = 보일 글.</summary>
    public const string RoomOpenedTool = "새 방";

    /// <summary>
    /// 방마다 카드에서 허용한 새 방 값 (한 번 쓰면 지운다). 터미널 표처럼 실행 수명이다: 실행 토큰을 거두면(실행 끝 · 중지) 그 방 표를 버리고,
    /// 허용 뒤 <see cref="RoomOpenTicketLife"/>가 지나도 버린다 — 남으면 사용자가 중지로 취소한 열기를 다음 실행이 토큰으로 직접 불러 카드 없이 열 수 있었다
    ///. 방마다 상한도 둔다.
    /// </summary>
    private readonly Dictionary<string, List<(RoomOpenSpec Spec, DateTimeOffset At)>> _roomOpenTickets = new(StringComparer.Ordinal);
    private readonly Lock _roomOpenGate = new();
    private const int MaxRoomOpenTickets = 4;
    internal static readonly TimeSpan RoomOpenTicketLife = TimeSpan.FromMinutes(5);

    /// <summary>표 시각 (시험이 바꾼다).</summary>
    internal Func<DateTimeOffset> RoomOpenClock { get; set; } = () => DateTimeOffset.Now;

    internal void ApproveRoomOpen(string roomId, RoomOpenSpec spec)
    {
        lock (_roomOpenGate)
        {
            if (!_roomOpenTickets.TryGetValue(roomId, out var list)) _roomOpenTickets[roomId] = list = [];
            if (list.Count >= MaxRoomOpenTickets) list.RemoveAt(0);
            list.Add((spec, RoomOpenClock()));
        }
    }

    /// <summary>카드에서 허용한 것과 같은 값의 표 하나를 쓴다 (지난 표는 먼저 버린다). 없으면 false.</summary>
    public bool TakeRoomOpen(string roomId, RoomOpenSpec spec)
    {
        lock (_roomOpenGate)
        {
            if (!_roomOpenTickets.TryGetValue(roomId, out var list)) return false;
            var now = RoomOpenClock();
            list.RemoveAll(t => now - t.At > RoomOpenTicketLife);
            var at = list.FindIndex(t => t.Spec == spec);
            if (at >= 0) list.RemoveAt(at);
            if (list.Count == 0) _roomOpenTickets.Remove(roomId);
            return at >= 0;
        }
    }

    /// <summary>그 방의 쓰지 않은 새 방 표를 모두 버린다 (실행 토큰을 거둘 때 · 방을 지울 때).</summary>
    internal void ForgetRoomOpen(string roomId)
    {
        lock (_roomOpenGate) _roomOpenTickets.Remove(roomId);
    }

    /// <summary>
    /// 여는 방 기록에 "새 방" 줄 (화면이 [열기] 링크로 그린다). 진행 칸에도 바로 링크 줄을 보낸다 — 기록 줄은 그 방 턴이 끝나 다시 그릴 때에야 보인다
    /// </summary>
    public void NoteRoomOpened(string fromRoomId, string openedRoomId, string name)
    {
        var text = $"새 방 '{name}'을 열었습니다";
        Log?.Write(fromRoomId, new ApprovalRequest(RoomOpenedTool, openedRoomId, null), text);
        Post(fromRoomId, () => new LiveEvent("room-opened") { Text = text, Detail = openedRoomId });
    }

    /// <summary>room_open 승인 (호스트): 미리 채운 카드 → 사용자가 고친 값을 다시 검사해 표를 남기고, 고친 값으로 입력을 다시 써서 허용한다.</summary>
    private async Task<string> DecideRoomOpenAsync(Room room, Live live, int generation, ToolPermission ask, CancellationToken aborted)
    {
        if (room.GroupId is not null) return PermissionDecision.Deny("단톡방 구성원은 새 방을 열 수 없습니다.");
        if (store.Get(room.Id) is not { } current) return PermissionDecision.Deny("이 방이 없어졌습니다 — 새 방을 열지 않습니다.");
        if (RoomOpen.Build(ask.InputJson, current, options.Value) is not { } card)
            return PermissionDecision.Deny("room_open 입력을 읽지 못해 거절했습니다.");
        var head = card.Prompt.Length > 120 ? card.Prompt[..120] + "…" : card.Prompt;
        var request = new ApprovalRequest(ask.Tool, $"{card.Title ?? card.Workspace}\n{head}", null) { RoomOpen = card };
        var answer = await RequestApprovalAsync(room.Id, live, generation, request, aborted);
        if (answer is not { Allow: true, RoomPick: { } pick })
            return (answer ?? new ApprovalAnswer(false, "실행이 이미 끝나 거절했습니다.")).ToDecisionJson(ask.InputJson);
        if (RoomOpen.Check(pick, options.Value) is { } bad) return PermissionDecision.Deny("카드 값이 맞지 않습니다: " + bad);
        ApproveRoomOpen(room.Id, pick);
        return PermissionDecision.Allow(RoomOpen.Input(pick));
    }

    /// <summary>새 방 카드의 답 (잠금 안에서). 허용은 고른 값이 있어야 하고 새 방 화면과 같은 검사를 지나야 한다.</summary>
    private string? DecideRoomOpenCard(PendingApproval pending, RoomOpenCard card, PermissionLevel? level, ApprovalChoice choice, string? reason,
        RoomOpenSpec? pick)
    {
        if (choice is ApprovalChoice.AllowTurn or ApprovalChoice.AllowFolder) return "이 요청에는 그 단추를 쓸 수 없습니다.";
        if (choice == ApprovalChoice.Allow)
        {
            if (level is null) return "이 방이 없어져 허용할 수 없습니다 — 거절만 됩니다.";
            if (pick is null) return "카드 값을 읽지 못했습니다.";
            if (RoomOpen.Check(pick, options.Value) is { } bad) return bad;
        }
        var message = string.IsNullOrWhiteSpace(reason) ? "사용자가 거절했습니다." : $"사용자가 거절했습니다: {reason.Trim()}";
        if (!pending.Answer.TrySetResult(new ApprovalAnswer(choice == ApprovalChoice.Allow, choice == ApprovalChoice.Deny ? message : null)
            { RoomPick = choice == ApprovalChoice.Allow ? pick : null }))
            return ApprovalDone;
        pending.Label = choice == ApprovalChoice.Deny ? "거절" : RoomOpenLabel(card, pick!);
        return null;
    }

    /// <summary>허용 기록 문구: 고친 것이 있으면 "허용 (고침: …)".</summary>
    public static string RoomOpenLabel(RoomOpenCard card, RoomOpenSpec pick)
    {
        var changes = new List<string>();
        if (pick.Workspace != card.Workspace) changes.Add("폴더 " + pick.Workspace);
        if (RoomOpen.LevelName(pick.Level) != card.Level) changes.Add("단계 " + RoomOpen.LevelName(pick.Level));
        if (pick.Model != card.Model) changes.Add("모델 " + pick.Model);
        if (pick.Title != card.Title) changes.Add("제목");
        if (pick.Prompt != card.Prompt) changes.Add("첫 말");
        return changes.Count == 0 ? "허용" : $"허용 (고침: {string.Join(", ", changes)})";
    }
}
