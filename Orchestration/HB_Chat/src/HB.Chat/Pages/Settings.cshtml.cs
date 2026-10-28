using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 설정 화면 (3차 d — 노션 "앱 내 설정탭에 세션 이름 및 경로 추가기능"): 작업 폴더 더하기 · 지우기(앱에서 더한 것만)와 방 이름 바꾸기
/// (결정 "방의 제목도 변경이 가능했음 좋긴행"). 양식 POST — antiforgery · 같은 출처 검사 아래. 성공하면 다시 이 화면으로(PRG).
/// </summary>
public sealed class SettingsModel(RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, IOptions<ChatOptions> options) : PageModel
{
    public SidebarView Sidebar { get; private set; } = new([], null);
    public string? Error { get; private set; }

    /// <summary>
    /// 방금 한 일 (리다이렉트 뒤 한 줄). 주소의 코드만 받고 문구는 여기서 고른다 — 아무 글이나 성공 문구처럼 띄우지 못하게 (/self-review security eee1adb777 C6).
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Done { get; set; }

    public string? DoneText => Done switch
    {
        "added" => "작업 폴더를 더했습니다.",
        "removed" => "작업 폴더를 목록에서 뺐습니다 (폴더 · 파일은 그대로).",
        "renamed" => "방 이름을 바꿨습니다.",
        _ => null,
    };

    /// <summary>되돌려 보일 입력 (더하기 실패 때).</summary>
    public string? NewName { get; private set; }
    public string? NewPath { get; private set; }

    public string McpServer => options.Value.McpServer;
    public string? WorkspaceLoadError => options.Value.AppWorkspaces?.LoadError;

    /// <summary>workspaces.json에 있지만 쓰지 않는 항목과 이유 (설정 파일 이름과 겹침 · 검사 실패).</summary>
    public IReadOnlyList<string> WorkspaceWarnings => options.Value.AppWorkspaces?.LoadWarnings ?? [];

    /// <summary>
    /// 작업 폴더 줄: 이름 · 경로 · 설정 파일 것인지 · MCP 서버가 실행할 명령(읽기 이상 방이 승인 없이 띄움) · 프로젝트 훅 유무 · 쓰는 방 수(휴지통 포함).
    /// </summary>
    public sealed record WorkspaceRow(WorkspaceEntry Entry, string? McpCommand, bool HasHooks, bool Exists, int Rooms);

    public IReadOnlyList<WorkspaceRow> Workspaces { get; private set; } = [];

    /// <summary>방 줄 (휴지통 밖, 최근 순): 방 · 보이는 제목.</summary>
    public IReadOnlyList<(Room Room, string Title)> Rooms { get; private set; } = [];

    public void OnGet() => Fill();

    public IActionResult OnPostAddWorkspace(string? name, string? path, bool confirm)
    {
        if (!confirm) Error = "편집 · 명령 · MCP 서버 · 프로젝트 훅이 그 폴더 기준으로 돈다는 확인에 체크해 주세요.";
        else if (options.Value.AppWorkspaces is not { } added) Error = "작업 폴더 저장소가 준비되지 않았습니다.";
        else Error = added.Add(name, path, store.All());
        if (Error is null) return RedirectToPage(new { done = "added" });
        (NewName, NewPath) = (name, path);
        Fill();
        return Page();
    }

    public IActionResult OnPostRemoveWorkspace(string? name)
    {
        Error = options.Value.AppWorkspaces is { } added ? added.Remove(name, store.All()) : "작업 폴더 저장소가 준비되지 않았습니다.";
        if (Error is null) return RedirectToPage(new { done = "removed" });
        Fill();
        return Page();
    }

    /// <summary>방 이름 바꾸기 — 방 화면의 Rename과 같은 검사 · 저장 · 목록 알림.</summary>
    public IActionResult OnPostRename(string? id, string? title)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        var (clean, error) = RoomList.CleanTitle(title);
        Error = error;
        if (Error is null)
        {
            try
            {
                if (store.Update(id!, r => r with { Title = clean }) is null) return NotFound();
                hub.Touch(id!);
                return RedirectToPage(new { done = "renamed" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Error = $"이름을 바꾸지 못했습니다: {ex.Message}";
            }
        }
        Fill();
        return Page();
    }

    private void Fill()
    {
        var all = store.All();
        Sidebar = new SidebarView(list.All(), null) { TrashCount = all.Count(r => r.DeletedAt is not null) };
        Workspaces =
        [
            .. options.Value.AllWorkspaces.Select(w => new WorkspaceRow(w,
                WorkspaceStore.McpCommand(w.Path, options.Value.McpServer), WorkspaceStore.HasProjectHooks(w.Path), Directory.Exists(w.Path),
                all.Count(r => string.Equals(r.Workspace, w.Name, StringComparison.OrdinalIgnoreCase)))),
        ];
        Rooms = [.. all.Where(r => r.DeletedAt is null).OrderByDescending(r => r.LastRunAt ?? r.CreatedAt)
            .Select(r => (r, RoomList.TitleOf(r, transcripts.Read(r.SessionId))))];
    }
}
