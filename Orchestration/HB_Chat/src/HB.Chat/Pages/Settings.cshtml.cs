using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 설정 화면 (3차 d — 노션 "앱 내 설정탭에 세션 이름 및 경로 추가기능"): 작업 폴더 더하기 · 지우기(앱에서 더한 것만)와 방 이름 바꾸기
/// (결정 "방의 제목도 변경이 가능했음 좋긴행"). 양식 POST — antiforgery · 같은 출처 검사 아래. 성공하면 다시 이 화면으로(PRG).
/// </summary>
public sealed class SettingsModel(
    RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, IOptions<ChatOptions> options,
    ProfileView profiles, FaceCatalog faces, RoomRuns runs) : PageModel
{
    public ProfileView Profiles => profiles;
    public FaceCatalog Faces => faces;

    /// <summary>프로필 줄 (3차 e): 나 · 지휘자 · 외부 에이전트(호출 기록에 나온 이름 + 프로필을 둔 이름).</summary>
    public IReadOnlyList<(string Key, string Label, string DefaultName)> ProfileRows { get; private set; } = [];

    /// <summary>
    /// 프로필 이름 · 색 · 그림 저장 (그림은 고르면 바꾸고, 지우기를 체크하면 글자 아바타로). 그림이 실패하면 이름 · 색도 저장하지 않게
    /// 그림을 먼저 하고, 둘을 같이 고르면 거절한다 (3차 e /self-review security). 이름 · 색 검사는 그림보다 먼저 (저장 없이).
    /// </summary>
    public async Task<IActionResult> OnPostProfileAsync(string? key, string? name, string? color, IFormFile? image, bool clearImage)
    {
        var p = profiles.Store;
        var hasImage = image is { Length: > 0 };
        Error = clearImage && hasImage ? "그림을 고르면서 지우기를 같이 할 수 없습니다 — 하나만 고르세요."
            : ProfileStore.Check(key, name, color);
        if (Error is null && clearImage) Error = p.ClearImage(key);
        else if (Error is null && hasImage)
        {
            if (image!.Length > ProfileStore.MaxImageBytes) Error = $"그림은 {ProfileStore.MaxImageBytes / 1024}KB까지입니다.";
            else
            {
                await using var stream = image.OpenReadStream();
                Error = await p.SetImageAsync(key, stream, HttpContext.RequestAborted);
            }
        }
        Error ??= p.Save(key, name, color);
        if (Error is null)
        {
            hub.ProfilesChanged(store.All().Select(r => r.Id)); // 목록 · 열린 화면이 새 이름 · 그림으로 다시 받게
            return RedirectToPage(new { done = "profile" });
        }
        Fill();
        return Page();
    }

    /// <summary>지휘자 표정 저장: 상태마다 스티커 태그 (빈 값이면 기본).</summary>
    public IActionResult OnPostFaces([FromForm(Name = "face")] Dictionary<string, string?>? face)
    {
        Error = profiles.Store.SetFaces(face ?? [], faces.KnownTags);
        if (Error is null)
        {
            hub.ProfilesChanged(store.All().Select(r => r.Id));
            return RedirectToPage(new { done = "faces" });
        }
        Fill();
        return Page();
    }

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
        "profile" => "프로필을 저장했습니다.",
        "faces" => "지휘자 표정을 저장했습니다.",
        _ => null,
    };

    /// <summary>요청을 받기 전에 막힌 오류 (리다이렉트 뒤 한 줄, 4차 B2) — Done처럼 코드만 받고 문구는 여기서 고른다.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Fail { get; set; }

    public string? FailText => Fail switch
    {
        "image-size" => $"그림이 너무 커서 받지 않았습니다 ({ProfileStore.MaxImageBytes / 1024}KB까지). 다른 그림을 고르세요 — 보통은 화면이 줄여서 올립니다.",
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
        // 프로필을 둔 이름이 먼저 자리를 잡고(상한에 밀려 고칠 수 없게 되지 않게), 남은 자리를 호출 기록 이름으로 (3차 e /self-review security)
        var saved = profiles.Store.Agents();
        var seen = saved.Select(ProfileStore.AgentKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var known = runs.KnownAgents().Where(n => ProfileStore.AgentKey(n) is { } k && seen.Add(k))
            .Order(StringComparer.Ordinal).Take(Math.Max(0, ProfileStore.MaxAgents - saved.Count));
        ProfileRows =
        [
            (ProfileStore.User, "나", "나"),
            (ProfileStore.Conductor, "지휘자", "지휘자"),
            .. saved.Concat(known).Order(StringComparer.Ordinal).Select(n => (ProfileStore.AgentKey(n)!, "외부: " + n, n)),
        ];
    }
}
