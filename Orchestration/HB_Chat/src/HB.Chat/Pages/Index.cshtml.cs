using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방을 고르지 않은 첫 화면: 왼쪽 방 목록 + 오른쪽 새 방 만들기.
/// ?handler=RoomList: 앱 전체 알림을 받은 화면이 목록 부분만 다시 받는다 (계획서 2-2 a).
/// </summary>
public sealed class IndexModel(RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, IOptions<ChatOptions> options) : PageModel
{
    /// <summary>휴지통 목록의 방 제목 — 화면 다른 곳과 같은 규칙 (이름 없으면 첫 말, /self-review 95e8b1c242 C6).</summary>
    public string TitleOf(Room room) => RoomList.TitleOf(room, transcripts.Read(room.SessionId));

    public SidebarView Sidebar { get; private set; } = new([], null);
    public ChatOptions Options => options.Value;
    public string? LoadError => store.LoadError;
    public string? Error { get; private set; }

    /// <summary>?trash=1이면 오른쪽에 휴지통 (계획서 2-2 c).</summary>
    public bool ShowTrash { get; private set; }
    public IReadOnlyList<Room> TrashRooms { get; private set; } = [];

    /// <summary>trash는 문자열로 받는다 — bool 바인딩은 "1"을 받지 않아 휴지통이 안 보였다 (2-2 c 실제 확인).</summary>
    public void OnGet(string? trash = null)
    {
        Sidebar = new SidebarView(list.All(), null) { TrashCount = store.All().Count(r => r.DeletedAt is not null) };
        ShowTrash = trash is "1" or "true";
        if (ShowTrash) TrashRooms = [.. store.All().Where(r => r.DeletedAt is not null).OrderByDescending(r => r.DeletedAt)];
    }

    public IActionResult OnGetRoomList(string? current)
    {
        Response.Headers.CacheControl = "no-store";
        return Partial("_RoomList", new SidebarView(list.All(), RoomStore.IsRoomId(current) ? current : null)
        {
            TrashCount = store.All().Count(r => r.DeletedAt is not null),
        });
    }

    /// <summary>검색 (계획서 2-2 c): 휴지통 밖 모든 방의 제목 · 말. 결과는 왼쪽 칸에 목록 대신.</summary>
    public IActionResult OnGetSearch(string? q)
    {
        Response.Headers.CacheControl = "no-store";
        return Partial("_SearchResults", list.Search(q ?? ""));
    }

    /// <summary>휴지통에서 되살리기 (휴지통 화면의 단추, 양식 POST).</summary>
    public IActionResult OnPostRestore(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        try
        {
            store.Update(id, r => r with { DeletedAt = null });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = $"되살리지 못했습니다: {ex.Message}";
            OnGet(trash: "1");
            return Page();
        }
        hub.Touch(id);
        return Redirect($"/Room/{id}");
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
