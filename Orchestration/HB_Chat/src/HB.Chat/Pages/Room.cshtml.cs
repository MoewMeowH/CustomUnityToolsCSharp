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
    // ---- 단톡방: 같은 방 주소, 칸 · 말풍선 · 보내기만 다르다 ----

    /// <summary>단톡방 대화 줄 (after가 있으면 그 줄 뒤만 — 새 말 받기).</summary>
    public IReadOnlyList<GroupEntry> GroupEntries { get; private set; } = [];

    /// <summary>새 말 받기 조각인지 (빈 단톡방 안내를 그리지 않음).</summary>
    public bool GroupFragment { get; private set; }

    /// <summary>대화 줄 마지막 줄 번호 (없으면 -1) — 화면의 data-last-line.</summary>
    public int GroupLastLine { get; private set; } = -1;

    public GroupMember? MemberOf(string? memberId) => Room.Members.FirstOrDefault(m => m.Id == memberId);

    /// <summary>구성원 상태 한 줄 (생각 중 · 답을 기다림 · 차례 기다림 · null).</summary>
    public string? MemberState(GroupMember m) => groups.MemberState(m.RoomId);

    /// <summary>구성원 단계 표시: 쓰는 구성원(명령)만 — 그 구성원이 낀 말은 차례로 돈다 (⑤).</summary>
    public string? WriterLabel(GroupMember m) => store.Get(m.RoomId) is { } child && GroupChat.IsWriter(child) ? RoomRules.LevelName(child.Level) : null;

    /// <summary>
    /// 그 구성원이 그 사용자 말에 답했는지 (답 줄 · 보내지 못함 안내 줄) — 아니고 실행 중도 아니면 "답이 없음"으로 보인다.
    /// 구성원마다 본다 — 여러 명을 부른 말(③)에서 한 명만 답해도 나머지의 끊김이 가려지지 않게.
    /// </summary>
    public bool Answered(GroupEntry user, string memberId) => _answered.Contains((user.Line, memberId));

    private HashSet<(int UserLine, string MemberId)> _answered = [];

    /// <summary>중지 단추: 도는 구성원 · 단톡방이 답을 기다리는 구성원(차례 기다림 포함)이 있으면.</summary>
    public bool AnyMemberBusy(GroupMember m) => hub.IsBusy(m.RoomId) || groups.Waiting(m.RoomId);

    /// <summary>그 구성원이 답하는 중인지 (실행 중이거나 단톡방이 답을 기다림).</summary>
    public bool Waiting(GroupMember m) => _waiting?.Contains(m.Id) ?? (hub.IsBusy(m.RoomId) || groups.Waiting(m.RoomId));

    /// <summary>대화 줄을 읽기 전에 찍어 둔 기다림 상태 (LoadGroup).</summary>
    private HashSet<string>? _waiting;

    private void LoadGroup(int? after)
    {
        // 기다림 상태를 대화 줄보다 먼저 찍는다 — 줄을 읽은 뒤 답이 올라오고 기다림이 풀리면 "답 없음 + 쉼"으로 보여 정상 답에도 "답이 없습니다"가
        // 그려졌고, 화면은 새 줄만 덧붙여 그 안내가 남았다 (③ 리뷰 6b7921ae50 1-F1 · Codex). 먼저 찍으면: 그때 쉬던 구성원의 답은 이미 줄에 있다
        // (답 줄을 쓴 뒤에 기다림을 지움 — Posting), 기다리던 구성원은 이번에 안내를 그리지 않는다.
        _waiting = [.. Room.Members.Where(m => hub.IsBusy(m.RoomId) || groups.Waiting(m.RoomId)).Select(m => m.Id)];
        var all = groups.Log.Read(Room.Id);
        _answered = GroupChat.AnsweredBy(all);
        GroupLastLine = all.Count > 0 ? all[^1].Line : -1;
        GroupEntries = after is { } a ? [.. all.Where(e => e.Line > a)] : all;
        GroupFragment = after is not null;
    }

    /// <summary>단톡방 구성원 말풍선 본문 (지휘자 말과 같은 규칙) · 사용자 말(줄바꿈 유지).</summary>
    public HtmlString RenderGroup(GroupEntry e) => e.Kind == GroupEntryKind.User
        ? new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, SafeMarkdown.KeepLineBreaks), Stickers))
        : new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, SafeMarkdown.Default), Stickers));

    // ---- 연결 탭 (Claude 앱 커넥터처럼) ----

    /// <summary>
    /// 연결 한 줄. Kind: base(기본 MCP) · app(앱 MCP hb-chat) · extra(작업 폴더 .mcp.json의 다른 서버) · unity. On: 이 방에서 켬.
    /// Applies: 지금 단계에서 실제로 붙는지 (끈 것과 따로 — 켜 둬도 단계가 낮으면 안 붙음). Init: 마지막 실행 init의 상태 (connected · failed …).
    /// </summary>
    public sealed record Connection(string Name, string Kind, string Transport, string? Command, bool On, bool Applies, string? Note,
        string? Init, ProbeResult? Probe, bool Probing, IReadOnlyList<(string Name, string? Description)>? StaticTools = null)
    {
        /// <summary>작업 폴더 .mcp.json과 앱 서버 목록에 같은 이름이 있음 — 작업 폴더 것을 쓴다 (줄에 표시).</summary>
        public bool Shadowed { get; init; }

        /// <summary>http 앱 서버 — OAuth 로그인 서버면 터미널 claude의 /mcp에서 한 번 로그인해야 붙는다 (안내).</summary>
        public bool NeedsLogin { get; init; }

        /// <summary>마지막 실행 init의 이 서버 도구 이름 (설명 없음) — 확인이 도구를 못 받았을 때(OAuth 서버 등) 대신 보인다.</summary>
        public IReadOnlyList<(string Name, string? Description)> InitTools { get; init; } = [];

        /// <summary>보일 도구 목록: 앱 도구 → 확인 결과 → 실행 init 이름 순.</summary>
        public IReadOnlyList<(string Name, string? Description)> ShownTools =>
            StaticTools ?? (Probe is { Ok: true } p ? p.Tools : InitTools);
    }

    public McpProbe Probe => probe;

    /// <summary>등록된 Unity 프로젝트 (연결 탭 select).</summary>
    public IReadOnlyList<WorkspaceEntry> UnityProjects => options.Value.AllUnityProjects;

    public IReadOnlyList<Connection> Connections
    {
        get
        {
            var o = options.Value;
            var room = Room;
            var ws = WorkspacePath;
            // 마지막 실행 init의 상태 · 도구 이름 — 방에 저장돼 앱을 다시 켜도 남는다 (RoomHub.KeepMcpStatus)
            string? Init(string name) => room.McpStatus.FirstOrDefault(s => s.Name == name)?.Status;
            IReadOnlyList<(string, string?)> InitTools(string name) =>
            [
                .. room.McpTools.Where(t => t.StartsWith($"mcp__{name}__", StringComparison.Ordinal))
                    .Select(t => (t[$"mcp__{name}__".Length..], (string?)null)).Order(),
            ];
            string Transport(string name) => ws is not null && McpConfigFile.Entry(ws, name)?["url"] is not null ? "http" : "stdio";
            var rows = new List<Connection>
            {
                new(o.McpServer, "base", ws is null ? "?" : Transport(o.McpServer), ws is null ? null : WorkspaceStore.McpCommand(ws, o.McpServer),
                    !room.McpOff.Contains(o.McpServer), true, null,
                    Init(o.McpServer), probe.Last(room.Id, o.McpServer), probe.IsRunning(room.Id, o.McpServer)) { InitTools = InitTools(o.McpServer) },
                new(TerminalMcp.Server, "app", "http", "이 앱 (터미널 · 지휘자 브라우저 도구)", !room.McpOff.Contains(TerminalMcp.Server),
                    ClaudeArgs.RunsCommands(room.Level), ClaudeArgs.RunsCommands(room.Level) ? null : "명령 방에서만 붙습니다",
                    Init(TerminalMcp.Server), null, false, McpProbe.AppTools()),
            };
            var names = ws is null ? [] : McpConfigFile.ServerNames(ws);
            // 앱에서 더한 서버 — 같은 이름이 작업 폴더에 있으면 그쪽을 쓴다
            var appServers = o.AppMcp?.All() ?? [];
            foreach (var name in names.Concat(appServers.Select(a => a.Name)).Concat(room.McpExtra).Distinct(StringComparer.Ordinal)
                         .Where(n => n != o.McpServer && n != TerminalMcp.Server && n != McpConfigFile.UnityServer))
            {
                var inWorkspace = names.Contains(name);
                var app = appServers.FirstOrDefault(a => a.Name == name).Entry;
                var exists = inWorkspace || app is not null;
                var note = !exists ? "작업 폴더 .mcp.json에도 앱 서버 목록에도 없어졌습니다 — 끄면 목록에서 빠집니다" : null;
                rows.Add(new(name, inWorkspace || app is null ? "extra" : "appserver",
                    inWorkspace ? Transport(name) : app is not null ? AppMcpStore.TransportOf(app) : "?",
                    inWorkspace ? WorkspaceStore.McpCommand(ws!, name) : app is not null ? AppMcpStore.Describe(app) : null,
                    room.McpExtra.Contains(name), exists, note,
                    Init(name), probe.Last(room.Id, name), probe.IsRunning(room.Id, name))
                {
                    InitTools = InitTools(name),
                    Shadowed = inWorkspace && app is not null, // 앱 서버에도 같은 이름 — 작업 폴더 것을 씀
                    NeedsLogin = !inWorkspace && app is not null && app["url"] is not null, // http 앱 서버 — OAuth면 터미널 claude의 /mcp에서 한 번 로그인
                });
            }
            if (room.UnityProject is { } unity)
            {
                var path = o.UnityProjectPath(unity);
                var cli = o.ResolveUnityCli();
                rows.Add(new(McpConfigFile.UnityServer, "unity", "stdio", cli is null || path is null ? null : $"unity mcp --project-path {path}",
                    true, ClaudeArgs.RunsCommands(room.Level) && path is not null && cli is not null,
                    path is null ? $"'{unity}'가 설정에 없습니다" : cli is null ? "Unity CLI(unity.exe)를 찾지 못했습니다 — 설정 Chat:UnityCliPath"
                    : ClaudeArgs.RunsCommands(room.Level) ? null : "명령 방에서만 붙습니다",
                    Init(McpConfigFile.UnityServer), probe.Last(room.Id, McpConfigFile.UnityServer), probe.IsRunning(room.Id, McpConfigFile.UnityServer))
                {
                    InitTools = InitTools(McpConfigFile.UnityServer),
                });
            }
            return rows;
        }
    }

    /// <summary>연결 켜기 · 끄기: 기본 · 앱은 McpOff, 작업 폴더의 다른 서버는 McpExtra. 다음 실행부터.</summary>
    public IActionResult OnPostMcpToggle(string id, string? name, bool on)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (GroupSettingsRefusal(room) is { } refusal) return BadRequest(refusal);
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(name) || !ClaudeArgs.IsServerName(name) || name == McpConfigFile.UnityServer) return BadRequest("알 수 없는 연결입니다.");
        if (name == o.McpServer || name == TerminalMcp.Server)
            return Change(id, r => r with { McpOff = on ? [.. r.McpOff.Where(n => n != name)] : [.. r.McpOff.Where(n => n != name), name] }, "연결을 바꾸지 못했습니다");
        // 켤 때는 작업 폴더 .mcp.json이나 앱 서버 목록에 있는 이름만 (끄기는 없어진 이름도 받는다).
        // 앱 서버면 저장된 정식 이름으로 — 대소문자만 다른 이름이 따로 남지 않게 (/self-review 779716239c C4)
        var inWorkspace = o.WorkspacePath(room.Workspace) is { } ws && McpConfigFile.ServerNames(ws).Contains(name);
        if (on && !inWorkspace)
        {
            if (o.AppMcp?.NameOf(name) is not { } canonical) return BadRequest("작업 폴더 .mcp.json에도 앱 서버 목록에도 없는 서버입니다.");
            name = canonical;
        }
        return Change(id, r => r with { McpExtra = on ? [.. r.McpExtra.Where(n => n != name), name] : [.. r.McpExtra.Where(n => n != name)] }, "연결을 바꾸지 못했습니다");
    }

    /// <summary>Unity 프로젝트 연결 (명령 방만 — 결정). 빈 값이면 끊는다. 다음 실행부터.</summary>
    public IActionResult OnPostUnity(string id, string? project)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (GroupSettingsRefusal(room) is { } refusal) return BadRequest(refusal);
        var p = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        if (p is not null)
        {
            if (!ClaudeArgs.RunsCommands(room.Level)) return BadRequest("Unity 프로젝트는 명령 방에서만 연결합니다.");
            if (options.Value.UnityProjectPath(p) is null) return BadRequest("설정에 없는 Unity 프로젝트입니다.");
            p = options.Value.AllUnityProjects.First(u => string.Equals(u.Name, p, StringComparison.OrdinalIgnoreCase)).Name;
        }
        return Change(id, r => r with { UnityProject = p }, "Unity 프로젝트를 바꾸지 못했습니다");
    }

    /// <summary>"확인": 앱이 그 서버를 띄워 도구 목록만 받고 닫는다 (<see cref="McpProbe"/>). 결과는 곁칸을 다시 받으면 보인다.</summary>
    public async Task<IActionResult> OnPostMcpProbe(string id, string? name)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(name) || !ClaudeArgs.IsServerName(name) || name == TerminalMcp.Server) return BadRequest("확인할 수 없는 연결입니다.");
        if (o.WorkspacePath(room.Workspace) is not { } ws || !Directory.Exists(ws)) return BadRequest("작업 폴더가 없습니다.");
        System.Text.Json.Nodes.JsonObject? entry;
        if (name == McpConfigFile.UnityServer)
        {
            if (room.UnityProject is not { } unity || o.UnityProjectPath(unity) is not { } path) return BadRequest("연결한 Unity 프로젝트가 없습니다.");
            if (!o.UnityProjects.ContainsKey(unity) && o.AppUnityProjects?.WhyUnusable(path) is { } why) return BadRequest($"지금은 쓸 수 없는 프로젝트입니다: {why}");
            if (o.ResolveUnityCli() is not { } cli) return BadRequest("Unity CLI(unity.exe)를 찾지 못했습니다.");
            entry = McpConfigFile.UnityEntry(cli, path);
        }
        else entry = McpConfigFile.Entry(ws, name) ?? o.AppMcp?.Get(name); // 작업 폴더 것 먼저
        if (entry is null) return BadRequest("작업 폴더 .mcp.json에도 앱 서버 목록에도 없는 서버입니다.");
        var result = await probe.ProbeAsync(id, name, entry, ws, HttpContext.RequestAborted);
        if (result is null) return BadRequest("이미 확인 중입니다.");
        return new JsonResult(new { ok = result.Ok, tools = result.Tools.Count, error = result.Error });
    }

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
        if (expectedLevel == PermissionLevel.App && lv != PermissionLevel.App) hub.Stop(id);
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
        hub.ForgetCommands(id);
        terminals.StopRoom(id, "방 삭제"); // 그 방 터미널도 끝낸다
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
        if (store.Get(id) is { IsGroup: true }) return groups.Send(id, text ?? "") is { } groupError ? BadRequest(groupError) : new JsonResult(new { ok = true });
        if (ChildRefusal(id) is { } refusal) return BadRequest(refusal);
        return hub.Send(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 단톡방 · 구성원 자식 방의 단계 · 모델 · 연결은 바꾸지 않는다 — 만들 때 정한 단계로만 돈다(결정 ①). 과정 보기 화면의 곁칸에서
    /// 자식 방을 명령 단계로 올릴 수 있었다 (b8697252eb C1 · Codex 리뷰).
    /// </summary>
    private static string? GroupSettingsRefusal(Room room) =>
        room.GroupId is not null ? "단톡방 구성원 방의 단계 · 모델 · 연결은 바꿀 수 없습니다 (지금은 만들 때 정한 값으로만 돕니다)."
        : room.IsGroup ? "단톡방의 단계 · 모델 · 연결은 바꿀 수 없습니다 (지금은 만들 때 정한 값으로만 돕니다)."
        : null;

    /// <summary>단톡방 화면이 다시 붙을 때 받는 구성원 카드 (승인 · 질문, ⑤): approval · question 알림 모양, detail = 구성원 ID.</summary>
    public IActionResult OnGetGroupCards(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { IsGroup: true } group) return NotFound();
        return new JsonResult(groups.Cards(group));
    }

    /// <summary>
    /// 단톡방 구성원 상태 (화면이 다시 붙을 때 · 대화 줄을 받을 때 칩을 맞춘다 — b8697252eb C4). 구성원이 쓰는 중인 글은 여기서 주지 않는다 —
    /// SSE를 붙일 때 같은 채널로 온다 (GroupChat.OnSubscribed, 7e68bf5791 C1 · C3).
    /// </summary>
    public IActionResult OnGetGroupState(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { IsGroup: true } group) return NotFound();
        return new JsonResult(groups.States(group).Select(s => new { member = s.MemberId, state = s.State }));
    }

    /// <summary>
    /// 단톡방 머리의 구성원 비용 합계 (결정 "구성원 누적 합계"):
    /// Claude 구성원은 claude가 알려 준 세션 누적 비용(클라이언트 추정)의 합, Codex 구성원은 턴마다 더해 둔 토큰(Room.CodexInputTokens · CodexOutputTokens —
    /// b40d9efb00 C3)의 합. 아무것도 없으면 null.
    /// </summary>
    public string? GroupCostLine(Room? group = null)
    {
        group ??= Room;
        double usd = 0;
        long input = 0, output = 0;
        bool anyUsd = false;
        foreach (var m in group.Members)
        {
            if (store.Get(m.RoomId) is not { } child) continue;
            if (child.Engine == RoomEngines.Codex)
                (input, output) = (input + child.CodexInputTokens, output + child.CodexOutputTokens);
            else if (child.TotalCostUsd is { } cost)
            {
                usd += cost;
                anyUsd = true;
            }
        }
        var parts = new List<string>();
        if (anyUsd) parts.Add($"Claude 구성원 ≈${usd:0.00} (추정)");
        if (input + output > 0) parts.Add($"Codex 토큰 입력 {input:N0}(캐시 포함) · 출력 {output:N0}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>단톡방 비용 합계 줄 (화면이 대화 줄을 받을 때 다시 받는다). 없으면 빈 글.</summary>
    public IActionResult OnGetGroupCost(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { IsGroup: true } group) return NotFound();
        return Content(GroupCostLine(group) ?? "", "text/plain; charset=utf-8");
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

    /// <summary>단톡방 칩의 엔진 표시: Codex면 "Codex · 모델"(빈 모델 = 기본), Claude면 null.</summary>
    public string? EngineLabel(GroupMember m) =>
        store.Get(m.RoomId) is { Engine: RoomEngines.Codex } child ? "Codex" + (child.Model.Length > 0 ? " · " + child.Model : "") + (child.Effort is { } e ? " · " + e : "") : null;

    /// <summary>단톡방 칩의 작업 폴더: 단톡방 작업 폴더와 다르면 그 이름, 같으면 null.</summary>
    public string? MemberWorkspace(GroupMember m) =>
        store.Get(m.RoomId) is { } child && !string.Equals(child.Workspace, Room.Workspace, StringComparison.OrdinalIgnoreCase) ? child.Workspace : null;

    /// <summary>작업 폴더 경로 (칩 title). 허용 목록에서 빠졌으면 안내 글.</summary>
    public string WorkspaceTitle(string workspace) => options.Value.WorkspacePath(workspace) ?? "(허용 목록에 없음)";

    /// <summary>
    /// 단톡방 머리의 작업 폴더: 구성원이 모두 같은 폴더면 그 이름(단톡방 폴더가 아니어도 — 칩 배지와 맞게), 섞였으면 "작업 폴더 N곳".
    /// 자식 방이 없으면 단톡방 폴더.
    /// </summary>
    public string GroupWorkspaceLabel => GroupChat.Workspaces(store, Room) switch
    {
        { Count: > 1 } all => $"작업 폴더 {all.Count}곳",
        [var one] => one,
        _ => Room.Workspace,
    };

    /// <summary>과정 보기(자식 방)의 단톡방 — 화면 안내 줄에 이름을 보인다.</summary>
    public Room? ParentGroup => Room.GroupId is { } g ? store.Get(g) : null;

    /// <summary>단톡방 구성원 자식 방에는 직접 보내지 않는다 — 답이 단톡방 대화 줄과 어긋나지 않게 (과정 보기에서 승인 · 질문 카드만 답함).</summary>
    private string? ChildRefusal(string id) =>
        store.Get(id) is { GroupId: not null } ? "단톡방 구성원 방입니다 — 단톡방에서 @이름으로 부르세요." : null;

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

    // ---- 터미널 곁칸 ----

    // ---- 지휘자 브라우저 ----

    /// <summary>방 기록의 스크린샷 그림: 그 방 폴더 · 이름 모양이 맞을 때만, nosniff · 같은 출처.</summary>
    public IActionResult OnGetShot(string id, string? name)
    {
        if (RoomShots.PathOf(options.Value.DataFolder, id, name) is not { } path) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        Response.Headers.CacheControl = "private, max-age=86400"; // 이름이 시각이라 바뀌지 않는다
        return PhysicalFile(path, RoomShots.ContentType(path)); // 브라우저 png · Unity png/jpg
    }

    /// <summary>곁칸 브라우저 탭을 열 때: 마지막 실시간 화면(jpeg). 없으면 204.</summary>
    public IActionResult OnGetBrowserFrame(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff"; // 스크린샷 그림과 같게 (/self-review security 8b11177bd5 C7)
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return browser.LastFrame(id) is { } jpeg ? File(jpeg, "image/jpeg") : new NoContentResult();
    }

    /// <summary>터미널 탭을 열 때: 출력 버퍼 전체(base64)와 끝 오프셋 — 화면은 그보다 앞선 실시간 조각을 버린다.</summary>
    public IActionResult OnGetTerminal(string id, string? term)
    {
        if (!RoomStore.IsRoomId(id) || terminals.Get(id, term) is not { } s) return NotFound();
        var (data, end) = s.Snapshot();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { data = Convert.ToBase64String(data), end, view = s.View });
    }

    /// <summary>내 셸 열기 (결정 3: "+"는 PowerShell, ▾로 PowerShell · 명령 프롬프트). 사용자 본인 동작이라 카드 없음, 방 작업 폴더에서.</summary>
    public IActionResult OnPostTerminalOpen(string id, string? shell)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (options.Value.WorkspacePath(room.Workspace) is not { } workspace) return BadRequest("작업 폴더를 찾지 못했습니다.");
        return terminals.OpenShell(id, shell ?? "powershell", workspace, out var s) is { } why
            ? BadRequest(why)
            : new JsonResult(new { ok = true, id = s!.Id });
    }

    /// <summary>곁칸에서 친 키 — 내 셸만 (사용자 본인 입력, 결정 "입력도 (ConPTY)"). b64 = xterm onData 글의 UTF-8 바이트(base64), 한 번에 4KB까지 — 화면이 나눠 보낸다.</summary>
    public IActionResult OnPostTerminalInput(string id, string? term, string? b64)
    {
        // base64로 받는다 — 폼 바인딩이 공백만 있는 글(Enter "\r" · 스페이스 · 탭 한 번)을 null로 바꿔 키가 사라졌다 (5291 실제 확인)
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term) || string.IsNullOrEmpty(b64)) return NotFound();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return BadRequest("입력 모양이 올바르지 않습니다."); }
        if (bytes.Length is 0 or > 4096) return BadRequest("한 번에 보내는 입력이 너무 깁니다.");
        return terminals.Write(id, term, bytes) ? new JsonResult(new { ok = true }) : BadRequest("끝난 터미널입니다.");
    }

    public IActionResult OnPostTerminalResize(string id, string? term, int cols, int rows)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term)) return NotFound();
        terminals.Resize(id, term, cols, rows); // 끝났거나 범위 밖이면 무시
        return new JsonResult(new { ok = true });
    }

    public IActionResult OnPostTerminalStop(string id, string? term)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term)) return NotFound();
        return terminals.Stop(id, term, "사용자") is { } why ? BadRequest(why) : new JsonResult(new { ok = true });
    }

    /// <summary>끝난 터미널을 탭에서 닫는다 (목록에서 뺌).</summary>
    public IActionResult OnPostTerminalClose(string id, string? term)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term)) return NotFound();
        return terminals.Remove(id, term) ? new JsonResult(new { ok = true }) : BadRequest("돌고 있는 터미널은 먼저 중지하세요.");
    }

    /// <summary>"이 턴 동안 묻지 않기" 끄기.</summary>
    public IActionResult OnPostTrustOff(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.EndTurnTrust(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>승인 카드 단추: choice = allow · turn · deny, reason은 거절 이유 (선택), picks는 모델 선택 카드에서 고른 값 (JSON, 선택).</summary>
    public IActionResult OnPostApprove(string id, string? approval, string? choice, string? reason, string? picks)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(approval)) return NotFound();
        if (ModeCard.ParsePicks(picks) is not { } chosen) return BadRequest("고른 값을 읽지 못했습니다.");
        ApprovalChoice? parsed = choice switch
        {
            "allow" => ApprovalChoice.Allow,
            "turn" => ApprovalChoice.AllowTurn,
            "deny" => ApprovalChoice.Deny,
            _ => null,
        };
        if (parsed is null) return BadRequest("알 수 없는 선택입니다.");
        if (reason is { Length: > 500 }) reason = reason[..500];
        return hub.Decide(id, approval, parsed.Value, reason, chosen.Count == 0 ? null : chosen) is { } error ? CardError(error) : new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 카드 답 실패: 이미 끝난 카드면 409 — 단톡방 화면과 과정 보기가 같은 카드를 보여 한쪽에서 답한 뒤 다른 쪽을 누른 경우라 화면이 오류 창 없이 카드만 닫는다
    /// 그 밖의 실패는 지금처럼 400(오류 창).
    /// </summary>
    private IActionResult CardError(string error) =>
        error is RoomHub.QuestionDone or RoomHub.ApprovalDone
            ? new ContentResult { StatusCode = StatusCodes.Status409Conflict, Content = error, ContentType = "text/plain; charset=utf-8" }
            : BadRequest(error);

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
        return hub.Answer(id, question, parsed) is { } error ? CardError(error) : new JsonResult(new { ok = true });
    }

    /// <summary>끼어들기: 지금 턴을 멈추고 이 말을 바로 보낸다. 큐의 다른 말은 남는다.</summary>
    public IActionResult OnPostInterrupt(string id, string? text)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (TooManyAttachments(text)) return BadRequest($"첨부는 한 말에 {Attachments.MaxPerMessage}개까지입니다.");
        if (store.Get(id) is { IsGroup: true }) return BadRequest("단톡방에는 끼어들기가 없습니다 — 중지한 뒤 보내세요.");
        if (ChildRefusal(id) is { } refusal) return BadRequest(refusal);
        return hub.Interrupt(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>기다리는 말 하나 취소.</summary>
    public IActionResult OnPostCancelQueued(string id, string? queued)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(queued)) return NotFound();
        return hub.CancelQueued(id, queued) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 파일 첨부: 하나씩 받아 그 방 첨부 폴더에 저장하고 절대 경로를 돌려준다 — 화면이 보낼 때 [첨부] 줄로 붙인다.
    /// 휴지통 방은 받지 않는다. 20MB 넘으면 거절.
    /// </summary>
    public async Task<IActionResult> OnPostAttach(string id, IFormFile? file)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
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
