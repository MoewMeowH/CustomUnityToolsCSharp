using HB.Orchestration.ChatUi;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방 하나. 말풍선은 세션 기록에서 읽는다 (앱은 말 본문을 따로 저장하지 않는다). 진행 중 턴은 /events/{id} SSE로 흘린다.
/// ?handler=Messages: 말풍선 한 쪽 — 맨 끝 50개, before=줄이면 그 앞 50개(위로 불러오기), after=줄이면 그 뒤 전부(턴이 끝난 뒤 새 말, 계획서 2-2 b).
/// ?handler=Pane: 방 전환 때 오른쪽 칸만 (2-2 a). Send · Stop · MarkRead는 POST (antiforgery + 같은 출처).
/// </summary>
public sealed class RoomModel(
    RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, StickerPacks packs, FaceCatalog faces, RoomRuns runs,
    IOptions<ChatOptions> options)
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

    /// <summary>데스크톱 미니 창 배치 (계획서 2-3 c, `?view=mini`). 서버 동작은 같고 CSS만 다르다.</summary>
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

    /// <summary>이 방에서 부른 run_mode 실행 말풍선의 자리 (계획서 2-2 e). 그릴 때 처음 한 번 계산한다 (Fork 등은 안 읽음 — a9e6066519 C3).</summary>
    public RunPlacement Runs => _runs ??= runs.For(Transcript);
    private RunPlacement? _runs;

    /// <summary>질문 카드 답 말풍선의 자리 (4차 D1, 세션 기록에서).</summary>
    public AnswerPlacement Answers => _answers ??= AnswerPlacement.Of(Transcript);
    private AnswerPlacement? _answers;

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
    public SidebarView Sidebar => new(list.All(), Room.Id) { TrashCount = store.All().Count(r => r.DeletedAt is not null) };
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

    /// <summary>곁칸 "첨부": 그 방 첨부 파일 (최근 것부터).</summary>
    public IReadOnlyList<(string Name, long Bytes, DateTimeOffset At)> AttachmentFiles =>
        Attachments.List(Attachments.Folder(options.Value.DataFolder, Room.Id));

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
        return new JsonResult(new { chat = room.Level == PermissionLevel.Chat, commands = CommandCatalog.Build(hub.Commands(id), workspace, user) });
    }

    /// <summary>명령 방: 작업 폴더 · 사용자 설정이 이미 미리 허용한 명령 (카드 없이 실행됨).</summary>
    public IReadOnlyList<(string Source, string Rule)> InheritedRules =>
        Room.Level == PermissionLevel.Command ? hub.InheritedCommandRules(Room) : [];

    public IActionResult OnGet(string id)
    {
        if (!Load(id)) return NotFound();
        return Page();
    }

    public IActionResult OnGetPane(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        return Partial("_RoomPane", this);
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
        // 읽기 실패는 실패로 돌려준다: 화면이 앞 말풍선을 그대로 둔다 (대시보드 채팅방과 같은 처리).
        if (Transcript?.FormatError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        Messages = RoomList.Page(Transcript?.Entries ?? [], before, after);
        Fragment = (before is not null || after is not null) && !Messages.Reset;
        AfterFragment = Fragment && after is not null;
        return Partial("_Messages", this);
    }

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
        result = new JsonResult(new { ok = true });
        if (result is JsonResult) hub.ProfilesChanged([id]);
        return result;
    }

    /// <summary>
    /// "계획 먼저" 켜기 · 끄기 (Claude 앱 기능 C — 입력칸 토글): 다음 실행을 계획 모드로. 편집 · 명령 단계만 켤 수 있다. 계획을 승인하면 서버가 끈다.
    /// </summary>
    public IActionResult OnPostPlanNext(string id, bool on)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (on && ClaudeArgs.PlanMode(room.Level) is null) return BadRequest("계획 모드는 편집 · 명령 방에서만 쓸 수 있습니다.");
        return Change(id, r => r with { PlanNext = on }, "계획 먼저를 바꾸지 못했습니다");
    }

    /// <summary>휴지통으로 (30일 뒤 완전히 지움). 실행 중인 방은 먼저 중지해야 한다 — 기다리는 승인 · 큐가 버려지지 않게.</summary>
    public IActionResult OnPostTrash(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (hub.IsBusy(id)) return BadRequest("실행 중인 방입니다 — 중지한 뒤 휴지통에 넣으세요.");
        hub.ForgetCommands(id);
        return Change(id, r => r with { DeletedAt = DateTimeOffset.Now }, "휴지통에 넣지 못했습니다");
    }

    public IActionResult OnPostRestore(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        return Change(id, r => r with { DeletedAt = null }, "되살리지 못했습니다");
    }

    /// <summary>
    /// 방 복제(갈래, 계획서 2-2 c): 지금까지 대화를 이어받는 새 방. 같은 작업 폴더 · 권한 · 모델, "항상 허용" 규칙은 옮기지 않는다.
    /// 첫 말을 보낼 때 CLI가 대화를 복사한다 (--fork-session). 세션 기록이 없거나 실행 중이면 거절.
    /// </summary>
    public IActionResult OnPostFork(string id)
    {
        if (!Load(id)) return NotFound();
        if (Room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (hub.IsBusy(id)) return BadRequest("실행 중인 방은 복제하지 않습니다 — 턴이 끝난 뒤 다시 하세요.");
        if (transcripts.Size(Room.SessionId) is null) return BadRequest("아직 대화가 없어 복제할 것이 없습니다.");
        var (title, _) = RoomList.CleanTitle(RoomList.Cut(Title, 190) + " (갈래)");
        try
        {
            var fork = store.Create(Room.Workspace, Room.Level, Room.Model, title, forkOf: Room.SessionId,
                forkSourceSize: transcripts.Size(Room.SessionId));
            // 이 방에 고른 스티커 팩 · 추론 강도도 따라간다 (4차 D2 — ef2c83416c C6). "항상 허용" 규칙은 그대로 옮기지 않는다
            if (Room.StickerPack is not null || Room.Effort is not null)
                store.Update(fork.Id, r => r with { StickerPack = Room.StickerPack, Effort = Room.Effort });
            hub.Touch(fork.Id);
            return new JsonResult(new { ok = true, id = fork.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"복제하지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>Markdown 내보내기 (내려받기).</summary>
    public IActionResult OnGetExport(string id)
    {
        if (!Load(id)) return NotFound();
        var now = DateTimeOffset.Now;
        Response.Headers.CacheControl = "no-store";
        return File(System.Text.Encoding.UTF8.GetBytes(ChatExport.Markdown(Room, Transcript, now, Runs, Answers)), "text/markdown; charset=utf-8",
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
        return hub.Send(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    public IActionResult OnPostStop(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.Stop(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>지금 끝내기: 답이 끝난 뒤 기다리는 백그라운드 작업(서버 등)을 정리한다 (계획서 2026-10-29_HB_Chat-백그라운드-명령).</summary>
    public IActionResult OnPostWrapUp(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.WrapUp(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>승인 카드 단추: choice = allow · always · deny, reason은 거절 이유 (선택).</summary>
    public IActionResult OnPostApprove(string id, string? approval, string? choice, string? reason)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(approval)) return NotFound();
        ApprovalChoice? parsed = choice switch
        {
            "allow" => ApprovalChoice.Allow,
            "always" => ApprovalChoice.AlwaysAllow,
            "deny" => ApprovalChoice.Deny,
            _ => null,
        };
        if (parsed is null) return BadRequest("알 수 없는 선택입니다.");
        if (reason is { Length: > 500 }) reason = reason[..500];
        return hub.Decide(id, approval, parsed.Value, reason) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>기다리는 말 하나를 지금 보내기 (3차 b): 큐 맨 앞으로 옮기고 지금 턴을 끊는다.</summary>
    public IActionResult OnPostSendNow(string id, string? queued)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(queued)) return NotFound();
        return hub.SendNow(id, queued) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>질문 카드의 답 (3차 a). answers는 JSON 객체(질문 글 → 답). skip이면 답하지 않기.</summary>
    public IActionResult OnPostAnswer(string id, string? question, string? answers, bool skip)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(question)) return NotFound();
        Dictionary<string, string>? parsed = null;
        if (!skip)
        {
            if (answers is null || answers.Length > 64 * 1024) return BadRequest("답을 읽지 못했습니다.");
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(answers);
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest("답을 읽지 못했습니다.");
            }
            if (parsed is null) return BadRequest("답을 읽지 못했습니다.");
        }
        return hub.Answer(id, question, parsed) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>"이 방에서 항상 허용" 규칙 지우기.</summary>
    public IActionResult OnPostRemoveRule(string id, string? rule)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(rule)) return NotFound();
        return hub.RemoveRule(id, rule) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>끼어들기 (계획서 2-2 d): 지금 턴을 멈추고 이 말을 바로 보낸다. 큐의 다른 말은 남는다.</summary>
    public IActionResult OnPostInterrupt(string id, string? text)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (TooManyAttachments(text)) return BadRequest($"첨부는 한 말에 {Attachments.MaxPerMessage}개까지입니다.");
        return hub.Interrupt(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>기다리는 말 하나 취소 (계획서 2-2 d).</summary>
    public IActionResult OnPostCancelQueued(string id, string? queued)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(queued)) return NotFound();
        return hub.CancelQueued(id, queued) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 파일 첨부 (계획서 2-2 d): 하나씩 받아 그 방 첨부 폴더에 저장하고 절대 경로를 돌려준다 — 화면이 보낼 때 [첨부] 줄로 붙인다.
    /// 대화 단계 방 · 휴지통 방은 받지 않는다. 20MB 넘으면 거절.
    /// </summary>
    public async Task<IActionResult> OnPostAttach(string id, IFormFile? file)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.Level == PermissionLevel.Chat) return BadRequest("대화 단계 방은 파일을 열 도구가 없어 첨부를 받지 않습니다.");
        if (room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (Request.ContentLength is > Attachments.MaxBytes + 64 * 1024 || file is { Length: > Attachments.MaxBytes })
            return StatusCode(StatusCodes.Status413PayloadTooLarge, $"파일 하나는 {Attachments.MaxBytes / 1024 / 1024}MB까지입니다.");
        if (file is not { Length: > 0 }) return BadRequest("빈 파일입니다.");
        try
        {
            await using var stream = file.OpenReadStream();
            var path = await Attachments.SaveAsync(Attachments.Folder(options.Value.DataFolder, id), file.FileName, stream, DateTimeOffset.Now,
                HttpContext.RequestAborted);
            var name = Path.GetFileName(path);
            return new JsonResult(new
            {
                ok = true, name, path, image = Attachments.IsImage(name),
                url = $"/Room/{id}?handler=Attachment&name={Uri.EscapeDataString(name)}",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BadRequest($"첨부를 저장하지 못했습니다: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult(); // 화면이 요청을 끊었다 — 임시 파일은 SaveAsync가 지웠다 (5b26cff7df C4)
        }
    }

    /// <summary>
    /// 첨부 보기: 그 방 폴더 안의 파일만. 그림(png · jpg · gif · webp)은 화면에, 나머지는 내려받기. nosniff.
    /// </summary>
    public IActionResult OnGetAttachment(string id, string? name)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is null) return NotFound();
        if (Attachments.Resolve(Attachments.Folder(options.Value.DataFolder, id), name) is not { } path) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin"; // 다른 사이트의 <img>로 있는지 떠보지 못하게 (0c3b7e3514 C4)
        Response.Headers.CacheControl = "private, max-age=3600";
        return Attachments.Images.TryGetValue(Path.GetExtension(path), out var type)
            ? PhysicalFile(path, type)
            : PhysicalFile(path, "application/octet-stream", Path.GetFileName(path));
    }

    private static bool TooManyAttachments(string? text) => text is not null && Attachments.Split(text).Paths.Count > Attachments.MaxPerMessage;

    private bool Load(string id)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return false;
        Room = room;
        Transcript = transcripts.Read(room.SessionId);
        Messages = RoomList.Page(Transcript?.FormatError is null ? Transcript?.Entries ?? [] : [], null, null);
        _runs = null;
        _answers = null;
        _stickers = null;
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
        _ => "system",
    };
}
