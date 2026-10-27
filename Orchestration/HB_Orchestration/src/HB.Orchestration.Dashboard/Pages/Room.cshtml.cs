using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>실시간 갱신 설정 하나 (_LiveControl · _LiveScript). Stamp는 화면을 그리기 전에 잡은 값.</summary>
public sealed record LiveView(string Stamp, string StampUrl, string ContentUrl, string TargetId, int Seconds, bool Follow);

/// <summary>채팅방 하나 (세션 하나). ?handler=Stamp · Messages는 실시간 갱신용 (계획서 2026-10-27_채팅방-실시간-갱신).</summary>
public sealed class RoomModel(ChatRoomService rooms, IOptions<DashboardOptions> options) : PageModel
{
    /// <summary>이보다 긴 에이전트 응답은 접어 둔다 (사용자 · 지휘자 말은 접지 않는다).</summary>
    public const int FoldChars = 1500;

    public ChatRoom Room { get; private set; } = null!;

    public LiveView Live { get; private set; } = null!;

    public IActionResult OnGet(string id)
    {
        // 그리기 전에 잡는다: 그리는 동안 바뀐 것은 다음 Stamp가 달라 다시 받는다.
        var stamp = rooms.RoomStamp(id);
        if (stamp is null || rooms.Room(id) is not { } room) return NotFound();
        Room = room;
        var self = $"/Room/{Uri.EscapeDataString(room.SessionId)}";
        Live = new LiveView(stamp, $"{self}?handler=Stamp", $"{self}?handler=Messages", "chat", Math.Max(0, options.Value.RoomPollSeconds), Follow: true);
        return Page();
    }

    public IActionResult OnGetStamp(string id)
    {
        Response.Headers.CacheControl = "no-store";
        return rooms.RoomStamp(id) is { } stamp ? Content(stamp, "text/plain") : NotFound();
    }

    public IActionResult OnGetMessages(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (rooms.Room(id) is not { } room) return NotFound();
        // 본문 읽기 실패(잠금 등)는 실패로 돌려준다: 스크립트가 앞 화면과 앞 Stamp를 그대로 두고 다시 묻는다.
        // 성공처럼 빈 방을 주면 Stamp가 갱신돼 파일이 다시 바뀔 때까지 빈 방으로 남는다 (/self-review bcba4ebfcb C1).
        if (room.Transcript.FormatError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        Room = room;
        return Partial("_RoomMessages", this);
    }

    public HtmlString Render(RoomMessage m) =>
        new(StickerRenderer.Replace(SafeMarkdown.ToHtml(m.Markdown, m.Role == RoomRole.User ? SafeMarkdown.KeepLineBreaks : SafeMarkdown.Default), rooms.Stickers));

    public HtmlString Sticker(string tag) => new(StickerRenderer.Html(tag, rooms.Stickers));

    public static bool Folds(RoomMessage m) =>
        m.Role is not (RoomRole.User or RoomRole.Conductor) && m.Markdown.Length > FoldChars;

    public static string RoleClass(RoomRole role) => role.ToString().ToLowerInvariant();

    /// <summary>아바타 글자: 이름의 첫 글자 (영문은 대문자).</summary>
    public static string Initial(RoomMessage m) =>
        string.IsNullOrEmpty(m.Name) ? "?" : char.ToUpperInvariant(m.Name[0]).ToString();
}
