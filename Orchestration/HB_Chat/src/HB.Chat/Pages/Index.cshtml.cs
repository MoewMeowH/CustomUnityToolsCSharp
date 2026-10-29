using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방을 고르지 않은 첫 화면: 왼쪽 방 목록 + 오른쪽 새 방 만들기.
/// ?handler=RoomList: 앱 전체 알림을 받은 화면이 목록 부분만 다시 받는다 (계획서 2-2 a).
/// </summary>
public sealed class IndexModel(RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, IOptions<ChatOptions> options, GroupChat groups) : PageModel
{
    /// <summary>
    /// 새 단톡방 (계획서 2026-10-29_HB_Chat-단톡방, 1단계): 구성원 이름 · 역할 줄들, 대화 · 읽기 단계만. 구성원마다 숨은 자식 방이 생긴다.
    /// </summary>
    /// <summary>Codex 구성원 모델 목록 (이 계정 — ~/.codex/models_cache.json). 비면 "Codex 기본"만.</summary>
    public IReadOnlyList<CodexModels.Model> CodexModelList => CodexModels.Available();

    /// <summary>Codex 구성원을 켤 수 있는 작업 폴더 (설정 화면).</summary>
    public IReadOnlyList<string> CodexWorkspaces => groups.CodexSettings?.Workspaces() ?? [];

    /// <summary>
    /// 새 단톡방. 단계는 구성원 칸마다(levels — ⑤ 결정, 비면 읽기 · Codex는 읽기), baseMcp: Claude 구성원에게 작업 폴더 기본 MCP (기본 끔).
    /// </summary>
    public IActionResult OnPostCreateGroup(string workspace, string model, string? title, string? effort,
        string[]? names, string[]? roles, string[]? engines, string[]? codexModels, string[]? codexEfforts, string[]? levels, bool baseMcp = false)
    {
        static string? At(string[]? values, int i) => values is not null && i < values.Length ? values[i] : null;
        var o = options.Value;
        var (cleanEffort, effortError) = RoomEffort.Clean(effort);
        Error = o.WorkspacePath(workspace) is null ? "허용 목록에 없는 작업 폴더입니다."
            : !o.ModelList.Contains(model, StringComparer.Ordinal) ? "허용 목록에 없는 모델입니다."
            : effortError;
        // 단계 칸: 비면 기본(읽기, Codex도 읽기), 모르는 값은 거절 — Enum.TryParse는 숫자 글도 받아 이름만 본다
        PermissionLevel? LevelAt(int i) => At(levels, i) is { Length: > 0 } s && Enum.GetNames<PermissionLevel>().Contains(s) ? Enum.Parse<PermissionLevel>(s) : null;
        if (Error is null && Enumerable.Range(0, names?.Length ?? 0).Any(i => At(levels, i) is { Length: > 0 } && LevelAt(i) is null)) Error = "알 수 없는 권한 단계입니다.";
        if (Error is null)
        {
            var members = (names ?? []).Select((n, i) => new GroupMemberSpec(n, At(roles, i) ?? "", At(engines, i), At(codexModels, i), At(codexEfforts, i), LevelAt(i))).ToList();
            var (group, error) = groups.Create(workspace, PermissionLevel.Read, model, cleanEffort, title, members, baseMcp);
            if (group is not null)
            {
                hub.Touch(group.Id);
                return Redirect($"/Room/{group.Id}");
            }
            Error = error;
        }
        OnGet();
        return Page();
    }

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
        Sidebar = new SidebarView(list.All(), null) { TrashCount = store.All().Count(r => r.DeletedAt is not null && r.GroupId is null) };
        ShowTrash = trash is "1" or "true";
        if (ShowTrash) TrashRooms = [.. store.All().Where(r => r.DeletedAt is not null && r.GroupId is null).OrderByDescending(r => r.DeletedAt)];
    }

    /// <summary>사이드바 구독 한도 줄 (계획서 2026-10-29_사용량-한도-표시) — 목록을 다시 받을 때 화면이 같이 받는다.</summary>
    public IActionResult OnGetLimits()
    {
        Response.Headers.CacheControl = "no-store";
        return Partial("_Limits");
    }

    public IActionResult OnGetRoomList(string? current)
    {
        Response.Headers.CacheControl = "no-store";
        return Partial("_RoomList", new SidebarView(list.All(), RoomStore.IsRoomId(current) ? current : null)
        {
            TrashCount = store.All().Count(r => r.DeletedAt is not null && r.GroupId is null),
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
            // 단톡방이면 구성원 자식 방도 함께 (계획서 2026-10-29_HB_Chat-단톡방)
            if (store.Get(id) is { IsGroup: true }) { if (groups.SetDeleted(id, null) is { } groupError) throw new InvalidOperationException(groupError); }
            else store.Update(id, r => r with { DeletedAt = null });
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

    /// <summary>
    /// 휴지통 즉시 비우기 (4차 C1): 줄의 "지금 지우기"는 id 하나, "휴지통 비우기"는 화면에 보인 방 ID들(ids — /self-review bd52c6f0d1 C3).
    /// 휴지통 밖 방은 지우지 않는다(Trash.DeleteNow). 세션 기록은 남는다. 휴지통 방이 쓰던 작업 폴더를 설정에서 뺄 수 있게 된다.
    /// </summary>
    public IActionResult OnPostDeleteTrash(string? id, string[]? ids)
    {
        string[] targets = ids is { Length: > 0 } ? ids : id is null ? [] : [id];
        if (targets.Length == 0 || !targets.All(RoomStore.IsRoomId)) return NotFound();
        try
        {
            foreach (var removed in Trash.DeleteNow(store, options.Value.DataFolder, targets))
            {
                hub.ForgetCommands(removed);
                hub.Touch(removed);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = $"휴지통에서 지우지 못했습니다: {ex.Message}";
            OnGet(trash: "1");
            return Page();
        }
        return Redirect("/?trash=1");
    }

    public IActionResult OnPostCreate(string workspace, PermissionLevel level, string model, string? title, string? effort = null)
    {
        var o = options.Value;
        var (cleanEffort, effortError) = RoomEffort.Clean(effort);
        Error = o.WorkspacePath(workspace) is null ? "허용 목록에 없는 작업 폴더입니다."
            : !o.ModelList.Contains(model, StringComparer.Ordinal) ? "허용 목록에 없는 모델입니다."
            : !Enum.IsDefined(level) ? "알 수 없는 권한 단계입니다."
            : effortError
            ?? (title is { Length: > 200 } ? "제목이 너무 깁니다." : null);
        if (Error is null)
        {
            try
            {
                var room = store.Create(workspace, level, model, title, effort: cleanEffort);
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
