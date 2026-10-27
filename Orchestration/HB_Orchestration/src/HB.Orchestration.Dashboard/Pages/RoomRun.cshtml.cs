using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>실행 → 그 실행이 이어진 채팅방의 스레드로 보낸다. 이어진 방이 없으면 그렇다고 알린다.</summary>
public sealed class RoomRunModel(ChatRoomService rooms) : PageModel
{
    public string RunId { get; private set; } = "";

    public IActionResult OnGet(string runId)
    {
        if (rooms.SessionOfRun(runId) is { } session) return Redirect($"/Room/{session}#run-{runId}");
        RunId = runId;
        return Page();
    }
}
