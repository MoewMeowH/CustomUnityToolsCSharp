using HB.Orchestration.ChatUi;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방 하나. 말풍선은 세션 기록에서 읽는다 (앱은 말 본문을 따로 저장하지 않는다). 진행 중 턴은 /events/{id} SSE로 흘린다.
/// ?handler=Messages: 말풍선 한 쪽 — 맨 끝 50개, before=줄이면 그 앞 50개(위로 불러오기), after=줄이면 그 뒤 전부(턴이 끝난 뒤 새 말).
/// ?handler=Pane: 방 전환 때 오른쪽 칸만 (2-2 a). Send · Stop · MarkRead는 POST (antiforgery + 같은 출처).
/// </summary>
public sealed partial class RoomModel(
    RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, StickerPacks packs, FaceCatalog faces, RoomRuns runs,
    RoomNotes notes, Terminals terminals, AgentBrowser browser, McpProbe probe, IOptions<ChatOptions> options, GroupChat groups)
    : PageModel
{
    public Room Room { get; private set; } = null!;

    /// <summary>이 방 마지막 실행이 알려 준 서브에이전트 이름 (Claude 앱 기능 D — 곁칸 목록). 이 앱을 켠 뒤 아직 돌지 않았으면 null.</summary>
    public IReadOnlyList<string>? Agents => hub.Commands(Room.Id)?.Agents;

    /// <summary>고를 수 있는 모델 (설정 Chat:Models — 곁칸 방 설정 select).</summary>
    public IReadOnlyList<string> Models => options.Value.ModelList;

    /// <summary>이 방 스티커 (4차 D2): 방에 고른 팩 → 전체 설정 → 기본. 말풍선 · 실행 스티커 · 표정 그림이 같이 쓴다.</summary>
    public StickerCatalog Stickers => _stickers ??= packs.ForRoom(Room);
    private StickerCatalog? _stickers;

    public StickerPacks Packs => packs;

    /// <summary>데스크톱 미니 창 배치 (`?view=mini`). 서버 동작은 같고 CSS만 다르다.</summary>
    public bool Mini => string.Equals(Request.Query["view"], "mini", StringComparison.OrdinalIgnoreCase);
    public ChatTranscript? Transcript { get; private set; }
    public string Title => RoomList.TitleOf(Room, Transcript);

    /// <summary>이번에 그릴 말풍선 쪽 (Load 뒤 맨 끝 50개, Messages 핸들러가 before · after로 바꾼다).</summary>
    public MessagePage Messages { get; private set; } = new([], false);

    /// <summary>after로 새 말만 붙이는 응답이면 true — 빈 방 안내 · 읽기 실패 줄을 그리지 않는다.</summary>
    public bool Fragment { get; private set; }

    /// <summary>
    /// after(새 말 받기) 조각이면 true — 맨 뒤(Trailing) 실행 말풍선도 다시 보내고, 화면은 앞서 그린 Trailing을 이것으로 바꾼다
    /// (다음 말이 생겨 그 앞으로 옮겨 간 말풍선이 두 번 보이지 않게 — /self-review a9e6066519 C1). 위로 불러오기 조각에는 없다.
    /// </summary>
    public bool AfterFragment { get; private set; }

    /// <summary>
    /// 턴 도중 실행 말풍선 조각(?handler=Runs)이면 그 말풍선들 — 화면에 그린 마지막 줄 뒤에 놓일 것만. null이면 보통 쪽.
    /// _Messages가 이것만 그리고 끝낸다 (말풍선 모양을 한 곳에서 — 턴 끝에는 기록 제자리 말풍선으로 바뀐다).
    /// </summary>
    public IReadOnlyList<RunBubble>? LiveRuns { get; private set; }

    /// <summary>이 방에서 부른 run_mode 실행 말풍선의 자리. 그릴 때 처음 한 번 계산한다 (Fork 등은 안 읽음 — a9e6066519 C3).</summary>
    public RunPlacement Runs => _runs ??= runs.For(Transcript);
    private RunPlacement? _runs;

    /// <summary>질문 카드 답 말풍선의 자리 (4차 D1, 세션 기록에서).</summary>
    public AnswerPlacement Answers => _answers ??= AnswerPlacement.Of(Transcript);
    private AnswerPlacement? _answers;

    /// <summary>방 기록 줄(승인 결과 · 백그라운드 정리 표시)의 자리.</summary>
    public NotePlacement Notes => _notes ??= notes.For(Room.Id, Transcript);
    private NotePlacement? _notes;

    /// <summary>길면 접어 둔다 (외부 에이전트 응답은 수천 자가 흔하다).</summary>
    public const int RunFoldChars = 1500;

    /// <summary>세션 기록의 마지막 말 줄 번호 (읽음 표시 · 새 말 받기의 기준).</summary>
    public int LastLine => RoomList.LastLine(Transcript);

    public FaceCatalog Faces => faces;

    /// <summary>갈래 방: 만든 뒤 원래 방이 더 진행됐는지 (첫 말 때 그 뒤 대화까지 이어받는다 — 95e8b1c242 C2).</summary>
    public bool ForkSourceGrew =>
        Room.ForkOf is { } source && Room.ForkSourceSize is { } then && transcripts.Size(source) is { } now && now != then;

    /// <summary>머리말 표정 (끝난 턴의 잠깐 표정 포함).</summary>
    public Face Face => hub.FaceOf(Room);

    /// <summary>왼쪽 방 목록 (첫 화면에서만 만든다 — 방 전환 · 말풍선 다시 받기에는 쓰지 않음).</summary>
    public SidebarView Sidebar => new(list.All(), Room.Id) { TrashCount = store.All().Count(r => r.DeletedAt is not null && r.GroupId is null) };
    /// <summary>대시보드 채팅방 (세션 기록이 생긴 뒤부터 보인다 — 첫 말 전에는 대시보드가 404).</summary>
    public string? DashboardLink =>
        options.Value.DashboardUrl is { Length: > 0 } url ? $"{url.TrimEnd('/')}/Room/{Room.SessionId}" : null;

    /// <summary>실행 말풍선 본문 (SafeMarkdown + 스티커 태그) — 외부 에이전트 응답은 지휘자 말과 같은 규칙으로 그린다.</summary>
    public HtmlString RenderRun(RunBubble b) => new(StickerRenderer.Replace(SafeMarkdown.ToHtml(b.Markdown, SafeMarkdown.Default), Stickers));

    /// <summary>실행 말풍선의 자동 스티커 (상황 → 태그, 대시보드 설정 Auto 그대로). 없으면 null.</summary>
    public HtmlString? RunSticker(RunBubble b) =>
        b.Situation is { } s && Stickers.ForSituation(s) is { } tag ? new HtmlString(StickerRenderer.Html(tag, Stickers)) : null;

    /// <summary>호출 상세 (대시보드). 대시보드 주소가 없으면 null.</summary>
    public string? CallLink(RunBubble b) =>
        options.Value.DashboardUrl is { Length: > 0 } url ? $"{url.TrimEnd('/')}/Call/{Uri.EscapeDataString(b.CallId)}" : null;

    /// <summary>말풍선 색 (ChatUi _ChatStyles의 .external · .apireview · .subagent).</summary>
    public static string RunClass(RunBubble b) => b.Kind switch
    {
        RunBubbleKind.ApiReview => "apireview",
        RunBubbleKind.SelfReview => "subagent",
        _ => "external",
    };

    /// <summary>곁칸 "방 정보": 작업 폴더 경로 (허용 목록에서 빠졌으면 null).</summary>
    public string? WorkspacePath => options.Value.WorkspacePath(Room.Workspace);

    /// <summary>곁칸 "외부 실행" 한 줄: 실행 하나 (모드 · 시작 시각 · 응답 수 · 실패 수 · 대시보드 링크).</summary>
    public sealed record SideRun(string RunId, string Mode, DateTimeOffset At, int Replies, int Errors, string? Link);

    /// <summary>곁칸 "외부 실행": 이 방에서 부른 run_mode 실행 (최근 것부터, 말풍선과 같은 자료 — 확정 연결만).</summary>
    public IReadOnlyList<SideRun> SideRuns =>
    [
        .. Runs.All.Where(b => b.RunId.Length > 0).GroupBy(b => b.RunId).Select(g =>
        {
            var start = g.FirstOrDefault(b => b.Kind == RunBubbleKind.Start);
            var replies = g.Where(b => b.Kind != RunBubbleKind.Start).ToList();
            var mode = start?.Detail?.Split(" · ")[0] ?? replies.FirstOrDefault()?.Detail ?? "?";
            var link = options.Value.DashboardUrl is { Length: > 0 } url ? $"{url.TrimEnd('/')}/Room/Run/{Uri.EscapeDataString(g.Key)}" : null;
            return new SideRun(g.Key, mode, (start ?? replies[0]).At, replies.Count, replies.Count(r => r.IsError), link);
        }).OrderByDescending(r => r.At),
    ];

    /// <summary>
    /// 곁칸 (3차 c): 방 정보 · 외부 실행 · 첨부. 열 때와 턴이 끝날 때 화면이 다시 받는다 (방 칸을 그릴 때는 만들지 않음 — 열지 않으면 계산하지 않게).
    /// </summary>
    public IActionResult OnGetSide(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        return Partial("_RoomSide", this);
    }

    /// <summary>곁칸 "문서" 탭 목록: 작업 폴더의 계획서 · 작업 로그 · 색인 (탭을 열 때 · 목록으로 돌아올 때).</summary>
    public IReadOnlyList<ProjectDocs.Project> DocProjects { get; private set; } = [];

    /// <summary>
    /// 곁칸 "문서" 탭의 방 기준 목록: 이 방 세션 기록의 도구 입력에 나온 계획서 · 로그.
    /// 상태 — null: 보이지 않음 (Codex 구성원 방 · 아직 세션 기록 없음), "error": 세션 기록을 읽지 못함, "ok": 목록(비어 있을 수 있음).
    /// </summary>
    public IReadOnlyList<ProjectDocs.RoomProject> RoomDocs { get; private set; } = [];
    public string? RoomDocsState { get; private set; }

    public IActionResult OnGetDocs(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        Room = room;
        // 세션 기록은 ChatTranscripts가 기억해 둔 데서 붙은 줄만 더 읽는다 (Load와 달리 말 목록은 만들지 않음)
        var transcript = room.Engine == RoomEngines.Codex ? null : transcripts.Read(room.SessionId);
        RoomDocsState = transcript is null ? null : transcript.FormatError is null ? "ok" : "error";
        (DocProjects, RoomDocs) = ProjectDocs.Find(WorkspacePath, RoomDocsState == "ok" ? transcript!.FileMentions : null);
        return Partial("_RoomDocs", this);
    }

    /// <summary>문서 하나를 SafeMarkdown으로 그린 HTML 조각 — 경로는 <see cref="ProjectDocs.Resolve"/>가 허락한 것만.</summary>
    public IActionResult OnGetDoc(string id, string? path)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        Room = room;
        if (ProjectDocs.Resolve(WorkspacePath, path) is not { } file) return NotFound();
        string text;
        try { text = System.IO.File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return NotFound(); }
        return Content(SafeMarkdown.ToHtml(text, SafeMarkdown.Default), "text/html; charset=utf-8");
    }

    /// <summary>
    /// 입력칸 '/' 목록 (Claude 앱 기능 A): 그 방 마지막 실행의 init 이름 + 작업 폴더 · 사용자 · 플러그인 파일 설명 (<see cref="CommandCatalog"/>).
    /// 화면이 방을 열 때 · 턴이 끝날 때 받는다.
    /// </summary>
    public IActionResult OnGetCommands(string id)
    {
        Response.Headers.CacheControl = "no-store";
        // 방 정보만 — Load는 세션 기록 전체를 읽는다 (방을 열 때 · 턴 끝마다 부르므로, 지휘자 검토 R1)
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var workspace = options.Value.WorkspacePath(room.Workspace);
        return new JsonResult(new { commands = CommandCatalog.Build(hub.Commands(id), workspace, user) });
    }

    /// <summary>명령 방: 작업 폴더 · 사용자 설정이 이미 미리 허용한 명령 (카드 없이 실행됨).</summary>
    public IReadOnlyList<(string Source, string Rule)> InheritedRules =>
        Room.Level == PermissionLevel.Command ? hub.InheritedCommandRules(Room) : [];

    public IActionResult OnGet(string id)
    {
        if (!Load(id)) return NotFound();
        return Page();
    }

    /// <summary>Codex 구성원 과정 창 (_CodexLog) — 화면이 실행 중 · 턴 끝에 다시 받는다 (sol 리뷰 ①-P2). Codex 방이 아니면 404.</summary>
    public IActionResult OnGetCodexLog(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id) || Room.Engine != RoomEngines.Codex) return NotFound();
        return Partial("_CodexLog", this);
    }

    public IActionResult OnGetPane(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        return Partial(Room.IsGroup ? "_GroupPane" : "_RoomPane", this);
    }

    /// <summary>
    /// 읽음 표시: 화면이 맨 아래를 보고 있을 때 마지막 말의 줄 번호를 보낸다. 세션 기록 마지막 줄로 자르고 앞으로만 간다
    /// (늦게 온 작은 값이 되돌리지 않게). 바뀌었을 때만 저장하고 목록 알림을 낸다 (<see cref="RoomList.NextReadUpTo"/>).
    /// </summary>
    public IActionResult OnPostMarkRead(string id, int line)
    {
        if (!RoomStore.IsRoomId(id) || line < 0 || store.Get(id) is not { } current) return NotFound();
        // 아직 갈라지지 않은 갈래 방은 읽음을 적지 않는다 — 빈 방의 0이 남으면 이어받은 말이 전부 안 읽음이 된다 (95e8b1c242 C1)
        if (current.ForkOf is not null) return new JsonResult(new { ok = true });
        if (RoomList.NextReadUpTo(current.ReadUpTo, line, RoomList.LastLine(transcripts.Read(current.SessionId))) is not { } next)
            return new JsonResult(new { ok = true });
        try
        {
            if (store.Update(id, r => next > (r.ReadUpTo ?? int.MinValue) ? r with { ReadUpTo = next } : r) is null) return NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"읽음 표시를 저장하지 못했습니다: {ex.Message}");
        }
        hub.Touch(id);
        return new JsonResult(new { ok = true });
    }

    public IActionResult OnGetMessages(string id, int? before, int? after)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        if (Room.IsGroup)
        {
            LoadGroup(after);
            return Partial("_GroupMessages", this);
        }
        // 읽기 실패는 실패로 돌려준다: 화면이 앞 말풍선을 그대로 둔다 (대시보드 채팅방과 같은 처리).
        if (Transcript?.FormatError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        Messages = RoomList.Page(Transcript?.Entries ?? [], before, after);
        Fragment = (before is not null || after is not null) && !Messages.Reset;
        AfterFragment = Fragment && after is not null;
        return Partial("_Messages", this);
    }

    /// <summary>
    /// 턴 도중 run_mode 결과가 온 뒤(runs 알림) 화면이 부른다: 화면이 그린 마지막 줄(after) 뒤에 놓일 실행 말풍선만 돌려준다.
    /// 이 방 세션 기록에서 찾으므로 다른 방 실행은 나오지 않는다. 단톡방은 아직 없음.
    /// </summary>
    public IActionResult OnGetRuns(string id, int after)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id) || Room.IsGroup) return NotFound();
        if (Transcript?.FormatError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        LiveRuns = LiveRunsAfter(Transcript?.Entries ?? [], Runs, after);
        return Partial("_Messages", this);
    }

    /// <summary>줄 번호가 after보다 큰 말 앞의 말풍선 + 맨 뒤 말풍선 (시각 차례). after가 마지막 말이면 맨 뒤 것만.</summary>
    internal static IReadOnlyList<RunBubble> LiveRunsAfter(IReadOnlyList<ChatEntry> entries, RunPlacement runs, int after) =>
        [.. entries.Where(e => e.Line > after).SelectMany(runs.For).Concat(runs.Trailing).OrderBy(b => b.At)];

    /// <summary>방 이름 바꾸기 (비우면 첫 말로 돌아감). 목록 알림을 낸다.</summary>
    public IActionResult OnPostRename(string id, string? title)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        var (clean, error) = RoomList.CleanTitle(title);
        if (error is not null) return BadRequest(error);
        return Change(id, r => r with { Title = clean }, "이름을 바꾸지 못했습니다");
    }

    /// <summary>
    /// 이 방 스티커 팩 (4차 D2, 곁칸 방 정보): 빈 값이면 전체 설정을 따른다. 있는 팩만. 바뀌면 열린 화면이 방을 다시 연다
    /// (말풍선 스티커 · 표정 그림 주소가 팩마다 다르다).
    /// </summary>
    public IActionResult OnPostStickerPack(string id, string? pack)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        var p = string.IsNullOrWhiteSpace(pack) ? null : pack.Trim();
        // 폴더가 사라진 팩이라도 지금 고른 값 그대로는 받는다 (지휘자 검토 K3)
        if (p is not null && !packs.Exists(p) && p != store.Get(id)?.StickerPack) return BadRequest("없는 스티커 팩입니다.");
        var result = Change(id, r => r with { StickerPack = p }, "스티커 팩을 바꾸지 못했습니다");
        if (result is JsonResult) hub.ProfilesChanged([id]);
        return result;
    }

    /// <summary>
    /// 방 설정 (Claude 앱 기능 B, 곁칸 방 정보): 모델(허용 목록) · 추론 강도(빈 값 = 기본) · 권한 단계(올리기 · 내리기 모두 — 결정).
    /// 다음 실행부터 반영된다 — 실행 중인 턴은 그대로. 머리 배지 · 목록이 바뀌게 열린 화면이 방을 다시 연다.
    /// </summary>
    public IActionResult OnPostSettings(string id, string? model, string? effort, PermissionLevel? level, PermissionLevel? expectedLevel)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (store.Get(id) is { } target && GroupSettingsRefusal(target) is { } refusal) return BadRequest(refusal);
        if (model is null || !options.Value.ModelList.Contains(model, StringComparer.Ordinal) || !ClaudeArgs.IsSafeName(model))
            return BadRequest("허용 목록에 없는 모델입니다.");
        var (cleanEffort, effortError) = RoomEffort.Clean(effort);
        if (effortError is not null) return BadRequest(effortError);
        if (level is not { } lv || !Enum.IsDefined(lv)) return BadRequest("알 수 없는 권한 단계입니다.");
        // 폼을 그릴 때의 단계와 지금 단계가 다르면 거절 — 다른 탭 · 옛 곁칸이 방금 내린 단계를 다시 올리지 않게 (/self-review security 8ccecae84d C2).
        // 비교는 잠금 안(Update)에서 한다
        var stale = false;
        IActionResult result;
        try
        {
            var updated = store.Update(id, r =>
            {
                if (r.Level != expectedLevel)
                {
                    stale = true;
                    return r;
                }
                return r with { Model = model, Effort = cleanEffort, Level = lv };
            });
            if (updated is null) return NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"방 설정을 바꾸지 못했습니다: {ex.Message}");
        }
        if (stale) return new ConflictObjectResult("다른 곳에서 이 방 권한이 바뀌었습니다 — 곁칸을 닫았다 다시 열어 지금 값을 본 뒤 바꾸세요.");
        hub.Touch(id);
        hub.ForgetCommands(id); // 단계 · 모델이 바뀌면 다음 실행의 init이 새 목록 (db1b130ff2 C7)
        // 명령 아래로 내리면 지휘자가 띄운 프로세스도 끝낸다 — "명령 실행 없음" 단계에서 돌던 서버가 남지 않게 (/self-review 57b59ef134 C1 · a3a9dd85c1 C1). 내 셸은 그대로
        // 앱 단계는 CLI가 묻지 않고 판단해 앱이 막을 자리가 없다 — 내리면 지금 턴을 멈춘다
        if (expectedLevel == PermissionLevel.App && lv != PermissionLevel.App) hub.StopTurn(id);
        // 쉬는 실행(답 뒤 백그라운드 작업)은 띄울 때의 단계 인자로 산다 — 단계를 내리면 작업째 끝낸다 (/cross-review 52ac1810e1 security C1)
        else if (lv < expectedLevel) hub.EndRest(id, "settings", afterTurn: true); // 쓰던 답은 자르지 않고 그 턴 끝에 (af6ffa3da5 C1)
        if (lv == PermissionLevel.Read && expectedLevel != PermissionLevel.Read)
        {
            hub.EndTurnTrust(id);
            terminals.StopRoom(id, "권한 낮춤", TerminalKind.Agent);
            browser.Close(id, "권한 낮춤"); // 지휘자 브라우저도 명령 방만
        }
        result = new JsonResult(new { ok = true });
        if (result is JsonResult) hub.ProfilesChanged([id]);
        return result;
    }

    /// <summary>
    /// "계획 먼저" 켜기 · 끄기 (Claude 앱 기능 C — 입력칸 토글): 다음 실행을 계획 모드로. 명령 단계만 켤 수 있다. 계획을 승인하면 서버가 끈다.
    /// </summary>
    public IActionResult OnPostPlanNext(string id, bool on)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (on && ClaudeArgs.PlanMode(room.Level) is null) return BadRequest("계획 모드는 명령 방에서만 쓸 수 있습니다.");
        return Change(id, r => r with { PlanNext = on }, "계획 먼저를 바꾸지 못했습니다");
    }

    /// <summary>"외부 요청 전에 묻기" 켜기 · 끄기: 다음 실행부터 run_mode를 모델 선택 카드로 묻는다.</summary>
    public IActionResult OnPostAskRunMode(string id, bool on)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (GroupSettingsRefusal(room) is { } refusal) return BadRequest(refusal);
        return Change(id, r => r with { AskRunMode = on }, "외부 요청 전에 묻기를 바꾸지 못했습니다");
    }

    /// <summary>+ 요청 창의 모드 · 에이전트 목록. 읽지 못하면 빈 목록과 error.</summary>
    public IActionResult OnGetModes(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        var list = ModesFor(room);
        return new JsonResult(new
        {
            modes = list.Modes.Select(m => new { name = m.Name, description = m.Description, flow = m.Flow, steps = m.Steps, ownReview = m.OwnReview }),
            agents = list.Agents,
            efforts = ModeCatalog.Efforts,
            error = list.Error,
        });
    }

    private ModeList ModesFor(Room room) =>
        options.Value.WorkspacePath(room.Workspace) is { } ws ? ModeCatalog.Read(ws, options.Value.McpServer) : ModeList.Empty with { Error = "작업 폴더를 찾지 못했습니다." };

    /// <summary>
    /// + 요청 창 보내기: 머리 줄을 붙인 말을 지휘자에게 보내고, 고른 값은 그 말에 붙인다 — 그 말의 실행만 run_mode를 카드로 묻고 미리 채운다
    /// (/self-review 785ebb5e97 C1). interrupt면 끼어들기로.
    /// </summary>
    public IActionResult OnPostModeRequest(string id, string? mode, string? picks, string? text, bool interrupt)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.IsGroup || room.GroupId is not null) return BadRequest("단톡방에서는 외부 에이전트 요청 창을 쓰지 않습니다.");
        if (room.McpOff.Contains(options.Value.McpServer)) return BadRequest($"이 방은 {options.Value.McpServer} 연결을 꺼 두었습니다 — 곁칸에서 켜 주세요.");
        if (string.IsNullOrWhiteSpace(text)) return BadRequest("요청 글을 써 주세요.");
        var list = ModesFor(room);
        if (list.Find(mode) is not { } info) return BadRequest(list.Error ?? $"모드 '{mode}'가 목록에 없습니다.");
        if (ModeCard.ParsePicks(picks) is not { } rows) return BadRequest("고른 값을 읽지 못했습니다.");
        if (ModeCard.CheckPick(info, list, rows) is { } bad) return BadRequest(bad);
        if (TooManyAttachments(text)) return BadRequest($"첨부는 한 말에 {Attachments.MaxPerMessage}개까지입니다.");
        return hub.Send(id, ModeCard.Header(info, rows) + "\n" + text, new ModePick(info.Name, rows), interrupt) is { } error
            ? BadRequest(error)
            : new JsonResult(new { ok = true });
    }

    /// <summary>휴지통으로 (30일 뒤 완전히 지움). 실행 중인 방은 먼저 중지해야 한다 — 기다리는 승인 · 큐가 버려지지 않게.</summary>
    public IActionResult OnPostTrash(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (store.Get(id) is { IsGroup: true })
            return groups.SetDeleted(id, DateTimeOffset.Now) is { } groupError ? BadRequest(groupError) : ChangedGroup(id);
        if (store.Get(id) is { GroupId: not null }) return BadRequest("단톡방 구성원 방은 단톡방과 함께 휴지통에 넣습니다.");
        if (hub.IsBusy(id)) return BadRequest("실행 중인 방입니다 — 중지한 뒤 휴지통에 넣으세요.");
        hub.EndRest(id); // 답 뒤 쉬던 백그라운드 작업도 끝낸다 — 휴지통 방에서 프로세스 · 자동 답이 남지 않게 (/cross-review 52ac1810e1 1-F4)
        hub.ForgetCommands(id);
        terminals.StopRoom(id, "방 삭제"); // 그 방 터미널도 끝낸다
        hub.StopShell(id); // 도는 '!' 명령도 (결과는 휴지통 방이라 지휘자에게 가지 않는다)
        browser.Close(id, "방 삭제");
        probe.Forget(id); // 연결 탭 확인 결과
        return Change(id, r => r with { DeletedAt = DateTimeOffset.Now }, "휴지통에 넣지 못했습니다");
    }

    public IActionResult OnPostRestore(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (store.Get(id) is { IsGroup: true })
            return groups.SetDeleted(id, null) is { } groupError ? BadRequest(groupError) : ChangedGroup(id);
        return Change(id, r => r with { DeletedAt = null }, "되살리지 못했습니다");
    }

    private JsonResult ChangedGroup(string id)
    {
        hub.Touch(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 방 복제(갈래): 지금까지 대화를 이어받는 새 방. 같은 작업 폴더 · 권한 · 모델.
    /// 첫 말을 보낼 때 CLI가 대화를 복사한다 (--fork-session). 세션 기록이 없거나 실행 중이면 거절.
    /// </summary>
    public IActionResult OnPostFork(string id)
    {
        if (!Load(id)) return NotFound();
        if (Room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (hub.IsBusy(id)) return BadRequest("실행 중인 방은 복제하지 않습니다 — 턴이 끝난 뒤 다시 하세요.");
        // 쉬는 실행은 작업이 끝나면 스스로 이어 답해 기록을 쓴다 — 갈래가 쓰는 중인 기록을 읽지 않게 (self C2)
        if (hub.HasRestingTasks(id)) return BadRequest("백그라운드 작업이 도는 방은 복제하지 않습니다 — 작업이 끝나거나 '지금 끝내기' 뒤 다시 하세요.");
        if (transcripts.Size(Room.SessionId) is null) return BadRequest("아직 대화가 없어 복제할 것이 없습니다.");
        var (title, _) = RoomList.CleanTitle(RoomList.Cut(Title, 190) + " (갈래)");
        try
        {
            var fork = store.Create(Room.Workspace, Room.Level, Room.Model, title, forkOf: Room.SessionId,
                forkSourceSize: transcripts.Size(Room.SessionId));
            // 이 방에 고른 스티커 팩 · 추론 강도도 따라간다 (4차 D2 — ef2c83416c C6)
            // 연결(더한 서버 · 끈 기본 연결 · Unity)도 따라간다 — 갈래가 원래 방과 다른 도구로 시작하지 않게 (/self-review 0bcf01451b C5)
            // 원래 방(과 그 조상)의 첨부 폴더도 읽게 — 이어받은 대화의 [첨부] 줄이 그 폴더를 가리킨다
            store.Update(fork.Id, r => r with
            {
                StickerPack = Room.StickerPack, Effort = Room.Effort, McpExtra = Room.McpExtra, McpOff = Room.McpOff, UnityProject = Room.UnityProject,
                AttachFrom = [.. Room.AttachFrom.Where(a => a != Room.Id), Room.Id],
            });
            hub.Touch(fork.Id);
            return new JsonResult(new { ok = true, id = fork.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"복제하지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// /clear · /reset · /new (<see cref="SlashCommands"/>): 같은 작업 폴더 · 단계 · 모델 · 강도에 연결 · 스티커 설정을 이어받은 새 방.
    /// 대화는 이어받지 않고 이전 방은 그대로 남는다. 제목은 비워 둔다 (첫 말로). 실행 중이면 거절.
    /// </summary>
    public IActionResult OnPostNewSession(string id)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.IsGroup || room.GroupId is not null) return BadRequest("단톡방에서는 /clear를 쓸 수 없습니다.");
        if (room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (hub.IsBusy(id)) return BadRequest("실행 중인 방입니다 — 턴이 끝난 뒤 다시 하세요.");
        try
        {
            // 이어받기는 만들 때 한 번에 저장한다 — 따로 저장하다 실패하면 기본 연결로 된 방이 남는다 (/self-review c11df0ad3c C2)
            var fresh = store.Create(room.Workspace, room.Level, room.Model, null, effort: room.Effort, shape: r => SlashCommands.CarryOver(r, room));
            hub.Touch(fresh.Id);
            return new JsonResult(new { ok = true, id = fresh.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"새 방을 만들지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// /model · /effort (<see cref="SlashCommands"/>): 방 모델 · 추론 강도를 바꾼다 (곁칸 설정과 같은 값 검사 · 다음 턴부터).
    /// 인자가 없거나 틀리면 400으로 지금 값과 고를 수 있는 값을 알린다.
    /// </summary>
    public IActionResult OnPostSlashSetting(string id, string? name, string? value)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (GroupSettingsRefusal(room) is { } refusal) return BadRequest(refusal);
        var models = options.Value.ModelList;
        Func<Room, Room> change;
        switch (name)
        {
            case "model":
                if (string.IsNullOrWhiteSpace(value)) return BadRequest(SlashCommands.ModelHelp(room.Model, models));
                if (SlashCommands.ResolveModel(value, models) is not { } model || !ClaudeArgs.IsSafeName(model))
                    return BadRequest(SlashCommands.ModelHelp(room.Model, models, value.Trim()));
                change = r => r with { Model = model };
                break;
            case "effort":
                if (string.IsNullOrWhiteSpace(value)) return BadRequest(SlashCommands.EffortHelp(room.Effort));
                var (effort, error) = SlashCommands.CleanEffort(value);
                if (error is not null) return BadRequest(SlashCommands.EffortHelp(room.Effort, value.Trim()));
                change = r => r with { Effort = effort };
                break;
            default:
                return BadRequest("알 수 없는 명령입니다.");
        }
        var result = Change(id, change, "방 설정을 바꾸지 못했습니다");
        if (result is JsonResult)
        {
            hub.ForgetCommands(id); // 모델이 바뀌면 다음 실행의 init이 새 목록
            hub.ProfilesChanged([id]);
        }
        return result;
    }

    /// <summary>앱이 처리하는 기본 명령이 보내기 · 끼어들기로 들어오면 거절 글 (화면이 먼저 가로챈다 — 다른 탭 · 옛 화면 대비).</summary>
    private string? SlashRefusal(string id, string? text) => SlashCommands.Classify(text) is not { } slash ? null
        : store.Get(id) is { IsGroup: true } or { GroupId: not null } ? $"단톡방에서는 /{slash.Name}을 쓸 수 없습니다."
        : $"/{slash.Name}은 앱이 처리합니다 — 화면을 새로 고친 뒤 다시 보내세요.";

    /// <summary>Markdown 내보내기 (내려받기).</summary>
    public IActionResult OnGetExport(string id)
    {
        if (!Load(id)) return NotFound();
        var now = DateTimeOffset.Now;
        Response.Headers.CacheControl = "no-store";
        return File(System.Text.Encoding.UTF8.GetBytes(ChatExport.Markdown(Room, Transcript, now, Runs, Answers, Notes)), "text/markdown; charset=utf-8",
            ChatExport.FileName(Title, now));
    }

    private IActionResult Change(string id, Func<Room, Room> change, string failure)
    {
        try
        {
            if (store.Update(id, change) is null) return NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"{failure}: {ex.Message}");
        }
        hub.Touch(id);
        return new JsonResult(new { ok = true });
    }

    public IActionResult OnPostSend(string id, string? text)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (TooManyAttachments(text)) return BadRequest($"첨부는 한 말에 {Attachments.MaxPerMessage}개까지입니다.");
        if (SlashRefusal(id, text) is { } slash) return BadRequest(slash);
        if (store.Get(id) is { IsGroup: true }) return groups.Send(id, text ?? "") is { } groupError ? BadRequest(groupError) : new JsonResult(new { ok = true });
        if (ChildRefusal(id) is { } refusal) return BadRequest(refusal);
        return hub.Send(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 채팅창 '!' 명령 — 사용자가 친 명령이라 승인 카드 없이 방 작업 폴더에서 돌고, 끝나면 결과가 지휘자에게 간다 (<see cref="RoomHub.RunShell"/>).
    /// 일반 방만 (단톡방 · 구성원 방은 '!'도 말로 간다 — 화면이 여기로 보내지 않는다). 모든 권한 단계 (결정 "모든 단계").
    /// </summary>
    public IActionResult OnPostShell(string id, string? command)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.IsGroup || room.GroupId is not null) return BadRequest("'!' 명령은 일반 방에서만 됩니다.");
        if (options.Value.WorkspacePath(room.Workspace) is not { } workspace) return BadRequest("작업 폴더를 찾지 못했습니다.");
        return hub.RunShell(id, command ?? "", workspace) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    public IActionResult OnPostShellStop(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        return hub.StopShell(id) ? new JsonResult(new { ok = true }) : BadRequest("도는 '!' 명령이 없습니다.");
    }

    /// <summary>Codex 구성원 과정 (앱이 남긴 이벤트 기록 — 보낸 말 · 답 · 실행한 명령 · 오류 · 토큰). Claude 방이면 빈 목록.</summary>
    public IReadOnlyList<CodexLog.Step> CodexSteps => Room.Engine == RoomEngines.Codex ? new CodexLog(() => options.Value.DataFolder).Read(Room.Id) : [];

    /// <summary>
    /// 보낸 말 앞의 안내 블록(&lt;system-reminder&gt; … &lt;/system-reminder&gt;, 하나 이상 — 규칙 바뀜 안내 · 단톡방 안내)과 나머지 사용자 말로 나눈다.
    /// 블록 안의 표식은 보낼 때 바꿔 두므로 첫 닫는 표식이 그 블록의 끝이다 (GroupChat.Delta). 안내가 없으면 Guide는 빈 글.
    /// </summary>
    public static (string Guide, string Text) SplitGuide(string text)
    {
        var m = LeadingGuide().Match(text);
        return m.Success && m.Length > 0 ? (m.Value.Trim(), text[m.Length..].TrimStart()) : ("", text);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A(?:\s*<system-reminder>.*?</system-reminder>)+\s*", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex LeadingGuide();

    /// <summary>
    /// 입력칸 + 메뉴의 "외부 에이전트 요청" · "외부 요청 전에 묻기" 스위치를 보일지: 읽기 이상 단계 · 기본 MCP 서버가 켜진 보통 방.
    /// </summary>
    public bool CanModeRequest => Room.DeletedAt is null && !Room.IsGroup && Room.GroupId is null
                                  && Room.Engine is null && !Room.McpOff.Contains(options.Value.McpServer);

    /// <summary>방 머리 · 곁칸의 모델 글: Codex 방(단톡방 구성원 과정 보기)이면 "Codex · 모델"(빈 모델 = 기본 모델 — ② K4), 그 밖은 모델 이름.</summary>
    public string ModelLabel => Room.Engine == RoomEngines.Codex
        ? "Codex · " + (Room.Model.Length > 0 ? Room.Model : "기본 모델")
        : Room.Model;

    public IActionResult OnPostStop(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (store.Get(id) is { IsGroup: true }) groups.Stop(id);
        else hub.Stop(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>지금 끝내기: 답이 끝난 뒤 기다리는 백그라운드 작업(서버 등)을 정리한다.</summary>
    public IActionResult OnPostWrapUp(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.WrapUp(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>설정 바꾸기 전 확인에서 허용함: 쉬는 실행을 작업째 끝낸다 (결정 "확인 뒤 작업 끝내고 적용" — 기록 줄은 "방 설정이 바뀌어").</summary>
    public IActionResult OnPostEndRest(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.EndRest(id, "settings", afterTurn: true); // 응답 중이면 쓰던 답은 끝까지 (af6ffa3da5 C1)
        return new JsonResult(new { ok = true });
    }

    /// <summary>백그라운드 작업 하나 끝내기 (작업 줄의 단추) — 다른 작업 · 프로세스는 그대로.</summary>
    public async Task<IActionResult> OnPostStopTask(string id, string? task)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 80) return BadRequest("작업 ID가 없습니다.");
        return await hub.StopTaskAsync(id, task) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    private bool Load(string id)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return false;
        Room = room;
        Transcript = transcripts.Read(room.SessionId);
        Messages = RoomList.Page(Transcript?.FormatError is null ? Transcript?.Entries ?? [] : [], null, null);
        _runs = null;
        _answers = null;
        _stickers = null;
        if (room.IsGroup) LoadGroup(null);
        return true;
    }

    /// <summary>토큰 수 짧게 (8K · 1.2M). K로 반올림해 1000이 되면 M으로 (/self-review 83b453653b C10 — "1000K"가 나오지 않게).</summary>
    public static string Tokens(long n)
    {
        if (n < 1_000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var k = Math.Round(n / 1_000d);
        return k < 1_000
            ? k.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "K"
            : (n / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>끝난 턴 표정을 앞으로 보일 밀리초 (없으면 0) · 그 표정을 보이는 전체 시간 (화면 타이머, 83b453653b C11).</summary>
    public long EndFaceLeftMs => (long)(hub.EndFaceLeft(Room.Id)?.TotalMilliseconds ?? 0);
    public static long EndFaceMs => (long)RoomHub.EndFaceFor.TotalMilliseconds;

    /// <summary>방 기록 "새 방" 줄의 [열기] 링크를 둘지 — 있고 휴지통에 없는 방만.</summary>
    public bool RoomAlive(string id) => store.Get(id) is { DeletedAt: null };

    public HtmlString Render(ChatEntry e)
    {
        if (e.Speaker != ChatSpeaker.User)
            return new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, SafeMarkdown.Default), Stickers));
        // 사용자 말의 [첨부] 줄은 칩으로 (2-2 d): 이 방 첨부 폴더의 파일만 링크 · 그림 미리보기, 그 밖의 경로는 글자 그대로
        var (body, paths) = Attachments.Split(e.Text);
        var html = new System.Text.StringBuilder(StickerRenderer.Replace(SafeMarkdown.ToHtml(body, SafeMarkdown.KeepLineBreaks), Stickers));
        if (paths.Count == 0) return new(html.ToString());
        var folder = Attachments.Folder(options.Value.DataFolder, Room.Id);
        var enc = System.Text.Encodings.Web.HtmlEncoder.Default;
        html.Append("<div class=\"attach-list\">");
        foreach (var path in paths)
        {
            if (Attachments.NameInFolder(folder, path) is { } name && Attachments.Resolve(folder, name) is not null)
            {
                var url = enc.Encode($"/Room/{Room.Id}?handler=Attachment&name={Uri.EscapeDataString(name)}");
                html.Append("<a class=\"attach\" target=\"_blank\" rel=\"noopener\" href=\"").Append(url).Append("\" title=\"").Append(enc.Encode(path)).Append("\">");
                if (Attachments.IsImage(name)) html.Append("<img src=\"").Append(url).Append("\" alt=\"\" loading=\"lazy\" />");
                html.Append("<span>").Append(enc.Encode(name)).Append("</span></a>");
            }
            else html.Append("<span class=\"attach missing\" title=\"이 방 첨부 폴더에 없는 파일\">").Append(enc.Encode(path)).Append("</span>");
        }
        return new(html.Append("</div>").ToString());
    }

    /// <summary>중지한 턴의 자리표시 답 — 지휘자 말풍선 대신 안내 한 줄로 (<see cref="RoomList.IsStoppedTurn"/>).</summary>
    public static bool IsStoppedTurn(ChatEntry e) => RoomList.IsStoppedTurn(e);

    public static string RoleClass(ChatSpeaker s) => s switch
    {
        ChatSpeaker.User => "user",
        ChatSpeaker.Conductor => "conductor",
        ChatSpeaker.Subagent => "subagent",
        _ => "system",
    };
}
