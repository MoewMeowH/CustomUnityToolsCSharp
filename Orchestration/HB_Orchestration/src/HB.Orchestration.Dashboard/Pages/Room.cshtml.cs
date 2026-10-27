using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>채팅방 하나 (세션 하나).</summary>
public sealed class RoomModel(ChatRoomService rooms) : PageModel
{
    /// <summary>이보다 긴 에이전트 응답은 접어 둔다 (사용자 · 지휘자 말은 접지 않는다).</summary>
    public const int FoldChars = 1500;

    public ChatRoom Room { get; private set; } = null!;

    public IActionResult OnGet(string id)
    {
        if (rooms.Room(id) is not { } room) return NotFound();
        Room = room;
        return Page();
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
