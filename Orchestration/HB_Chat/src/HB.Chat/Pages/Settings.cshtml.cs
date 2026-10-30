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
    ProfileView profiles, FaceCatalog faces, RoomRuns runs, StickerPacks packs, CodexSettings codexSettings, CliVersions cliVersions) : PageModel
{
    // ---- Codex 구성원: 켤 수 있는 작업 폴더 · 실행 파일 · 이 계정 모델 ----

    public IReadOnlyList<string> CodexWorkspaces => codexSettings.Workspaces();
    public IReadOnlyList<WorkspaceEntry> AllWorkspaceList => options.Value.AllWorkspaces;
    public string? CodexExe => options.Value.ResolveCodex();
    public IReadOnlyList<CodexModels.Model> CodexModelList => CodexModels.Available();

    /// <summary>Codex 구성원을 켤 작업 폴더 저장 — 허용 목록에 있는 이름만.</summary>
    public IActionResult OnPostCodexWorkspaces(string[]? names)
    {
        var known = options.Value.AllWorkspaces.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            codexSettings.Save((names ?? []).Where(known.Contains));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Codex 설정을 저장하지 못했습니다: {ex.Message}";
            OnGet();
            return Page();
        }
        return Redirect("/Settings?done=codex");
    }

    public ProfileView Profiles => profiles;
    public FaceCatalog Faces => faces;
    public StickerPacks Packs => packs;

    /// <summary>전체 스티커 팩의 그림 찾기 (표정 미리보기 — 4차 D2).</summary>
    public HB.Orchestration.ChatUi.StickerCatalog GlobalStickers => packs.For(packs.Effective((string?)null));

    /// <summary>화면 테마 저장 (5차 a). 저장하면 열린 화면이 방을 다시 연다 — 데스크톱 앱은 같은 신호로 /api/appearance를 다시 읽는다.</summary>
    public IActionResult OnPostTheme(string? theme)
    {
        Error = profiles.Store.SetTheme(theme);
        if (Error is null)
        {
            hub.ProfilesChanged(store.All().Select(r => r.Id));
            return RedirectToPage(new { done = "theme" });
        }
        Fill();
        return Page();
    }

    /// <summary>데스크톱 알림에 답 앞부분 보이기 저장 (체크 상자 — 끄면 값이 오지 않는다). 데스크톱 앱은 방 신호로 다시 읽는다.</summary>
    public IActionResult OnPostNotify(bool notifyBody)
    {
        Error = profiles.Store.SetNotifyBody(notifyBody);
        if (Error is null)
        {
            hub.ProfilesChanged(store.All().Select(r => r.Id));
            return RedirectToPage(new { done = "notify" });
        }
        Fill();
        return Page();
    }

    /// <summary>테마 이름 (설정 화면).</summary>
    public static string ThemeName(string theme) => theme switch
    {
        ProfileStore.ThemeSystem => "시스템 따름 (Windows 밝게 · 어둡게 설정대로)",
        ProfileStore.ThemeLight => "밝게",
        _ => "디코 다크",
    };

    /// <summary>
    /// 전체 스티커 팩 저장 (4차 D2): 빈 값이면 기본(스티커 폴더 바로 아래 파일). 있는 팩만. 방에 따로 고른 팩이 먼저다.
    /// 저장하면 열린 화면이 방을 다시 연다.
    /// </summary>
    public IActionResult OnPostStickerPack(string? pack)
    {
        var p = string.IsNullOrWhiteSpace(pack) ? null : pack.Trim();
        // 폴더가 사라진 팩이라도 지금 저장된 값 그대로는 받는다 — 화면이 그 값을 선택된 채 다시 보낸다 (지휘자 검토 K3)
        Error = p is not null && !packs.Exists(p) && p != profiles.Store.StickerPack() ? "없는 스티커 팩입니다." : profiles.Store.SetStickerPack(p);
        if (Error is null)
        {
            hub.ProfilesChanged(store.All().Select(r => r.Id));
            return RedirectToPage(new { done = "pack" });
        }
        Fill();
        return Page();
    }

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
        "pack" => "스티커 팩을 바꿨습니다.",
        "theme" => "화면 테마를 바꿨습니다.",
        "notify" => "데스크톱 알림 설정을 바꿨습니다.",
        "mcp-added" => "MCP 서버를 더했습니다. 쓸 방의 곁칸 → 연결에서 켜세요.",
        "mcp-secret" => "MCP 서버를 더했습니다 — 다만 확인할 점이 있습니다 (평문 토큰 · 원격 http · 원격으로 보내는 환경 변수 — 목록의 경고 줄). 토큰은 ${변수} 자리표시를 권합니다.",
        "mcp-removed" => "MCP 서버를 목록에서 뺐고, 켜 두었던 방에서도 껐습니다.",
        "codex" => "Codex 구성원을 켤 작업 폴더를 저장했습니다.",
        "cli" => Cli.LastTest is { } t ? t.Passed ? $"카드 자가 시험 통과 — claude CLI {t.Version}을 확인한 판으로 적었습니다." : "카드 자가 시험이 통과하지 못했습니다 — 아래 단계를 보세요." : null,
        _ => null,
    };

    // ---- 데이터 백업 (결정: 설정 단추로 zip 내려받기 · 앱 데이터 + 세션 기록 · 복원은 zip 안 글대로) ----

    /// <summary>지금 백업하면 담길 것 (화면에 크기 · 수). 화면을 그릴 때마다 파일 목록을 훑는다 — 캐시 폴더는 들어가지 않으므로 기록 파일 수만큼.</summary>
    public ChatBackup.Plan BackupPlan => _backupPlan ??= ChatBackup.Collect(options.Value, store.All(), transcripts);
    private ChatBackup.Plan? _backupPlan;

    /// <summary>실행 중인 방 수 — 있으면 끝난 뒤 받기를 권한다 (방 색인과 세션 기록이 서로 다른 순간으로 담길 수 있음).</summary>
    public int RunningRooms => store.All().Count(r => r.RunStartedAt is not null);

    /// <summary>
    /// 백업 zip 내려받기 — POST(같은 출처 · antiforgery)로만: 다른 사이트가 내려받기를 일으키지 못하게. 닫으면 지워지는 임시 파일 하나에 쓰고
    /// 그대로 보낸다 — 실패하든 앱이 꺼지든 대화 · 토큰이 든 zip이 남지 않게 (ZipArchive는 동기로 쓰므로 응답 스트림에 바로 쓰지 않는다).
    /// </summary>
    public IActionResult OnPostBackup()
    {
        var now = DateTimeOffset.Now;
        var tmp = Path.Combine(Path.GetTempPath(), "hbchat-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        FileStream? file = null;
        try
        {
            file = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            ChatBackup.Write(file, ChatBackup.Collect(options.Value, store.All(), transcripts), options.Value, now);
            file.Position = 0;
            var send = file;
            file = null; // 응답이 닫는다
            return File(send, "application/zip", ChatBackup.FileName(now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"백업을 만들지 못했습니다: {ex.Message}";
            Fill();
            return Page();
        }
        finally
        {
            file?.Dispose();
        }
    }

    // ---- claude CLI 판 · 카드 자가 시험 (결정 "설정 단추로만") ----

    public CliVersions.State Cli => cliVersions.Read();
    public bool CliTestRunning => CliSelfTest.Running;

    /// <summary>
    /// 카드 자가 시험 — 임시 작업 폴더에서 claude를 명령 단계로 한 턴 (haiku, 없으면 모델 목록 첫 값). 끝날 때까지(최대 2분) 기다렸다가 결과를 cli.json에 남긴다.
    /// </summary>
    public async Task<IActionResult> OnPostCliSelfTest()
    {
        var o = options.Value;
        var model = o.ModelList.Contains("haiku", StringComparer.Ordinal) ? "haiku" : o.ModelList[0];
        var result = await CliSelfTest.RunAsync(o.DataFolder, model, o.McpServer,
            (spec, workspace) => new ProcessSpec(o.ResolveClaude(), ClaudeArgs.Build(spec, [.. o.ModelList]), workspace), HttpContext.RequestAborted);
        if (result.Error == CliSelfTest.BusyError)
        {
            Error = result.Error;
            Fill();
            return Page();
        }
        // 화면을 떠나 취소된 시험은 실패가 아니다 — 기록하지 않는다 (/self-review 5ccb0f709a C2)
        if (result.Error == CliSelfTest.AbortedError) return new EmptyResult();
        cliVersions.Save(result);
        hub.TouchAll(); // 사이드바 띠
        return Redirect("/Settings?done=cli#cli");
    }

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
    // Unusable: 앱에서 더한 폴더가 지금 검사(WhyUnsafe — 방 실행 전과 같은)에 걸리는 이유 (4차 C2: 정션으로 바뀐 폴더가 표에서는 멀쩡해 보였음).
    public sealed record WorkspaceRow(WorkspaceEntry Entry, string? McpCommand, bool HasHooks, bool Exists, int Rooms, string? Unusable = null);

    public IReadOnlyList<WorkspaceRow> Workspaces { get; private set; } = [];

    // ---- Unity 프로젝트 ----

    public sealed record UnityRow(WorkspaceEntry Entry, bool Exists, int Rooms, string? Unusable);

    public IReadOnlyList<UnityRow> UnityRows { get; private set; } = [];
    public string? UnityLoadError => options.Value.AppUnityProjects?.LoadError;
    public IReadOnlyList<string> UnityWarnings => options.Value.AppUnityProjects?.LoadWarnings ?? [];
    public string? UnityCli => options.Value.ResolveUnityCli();
    public string? NewUnityName { get; private set; }
    public string? NewUnityPath { get; private set; }

    public IActionResult OnPostAddUnity(string? name, string? path, bool confirm)
    {
        if (!confirm) Error = "연결한 방이 에디터를 묻지 않고 조작한다는 확인에 체크해 주세요.";
        else if (options.Value.AppUnityProjects is not { } added) Error = "Unity 프로젝트 저장소가 준비되지 않았습니다.";
        else Error = added.Add(name, path, store.All());
        if (Error is null) return RedirectToPage(new { done = "added" });
        (NewUnityName, NewUnityPath) = (name, path);
        Fill();
        return Page();
    }

    // ---- 앱에서 더한 MCP 서버 ----

    /// <summary>목록 줄: 이름 · 종류 · 명령/URL(env · headers 값은 안 보임) · 켠 방 수 · 평문 비밀 값으로 보이는 칸.</summary>
    public sealed record McpRow(string Name, string Transport, string Command, int Rooms, IReadOnlyList<string> Secrets);

    public IReadOnlyList<McpRow> McpRows { get; private set; } = [];
    public string? McpLoadError => options.Value.AppMcp?.LoadError;
    public IReadOnlyList<string> McpWarnings => options.Value.AppMcp?.LoadWarnings ?? [];

    /// <summary>~/.claude.json에서 가져올 수 있는 서버 (user · local 범위).</summary>
    public IReadOnlyList<ImportableMcp> McpImports { get; private set; } = [];
    public string? NewMcpName { get; private set; }
    public string? NewMcpType { get; private set; }
    public string? NewMcpCommand { get; private set; }
    public string? NewMcpUrl { get; private set; }
    public string? NewMcpJson { get; private set; }

    private const string McpConfirm = "켠 방에서 이 서버 명령이 승인 없이 돈다는 확인에 체크해 주세요.";

    /// <summary>칸 채우기: 이름 · 종류(stdio · http · sse) · 명령 줄 또는 URL.</summary>
    public IActionResult OnPostAddMcpFields(string? name, string? type, string? command, string? url, bool confirm)
    {
        (NewMcpName, NewMcpType, NewMcpCommand, NewMcpUrl) = (name, type, command, url);
        return AddMcp([(name?.Trim() ?? "", AppMcpStore.FromFields(type, command, url))], confirm);
    }

    /// <summary>JSON 붙여넣기 — {"mcpServers": {...}} · {"이름": {...}} · 항목 하나(이름 칸과 함께).</summary>
    public IActionResult OnPostAddMcpJson(string? name, string? json, bool confirm)
    {
        (NewMcpName, NewMcpJson) = (name, json);
        var (items, error) = AppMcpStore.ParsePaste(json, name);
        if (error is not null)
        {
            Error = error;
            Fill();
            return Page();
        }
        return AddMcp(items, confirm);
    }

    /// <summary>~/.claude.json에서 가져오기 — 그 범위 · 이름의 항목을 그대로 복사한다.</summary>
    public IActionResult OnPostImportMcp(string? scope, string? name, bool confirm)
    {
        if (AppMcpStore.ReadClaudeJson(AppMcpStore.ClaudeJsonPath()).FirstOrDefault(i => i.Scope == scope && i.Name == name) is not { } item)
        {
            Error = "~/.claude.json에서 그 서버를 찾지 못했습니다 — 화면을 새로 고쳐 보세요.";
            Fill();
            return Page();
        }
        return AddMcp([(item.Name, item.Entry)], confirm);
    }

    /// <summary>
    /// 빼기 — 그 이름을 켜 둔 방에서도 끈다(작업 폴더 .mcp.json에 같은 이름이 있는 방은 그 서버를 쓰고 있으니 그대로). 안 끄면 같은 이름으로 다른 서버를
    /// 다시 더할 때 그 방들이 확인 없이 새 서버를 쓴다 (/self-review 779716239c C5).
    /// </summary>
    public IActionResult OnPostRemoveMcp(string? name)
    {
        var n = name?.Trim() ?? "";
        Error = options.Value.AppMcp is { } servers ? servers.Remove(n) : "MCP 서버 저장소가 준비되지 않았습니다.";
        if (Error is null)
        {
            foreach (var room in store.All().Where(r => r.McpExtra.Contains(n, StringComparer.OrdinalIgnoreCase) && !InWorkspace(r, n)))
            {
                try
                {
                    store.Update(room.Id, r => r with { McpExtra = [.. r.McpExtra.Where(x => !string.Equals(x, n, StringComparison.OrdinalIgnoreCase))] });
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Error = $"목록에서는 뺐지만 일부 방에서 끄지 못했습니다: {ex.Message}";
                }
            }
            if (Error is null) return RedirectToPage(new { done = "mcp-removed" });
        }
        Fill();
        return Page();
    }

    /// <summary>그 방 작업 폴더 .mcp.json에 그 이름이 있는지 — 있으면 앱 서버가 아니라 그쪽을 쓴다.</summary>
    private bool InWorkspace(Room room, string name) =>
        options.Value.WorkspacePath(room.Workspace) is { } ws && McpConfigFile.ServerNames(ws).Contains(name);

    /// <summary>여럿을 차례로 더한다 — 하나라도 실패하면 그 앞까지 더하고 오류를 보인다. 평문 비밀 값이 있으면 경고 문구로 돌아간다(결정 2 "경고만").</summary>
    private IActionResult AddMcp(IReadOnlyList<(string Name, System.Text.Json.Nodes.JsonObject Entry)> items, bool confirm)
    {
        if (!confirm) Error = McpConfirm;
        else if (options.Value.AppMcp is not { } servers) Error = "MCP 서버 저장소가 준비되지 않았습니다.";
        else
            foreach (var (name, entry) in items)
                if (servers.Add(name, entry) is { } why)
                {
                    Error = items.Count > 1 ? $"{name}: {why}" : why;
                    break;
                }
        if (Error is null)
            return RedirectToPage(new { done = items.Any(i => AppMcpStore.SecretWarnings(i.Entry).Count > 0) ? "mcp-secret" : "mcp-added" });
        Fill();
        return Page();
    }

    public IActionResult OnPostRemoveUnity(string? name)
    {
        Error = options.Value.AppUnityProjects is { } added ? added.Remove(name, store.All()) : "Unity 프로젝트 저장소가 준비되지 않았습니다.";
        if (Error is null) return RedirectToPage(new { done = "removed" });
        Fill();
        return Page();
    }

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
        Sidebar = new SidebarView(list.All(), null) { TrashCount = all.Count(r => r.DeletedAt is not null && r.GroupId is null) };
        Workspaces =
        [
            .. options.Value.AllWorkspaces.Select(w => new WorkspaceRow(w,
                WorkspaceStore.McpCommand(w.Path, options.Value.McpServer), WorkspaceStore.HasProjectHooks(w.Path), Directory.Exists(w.Path),
                all.Count(r => string.Equals(r.Workspace, w.Name, StringComparison.OrdinalIgnoreCase)),
                w.FromConfig || !Directory.Exists(w.Path) ? null : options.Value.AppWorkspaces?.WhyUnsafe(w.Path))),
        ];
        McpRows =
        [
            // 켠 방: 휴지통 밖, 작업 폴더에 같은 이름이 없어 이 앱 서버를 실제로 쓰는 방만 (779716239c C6)
            .. (options.Value.AppMcp?.All() ?? []).Select(s => new McpRow(s.Name, AppMcpStore.TransportOf(s.Entry), AppMcpStore.Describe(s.Entry),
                all.Count(r => r.DeletedAt is null && r.McpExtra.Contains(s.Name, StringComparer.OrdinalIgnoreCase) && !InWorkspace(r, s.Name)),
                AppMcpStore.SecretWarnings(s.Entry))),
        ];
        McpImports = AppMcpStore.ReadClaudeJson(AppMcpStore.ClaudeJsonPath());
        UnityRows =
        [
            .. options.Value.AllUnityProjects.Select(u => new UnityRow(u, Directory.Exists(u.Path),
                all.Count(r => string.Equals(r.UnityProject, u.Name, StringComparison.OrdinalIgnoreCase)),
                u.FromConfig || !Directory.Exists(u.Path) ? null : options.Value.AppUnityProjects?.WhyUnusable(u.Path))),
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
