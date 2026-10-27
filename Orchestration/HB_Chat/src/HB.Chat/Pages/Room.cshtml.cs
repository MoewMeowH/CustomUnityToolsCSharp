using HB.Orchestration.ChatUi;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방 하나. 말풍선은 세션 기록에서 읽는다 (앱은 말 본문을 따로 저장하지 않는다). 진행 중 턴은 /events/{id} SSE로 흘린다.
/// ?handler=Messages: 턴이 끝난 뒤 화면이 말풍선 목록만 다시 받는다. Send · Stop은 POST (antiforgery + 같은 출처).
/// </summary>
public sealed class RoomModel(RoomStore store, RoomHub hub, ChatTranscripts transcripts, StickerCatalog stickers, IOptions<ChatOptions> options)
    : PageModel
{
    public Room Room { get; private set; } = null!;
    public ChatTranscript? Transcript { get; private set; }
    public string Title => Room.Title ?? Transcript?.Title ?? FirstUserLine() ?? "새 방";
    /// <summary>대시보드 채팅방 (세션 기록이 생긴 뒤부터 보인다 — 첫 말 전에는 대시보드가 404).</summary>
    public string? DashboardLink =>
        options.Value.DashboardUrl is { Length: > 0 } url ? $"{url.TrimEnd('/')}/Room/{Room.SessionId}" : null;

    /// <summary>명령 방: 작업 폴더 · 사용자 설정이 이미 미리 허용한 명령 (카드 없이 실행됨).</summary>
    public IReadOnlyList<(string Source, string Rule)> InheritedRules =>
        Room.Level == PermissionLevel.Command ? hub.InheritedCommandRules(Room) : [];

    public IActionResult OnGet(string id)
    {
        if (!Load(id)) return NotFound();
        return Page();
    }

    public IActionResult OnGetMessages(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        // 읽기 실패는 실패로 돌려준다: 화면이 앞 말풍선을 그대로 둔다 (대시보드 채팅방과 같은 처리).
        if (Transcript?.FormatError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Partial("_Messages", this);
    }

    public IActionResult OnPostSend(string id, string? text)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        return hub.Send(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    public IActionResult OnPostStop(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.Stop(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>승인 카드 단추: choice = allow · always · deny, reason은 거절 이유 (선택).</summary>
    public IActionResult OnPostApprove(string id, string? approval, string? choice, string? reason)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(approval)) return NotFound();
        ApprovalChoice? parsed = choice switch
        {
            "allow" => ApprovalChoice.Allow,
            "always" => ApprovalChoice.AlwaysAllow,
            "deny" => ApprovalChoice.Deny,
            _ => null,
        };
        if (parsed is null) return BadRequest("알 수 없는 선택입니다.");
        if (reason is { Length: > 500 }) reason = reason[..500];
        return hub.Decide(id, approval, parsed.Value, reason) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>"이 방에서 항상 허용" 규칙 지우기.</summary>
    public IActionResult OnPostRemoveRule(string id, string? rule)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(rule)) return NotFound();
        return hub.RemoveRule(id, rule) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    private bool Load(string id)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return false;
        Room = room;
        Transcript = transcripts.Read(room.SessionId);
        return true;
    }

    private string? FirstUserLine()
    {
        var text = Transcript?.Entries.FirstOrDefault(e => e.Speaker == ChatSpeaker.User)?.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    public HtmlString Render(ChatEntry e) =>
        new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, e.Speaker == ChatSpeaker.User ? SafeMarkdown.KeepLineBreaks : SafeMarkdown.Default), stickers));

    /// <summary>
    /// 중지한 턴 뒤 다음 말을 이으면 CLI가 답 없는 앞 턴에 넣는 자리표시 답 (조사 로그 2026-10-27 "No response requested." 관찰, 실제 확인에서도 봄).
    /// 지휘자 말풍선 대신 안내 한 줄로 보인다.
    /// </summary>
    public static bool IsStoppedTurn(ChatEntry e) =>
        e.Speaker == ChatSpeaker.Conductor && e.Text.Trim() == "No response requested.";

    public static string RoleClass(ChatSpeaker s) => s switch
    {
        ChatSpeaker.User => "user",
        ChatSpeaker.Conductor => "conductor",
        _ => "system",
    };
}
