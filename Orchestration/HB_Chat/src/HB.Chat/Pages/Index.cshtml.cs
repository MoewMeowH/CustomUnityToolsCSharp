using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방을 고르지 않은 첫 화면: 왼쪽 방 목록 + 오른쪽 새 방 만들기.
/// ?handler=RoomList: 앱 전체 알림을 받은 화면이 목록 부분만 다시 받는다 (계획서 2-2 a).
/// </summary>
public sealed class IndexModel(RoomStore store, RoomHub hub, RoomList list, IOptions<ChatOptions> options) : PageModel
{
    public SidebarView Sidebar { get; private set; } = new([], null);
    public ChatOptions Options => options.Value;
    public string? LoadError => store.LoadError;
    public string? Error { get; private set; }

    public void OnGet() => Sidebar = new SidebarView(list.All(), null);

    public IActionResult OnGetRoomList(string? current)
    {
        Response.Headers.CacheControl = "no-store";
        return Partial("_RoomList", new SidebarView(list.All(), RoomStore.IsRoomId(current) ? current : null));
    }

    public IActionResult OnPostCreate(string workspace, PermissionLevel level, string model, string? title)
    {
        var o = options.Value;
        Error = o.WorkspacePath(workspace) is null ? "허용 목록에 없는 작업 폴더입니다."
            : !o.ModelList.Contains(model, StringComparer.Ordinal) ? "허용 목록에 없는 모델입니다."
            : !Enum.IsDefined(level) ? "알 수 없는 권한 단계입니다."
            : title is { Length: > 200 } ? "제목이 너무 깁니다."
            : null;
        if (Error is null)
        {
            try
            {
                var room = store.Create(workspace, level, model, title);
                hub.Touch(room.Id); // 다른 탭의 목록에도 보이게
                return Redirect($"/Room/{room.Id}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
            }
        }
        OnGet();
        return Page();
    }
}
