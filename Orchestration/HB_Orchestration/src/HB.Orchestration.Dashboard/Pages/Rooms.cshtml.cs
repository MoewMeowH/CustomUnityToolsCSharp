using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>
/// 채팅방 목록: 최근 Claude 세션마다 방 하나 (계획서 2026-10-27_에이전트-채팅방).
/// ?handler=Stamp · List는 실시간 갱신용 (계획서 2026-10-27_채팅방-실시간-갱신 결정: 방 목록도 실시간).
/// </summary>
public sealed class RoomsModel(ChatRoomService rooms, IOptions<DashboardOptions> options) : PageModel
{
    public IReadOnlyList<ChatRoomService.RoomSummary> Rooms { get; private set; } = [];

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
