using System.Collections.Concurrent;

namespace HB.Chat;

/// <summary>
/// 놀이터 진행기: 사용자가 시작하면 구성원이 한 명씩 차례로 말한다 — 다음 차례는 사람이 @로 정한 구성원, 아니면 답에서 @부른 구성원, 아니면 순서상 다음.
/// 상한(말 · 분 · 추정 비용)에 닿으면 첫 구성원이 마무리 요약을 한 번 하고 끝난다. 일반 단톡방의 이어 부르기(PlanChain · 차례 대기열)는 쓰지 않는다 —
/// 답 처리(OnTurnEnded · 턴 없는 쉼)에서 놀이터면 이 진행기만 다음을 정한다 (설계 검토 b420efd860 R2: 둘 중 하나만).
/// 모두 단톡방 잠금 안에서 부른다.
/// </summary>
public sealed partial class GroupChat
{
    /// <summary>지금 시각 (시험이 바꾼다).</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    private Timer? _clock;

    /// <summary>
    /// 놀이터 만들기: 판 폴더 → 앱 작업 폴더 등록 → 단톡방(구성원 자식 방, 기존 되돌리기) → Codex 허용 순서. 실패하면 앞 단계를 되돌린다 (설계 검토 R5).
    /// Claude 구성원은 놀이터 단계(명령 단계 인자에서 명령 도구를 뺀 것 — <see cref="ClaudeArgs"/>), Codex 구성원은 읽기 전용 해설자.
    /// </summary>
    public (Room? Group, string? Error) CreatePlayground(string model, string? effort, string? title, IReadOnlyList<GroupMemberSpec> members,
        string? topic, int maxMessages, int maxMinutes, double maxCostUsd)
    {
        if (Playground.Check(topic, maxMessages, maxMinutes, maxCostUsd) is { } bad) return (null, bad);
        if (_options?.Value is not { AppWorkspaces: { } workspaces } o) return (null, "작업 폴더 목록이 없어 놀이터를 만들 수 없습니다.");
        if (string.IsNullOrWhiteSpace(title)) return (null, "놀이터 이름을 쓰세요.");
        var stamp = Now().ToLocalTime().ToString("yyyyMMdd-HHmm");
        var id = Guid.NewGuid().ToString("N")[..6];
        var name = $"놀이터-{stamp}-{id}";
        string folder;
        try
        {
            folder = Directory.CreateDirectory(Path.Combine(o.PlaygroundFolder, $"{stamp}-{id}")).FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, $"놀이터 폴더를 만들지 못했습니다: {ex.Message}");
        }
        void RemoveFolder()
        {
            try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (workspaces.Add(name, folder, _store.All()) is { } addError)
        {
            RemoveFolder();
            return (null, $"놀이터 폴더를 작업 폴더로 등록하지 못했습니다: {addError}");
        }
        var codex = members.Any(m => m.Engine == RoomEngines.Codex);
        var allowedByUs = false;
        void Undo()
        {
            if (allowedByUs && CodexSettings is { } cs)
                try { cs.Remove(name); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            workspaces.Remove(name, _store.All());
            RemoveFolder();
        }
        if (codex && CodexSettings is { } settings)
        {
            try
            {
                settings.Add(name);
                allowedByUs = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Undo();
                return (null, $"Codex 허용 목록에 놀이터 폴더를 더하지 못했습니다: {ex.Message}");
            }
        }
        // Claude 구성원은 명령 단계 값(작업 폴더 편집 묻지 않음)으로 만든다 — 명령 도구는 놀이터라서 빠진다. 구성원 작업 폴더 칸은 쓰지 않는다
        var specs = members.Select(m => m with
        {
            Workspace = name,
            Level = m.Engine == RoomEngines.Codex ? PermissionLevel.Read : PermissionLevel.Command,
        }).ToList();
        var (group, error) = Create(name, PermissionLevel.Command, model, effort, title, specs, baseMcp: false);
        if (group is null)
        {
            Undo();
            return (null, error);
        }
        try
        {
            group = _store.Update(group.Id, r => r with
            {
                Playground = new Playground
                {
                    Topic = topic!.Trim(), MaxMessages = maxMessages, MaxMinutes = maxMinutes, MaxCostUsd = maxCostUsd,
                    Folder = folder, CodexAllowedByUs = allowedByUs,
                },
            }) ?? group;
            return (group, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try { _store.RemoveWhere(r => r.Id == group.Id || r.GroupId == group.Id); } catch (Exception again) when (again is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            Undo();
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// 휴지통에서 완전히 지운 놀이터 정리 (<see cref="Trash"/>): 판 폴더 작업 폴더 등록을 빼고, 이 판이 더한 Codex 허용만 뺀다 (설계 검토 R5 — 사용자가 원래 허용한 항목은 두기).
    /// 판 폴더 파일은 남긴다 (사용자 결정). 빼지 못하면 그대로 둔다 — 설정 화면에서 지울 수 있다.
    /// </summary>
    public void ForgetPlayground(Room group)
    {
        if (group.Playground is not { } pg) return;
        // 자동 Codex 허용은 작업 폴더를 다른 방(갈래 등)이 써도 뺀다 — 판 기록이 지워지면 주인 없이 남았다 (security review C4)
        if (pg.CodexAllowedByUs && CodexSettings is { } cs)
            try { cs.Remove(group.Workspace); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (_options?.Value.AppWorkspaces is not { } workspaces) return;
        var rooms = _store.All();
        if (rooms.Any(r => string.Equals(r.Workspace, group.Workspace, StringComparison.OrdinalIgnoreCase))) return; // 다른 방이 그 이름을 쓰면 등록은 두기
        workspaces.Remove(group.Workspace, rooms);
    }

    /// <summary>놀이터 구성원 방인지 (부모 단톡방이 놀이터). 부모를 찾지 못하면 놀이터로 본다 — 일반 권한으로 돌아가지 않게 (설계 검토 R3, fail-closed).</summary>
    public static bool IsPlaygroundMember(RoomStore store, Room room) =>
        room.GroupId is { } groupId && (store.Get(groupId) is not { } group || group.Playground is not null);

    /// <summary>구성원 Claude 추정 비용 합 (자식 방 누적값).</summary>
    private double CostNow(Room group) => group.Members.Sum(m => _store.Get(m.RoomId)?.TotalCostUsd ?? 0);

    /// <summary>시작 · 계속. 오류 문구(null이면 됨).</summary>
    public string? PlaygroundStart(string groupId)
    {
        lock (_gates.GetOrAdd(groupId, _ => new Lock()))
        {
            if (_store.Get(groupId) is not { Playground: { } pg } group) return "놀이터가 아닙니다.";
            if (group.DeletedAt is not null) return "휴지통에 있는 놀이터입니다.";
            if (group.Members.Count == 0 || group.Members.Any(m => m.RoomId.Length == 0)) return "놀이터가 아직 준비되지 않았습니다.";
            var now = Now();
            switch (pg.State)
            {
                case PlaygroundState.Done: return "끝난 놀이터입니다 — 새 놀이터를 만드세요.";
                case PlaygroundState.Running: return "이미 진행 중입니다.";
                case PlaygroundState.Waiting:
                {
                    var first = group.Members[0];
                    // 받는 구성원(To)은 달지 않는다 — 첫 차례가 실패하면 "답이 없습니다 (실행이 끊김) — 다시 불러 주세요"가 남았다 (self review C8)
                    try { _log.Append(groupId, new GroupEntry(0, now, GroupEntryKind.User, null, pg.Topic)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return $"대화 줄을 쓰지 못했습니다: {ex.Message}"; }
                    var started = pg with { State = PlaygroundState.Running, RunningSince = now, CostAtStart = CostNow(group) };
                    if (SavePlayground(groupId, _ => started) is null) return "놀이터 상태를 저장하지 못했습니다.";
                    if (SendTurn(groupId, first, PlaygroundRules.StartText(started), count: true) is { } error)
                        Halt(groupId, PlaygroundState.Paused, $"{first.Name}에게 보내지 못해 일시 정지했습니다: {error}");
                    break;
                }
                case PlaygroundState.Paused:
                    if (SavePlayground(groupId, p => p with { State = PlaygroundState.Running, RunningSince = now, FailStreak = 0 }) is null)
                        return "놀이터 상태를 저장하지 못했습니다.";
                    Note(groupId, "놀이터를 계속합니다.");
                    // 도는 답이 있으면(일시 정지한 사이 아직 답하는 중) 그 답이 끝날 때 — 끝난 뒤 방이 쉬면 OnActivity가 다시 깨운다(PlaygroundIdle)
                    if (!group.Members.Any(m => Active(m.RoomId, null))) Advance(groupId, pg.LastMemberId);
                    break;
            }
        }
        Announce(groupId);
        EnsureClock();
        return null;
    }

    /// <summary>일시 정지: 도는 답은 끝까지 두고 다음 차례를 보내지 않는다.</summary>
    public string? PlaygroundPause(string groupId)
    {
        lock (_gates.GetOrAdd(groupId, _ => new Lock()))
        {
            if (_store.Get(groupId) is not { Playground: { } pg }) return "놀이터가 아닙니다.";
            if (pg.State != PlaygroundState.Running) return "진행 중이 아닙니다.";
            Halt(groupId, PlaygroundState.Paused, "일시 정지했습니다 — 도는 답은 끝까지 두고 다음 차례를 보내지 않습니다.");
        }
        Announce(groupId);
        return null;
    }

    /// <summary>멈춤: 판을 끝내고 도는 답을 멈춘다.</summary>
    public string? PlaygroundStop(string groupId)
    {
        lock (_gates.GetOrAdd(groupId, _ => new Lock()))
        {
            if (_store.Get(groupId) is not { Playground: { } pg }) return "놀이터가 아닙니다.";
            if (pg.State == PlaygroundState.Done) return "이미 끝난 놀이터입니다.";
            Halt(groupId, PlaygroundState.Done, "사용자가 멈췄습니다.");
        }
        Stop(groupId); // 끝 상태를 먼저 적어 멈춘 답이 다음 차례를 부르지 않게
        return null;
    }

    /// <summary>
    /// 놀이터의 사람 말 (끼어들기): 거절하지 않고 사용자 줄로 남긴다 — 다음 차례 구성원의 새 줄에 같이 간다. @이름이 있으면 그 구성원이 다음 차례.
    /// 도는 중인데 아무도 답하지 않는 틈(보내기 실패 뒤 등)이면 바로 다음 차례를 보낸다. 단톡방 잠금 안에서.
    /// </summary>
    private string? PlaygroundSay(string groupId, Room group, Playground pg, Mentions.Result parsed, string text)
    {
        if (pg.State == PlaygroundState.Done) return "끝난 놀이터입니다 — 새 놀이터를 만드세요.";
        // 글에 처음 나온 @이름 (구성원 순서가 아니라 — 사용자 결정 "첫 @이름", review 1-F5 · self C2)
        var named = parsed.Names.Select(n => group.Members.FirstOrDefault(m => m.Name == n)).FirstOrDefault(m => m is not null);
        // 받는 구성원(To)은 달지 않는다 — 바로 보내는 말이 아니라 화면이 "답이 없습니다 (실행이 끊김)"을 그렸다 (5297 실제 확인)
        _log.Append(groupId, new GroupEntry(0, Now(), GroupEntryKind.User, null, text));
        if (named is not null)
        {
            SavePlayground(groupId, p => p with { NextMemberId = named.Id });
            Note(groupId, $"다음 차례를 {named.Name}(으)로 정했습니다.");
        }
        if (pg.State == PlaygroundState.Waiting)
            Note(groupId, "시작 전 말입니다 — 시작하면 첫 구성원이 주제와 함께 봅니다.");
        else if (pg.State == PlaygroundState.Running && !pg.Wrapping && !group.Members.Any(m => Active(m.RoomId, null)))
            Advance(groupId, pg.LastMemberId);
        Announce(groupId);
        return null;
    }

    /// <summary>
    /// 구성원 방이 쉼으로 돌아갔을 때 (단톡방 잠금 안): 도는 판인데 아무도 돌지 않으면 다음 차례를 보낸다. 답 처리(TurnEnded)는 그 방이 아직 Busy일 때 불려,
    /// 그 틈에 끝난 다른 답 · 계속 단추가 "도는 구성원이 있다"고 보고 다음을 보내지 않으면 판이 진행 중인 채 멈췄다 (review 1-F2 · self C4).
    /// </summary>
    private void PlaygroundIdle(string groupId)
    {
        if (_store.Get(groupId) is not { Playground: { State: PlaygroundState.Running, Wrapping: false } pg } group) return;
        if (group.Members.Any(m => Active(m.RoomId, null))) return;
        Advance(groupId, pg.LastMemberId);
    }

    /// <summary>
    /// 구성원 답이 끝난 뒤 (답 줄을 올린 다음, 단톡방 잠금 안): 마무리였으면 끝, 실패가 이어지면 일시 정지, 상한이면 마무리 요약, 아니면 다음 차례.
    /// </summary>
    /// <param name="answer">성공한 답 글 (다음 차례 @ 찾기). 실패 · 빈 답이면 null.</param>
    private void PlaygroundAfter(string groupId, string memberId, string? answer)
    {
        if (_store.Get(groupId) is not { Playground: { } pg } group) return;
        var ok = !string.IsNullOrWhiteSpace(answer);
        // 답에서 @부른 구성원은 일시 정지 중에 끝난 답이어도 저장해 둔다 — 계속할 때 쓴다 (review 1-F4)
        var called = ok ? CalledIn(group, memberId, answer!) : null;
        if (SavePlayground(groupId, p => p with
            {
                CurrentMemberId = p.CurrentMemberId == memberId ? null : p.CurrentMemberId,
                LastMemberId = memberId,
                FailStreak = ok ? 0 : p.FailStreak + 1,
                AnswerNextId = called?.Id,
            }) is not { } now) return;
        if (now.State == PlaygroundState.Done) return; // 이미 끝남 (멈춤 단추 뒤 멈춘 답) — 끝난 까닭을 덮지 않는다 (self C3)
        if (pg.Wrapping)
        {
            // 마무리 차례: 도는 판이면 끝, 일시 정지면 계속할 때 다시 마무리. 실패 · 중지된 마무리는 그대로 적는다 (self C3)
            if (now.State == PlaygroundState.Running)
                Halt(groupId, PlaygroundState.Done, ok ? "마무리 요약까지 끝났습니다." : "마무리 요약이 실패하거나 멈춰 끝냈습니다.");
            return;
        }
        if (now.State != PlaygroundState.Running) return; // 일시 정지: 다음을 보내지 않는다
        if (group.Members.Any(m => m.Id != memberId && Active(m.RoomId, null))) return; // 다른 구성원이 도는 중 — 그 방이 쉬면 PlaygroundIdle이 이어 간다
        // 상한이 먼저 — 시간 감시가 멈춘 답이 실패로 세어져 마무리 대신 일시 정지로 가지 않게 (self C7)
        if (now.FailStreak >= Playground.MaxFailStreak && CapReached(group, now) is null)
        {
            Halt(groupId, PlaygroundState.Paused, $"실패 · 빈 답이 {now.FailStreak}번 이어져 일시 정지했습니다 — '계속'을 누르면 다음 차례부터.");
            return;
        }
        Advance(groupId, memberId);
    }

    /// <summary>답에서 @부른 구성원 (자기 빼고, 글에 나온 차례). 없으면 null.</summary>
    private static GroupMember? CalledIn(Room group, string memberId, string answer)
    {
        var parsed = Mentions.Parse(answer, [.. group.Members.Select(m => m.Name)]);
        return parsed.Names.Select(n => group.Members.FirstOrDefault(m => m.Name == n)).FirstOrDefault(m => m is not null && m.Id != memberId);
    }

    /// <summary>
    /// 상한을 보고 마무리 요약 또는 다음 차례를 보낸다 (단톡방 잠금 안, 도는 구성원이 없을 때). 보내기가 실패하면(예외 포함) 일시 정지 · 끝 —
    /// 진행 중인 채 아무도 돌지 않는 판이 남지 않게 (self C1 · review 1-F1).
    /// </summary>
    private void Advance(string groupId, string? afterMemberId)
    {
        if (_store.Get(groupId) is not { Playground: { } pg } group || pg.State != PlaygroundState.Running) return;
        if (CapReached(group, pg) is { } why)
        {
            var first = group.Members[0];
            if (SavePlayground(groupId, p => p with { Wrapping = true }) is null)
            {
                Halt(groupId, PlaygroundState.Paused, "놀이터 상태를 저장하지 못해 일시 정지했습니다.");
                return;
            }
            Note(groupId, $"상한({why})에 닿아 {first.Name}에게 마무리 요약을 부탁합니다.");
            if (SendTurn(groupId, first, PlaygroundRules.WrapText(why), count: false) is { } wrapError)
                Halt(groupId, PlaygroundState.Done, $"마무리 요약을 보내지 못해 끝냈습니다: {wrapError}");
            return;
        }
        var next = NextMember(group, pg, afterMemberId);
        if (SendTurn(groupId, next, PlaygroundRules.TurnText, count: true) is { } error)
            Halt(groupId, PlaygroundState.Paused, $"{next.Name}에게 보내지 못해 일시 정지했습니다: {error}");
    }

    /// <summary>다음 차례: 사람이 @로 정한 구성원 → 마지막 답에서 @부른 구성원(그 답 쓴 구성원 빼고) → 순서상 다음.</summary>
    internal static GroupMember NextMember(Room group, Playground pg, string? afterMemberId)
    {
        if (pg.NextMemberId is { } chosen && group.Members.FirstOrDefault(m => m.Id == chosen) is { } human) return human;
        if (pg.AnswerNextId is { } asked && asked != afterMemberId && group.Members.FirstOrDefault(m => m.Id == asked) is { } called) return called;
        var at = -1;
        for (var i = 0; i < group.Members.Count; i++)
            if (group.Members[i].Id == afterMemberId) at = i;
        return group.Members[(at + 1) % group.Members.Count];
    }

    /// <summary>닿은 상한 이름 (없으면 null). 시간 · 비용은 답이 끝난 뒤 본다 — 답 하나만큼 넘을 수 있다.</summary>
    private string? CapReached(Room group, Playground pg) =>
        pg.Messages >= pg.MaxMessages ? $"말 {pg.MaxMessages}개"
        : pg.ElapsedSeconds(Now()) >= pg.MaxMinutes * 60 ? $"{pg.MaxMinutes}분"
        : CostNow(group) - pg.CostAtStart >= pg.MaxCostUsd ? $"추정 비용 ${pg.MaxCostUsd:0.##}"
        : null;

    /// <summary>
    /// 한 차례 보내기: 말 수는 보내기 전에 저장한다 (설계 검토 R4). 실패하면 오류 문구 — 부른 쪽이 일시 정지 · 끝으로 처리한다.
    /// </summary>
    private string? SendTurn(string groupId, GroupMember member, string text, bool count)
    {
        if (_store.Get(groupId) is not { Playground: not null } group) return "놀이터가 아닙니다.";
        var current = group.Members.FirstOrDefault(m => m.Id == member.Id) ?? member;
        var sent = false;
        try
        {
            var log = _log.Read(groupId);
            var line = log.Count > 0 ? log[^1].Line : -1;
            // 사람이 끼어든 말은 블록 밖 안내 아래에 따로 (블록 속 사용자 줄은 구성원 답이 꾸밀 수 있다 — security C1).
            // 스레드 없는 Codex는 처음부터 받으므로 사람 말도 처음부터
            var from = _store.Get(current.RoomId) is { Engine: RoomEngines.Codex, CodexThread: null } ? 0 : current.SeenUpTo;
            var human = log.Where(e => e.Kind == GroupEntryKind.User && e.Line >= from).Select(e => e.Text).ToList();
            var prompt = PromptFor(group, current, log, PlaygroundRules.WithHuman(text, human));
            if (prompt.Length > RoomHub.MaxPromptChars) return $"앞 대화를 붙이면 너무 깁니다 ({prompt.Length:N0}자)";
            // 말 수는 보내기 전에 저장 — 저장하지 못하면 보내지 않는다 (review 1-F1).
            // 사람이 고른 다음 차례는 그 구성원에게 보낼 때만 지운다 — 시작(첫 구성원) 전에 고른 것은 그다음 차례로 남는다
            if (SavePlayground(groupId, p => p with
                {
                    Messages = count ? p.Messages + 1 : p.Messages, CurrentMemberId = current.Id,
                    NextMemberId = p.NextMemberId == current.Id ? null : p.NextMemberId,
                    AnswerNextId = null,
                }) is null) return "놀이터 상태를 저장하지 못했습니다.";
            sent = true;
            _pending[current.RoomId] = new Pending(line, DateTimeOffset.UtcNow, current.SeenUpTo);
            SetSeen(groupId, current.Id, line + 1);
            if (_hub.Send(current.RoomId, prompt) is not { } error) return null;
            Unsend();
            return error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 대화 줄 · 색인을 못 읽고 쓰면 — 걸어 둔 기다림이 남으면 판이 "도는 답이 있다"고 보고 영영 멈췄다 (self C1)
            if (sent) Unsend();
            return ex.Message;
        }

        void Unsend()
        {
            _pending.TryRemove(current.RoomId, out _);
            RestoreSeen(groupId, current.Id, current.SeenUpTo);
            SavePlayground(groupId, p => p with { Messages = count ? Math.Max(0, p.Messages - 1) : p.Messages, CurrentMemberId = null });
        }
    }

    /// <summary>일시 정지 · 끝으로 바꾸고 안내 줄 (도는 구간을 닫는다).</summary>
    private void Halt(string groupId, PlaygroundState state, string note)
    {
        var now = Now();
        SavePlayground(groupId, p => p.Close(now) with
        {
            State = state,
            EndReason = state == PlaygroundState.Done ? note : p.EndReason,
            EndedAt = state == PlaygroundState.Done ? now : p.EndedAt,
        });
        Note(groupId, (state == PlaygroundState.Done ? "놀이터 끝 — " : "") + note);
    }

    private Playground? SavePlayground(string groupId, Func<Playground, Playground> change)
    {
        try
        {
            return _store.Update(groupId, r => r.Playground is { } p ? r with { Playground = change(p) } : r)?.Playground;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // 저장하지 못하면 다음 차례를 보내지 않는다 (부른 쪽이 null을 보고 멈춤)
        }
    }

    private void Note(string groupId, string text)
    {
        try { _log.Append(groupId, new GroupEntry(0, Now(), GroupEntryKind.Note, null, text)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// <summary>
    /// 앱을 다시 켰을 때: 도는 중이던 판은 일시 정지로 (사용자 결정 — 끊긴 차례는 다시 보내지 않는다). 마무리 중이었으면 끝으로.
    /// 도는 구간은 구성원 마지막 실행 시각까지로 닫는다 (꺼져 있던 시간을 세지 않게).
    /// </summary>
    private void PauseInterrupted()
    {
        foreach (var group in _store.All().Where(r => r.Playground is { State: PlaygroundState.Running }))
        {
            var last = group.Members.Select(m => _store.Get(m.RoomId)?.LastRunAt).Where(t => t is not null).Max() ?? group.Playground!.RunningSince ?? Now();
            var wrapping = group.Playground!.Wrapping;
            SavePlayground(group.Id, p => p.Close(p.RunningSince is { } since && last < since ? since : last) with
            {
                State = wrapping ? PlaygroundState.Done : PlaygroundState.Paused,
                CurrentMemberId = null,
                // 끊긴 차례의 구성원을 마지막에 말한 것으로 — 계속할 때 그 구성원을 다시 부르지 않게 (review 1-F3)
                LastMemberId = p.CurrentMemberId ?? p.LastMemberId,
                EndReason = wrapping ? "앱이 다시 켜져 마무리 요약 중에 끝났습니다." : p.EndReason,
                EndedAt = wrapping ? Now() : p.EndedAt,
            });
            Note(group.Id, wrapping ? "놀이터 끝 — 앱이 다시 켜져 마무리 요약 중에 끝났습니다." : "앱이 다시 켜져 일시 정지했습니다 — '계속'을 누르면 다음 차례부터.");
        }
    }

    private readonly Lock _clockGate = new();

    /// <summary>시간 감시를 켠다 (판을 처음 시작할 때 — 놀이터를 안 쓰면 타이머가 없다). 앱과 수명이 같아 해제하지 않는다.</summary>
    private void EnsureClock()
    {
        lock (_clockGate)
            // 타이머(스레드 풀) 콜백 예외는 앱을 끝낸다 — 경계라 모두 잡는다 (지휘자 K1)
            _clock ??= new Timer(_ => { try { CheckPlaygroundClock(); } catch (Exception) { } },
                null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>시간 감시가 이미 멈춘 단톡방 — 멈춘 답이 끝날 때까지 안내 줄을 거듭 남기지 않게 (지휘자 K2).</summary>
    private readonly ConcurrentDictionary<string, byte> _clockStopped = new(StringComparer.Ordinal);

    /// <summary>
    /// 시간 감시 (설계 검토 R1): 도는 판이 시간 상한 + <see cref="Playground.HardGrace"/>(마무리면 두 배)를 넘겼는데 답하는 구성원이 있으면 그 답을 멈춘다 —
    /// 멈춘 답이 끝나면 진행기가 상한을 보고 마무리 · 끝으로 간다. 상태는 잠금 안에서 다시 읽는다 — 그 사이 일시 정지했으면 도는 답을 두기 (review 1-F7).
    /// </summary>
    public void CheckPlaygroundClock()
    {
        foreach (var id in _store.All().Where(r => r.Playground is { State: PlaygroundState.Running } && r.DeletedAt is null).Select(r => r.Id).ToList())
        {
            var stopped = false;
            lock (_gates.GetOrAdd(id, _ => new Lock()))
            {
                if (_store.Get(id) is not { Playground: { State: PlaygroundState.Running } pg, DeletedAt: null } group) continue;
                var limit = pg.MaxMinutes * 60 + Playground.HardGrace.TotalSeconds * (pg.Wrapping ? 2 : 1);
                var busy = group.Members.Where(m => m.RoomId.Length > 0 && _hub.IsBusy(m.RoomId)).ToList();
                if (busy.Count == 0)
                {
                    _clockStopped.TryRemove(id, out _);
                    _clockStopped.TryRemove(id + ":wrap", out _);
                }
                // 마무리 요약은 따로 센다 — 멈춘 답 뒤 바로 마무리가 돌면 쉬는 틈 없이 이어져 표시가 안 지워진다
                if (pg.ElapsedSeconds(Now()) < limit || busy.Count == 0 || !_clockStopped.TryAdd(pg.Wrapping ? id + ":wrap" : id, 0)) continue;
                Note(id, $"시간 상한({pg.MaxMinutes}분)을 넘겨 도는 답을 멈춥니다.");
                foreach (var m in busy) _hub.Stop(m.RoomId);
                stopped = true;
            }
            if (stopped) Announce(id);
        }
    }
}
