using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>방 목록 · 새 방 만들기.</summary>
public sealed class IndexModel(RoomStore store, RoomHub hub, IOptions<ChatOptions> options) : PageModel
{
    public IReadOnlyList<Room> Rooms { get; private set; } = [];
    public ChatOptions Options => options.Value;
    public string? LoadError => store.LoadError;
    public string? Error { get; private set; }

    public bool IsBusy(Room r) => hub.IsBusy(r.Id);

    public void OnGet() => Rooms = [.. store.All().OrderByDescending(r => r.LastRunAt ?? r.CreatedAt)];

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
