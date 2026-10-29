using HB.Orchestration.ChatUi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>
/// 채팅방 목록: 최근 Claude 세션마다 방 하나.
/// ?handler=Stamp · List는 실시간 갱신용 (결정: 방 목록도 실시간).
/// </summary>
public sealed class RoomsModel(ChatRoomService rooms, ChatAppRooms appRooms, IOptions<DashboardOptions> options) : PageModel
{
    public IReadOnlyList<ChatRoomService.RoomSummary> Rooms { get; private set; } = [];

    /// <summary>그 세션이 채팅 앱(HB_Chat)의 방이면 그 방 (표시 · 앱 링크).</summary>
    public ChatAppRoom? AppRoom(ChatRoomService.RoomSummary r) => appRooms.For(r.Session.FileSessionId);

    public LiveView Live { get; private set; } = null!;

    public void OnGet()
    {
        var stamp = rooms.ListStamp();
        Rooms = rooms.List();
        Live = new LiveView(stamp, "/Rooms?handler=Stamp", "/Rooms?handler=List", "room-list", Math.Max(0, options.Value.RoomPollSeconds), Follow: false);
    }

    public IActionResult OnGetStamp()
    {
        Response.Headers.CacheControl = "no-store";
        return Content(rooms.ListStamp(), "text/plain");
    }

    public IActionResult OnGetList()
    {
        Response.Headers.CacheControl = "no-store";
        Rooms = rooms.List();
        return Partial("_RoomList", this);
    }
}
