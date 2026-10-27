using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>채팅방 목록: 최근 Claude 세션마다 방 하나 (계획서 2026-10-27_에이전트-채팅방).</summary>
public sealed class RoomsModel(ChatRoomService rooms) : PageModel
{
    public IReadOnlyList<ChatRoomService.RoomSummary> Rooms { get; private set; } = [];

    public void OnGet() => Rooms = rooms.List();
}
