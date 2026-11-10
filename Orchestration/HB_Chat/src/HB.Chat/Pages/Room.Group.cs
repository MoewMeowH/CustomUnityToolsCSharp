using HB.Orchestration.ChatUi;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace HB.Chat.Pages;

/// <summary>방 화면의 단톡방 쪽: 대화 줄 · 구성원 표시 · 단톡방 처리기.</summary>
public sealed partial class RoomModel
{
    // ---- 단톡방: 같은 방 주소, 칸 · 말풍선 · 보내기만 다르다 ----

    /// <summary>단톡방 대화 줄 (after가 있으면 그 줄 뒤만 — 새 말 받기).</summary>
    public IReadOnlyList<GroupEntry> GroupEntries { get; private set; } = [];

    /// <summary>새 말 받기 조각인지 (빈 단톡방 안내를 그리지 않음).</summary>
    public bool GroupFragment { get; private set; }

    /// <summary>대화 줄 마지막 줄 번호 (없으면 -1) — 화면의 data-last-line.</summary>
    public int GroupLastLine { get; private set; } = -1;

    public GroupMember? MemberOf(string? memberId) => Room.Members.FirstOrDefault(m => m.Id == memberId);

    /// <summary>구성원 상태 한 줄 (생각 중 · 답을 기다림 · 차례 기다림 · null).</summary>
    public string? MemberState(GroupMember m) => groups.MemberState(m.RoomId);

    /// <summary>구성원 단계 표시: 쓰는 구성원(명령)만 — 그 구성원이 낀 말은 차례로 돈다 (⑤).</summary>
    public string? WriterLabel(GroupMember m) => store.Get(m.RoomId) is { } child && GroupChat.IsWriter(child) ? RoomRules.LevelName(child.Level) : null;

    /// <summary>
    /// 그 구성원이 그 사용자 말에 답했는지 (답 줄 · 보내지 못함 안내 줄) — 아니고 실행 중도 아니면 "답이 없음"으로 보인다.
    /// 구성원마다 본다 — 여러 명을 부른 말(③)에서 한 명만 답해도 나머지의 끊김이 가려지지 않게.
    /// </summary>
    public bool Answered(GroupEntry user, string memberId) => _answered.Contains((user.Line, memberId));

    private HashSet<(int UserLine, string MemberId)> _answered = [];

    /// <summary>중지 단추: 도는 구성원 · 단톡방이 답을 기다리는 구성원(차례 기다림 포함)이 있으면.</summary>
    public bool AnyMemberBusy(GroupMember m) => hub.IsBusy(m.RoomId) || groups.Waiting(m.RoomId);

    /// <summary>그 구성원이 답하는 중인지 (실행 중이거나 단톡방이 답을 기다림).</summary>
    public bool Waiting(GroupMember m) => _waiting?.Contains(m.Id) ?? (hub.IsBusy(m.RoomId) || groups.Waiting(m.RoomId));

    /// <summary>대화 줄을 읽기 전에 찍어 둔 기다림 상태 (LoadGroup).</summary>
    private HashSet<string>? _waiting;

    private void LoadGroup(int? after)
    {
        // 기다림 상태를 대화 줄보다 먼저 찍는다 — 줄을 읽은 뒤 답이 올라오고 기다림이 풀리면 "답 없음 + 쉼"으로 보여 정상 답에도 "답이 없습니다"가
        // 그려졌고, 화면은 새 줄만 덧붙여 그 안내가 남았다 (③ 리뷰 6b7921ae50 1-F1 · Codex). 먼저 찍으면: 그때 쉬던 구성원의 답은 이미 줄에 있다
        // (답 줄을 쓴 뒤에 기다림을 지움 — Posting), 기다리던 구성원은 이번에 안내를 그리지 않는다.
        _waiting = [.. Room.Members.Where(m => hub.IsBusy(m.RoomId) || groups.Waiting(m.RoomId)).Select(m => m.Id)];
        var all = groups.Log.Read(Room.Id);
        _answered = GroupChat.AnsweredBy(all);
        GroupLastLine = all.Count > 0 ? all[^1].Line : -1;
        GroupEntries = after is { } a ? [.. all.Where(e => e.Line > a)] : all;
        GroupFragment = after is not null;
    }

    /// <summary>단톡방 구성원 말풍선 본문 (지휘자 말과 같은 규칙) · 사용자 말(줄바꿈 유지).</summary>
    public HtmlString RenderGroup(GroupEntry e) => e.Kind == GroupEntryKind.User
        ? new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, SafeMarkdown.KeepLineBreaks), Stickers))
        : new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, SafeMarkdown.Default), Stickers));

    /// <summary>
    /// 단톡방 · 구성원 자식 방의 단계 · 모델 · 연결은 바꾸지 않는다 — 만들 때 정한 단계로만 돈다(결정 ①). 과정 보기 화면의 곁칸에서
    /// 자식 방을 명령 단계로 올릴 수 있었다 (b8697252eb C1 · Codex 리뷰).
    /// </summary>
    private static string? GroupSettingsRefusal(Room room) =>
        room.GroupId is not null ? "단톡방 구성원 방의 단계 · 모델 · 연결은 바꿀 수 없습니다 (지금은 만들 때 정한 값으로만 돕니다)."
        : room.IsGroup ? "단톡방의 단계 · 모델 · 연결은 바꿀 수 없습니다 (지금은 만들 때 정한 값으로만 돕니다)."
        : null;

    /// <summary>단톡방 화면이 다시 붙을 때 받는 구성원 카드 (승인 · 질문, ⑤): approval · question 알림 모양, detail = 구성원 ID.</summary>
    public IActionResult OnGetGroupCards(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { IsGroup: true } group) return NotFound();
        return new JsonResult(groups.Cards(group));
    }

    /// <summary>
    /// 단톡방 구성원 상태 (화면이 다시 붙을 때 · 대화 줄을 받을 때 칩을 맞춘다 — b8697252eb C4). 구성원이 쓰는 중인 글은 여기서 주지 않는다 —
    /// SSE를 붙일 때 같은 채널로 온다 (GroupChat.OnSubscribed, 7e68bf5791 C1 · C3).
    /// </summary>
    public IActionResult OnGetGroupState(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { IsGroup: true } group) return NotFound();
        return new JsonResult(groups.States(group).Select(s => new { member = s.MemberId, state = s.State }));
    }

    /// <summary>
    /// 단톡방 머리의 구성원 비용 합계 (결정 "구성원 누적 합계"):
    /// Claude 구성원은 claude가 알려 준 세션 누적 비용(클라이언트 추정)의 합, Codex 구성원은 턴마다 더해 둔 토큰(Room.CodexInputTokens · CodexOutputTokens —
    /// b40d9efb00 C3)의 합. 아무것도 없으면 null.
    /// </summary>
    public string? GroupCostLine(Room? group = null)
    {
        group ??= Room;
        double usd = 0;
        long input = 0, output = 0;
        bool anyUsd = false;
        foreach (var m in group.Members)
        {
            if (store.Get(m.RoomId) is not { } child) continue;
            if (child.Engine == RoomEngines.Codex)
                (input, output) = (input + child.CodexInputTokens, output + child.CodexOutputTokens);
            else if (child.TotalCostUsd is { } cost)
            {
                usd += cost;
                anyUsd = true;
            }
        }
        var parts = new List<string>();
        if (anyUsd) parts.Add($"Claude 구성원 ≈${usd:0.00} (추정)");
        if (input + output > 0) parts.Add($"Codex 토큰 입력 {input:N0}(캐시 포함) · 출력 {output:N0}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>단톡방 비용 합계 줄 (화면이 대화 줄을 받을 때 다시 받는다). 없으면 빈 글.</summary>
    public IActionResult OnGetGroupCost(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { IsGroup: true } group) return NotFound();
        return Content(GroupCostLine(group) ?? "", "text/plain; charset=utf-8");
    }

    /// <summary>단톡방 칩의 엔진 표시: Codex면 "Codex · 모델"(빈 모델 = 기본), Claude면 null.</summary>
    public string? EngineLabel(GroupMember m) =>
        store.Get(m.RoomId) is { Engine: RoomEngines.Codex } child ? "Codex" + (child.Model.Length > 0 ? " · " + child.Model : "") + (child.Effort is { } e ? " · " + e : "") : null;

    /// <summary>단톡방 칩의 작업 폴더: 단톡방 작업 폴더와 다르면 그 이름, 같으면 null.</summary>
    public string? MemberWorkspace(GroupMember m) =>
        store.Get(m.RoomId) is { } child && !string.Equals(child.Workspace, Room.Workspace, StringComparison.OrdinalIgnoreCase) ? child.Workspace : null;

    /// <summary>작업 폴더 경로 (칩 title). 허용 목록에서 빠졌으면 안내 글.</summary>
    public string WorkspaceTitle(string workspace) => options.Value.WorkspacePath(workspace) ?? "(허용 목록에 없음)";

    /// <summary>
    /// 단톡방 머리의 작업 폴더: 구성원이 모두 같은 폴더면 그 이름(단톡방 폴더가 아니어도 — 칩 배지와 맞게), 섞였으면 "작업 폴더 N곳".
    /// 자식 방이 없으면 단톡방 폴더.
    /// </summary>
    public string GroupWorkspaceLabel => GroupChat.Workspaces(store, Room) switch
    {
        { Count: > 1 } all => $"작업 폴더 {all.Count}곳",
        [var one] => one,
        _ => Room.Workspace,
    };

    /// <summary>과정 보기(자식 방)의 단톡방 — 화면 안내 줄에 이름을 보인다.</summary>
    public Room? ParentGroup => Room.GroupId is { } g ? store.Get(g) : null;

    /// <summary>단톡방 구성원 자식 방에는 직접 보내지 않는다 — 답이 단톡방 대화 줄과 어긋나지 않게 (과정 보기에서 승인 · 질문 카드만 답함).</summary>
    private string? ChildRefusal(string id) =>
        store.Get(id) is { GroupId: not null } ? "단톡방 구성원 방입니다 — 단톡방에서 @이름으로 부르세요." : null;
}
