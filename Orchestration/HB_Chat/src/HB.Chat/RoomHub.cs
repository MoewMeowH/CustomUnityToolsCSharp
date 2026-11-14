using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace HB.Chat;

/// <summary>
/// 방 화면에 보내는 실시간 알림 (SSE data 한 줄 = JSON 하나).
/// kind: snapshot · start · delta · tool · denied · retry · end · queued · notice · idle · approval · approval-done · question · question-done ·
/// rooms(방 목록 줄이 바뀌었을 수 있음, Text = 그 방 ID — 어느 방이든).
/// Prompt는 지금 처리 중인 사용자 말, Pending은 큐에서 기다리는 말들. Approval은 승인 카드(approval) · 끝난 카드의 ID(approval-done, Text = 결정).
/// Question은 질문 카드(question) · 끝난 카드(question-done, Text = 결정 한 줄 — 3차 a).
/// </summary>
public sealed record LiveEvent(string Kind)
{
    public string? Text { get; init; }
    public string? Prompt { get; init; }
    public bool? Ok { get; init; }
    public bool? Busy { get; init; }
    public int? Queued { get; init; }
    public double? CostUsd { get; init; }
    public IReadOnlyList<string>? Pending { get; init; }

    /// <summary>기다리는 말 (ID 붙음 — 화면이 하나씩 취소). Pending과 같은 차례.</summary>
    public IReadOnlyList<QueuedMessage>? PendingItems { get; init; }
    public ApprovalView? Approval { get; init; }
    public QuestionView? Question { get; init; }

    /// <summary>question-done 알림: 고른 답 (질문 글 → 답, 4차 D1 — 화면이 '나' 말풍선으로). 답하지 않았으면 null.</summary>
    public IReadOnlyDictionary<string, string>? Answers { get; init; }

    /// <summary>face 알림 · snapshot: 지휘자 표정 상태 (<see cref="HB.Chat.Face"/> 이름).</summary>
    public string? Face { get; init; }

    /// <summary>end 알림: 이번 턴이 끝날 때의 컨텍스트 크기 · 창 (토큰).</summary>
    public long? ContextTokens { get; init; }
    public int? ContextWindow { get; init; }

    /// <summary>tool 알림: 도구의 짧은 대상 (파일 이름 · 명령 첫 줄 …, 3차 b). 없으면 null.</summary>
    public string? Detail { get; init; }

    /// <summary>subagent-said 알림: 글을 쌓을 서브에이전트 말풍선 (작업 ID).</summary>
    public string? TaskId { get; init; }

    /// <summary>subagent-said 알림: 서버가 요약한 중간 글이면 true.</summary>
    public bool? Narration { get; init; }

    /// <summary>subagents 알림 · snapshot: 진행 칸의 서브에이전트 말풍선 전체 (화면은 이 목록으로 갈아 끼운다).</summary>
    public IReadOnlyList<SubagentBubble>? Subagents { get; init; }

    /// <summary>steps 알림 · snapshot: 지휘자 할 일 목록 전체 (3차 b).</summary>
    public IReadOnlyList<StepItem>? Steps { get; init; }

    /// <summary>status 알림 · snapshot: 지휘자 상태 한 줄 (post_turn_summary, 3차 b).</summary>
    public RoomStatus? Status { get; init; }

    /// <summary>agent 알림: 서브에이전트 · 백그라운드 명령 진행 (Claude 앱 기능 D — task_started · task_progress · task_notification).</summary>
    public TaskEvent? Task { get; init; }

    /// <summary>background 알림 · snapshot: 메인 답 뒤 기다리는 백그라운드 작업 수와 정리 시각 (0이면 기다림 끝 — 화면 "지금 끝내기" 줄).</summary>
    public int? BackgroundCount { get; init; }
    public DateTimeOffset? BackgroundUntil { get; init; }

    /// <summary>background 알림 · snapshot: 쉬는 실행의 백그라운드 작업 목록 (화면 작업 줄 · 하나씩 끝내기). 예전 기다림(단톡방 구성원)은 null.</summary>
    public IReadOnlyList<BgTaskView>? Tasks { get; init; }

    /// <summary>denied 알림: 승인 기록에도 남겨 기록 화면 제자리에 방 기록 줄로 보인다 — 화면이 턴 끝에 진행 칸의 줄을 지운다.</summary>
    public bool? Logged { get; init; }

    /// <summary>terminals 알림 · snapshot: 이 방 터미널 목록 (곁칸 터미널 탭).</summary>
    public IReadOnlyList<TerminalView>? Terminals { get; init; }

    /// <summary>term-out 알림: 터미널 출력 조각 (base64, VT 바이트) · 그 조각의 시작 오프셋 — 화면은 받은 버퍼 끝보다 앞선 조각을 버린다.</summary>
    public string? TermId { get; init; }
    public string? Data { get; init; }
    public long? Offset { get; init; }

    /// <summary>browser 알림 · snapshot: 지휘자 브라우저 상태 (곁칸 브라우저 탭). browser-frame 알림은 Data에 jpeg(base64).</summary>
    public BrowserView? Browser { get; init; }

    /// <summary>단톡방 member-delta · member-tool 알림: 그 글 조각 · 도구를 낸 구성원 ID (tool의 Detail은 도구 대상 그대로).</summary>
    public string? Member { get; init; }

    /// <summary>trust 알림 · snapshot: 이 턴 동안 명령을 묻지 않는 중인지.</summary>
    public bool? Trust { get; init; }

    /// <summary>shell-start · snapshot: 사용자가 '!'로 돌리는 명령 (snapshot은 도는 중일 때만).</summary>
    public ShellView? Shell { get; init; }
}

/// <summary>
/// 화면의 승인 카드 하나.
/// Truncated면 명령이 너무 길어 잘린 것 — 거절만 보인다 (서버도 허용을 받지 않는다).
/// Extra는 명령 밖의 입력(허용하면 같이 실행됨), Risky면 위험한 이름의 입력이 있어 거절만.
/// </summary>
public sealed record ApprovalView(string Id, string Tool, string Summary, string? Description, DateTimeOffset ExpiresAt, bool Truncated)
{
    public string? Extra { get; init; }
    public bool Risky { get; init; }

    /// <summary>계획 승인 카드 (Summary = 계획 글).</summary>
    public bool Plan { get; init; }

    /// <summary>서브에이전트가 낸 요청 (Claude 앱 기능 D).</summary>
    public bool FromAgent { get; init; }

    /// <summary>백그라운드 실행 명령이면 답이 끝난 뒤에도 계속 도는 최대 분 (카드 표시, 결정 "넣음"). 아니면 null.</summary>
    public int? BackgroundMinutes { get; init; }

    /// <summary>run_mode 모델 선택 카드. 아니면 null.</summary>
    public ModeCard? Mode { get; init; }

    /// <summary>새 방 열기 카드 (room_open). 아니면 null.</summary>
    public RoomOpenCard? RoomOpen { get; init; }

    /// <summary>작업 폴더 밖 파일 쓰기 카드 (Summary = 경로, Extra = 쓸 내용 미리보기 — 잘려도 허용).</summary>
    public bool OutsideWrite { get; init; }

    /// <summary>"이 턴 동안 묻지 않기" 단추를 보이는 명령 카드 (<see cref="ApprovalRequest.TurnAllowable"/>).</summary>
    public bool TurnAllow { get; init; }

    /// <summary>"이 폴더 늘 허용" 단추의 폴더 (앱 단계 폴더 밖 쓰기 카드). 없으면 null.</summary>
    public string? AlwaysFolder { get; init; }
}

/// <summary>
/// 방마다 한 번에 한 턴. 실행 중에 온 말은 큐에 넣고 끝나면 차례로 보낸다.
/// 중지는 세대(generation)를 올려 큐를 비우고 실행을 끝낸다 — 늦게 끝난 실행은 화면 · 큐에 손대지 않는다.
/// 진행 중 턴의 흘림 글 · 도구 상태는 여기서만 들고 SSE로 흘린다. 끝난 말은 세션 기록에서 다시 읽는다.
/// </summary>
public sealed partial class RoomHub(RoomStore store, ChatTranscripts transcripts, IOptions<ChatOptions> options) : IDisposable
{
    /// <summary>한 번에 보낼 수 있는 글 길이 상한 (글자).</summary>
    public const int MaxPromptChars = 100_000;

    /// <summary>진행 중 흘림 글을 화면용으로 들고 있는 상한 (글자). 넘으면 앞을 버린다 (끝나면 세션 기록으로 바뀐다).</summary>
    private const int MaxPartialChars = 200_000;

    private sealed class Live(string id)
    {
        public readonly string Id = id;
        public readonly Lock Gate = new();
        public int Generation;
        public bool Busy;
        public readonly List<QueuedMessage> Pending = [];
        public CancellationTokenSource? Cts;

        /// <summary>도는 '!' 명령 (방마다 하나). 바꿀 때는 잠금 안에서.</summary>
        public ShellRun? Shell;

        /// <summary>"지금 끝내기" — 메인 답 뒤 백그라운드 작업을 기다리는 실행을 끝낸다 (Cts와 같은 수명). 기다리는 수 · 정리 시각은 snapshot용.</summary>
        public CancellationTokenSource? WrapUpCts;
        public int BackgroundCount;
        public DateTimeOffset? BackgroundUntil;
        public string? Current;
        public readonly System.Text.StringBuilder Partial = new();

        /// <summary>
        /// Partial 앞쪽 중 세션 기록에 이미 쓰인 글자 수 · 이 턴의 말이 기록에 들어갔는지 (assistant 줄이 나왔는지). 턴 도중 방을 다시 열면 기록으로 그린 부분을
        /// snapshot이 다시 보내지 않는다 — 내 말과 지휘자 글이 두 번 보이던 문제 (사용자 보고 2026-10-30).
        /// </summary>
        public int PartialLogged;
        public bool TurnLogged;
        public ToolUse? Tool;
        public string? Notice;
        public readonly List<Channel<LiveEvent>> Subscribers = [];

        /// <summary>실행 중 표정 (생각 중 → 답 쓰는 중 · 도구 · 승인 기다림). 쉴 때는 LastEnd · 방 색인으로 정한다 (<see cref="FaceOf"/>).</summary>
        public Face Phase = Face.Idle;
        public Face? LastEnd;
        public DateTimeOffset LastEndAt;

        /// <summary>지금 실행을 끼어들기로 끝냈다 — end 알림 글을 "끼어들기로 멈춤"으로 (지휘자 검토 R5).</summary>
        public bool Interrupted;

        /// <summary>
        /// 지금 실행의 말에 붙은 + 요청 창 값 (/self-review 785ebb5e97 C1 — 방이 아니라 말에 붙인다).
        /// 같은 모드의 첫 run_mode 카드가 쓰고 <see cref="PickUsed"/>를 켠다. 말을 꺼낼 때 잠금 안에서 바꾼다.
        /// </summary>
        public ModePick? CurrentPick;
        public bool PickUsed;

        /// <summary>
        /// "이 턴 동안 묻지 않기": 켜져 있으면 명령을 카드 없이 허용한다. 저장하지 않는다 — 턴이 끝날 때 · 중지 · 명령 아래로 내릴 때 · 화면의 끄기로 꺼진다.
        /// 바꿀 때는 <see cref="SetTrust"/>로 (화면 표시).
        /// </summary>
        public bool TrustCommands;

        /// <summary>계획 카드를 허용한 실행의 세대 — 그 뒤로는 계획 모드가 아니다 (앱 단계 쓰기 판정이 계획 중 쓰기를 그 전처럼 거절하게).</summary>
        public int PlanApprovedGeneration = -1;
        public readonly Dictionary<string, PendingApproval> Approvals = [];
        public readonly Dictionary<string, PendingQuestion> Questions = [];

        /// <summary>
        /// 지휘자 할 일 목록 (마지막으로 읽은 것, 3차 b). 이번 턴에 할 일 도구를 썼으면(StepsWatch) 그 뒤 글 조각이 아닌 출력마다 · 턴 끝에 다시 읽는다 —
        /// CLI가 도구 결과 줄을 낸 뒤에 파일을 쓰는 일이 있어 한 번만 읽으면 마지막 변경을 놓쳤다 (실제 확인).
        /// </summary>
        public IReadOnlyList<StepItem>? Steps;
        public bool StepsWatch;
        public DateTimeOffset StepsReadAt;

        /// <summary>
        /// 이 방의 claude 프로세스 (쉬는 실행 — 답 뒤에도 백그라운드 작업이 남으면 산다). 없거나 끝났으면 null. 바꿀 때는 잠금 안에서.
        /// </summary>
        public RoomRun? Session;

        /// <summary>
        /// 화면 턴으로 열기 전에 끝난 자동 턴 — LoopAsync가 차례로 열어 기록한다. 방에 둔다: 그 직후 프로세스가 끝나
        /// 쉬는 실행이 방에서 떨어져도 그 답은 남는다. 잠금 안에서.
        /// </summary>
        public readonly Queue<(RoomRun Run, TurnWait Wait)> Unshown = new();

        /// <summary>쉬는 실행의 백그라운드 작업 목록 (화면 · 방 목록). 바꿀 때는 잠금 안에서.</summary>
        public IReadOnlyList<BgTaskView> BgTasks = [];

        /// <summary>진행 칸의 서브에이전트 말풍선 (<see cref="RoomHub.TrackSubagent"/>). 잠금 안에서.</summary>
        public readonly List<SubagentBubble> Subagents = [];

        /// <summary>답을 기다리는 카드(승인 · 질문)가 있는지.</summary>
        public bool Waiting => Approvals.Values.Any(p => !p.Answer.Task.IsCompleted) || Questions.Values.Any(q => !q.Answer.Task.IsCompleted);
    }

    private sealed class PendingApproval(ApprovalRequest request, DateTimeOffset expiresAt)
    {
        public readonly string Id = Guid.NewGuid().ToString("N")[..10];
        public readonly ApprovalRequest Request = request;
        public readonly DateTimeOffset ExpiresAt = expiresAt;
        public readonly TaskCompletionSource<ApprovalAnswer> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>카드에 남길 결정 (허용 · 이 턴 허용 · 거절 …). 답을 정한 쪽이 적는다.</summary>
        public string? Label;
        /// <summary>화면 턴 도중 뜬 카드인지 — 쉬는 동안(턴 없음) 뜬 카드는 "이 턴 동안 묻지 않기"를 받지 않는다(턴 경계가 없어 쉬는 시간 전체로 넓어짐, self C8).</summary>
        public bool InTurn;

        /// <summary>쉬는 실행 방이면 백그라운드가 사는 최대 분(상한) — 카드에 보인다 (self C3). 단톡방 구성원은 null(예전 10 · 30분).</summary>
        public int? RestMinutes;

        /// <summary>결정을 기록하고 approval-done을 알린 뒤 끝난다 — 실행 끝 알림(end)이 그 뒤에 나가게 기다린다.</summary>
        public readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ApprovalView View => new(Id, Request.Tool, Request.Summary, Request.Description, ExpiresAt, Request.Truncated)
        {
            Mode = Request.Mode,
            RoomOpen = Request.RoomOpen,
            Extra = Request.Extra,
            OutsideWrite = Request.OutsideWrite,
            Risky = Request.Risky,
            Plan = Request.IsPlan,
            FromAgent = Request.FromAgent,
            TurnAllow = Request.TurnAllowable && InTurn,
            AlwaysFolder = Request.DenyOnly ? null : Request.AlwaysFolder,
            // 서브에이전트가 낸 요청이면 그 서브에이전트를 기다리는 상한(30분)까지 돈다
            BackgroundMinutes = !Request.Background ? null
                : RestMinutes ?? (Request.FromAgent ? ClaudeRunner.BackgroundAgentMinutes : ClaudeRunner.BackgroundBashMinutes),
        };
    }

    /// <summary>질문 카드 하나 (3차 a). 답은 질문 글 → 답, null이면 답하지 않음(거절 — Label에 이유).</summary>
    private sealed class PendingQuestion(QuestionRequest request, DateTimeOffset expiresAt)
    {
        public readonly string Id = Guid.NewGuid().ToString("N")[..10];
        public readonly QuestionRequest Request = request;
        public readonly DateTimeOffset ExpiresAt = expiresAt;
        public readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Label;

        public QuestionView View => new(Id, Request.Items, ExpiresAt);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Live> _rooms = new();

    /// <summary>승인 기록에 남길 계획 글 길이 (글자).</summary>
    public const int PlanLogChars = 2_000;

    /// <summary>터미널 시작 카드 · 기록 줄의 도구 이름 (방 기록 줄: "승인 허용 — 터미널: 명령").</summary>
    public const string TerminalTool = "터미널";

    /// <summary>지휘자 브라우저 사이트 카드의 도구 이름 (방 기록 줄: "승인 허용 — 브라우저: 주소").</summary>
    public const string BrowserTool = "브라우저";

    /// <summary>웹 가져오기(WebFetch) 사이트 카드의 도구 이름 (방 기록 줄: "승인 허용 — 웹 가져오기: 주소").</summary>
    public const string WebFetchCardTool = "웹 가져오기";

    /// <summary>방마다 웹 가져오기로 허용한 사이트 — 앱을 다시 켤 때까지 (사용자 결정).</summary>
    private readonly WebFetchSites _webSites = new();

    /// <summary>실행 끝에 남은 승인 카드의 기록 · approval-done을 기다리는 상한 — 넘으면 end를 그대로 보낸다.</summary>
    private static readonly TimeSpan ApprovalSettleWait = TimeSpan.FromSeconds(2);

    /// <summary>방마다 마지막 실행의 init (명령 · 스킬 · 플러그인 목록 — Claude 앱 기능 A). 메모리만.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InitEvent> _commandSets = new();

    /// <summary>그 방 마지막 실행이 알려 준 명령 목록. 이 앱을 켠 뒤 그 방이 아직 돌지 않았으면 null.</summary>
    public InitEvent? Commands(string roomId) => _commandSets.TryGetValue(roomId, out var init) ? init : null;

    /// <summary>기억한 명령 목록을 버린다 — 방을 휴지통 · 지우기 · 설정(단계)을 바꿀 때. 다음 실행이 다시 알려 준다.</summary>
    public void ForgetCommands(string roomId)
    {
        _commandSets.TryRemove(roomId, out _);
        _webSites.Forget(roomId); // 방을 지우면 웹 가져오기 허용 사이트도
    }

    private bool _disposed;

    /// <summary>앱을 끄는 중 (Dispose 뒤) — 그 때문에 끊긴 실행은 끝난 것으로 적지 않는다 (단톡방 답 줄도).</summary>
    public bool ShuttingDown => _disposed;

    /// <summary>앱 전체 구독 (방 목록 화면 · 2-3 알림): 목록 줄이 바뀌었을 수 있는 방 ID를 받는다.</summary>
    private readonly Lock _appGate = new();
    private readonly List<Channel<string>> _appSubscribers = [];

    /// <summary>
    /// 방 구독도 목록 신호(rooms)를 같이 받는다 — 탭 하나가 SSE를 하나만 열게 (HTTP/1.1 한 호스트 연결 6개 상한, /self-review 5225b1ffaf C1).
    /// 잠금은 앱 잠금만 잡는다: 방 잠금을 잡은 채 Touch가 불려도 다른 방 잠금으로 가지 않는다.
    /// </summary>
    private readonly List<Channel<LiveEvent>> _roomListeners = [];

    /// <summary>방 목록 줄을 바꾸는 알림 (바쁨 · 승인 · 끝). 흘림 글(delta) 같은 잦은 알림은 넣지 않는다.</summary>
    // background: 쉬는 방의 "백그라운드 N"이 작업 하나 끝내기 · 정리에도 따라가게
    private static readonly HashSet<string> ListKinds = ["start", "end", "idle", "approval", "approval-done", "question", "question-done", "queued", "steps", "background"];

    /// <summary>
    /// 이 실행기를 바꿀 때 (테스트: 가짜 claude). 기본은 claude 실행 파일 + <see cref="ClaudeArgs"/> 인자.
    /// </summary>
    public Func<Room, RunSpec, string, ProcessSpec>? ProcessFactory { get; init; }

    /// <summary>승인 기록 (없으면 남기지 않음).</summary>
    public ApprovalLog? Log { get; init; }

    /// <summary>구독 한도 (실행이 알려 준 rate_limit_event를 남김). 없으면 남기지 않음.</summary>
    public UsageLimits? Limits { get; init; }

    /// <summary>claude CLI 판 기록 — 실행 init마다 본 판을 적는다 (확인하지 않은 판 띠). 없으면(시험) 적지 않는다.</summary>
    public CliVersions? Cli { get; init; }

    /// <summary>
    /// 터미널 곁칸. 있으면 명령 방 실행에 앱 MCP(터미널 도구)를 붙이고, 출력 · 목록 변화를 방 알림으로 흘린다.
    /// 설정하면 이벤트를 구독한다 (앱에 하나).
    /// </summary>
    public Terminals? Terminals
    {
        get => _terminals;
        init
        {
            _terminals = value;
            if (value is null) return;
            value.Output += (s, data, offset) => Post(s.RoomId, () => new LiveEvent("term-out") { TermId = s.Id, Data = Convert.ToBase64String(data), Offset = offset });
            value.Changed += roomId => Post(roomId, () => new LiveEvent("terminals") { Terminals = value.List(roomId) });
            value.Revoked += ForgetRoomOpen; // 새 방 표도 실행 수명 (터미널 표와 같게 — /self-review 4f5fd81b8e C1)
            // 방 기록 줄은 지휘자 프로세스만 — 내 셸은 사용자가 직접 연 것이라 남기지 않는다
            value.Started += s =>
            {
                if (s.Kind == TerminalKind.Agent) Log?.Note(s.RoomId, RoomNotes.TerminalTool, $"터미널 {s.Name} 시작: {s.Command}");
            };
            value.Ended += s =>
            {
                if (s.Kind == TerminalKind.Agent)
                    Log?.Note(s.RoomId, RoomNotes.TerminalTool, s.StoppedBy is { } by ? $"터미널 {s.Name} 중지 ({by})" : $"터미널 {s.Name} 끝남 (코드 {s.ExitCode})");
            };
        }
    }
    private readonly Terminals? _terminals;

    /// <summary>
    /// 지휘자 브라우저. 있으면 browser_open 사이트 카드를 보이고, 실시간 화면 · 상태를 방 알림으로 흘린다(화면이 있을 때만).
    /// </summary>
    public AgentBrowser? Browser
    {
        get => _browser;
        init
        {
            _browser = value;
            if (value is null) return;
            value.Frame += (roomId, jpeg) => Post(roomId, () => new LiveEvent("browser-frame") { Data = Convert.ToBase64String(jpeg) });
            value.Changed += roomId => Post(roomId, () => new LiveEvent("browser") { Browser = value.View(roomId) });
        }
    }
    private readonly AgentBrowser? _browser;

    /// <summary>이 앱의 loopback 주소 — 앱 MCP를 claude에 알려 줄 때 (<see cref="TerminalMcp.LoopbackUrl"/>). 없으면 터미널 도구를 붙이지 않는다.</summary>
    public Func<string?>? SelfUrl { get; init; }

    /// <summary>스티커 설정 — Claude 방 규칙에 스티커 안내(<see cref="StickerRules"/>)를 붙일 때. 없으면 안내 없음.</summary>
    public HB.Orchestration.ChatUi.StickerCatalog? Stickers { get; init; }

    /// <summary>
    /// 방을 열어 둔 화면이 있을 때만 그 방 알림으로 — 없으면 알림을 만들지도 않는다(터미널 출력 base64, /self-review 57b59ef134 C4).
    /// 터미널 출력은 snapshot에 남기지 않는다 — 화면이 버퍼로 다시 받는다.
    /// </summary>
    private void Post(string roomId, Func<LiveEvent> make)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
            if (live.Subscribers.Count > 0) Broadcast(live, make());
    }

    public bool IsBusy(string roomId) => _rooms.TryGetValue(roomId, out var live) && live.Busy;

    /// <summary>
    /// 실행 중이면 지금까지 흘린 글(reset) · 마지막 도구(tool)를 그 채널에 넣는다 — 단톡방 화면이 구독할 때 구성원 임시 말풍선을 되살린다.
    /// 이 방 잠금 안에서 써서, 같은 채널로 오는 이 방의 글 조각(<see cref="Streamed"/>)과 겹치거나 빠지지 않는다 (/self-review 7e68bf5791 C1 —
    /// 따로 HTTP로 받으면 그 사이 조각이 두 번 붙었다). <paramref name="shape"/>: 알림 모양을 바꾼다 (단톡방 — member- 종류 · 구성원 ID).
    /// </summary>
    /// <param name="also">같은 잠금 안에서 reset 뒤에 더 보낼 알림 (단톡방 — 서버 요약 중간 글).</param>
    public void ReplayLive(string roomId, ChannelWriter<LiveEvent> to, Func<LiveEvent, LiveEvent> shape, Func<IEnumerable<LiveEvent>>? also = null)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
        {
            if (!live.Busy) return;
            to.TryWrite(shape(new LiveEvent("reset") { Text = live.Partial.ToString() }));
            foreach (var ev in also?.Invoke() ?? []) to.TryWrite(shape(ev));
            if (live.Tool is { } tool) to.TryWrite(shape(new LiveEvent("tool") { Text = tool.Name, Detail = tool.Detail }));
        }
    }

    /// <summary>방 SSE 구독이 붙은 뒤 (snapshot 다음, 잠금 밖) — 단톡방이 구성원 진행 글을 같은 채널에 이어 넣는다 (<see cref="ReplayLive"/>).</summary>
    public event Action<string, ChannelWriter<LiveEvent>>? Subscribed;

    /// <summary>방 목록에 보일 실행 상태: 쉼 · 실행 중 · 답 기다림(승인 · 질문 카드) (실패는 방 색인의 LastError로 따로 본다).</summary>
    public RoomActivity ActivityOf(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return RoomActivity.Idle;
        lock (live.Gate)
            return live.Waiting ? RoomActivity.Approval
                : live.Busy ? RoomActivity.Busy
                : RoomActivity.Idle;
    }

    /// <summary>끝난 턴의 표정(끝남 · 실패 · 중지)을 보이는 시간. 그 뒤에는 쉼 · 실패 · 오래 쉼.</summary>
    public static readonly TimeSpan EndFaceFor = TimeSpan.FromSeconds(10);

    /// <summary>마지막 활동에서 이만큼 지나면 "오래 쉼" 표정.</summary>
    public static readonly TimeSpan SleepAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// 지휘자 표정. 실행 중이면 그 단계, 쉬면 방금 끝난 턴(transient일 때만, <see cref="EndFaceFor"/> 안) → 마지막 실패 → 오래 쉼 → 쉼.
    /// 목록은 transient 없이 쓴다 — 목록은 알림이 올 때만 다시 받아 "끝남"이 오래 남는다.
    /// </summary>
    public Face FaceOf(Room room, bool transient = true)
    {
        Face? running = null;
        if (_rooms.TryGetValue(room.Id, out var live))
            lock (live.Gate) running = LiveFace(live, transient);
        return running ?? RestFace(room);
    }

    /// <summary>
    /// 끝난 턴의 표정을 앞으로 얼마나 더 보일지 (화면이 처음 그릴 때의 타이머, /self-review 83b453653b C11). 아니면 null.
    /// </summary>
    public TimeSpan? EndFaceLeft(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return null;
        lock (live.Gate)
        {
            var left = EndFaceFor - (DateTimeOffset.Now - live.LastEndAt);
            return !live.Busy && live.LastEnd is not null && left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>실행 중 · 방금 끝난 턴의 표정 (방 잠금 안에서 부른다). 쉬는 방이면 null.</summary>
    private static Face? LiveFace(Live live, bool transient)
    {
        if (live.Busy)
            return live.Approvals.Values.Any(p => !p.Answer.Task.IsCompleted) ? Face.Approval
                : live.Questions.Values.Any(q => !q.Answer.Task.IsCompleted) ? Face.Question
                : live.Phase;
        if (transient && live.LastEnd is { } end && DateTimeOffset.Now - live.LastEndAt < EndFaceFor) return end;
        return null;
    }

    /// <summary>쉬는 방의 표정: 마지막 실패 → 오래 쉼 → 쉼.</summary>
    private static Face RestFace(Room room)
    {
        if (room.LastError is not null) return Face.Failed;
        return DateTimeOffset.Now - (room.LastRunAt ?? room.CreatedAt) > SleepAfter ? Face.Sleep : Face.Idle;
    }

    /// <summary>표정이 바뀌면 방 화면에 face 알림 (잠금 안에서 부른다). 도구는 이름이 바뀔 때마다 (Text = 도구 이름).</summary>
    private void SetFace(Live live, Face face, string? tool = null)
    {
        if (live.Phase == face && tool is null) return;
        live.Phase = face;
        Broadcast(live, new LiveEvent("face") { Face = face.ToString(), Text = tool });
    }

    /// <summary>앱 전체 구독. 목록 줄이 바뀌었을 수 있는 방 ID가 온다. 끝나면 <see cref="UnsubscribeAll"/>.</summary>
    public Channel<string> SubscribeAll()
    {
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        lock (_appGate)
        {
            if (_disposed) ch.Writer.TryComplete();
            else _appSubscribers.Add(ch);
        }
        return ch;
    }

    public void UnsubscribeAll(Channel<string> ch)
    {
        lock (_appGate) _appSubscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    /// <summary>방 목록 줄이 바뀌었다고 알린다 (이름 바꾸기 · 읽음 · 방 만들기 같은, 실행 밖의 변경도 화면이 부른다).</summary>
    public void Touch(string roomId)
    {
        lock (_appGate)
        {
            foreach (var ch in _appSubscribers) ch.Writer.TryWrite(roomId);
            foreach (var ch in _roomListeners) ch.Writer.TryWrite(new LiveEvent("rooms") { Text = roomId });
        }
    }

    /// <summary>
    /// 방 줄이 아니라 사이드바 전체가 바뀌었을 때(예 CLI 판 띠) 열린 화면이 목록을 다시 받게 한다 — 목록 알림은 방 ID를 달고 가므로 보이는 방 하나로 낸다.
    /// 방이 없으면 다시 받을 목록도 없다.
    /// </summary>
    public void TouchAll()
    {
        if (store.All().FirstOrDefault(r => r.GroupId is null && r.DeletedAt is null) is { } any) Touch(any.Id);
    }

    /// <summary>
    /// 프로필 · 지휘자 표정이 바뀌었다고 알린다 (3차 e): 방 목록 줄마다 Touch에 더해, 열린 방 화면이 방을 다시 열어
    /// 말풍선 · 진행 중 말풍선 · 표정을 새 이름 · 그림으로 그린다 (data-profiles · data-faces는 방을 열 때만 받으므로).
    /// </summary>
    public void ProfilesChanged(IEnumerable<string> roomIds)
    {
        foreach (var id in roomIds) Touch(id);
        lock (_appGate)
            foreach (var ch in _roomListeners) ch.Writer.TryWrite(new LiveEvent("profiles"));
    }

    /// <summary>말 보내기. 실행 중이면 큐에 넣는다. 오류 문구(null이면 받음).</summary>
    public string? Send(string roomId, string text) => Enqueue(roomId, text, interrupt: false);

    /// <summary>+ 요청 창 말: 고른 값을 그 말에 붙여 큐에 넣는다 — 그 말의 실행만 run_mode를 카드로 묻고 미리 채운다.</summary>
    public string? Send(string roomId, string text, ModePick pick, bool interrupt) => Enqueue(roomId, text, interrupt, pick);

    /// <summary>
    /// 끼어들기: 이 말을 큐 맨 앞에 넣고 지금 실행만 끝낸다 — 세대는 올리지 않아 큐의 다른 말은 그대로 남는다.
    /// 기다리던 승인은 "끼어들기"로 거절. 끊긴 턴은 세션에 결과 없이 남고 다음 말(이 말)이 이어 간다 (1차 실제 확인).
    /// 실행 중이 아니면 보통 보내기와 같다.
    /// </summary>
    public string? Interrupt(string roomId, string text) => Enqueue(roomId, text, interrupt: true);

    /// <summary>
    /// 기다리는 말 하나를 지금 보내기 (3차 b, 노션 "지금보내기"): 큐 맨 앞으로 옮기고 지금 실행만 끝낸다 — 끼어들기와 같은 길이라
    /// 다른 기다리는 말은 남고, 기다리던 승인 · 질문은 "끼어들기"로 끝난다. 실행 중이 아니면 맨 앞으로만 옮긴다(곧 차례가 온다).
    /// </summary>
    public string? SendNow(string roomId, string queuedId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return "없는 방입니다.";
        lock (live.Gate)
        {
            var i = live.Pending.FindIndex(p => p.Id == queuedId);
            if (i < 0) return "이미 보냈거나 없는 말입니다.";
            var message = live.Pending[i];
            live.Pending.RemoveAt(i);
            live.Pending.Insert(0, message);
            if (live.Busy) InterruptTurn(live);
            Broadcast(live, QueueEvent("queued", live));
        }
        return null;
    }

    /// <summary>기다리는 말 하나 취소. 이미 보냈거나 없는 말이면 오류 문구.</summary>
    public string? CancelQueued(string roomId, string queuedId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return "없는 방입니다.";
        lock (live.Gate)
        {
            var i = live.Pending.FindIndex(p => p.Id == queuedId);
            if (i < 0) return "이미 보냈거나 없는 말입니다.";
            live.Pending.RemoveAt(i);
            Broadcast(live, QueueEvent("queued", live));
        }
        return null;
    }

    private string? Enqueue(string roomId, string text, bool interrupt, ModePick? pick = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "빈 글은 보내지 않습니다.";
        if (text.Length > MaxPromptChars) return $"글이 너무 깁니다 ({text.Length:N0}자, 상한 {MaxPromptChars:N0}자).";
        if (store.Get(roomId) is not { } room) return "없는 방입니다.";
        if (room.DeletedAt is not null) return "휴지통에 있는 방입니다 — 되살린 뒤 보내세요.";
        if (store.LoadError is not null) return "방 색인을 읽지 못해 보내지 않습니다.";

        var live = _rooms.GetOrAdd(roomId, id => new Live(id));
        lock (live.Gate)
        {
            var message = new QueuedMessage(Guid.NewGuid().ToString("N")[..10], text) { Pick = pick };
            if (live.Busy)
            {
                if (interrupt)
                {
                    live.Pending.Insert(0, message);
                    InterruptTurn(live);
                }
                else live.Pending.Add(message);
                Broadcast(live, QueueEvent("queued", live));
                return null;
            }
            live.Pending.Add(message);
            live.Busy = true;
            var generation = live.Generation;
            _ = Task.Run(() => LoopAsync(roomId, live, generation));
        }
        return null;
    }

    /// <summary>큐 상태를 실은 알림 (queued · start · snapshot): 글 목록(Pending)과 ID 붙은 목록(PendingItems — 하나씩 취소).</summary>
    private static LiveEvent QueueEvent(string kind, Live live) => new(kind)
    {
        Queued = live.Pending.Count,
        Pending = [.. live.Pending.Select(p => p.Text)],
        PendingItems = [.. live.Pending],
    };

    /// <summary>
    /// 지금 끝내기: 메인 답이 끝나고 백그라운드 작업(서버 등)을 기다리는 실행을 정리한다 — 큐 · 다음 말은 그대로 (중지와 다름).
    /// 답 전이면 러너가 무시한다.
    /// </summary>
    public void WrapUp(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
        {
            // 쉬는 실행: 쉬는 중이면 작업을 모두 끝낸다 (응답 중이면 받지 않는다 — 그 턴은 중지로)
            if (live.Session is { } run && run.Proc.WrapUp()) return;
            // 기다림이 시작된 뒤(정리 시각이 있음)면 — 작업이 다 끝나 지휘자가 이어 답하는 중이어도 받는다
            if (live.BackgroundUntil is not null) live.WrapUpCts?.Cancel();
        }
    }

    /// <summary>중지: 큐를 비우고 진행 중 실행을 끝낸다. 기다리던 승인은 거절로 돌려준다.</summary>
    public void Stop(string roomId) => EndTurn(roomId, keepQueue: false);

    /// <summary>지금 턴만 끝낸다 — 기다리는 말은 그대로 다음 실행으로 간다 (앱 단계에서 내릴 때).</summary>
    public void StopTurn(string roomId) => EndTurn(roomId, keepQueue: true);

    private void EndTurn(string roomId, bool keepQueue)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
        {
            live.Generation++;
            if (!keepQueue) live.Pending.Clear();
            SetTrust(live, false);
            EndApprovals(live, "중지됨");
            live.Cts?.Cancel();
            // 쉬는 실행도 끝낸다 — 응답 중이면 위 취소가 이미 정리하고, 쉬는 중이면 남은 백그라운드 작업째 (중지 = 다 멈춤)
            live.Session?.Proc.Stop();
        }
    }

    /// <summary>
    /// 끼어들기 · 지금 보내기 (잠금 안에서): 지금 실행만 멈춘다 — 루프는 같은 세대로 큐 맨 앞(새 말)을 이어 돈다. 쉬는 실행이 이 턴을 답하는 중이면
    /// interrupt로 답만 멈춰 백그라운드 작업을 살리고(결정 "끼어들기만 살림"), 그때는 서브에이전트가 낸 카드는 남긴다 — 그 서브에이전트도 산다
    ///. 아니면 예전처럼 카드를 모두 끝내고 실행을 취소한다 (끝나는 실행의 토큰도 비운다).
    /// </summary>
    private void InterruptTurn(Live live)
    {
        if (live.Cts is not { } running)
        {
            EndApprovals(live, "끼어들기");
            return;
        }
        live.Interrupted = true;
        if (TryInterruptRest(live))
        {
            EndApprovals(live, "끼어들기", keepAgents: true);
            return;
        }
        EndApprovals(live, "끼어들기");
        running.Cancel();
    }

    /// <summary>SSE 구독. 먼저 지금 상태(snapshot)를 넣어 준다. 끝나면 <see cref="Unsubscribe"/>.</summary>
    public Channel<LiveEvent> Subscribe(string roomId)
    {
        var live = _rooms.GetOrAdd(roomId, id => new Live(id));
        var ch = Channel.CreateUnbounded<LiveEvent>(new UnboundedChannelOptions { SingleReader = true });
        var room = store.Get(roomId);
        // 처음 여는 방의 할 일 목록은 파일에서 (실행 중이면 들고 있는 것) — 잠금 밖에서 읽는다 (3차 b)
        IReadOnlyList<StepItem>? disk = null;
        lock (live.Gate) if (live.Steps is null && room is not null) disk = [];
        if (disk is not null) disk = Steps.Read(options.Value.TasksRoot, room!.SessionId);
        lock (live.Gate)
        {
            if (live.Steps is null && disk is not null) live.Steps = disk;
            // 표정도 Busy · Partial과 같은 잠금에서 정한다
            var face = LiveFace(live, transient: true) ?? (room is null ? Face.Idle : RestFace(room));
            ch.Writer.TryWrite(QueueEvent("snapshot", live) with
            {
                Face = face.ToString(),
                Busy = live.Busy,
                Text = live.Busy ? live.Partial.ToString(live.PartialLogged, live.Partial.Length - live.PartialLogged).TrimStart() : null,
                Prompt = live.TurnLogged ? null : live.Current,
                Steps = live.Steps,
                Status = room?.Status,
                // 쉬는 실행이면 답이 끝난 뒤(Busy 아님)에도 작업 줄을 보인다
                BackgroundCount = (live.Busy || Resting(live)) && live.BackgroundUntil is not null ? live.BackgroundCount : null,
                BackgroundUntil = live.Busy || Resting(live) ? live.BackgroundUntil : null,
                Tasks = Resting(live) && live.BackgroundUntil is not null ? live.BgTasks : null,
                Subagents = SnapshotSubagents(live),
                Trust = live.TrustCommands,
                Terminals = _terminals?.List(roomId), // 곁칸 터미널 탭 — 출력은 탭을 열 때 버퍼로 받는다
                Browser = _browser?.View(roomId), // 곁칸 브라우저 탭 — 마지막 화면은 탭을 열 때 받는다
                Shell = ShellSnapshot(live),
            });
            if (live.Busy && live.Tool is { } tool) ch.Writer.TryWrite(new LiveEvent("tool") { Text = tool.Name, Detail = tool.Detail });
            if (live.Notice is { } notice) ch.Writer.TryWrite(new LiveEvent("notice") { Text = notice });
            foreach (var p in live.Approvals.Values.Where(p => !p.Answer.Task.IsCompleted))
                ch.Writer.TryWrite(new LiveEvent("approval") { Approval = p.View });
            foreach (var q in live.Questions.Values.Where(q => !q.Answer.Task.IsCompleted))
                ch.Writer.TryWrite(new LiveEvent("question") { Question = q.View });
            live.Subscribers.Add(ch);
        }
        lock (_appGate)
        {
            if (_disposed) ch.Writer.TryComplete();
            else _roomListeners.Add(ch);
        }
        try { Subscribed?.Invoke(roomId, ch.Writer); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        return ch;
    }

    public void Unsubscribe(string roomId, Channel<LiveEvent> ch)
    {
        lock (_appGate) _roomListeners.Remove(ch);
        if (_rooms.TryGetValue(roomId, out var live))
            lock (live.Gate) live.Subscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    /// <param name="auto">쉬는 실행이 스스로 시작한 턴(작업 끝 뒤 자동 답)을 먼저 화면 턴으로 — 그 뒤 기다리는 말을 이어 돈다.</param>
    private async Task LoopAsync(string roomId, Live live, int generation, (RoomRun Run, TurnWait Wait)? auto = null)
    {
        while (true)
        {
            string? text;
            CancellationTokenSource cts, wrapUp;
            lock (live.Gate)
            {
                SetTrust(live, false); // 턴마다 다시 — 다음 말로 넘어가지 않는다
                if (auto is not null && (live.Generation != generation || _disposed)) auto = null;
                // 앞 턴이 끝나는 사이 쉬는 실행이 스스로 시작한 턴 — 기다리는 말보다 먼저 화면 턴으로 (안 열면 그 답이 화면에 안 보인다)
                if (auto is null && !_disposed && live.Generation == generation) auto = TakeAutoTurn(live, generation);
                if (auto is null && (live.Generation != generation || live.Pending.Count == 0 || _disposed))
                {
                    live.Busy = false;
                    live.Current = null;
                    live.Cts = null;
                    live.WrapUpCts = null;
                    live.Tool = null;
                    ClearPartial(live);
                    // 중지로 끝난 경우 새 세대에서 이미 받은 말이 있으면 이어서 돈다.
                    if (live.Pending.Count > 0 && !_disposed)
                    {
                        live.Busy = true;
                        generation = live.Generation;
                        continue;
                    }
                    Broadcast(live, new LiveEvent("idle") { Busy = false });
                    return;
                }
                if (auto is null)
                {
                    text = live.Pending[0].Text;
                    live.CurrentPick = live.Pending[0].Pick;
                    live.Pending.RemoveAt(0);
                }
                else
                {
                    text = null; // 자동 턴 — 사용자 말 없음
                    live.CurrentPick = null;
                }
                live.PickUsed = false;
                live.Current = text;
                ClearPartial(live);
                live.Tool = null;
                // 지난 턴의 "앱 밖에서 바뀜" 경고는 지운다 — 이번 턴에도 그렇다면 RunRoomAsync가 다시 낸다. 화면은 start에서 숨긴다.
                live.Notice = null;
                cts = live.Cts = new CancellationTokenSource();
                wrapUp = live.WrapUpCts = new CancellationTokenSource();
                // 쉬는 실행의 작업 줄 · 도는 서브에이전트 말풍선은 턴 사이에도 남긴다
                if (!Resting(live))
                {
                    live.BackgroundCount = 0;
                    live.BgTasks = [];
                    live.BackgroundUntil = null;
                    live.Subagents.Clear();
                }
                Broadcast(live, QueueEvent("start", live) with { Prompt = text });
                RestLine(live); // 화면은 턴 시작에 진행 칸을 비운다 — 쉬는 실행의 작업 줄을 다시 보인다
                SubagentLines(live);
                SetFace(live, Face.Thinking);
            }

            RunResult result;
            try
            {
                result = auto is { } a ? await AutoTurnAsync(roomId, live, a.Run, a.Wait, generation, cts.Token)
                    : await RunOneAsync(roomId, live, text!, generation, cts.Token, wrapUp.Token);
            }
            catch (Exception ex)
            {
                result = new RunResult(false, false, $"내부 오류: {ex.Message}", null, null, null, 0, 0, null);
            }

            // 창을 모르는 턴(중지 · result 없음)은 방 색인에 남은 창으로 — 화면 막대가 0%로 떨어지지 않게
            var window = result.ContextWindow ?? store.Get(roomId)?.ContextWindow;

            // 남은 승인 카드를 끝내고 그 기록 · approval-done이 끝나기를 잠깐 기다린다 — 답은 다른 스레드에서 이어지므로(RunContinuationsAsynchronously)
            // 기다리지 않으면 end가 먼저 나가 화면이 그 줄 없는 기록을 받고, 결과 줄은 진행 칸에 다음 턴까지 남는다
            auto = null;
            Task[] settling = [];
            lock (live.Gate)
            {
                // 쉬는 실행이 살아 있으면 카드를 닫지 않는다 — 쉬는 동안 백그라운드 서브에이전트가 낸 승인도 받는다. 끝나면 WatchRunAsync가 닫는다
                if (!Resting(live))
                {
                    EndApprovals(live, "실행 끝남");
                    settling = [.. live.Approvals.Values.Select(p => p.Settled.Task)];
                }
            }
            if (settling.Length > 0)
                try { await Task.WhenAll(settling).WaitAsync(ApprovalSettleWait); } catch (TimeoutException) { }

            // live.Cts를 잠금 안에서 먼저 비운 뒤 Dispose한다: Stop · Dispose가 해제된 CTS에 Cancel하지 않게.
            lock (live.Gate)
            {
                live.Cts = null;
                live.WrapUpCts = null;
                if (!Resting(live))
                {
                    live.BackgroundCount = 0;
                    live.BgTasks = [];
                    live.BackgroundUntil = null;
                    live.Subagents.Clear();
                    EndApprovals(live, "실행 끝남");
                }
                else KeepRunningSubagents(live);
                live.LastEnd = result.Ok ? Face.Done : result.Cancelled ? Face.Stopped : Face.Failed;
                live.LastEndAt = DateTimeOffset.Now;
                live.Phase = Face.Idle; // 다음 턴의 start가 생각 중으로 바꾼다
                var endText = result.Cancelled && live.Interrupted ? "끼어들기로 멈춤" : result.Error;
                live.Interrupted = false;
                Broadcast(live, new LiveEvent("end")
                {
                    Ok = result.Ok, Text = endText, CostUsd = result.CostUsd, Face = live.LastEnd.ToString(),
                    ContextTokens = result.ContextTokens, ContextWindow = window,
                    Prompt = result.NotStarted ? text : null, // 보내지 못한 말 (4차 C2) — 화면이 남기고 입력칸에 돌려준다
                });
                RestLine(live); // 화면은 턴 끝에 작업 줄을 지운다 — 쉬는 중이면 다시 보인다
                SubagentLines(live);
            }
            try { TurnEnded?.Invoke(roomId, result); } catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
            cts.Dispose();
            wrapUp.Dispose();
        }
    }

    private async Task<RunResult> RunOneAsync(string roomId, Live live, string text, int generation, CancellationToken ct, CancellationToken wrapUp = default)
    {
        if (store.Get(roomId) is not { } room) return new RunResult(false, false, "없는 방입니다.", null, null, null, 0, 0, null);
        // Codex 구성원 (②): claude 대신 codex exec — 승인 · MCP · 터미널 없음, 읽기 전용
        if (room.Engine == RoomEngines.Codex)
        {
            try { return await RunCodexAsync(room, text, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new RunResult(false, false, $"내부 오류: {ex.GetType().Name}: {ex.Message}", null, null, null, 0, 0, null);
            }
        }
        var before = transcripts.Size(room.SessionId);
        // 쉬는 실행 (백그라운드 명령): 단톡방 구성원이 아닌 방 — 쉬는 프로세스가 있으면 그리로 보낸다. 못 쓰면 끝내고 새로 띄운다
        var rest = room.GroupId is null;
        RunResult? result = null;
        if (rest)
        {
            try { result = await TryRestTurnAsync(room, live, text, generation, ct); }
            catch (OperationCanceledException) { result = Record(room, new RunResult(false, true, "중지됨", null, null, null, 0, 0, null)); }
            // 쉬는 실행을 끝냈으면 그때 적은 기록 크기(RetireAsync)를 새로 띄우기 전 검사가 쓰게 다시 읽는다. 끝나며 CLI가 쓴 줄 뒤로 before도 다시 잰다
            // (안 재면 새 실행이 말을 기록하지 못하고 실패해도 크기가 달라 '보내지 못함'이 안 붙는다)
            if (result is null && store.Get(roomId) is { } fresh)
            {
                room = fresh;
                before = transcripts.Size(room.SessionId);
            }
        }
        if (result is null)
        {
            // 앱 MCP (터미널 · 지휘자 브라우저 · 새 방 열기): 명령 · 앱 단계는 다 붙이고, 읽기 단계는 새 방 열기만 쓰게 붙인다(나머지는 실행 인자가 뺀다).
            // 단톡방 구성원 방은 예전처럼 명령 단계에만 (새 방 열기는 쓰지 않음). 이 실행 동안만 듣는 토큰 — 끝나면 거두고 설정 파일도 지운다 (쉬는 실행이면 프로세스가 끝날 때)
            var resources = new RunResources(_terminals);
            if ((ClaudeArgs.RunsCommands(room.Level) || room.GroupId is null) && _terminals is not null && !room.McpOff.Contains(TerminalMcp.Server) && SelfUrl?.Invoke() is { } self)
            {
                resources.TerminalToken = _terminals.Issue(room.Id);
                try
                {
                    resources.TerminalConfig = TerminalMcp.Write(options.Value.DataFolder, room.Id, self, resources.TerminalToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    resources.TerminalConfig = null; // 터미널 도구 없이 돈다
                }
            }
            try
            {
                result = await RunRoomAsync(room, live, text, generation, ct, wrapUp, resources, rest);
            }
            catch (Exception ex)
            {
                // 예상 밖 실패(IO 등)도 방에 남긴다 — 안 남기면 KnownSize가 낡아 다음 말에 잘못된 "앱 밖에서 바뀜" 경고가 뜬다.
                result = Record(room, new RunResult(false, false, $"내부 오류: {ex.GetType().Name}: {ex.Message}", null, null, null, 0, 0, null));
            }
            finally
            {
                if (!resources.Adopted) resources.Release();
            }
        }
        // 실패했는데 세션 기록이 그대로면 사용자 말이 기록되지 않았다 — claude 실행 파일이 없음 · 인자 오류 · 바로 끝남도 '보내지 못함'으로
        // (/self-review bd52c6f0d1 C1: 띄우기 전 검사 넷만 표시하면 그 밖의 경로에서 말이 사라졌다). 중지는 사용자가 멈춘 것이라 뺀다.
        return !result.Ok && !result.Cancelled && !result.NotStarted && transcripts.Size(room.SessionId) == before
            ? result with { NotStarted = true }
            : result;
    }

    /// <summary>Codex 실행기를 바꿀 때 (테스트: 가짜 codex). 기본은 Codex 실행 파일 + <see cref="CodexRunner.Args"/>.</summary>
    public Func<Room, IReadOnlyList<string>, string, ProcessSpec>? CodexProcessFactory { get; init; }

    /// <summary>
    /// Codex 구성원 한 턴: 설정에서 고른 작업 폴더만, 새 스레드는 규칙을 developer_instructions로, 이어 가는 스레드는 규칙이 바뀌었으면
    /// 이번 말 앞에 "규칙 바뀜" 안내(resume에서는 바꾼 지시가 먹지 않음 — 0단계 실측 ②). 이벤트 줄은 과정 기록(CodexLog)에 그대로 남긴다.
    /// </summary>
    private async Task<RunResult> RunCodexAsync(Room room, string text, CancellationToken ct)
    {
        var o = options.Value;
        static RunResult Blocked(string error) => new(false, false, error, null, null, null, 0, 0, null) { NotStarted = true };
        RunResult Refused(RunResult r)
        {
            try { store.Update(room.Id, x => x with { LastRunAt = DateTimeOffset.Now, LastError = r.Error, RunStartedAt = null }); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
            return r;
        }
        if (o.WorkspacePath(room.Workspace) is not { } workspace || !Directory.Exists(workspace))
            return Refused(Blocked($"작업 폴더 '{room.Workspace}'가 허용 목록에 없거나 없는 폴더입니다."));
        if (!o.Workspaces.ContainsKey(room.Workspace) && o.AppWorkspaces?.WhyUnsafe(workspace) is { } unsafeWhy)
            return Refused(Blocked($"작업 폴더 '{room.Workspace}'를 지금은 쓸 수 없습니다: {unsafeWhy}"));
        if (!new CodexSettings(() => o.DataFolder, o.WorkspacePath).Allowed(room.Workspace))
            return Refused(Blocked($"작업 폴더 '{room.Workspace}'에서는 Codex 구성원을 켜지 않았습니다 — 설정 화면의 'Codex'에서 고르세요."));
        var exe = o.ResolveCodex();
        if (exe is null && CodexProcessFactory is null)
            return Refused(Blocked("Codex CLI를 찾지 못했습니다 — npm i -g @openai/codex 뒤 터미널에서 codex login (또는 설정 Chat:CodexPath)."));

        var rules = CodexRunner.Rules + GroupRules.For(store, room, o.WorkspacePath);
        var hash = RoomRules.Hash(rules);
        var thread = room.CodexThread;
        var noted = thread is not null && room.RulesHash != hash;
        var prompt = noted ? RoomRules.ChangedNote(rules) + text : text;
        var args = CodexRunner.Args(workspace, string.IsNullOrWhiteSpace(room.Model) ? null : room.Model, room.Effort, rules, thread);
        var spec = CodexProcessFactory?.Invoke(room, args, workspace) ?? new ProcessSpec(exe!, args, workspace);
        var log = new CodexLog(() => o.DataFolder);
        log.Append(room.Id, CodexLog.PromptLine(text));
        try { store.Update(room.Id, r => r with { RunStartedAt = DateTimeOffset.Now }); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }

        var outcome = await CodexRunner.RunAsync(spec, prompt, line => log.Append(room.Id, CodexLog.Slim(line)), ct);
        // 규칙을 받은 때: 새 스레드가 시작됐거나(developer_instructions) 안내를 붙인 턴이 성공
        var delivered = thread is null && outcome.Thread is not null || noted && outcome.Result.Ok;
        // 이어 가던 스레드를 Codex가 찾지 못함(세션 기록이 지워짐 · 계정 바뀜 — 실측: 이벤트 없이 "no rollout found", 종료 코드 1):
        // 스레드를 잊고 다음 말부터 새로 시작한다 — 안 그러면 이 구성원은 계속 실패했다.
        // 그 문구이거나, 문구가 없어도(판이 바뀜) 이어 가기가 thread.started 없이 실패했고 그 스레드의 세션 기록이 Codex 홈에 정말 없을 때만 —
        // 시작 실패 · 설정 오류 · 시간 초과까지 잊으면 멀쩡한 대화를 버렸다 (sol 리뷰 ②-P2 · 9c54e0c54a C1).
        // 새 대화의 첫 말에는 단톡방 대화를 처음부터 다시 붙인다 (GroupChat.SendLocked — 스레드 없는 Codex 구성원)
        var failed = thread is not null && !outcome.Result.Ok && !outcome.Result.Cancelled;
        var lost = failed && (outcome.ThreadMissing
            || outcome.Ran && outcome.Thread is null && CodexRunner.RolloutExists(o.CodexHomeDir(), thread!) == false);
        var result = lost ? outcome.Result with { Error = outcome.Result.Error + " — 이어 가던 Codex 대화를 찾지 못해 다음 말부터 새 대화로 시작합니다." } : outcome.Result;
        try
        {
            store.Update(room.Id, r => r with
            {
                LastRunAt = DateTimeOffset.Now,
                LastError = result.Ok || result.Cancelled ? null : result.Error,
                RunStartedAt = null,
                CodexThread = lost ? null : outcome.Thread ?? r.CodexThread,
                RulesHash = lost ? null : delivered ? hash : r.RulesHash,
                CodexInputTokens = r.CodexInputTokens + (outcome.InputTokens ?? 0),
                CodexOutputTokens = r.CodexOutputTokens + (outcome.OutputTokens ?? 0),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    private async Task<RunResult> RunRoomAsync(Room room, Live live, string text, int generation, CancellationToken ct, CancellationToken wrapUp,
        RunResources resources, bool rest)
    {
        var terminalConfig = resources.TerminalConfig;
        var o = options.Value;
        // claude를 띄우기 전에 막힌 실패는 NotStarted — 화면이 그 말을 남기고 입력칸에 돌려준다 (4차 C2: 막힌 말이 사라졌음)
        static RunResult Blocked(string error) => new(false, false, error, null, null, null, 0, 0, null) { NotStarted = true };
        if (o.WorkspacePath(room.Workspace) is not { } workspace || !Directory.Exists(workspace))
            return Record(room, Blocked($"작업 폴더 '{room.Workspace}'가 허용 목록에 없거나 없는 폴더입니다."));
        // 앱에서 더한 작업 폴더는 실행 전에 다시 검사 — 더한 뒤 링크로 바뀌었을 수 있다 (3차 d, /self-review security eee1adb777 C5)
        if (!o.Workspaces.ContainsKey(room.Workspace) && o.AppWorkspaces?.WhyUnsafe(workspace) is { } unsafeWhy)
            return Record(room, Blocked($"작업 폴더 '{room.Workspace}'를 지금은 쓸 수 없습니다: {unsafeWhy}"));

        var size = transcripts.Size(room.SessionId);
        var resume = size is not null;
        // 갈래 첫 실행: 원래 방이 턴 도중이면(기록을 쓰는 중) 갈라지지 않게 기다리게 한다. 갈래 표시는 남는다.
        if (!resume && room.ForkOf is { } source
            && store.All().FirstOrDefault(r => string.Equals(r.SessionId, source, StringComparison.OrdinalIgnoreCase)) is { } sourceRoom
            && (IsBusy(sourceRoom.Id) || HasRestingTasks(sourceRoom.Id)))
            return Record(room, Blocked("원래 방이 실행 중입니다 — 그 턴이 끝난 뒤 다시 보내세요 (갈래는 그때까지의 대화를 이어받습니다)."));
        if (resume && room.KnownSize is { } known && known != size)
        {
            // 앞 실행이 끝을 남기지 못했으면(앱 종료) 그 실행이 쓴 것이다. 아니면 다른 곳(Claude 앱 · CLI)에서
            // 같은 세션을 이어 쓴 것으로 보인다 — 갈래가 생겼을 수 있다.
            var notice = room.RunStartedAt is not null
                ? "앞 실행이 앱 종료로 끊겼습니다 — 그 턴의 답은 없거나 일부만 남았을 수 있습니다."
                : $"세션 기록이 이 앱 밖에서 바뀌었습니다 ({known:N0} → {size:N0}바이트). 다른 곳에서 이 세션을 이어 쓰면 대화가 갈라질 수 있습니다.";
            lock (live.Gate)
            {
                live.Notice = notice;
                Broadcast(live, new LiveEvent("notice") { Text = notice });
            }
        }

        string? mcpConfig = null, roomConfig = null, unityName = null;
        IReadOnlyList<string> extraServers = [];
        // 방별 연결: 더한 서버 · 끈 기본 서버 · Unity가 없으면 예전처럼 작업 폴더별 공용 파일
        (string Cli, string Project)? unity = null;
        if (ClaudeArgs.RunsCommands(room.Level) && room.UnityProject is { } unityProject)
        {
            var why = o.UnityProjectPath(unityProject) is not { } projectPath ? $"Unity 프로젝트 '{unityProject}'가 설정에 없습니다"
                : !o.UnityProjects.ContainsKey(unityProject) && o.AppUnityProjects?.WhyUnusable(projectPath) is { } unsafeUnity ? $"Unity 프로젝트 '{unityProject}'를 지금은 쓸 수 없습니다: {unsafeUnity}"
                : o.ResolveUnityCli() is not { } cli ? "Unity CLI(unity.exe)를 찾지 못했습니다 — 설정 Chat:UnityCliPath"
                : null;
            if (why is null)
            {
                unity = (o.ResolveUnityCli()!, o.UnityProjectPath(unityProject)!);
                unityName = unityProject;
            }
            else Notice(live, why + " — 이번 실행은 Unity 연결 없이 돕니다.");
        }
        var baseOn = !room.McpOff.Contains(o.McpServer);
        try
        {
            if (baseOn && room.McpExtra.Count == 0 && unity is null)
                mcpConfig = McpConfigFile.Write(workspace, o.McpServer, o.DataFolder);
            else
            {
                var servers = room.McpExtra.Where(s => s != o.McpServer && ClaudeArgs.IsServerName(s)).ToList();
                if (baseOn) servers.Insert(0, o.McpServer);
                // 작업 폴더 .mcp.json에 없는 이름은 앱에서 더한 서버에서
                var (path, missing) = McpConfigFile.WriteRoom(workspace, servers, unity, o.DataFolder, room.Id, o.AppMcp is { } app ? app.Get : null);
                mcpConfig = roomConfig = resources.RoomConfig = path;
                if (baseOn && missing.Contains(o.McpServer))
                    throw new InvalidOperationException($"{Path.Combine(workspace, ".mcp.json")}에 mcpServers.{o.McpServer} 항목이 없습니다.");
                if (missing.Count > 0) Notice(live, $"작업 폴더 .mcp.json에도 앱 서버 목록에도 없는 연결은 빼고 돕니다: {string.Join(", ", missing)}");
                var allowed = servers.Where(s => s != o.McpServer && !missing.Contains(s)).ToList();
                if (unity is not null) allowed.Add(McpConfigFile.UnityServer);
                extraServers = allowed;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (roomConfig is not null) McpConfigFile.DeleteRoom(roomConfig);
            return Record(room, Blocked(ex.Message));
        }
        // 방 MCP 설정 파일은 실행 자원과 같이 정리한다 (RunOneAsync, 쉬는 실행이면 프로세스가 끝날 때)
        return await RunWithConfigAsync(room, live, text, generation, ct, wrapUp, terminalConfig, workspace, resume, mcpConfig, extraServers, unityName, resources, rest);
    }

    /// <summary>
    /// 실행 시작(init)의 MCP 서버 상태 · MCP 도구 이름을 방에 남기고 화면에 "mcp" 알림 — 연결 탭이 첫 턴 도중에도, 앱을 다시 켠 뒤에도 상태를 보인다
    /// (사용자 확인 2026-10-29). 저장에 실패해도 실행은 그대로.
    /// </summary>
    private void KeepMcpStatus(string roomId, Live live, int generation, InitEvent init)
    {
        try
        {
            store.Update(roomId, r => r with
            {
                McpStatus = [.. init.McpServers.Select(s => new McpServerState(s.Name, s.Status))],
                McpTools = [.. init.Tools.Where(t => t.StartsWith("mcp__", StringComparison.Ordinal))],
                McpStatusAt = DateTimeOffset.Now,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        lock (live.Gate)
        {
            if (live.Generation == generation) Broadcast(live, new LiveEvent("mcp"));
        }
    }

    /// <summary>hb-orchestration run_mode 도구 (MCP 서버 이름은 설정마다 다를 수 있어 끝 이름으로 본다).</summary>
    internal static bool IsRunModeTool(string name) => name.StartsWith("mcp__", StringComparison.Ordinal) && name.EndsWith("__run_mode", StringComparison.Ordinal);

    /// <summary>
    /// 외부 말풍선을 새로 받을 때인가: 기다리던 run_mode 호출의 결과가 왔을 때(받은 id는 지운다).
    /// 결과 대신 "백그라운드로 넘어감" 안내가 왔으면 그 작업 id만 기억했다가, 그 작업이 끝날 때 알린다 —
    /// 다른 백그라운드 작업(Bash · 서브에이전트) 끝에는 알리지 않는다.
    /// </summary>
    internal static bool RunResultArrived(StreamEvent ev, HashSet<string> runUses, HashSet<string> runTasks)
    {
        switch (ev)
        {
            case ToolImagesEvent results:
                var arrived = false;
                foreach (var id in results.ToolUseIds)
                {
                    if (!runUses.Remove(id)) continue;
                    if (results.BackgroundTasks.TryGetValue(id, out var task)) runTasks.Add(task);
                    else arrived = true;
                }
                return arrived;
            case TaskEvent { Phase: "done" } done:
                return runTasks.Remove(done.TaskId);
            default:
                return false;
        }
    }

    /// <summary>Unity 캡처 도구 이름 앞부분 — 명령마다 도구일 때 capture_game_view · capture_scene_view · capture_editor_window.</summary>
    public const string UnityCapturePrefix = "mcp__" + McpConfigFile.UnityServer + "__capture_";

    /// <summary>Unity 캡처 묶음 도구 (<c>--tool-groups</c>) — 입력 command가 game_view · scene_view · editor_window.</summary>
    public const string UnityCaptureGroup = "mcp__" + McpConfigFile.UnityServer + "__capture";

    /// <summary>
    /// Unity 캡처 결과 그림을 방 기록에 남긴다 (결정 3 "capture_game_view로" · 2026-11-14 "셋 다"): assistant 줄의 도구 호출 id를 기억했다가
    /// 같은 id의 tool_result 그림을 RoomShots에 쓰고 기록 줄("Unity capture_game_view" 등)을 단다. 남기지 못해도 실행은 그대로.
    /// </summary>
    internal void KeepUnityShots(string roomId, StreamEvent ev, Dictionary<string, string> captures)
    {
        if (ev is UsageEvent u)
        {
            foreach (var tool in u.Tools)
            {
                if (tool.Id is not { Length: > 0 } id) continue;
                if (tool.Name == UnityCaptureGroup && tool.Command is { Length: > 0 and <= 40 } command && command.All(c => char.IsAsciiLetterLower(c) || c == '_'))
                    captures[id] = "capture_" + command;
                else if (tool.Name.StartsWith(UnityCapturePrefix, StringComparison.Ordinal))
                    captures[id] = tool.Name[(UnityCapturePrefix.Length - "capture_".Length)..];
            }
            return;
        }
        if (ev is not ToolImagesEvent images) return;
        // 결과 하나에 그림이 여러 장이어도 다 남긴다 — id는 이 줄을 다 본 뒤 지운다
        var matched = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in images.Images)
            if (captures.Remove(image.ToolUseId, out var tool)) matched[image.ToolUseId] = tool;
        foreach (var image in images.Images)
        {
            if (!matched.TryGetValue(image.ToolUseId, out var name) || RoomShots.Ext(image.MediaType) is not { } ext) continue;
            try
            {
                var file = RoomShots.Save(options.Value.DataFolder, roomId, Convert.FromBase64String(image.Base64), ext);
                Log?.Write(roomId, new ApprovalRequest(RoomNotes.UnityShotTool, file, null), "Unity " + name);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
    }

    /// <summary>방 칸 알림 한 줄 (잠금 안에서 보낸다).</summary>
    private void Notice(Live live, string text)
    {
        lock (live.Gate)
        {
            live.Notice = text;
            Broadcast(live, new LiveEvent("notice") { Text = text });
        }
    }

    private async Task<RunResult> RunWithConfigAsync(Room room, Live live, string text, int generation, CancellationToken ct, CancellationToken wrapUp,
        string? terminalConfig, string workspace, bool resume, string? mcpConfig, IReadOnlyList<string> extraServers, string? unityName,
        RunResources resources, bool rest)
    {
        var o = options.Value;
        // 놀이터 구성원 (부모를 못 찾아도 놀이터로 — 일반 명령 권한으로 돌아가지 않게, 설계 검토 b420efd860 R3)
        var playground = GroupChat.IsPlaygroundMember(store, room);
        if (playground && room.Level != PermissionLevel.Command)
            return Record(room, new RunResult(false, false, "놀이터 구성원은 명령 단계로만 돕니다 — 이 방 단계가 바뀌었습니다.", null, null, null, 0, 0, null) { NotStarted = true });

        // 그 방 첨부 폴더: 열어도 되는 폴더일 때만 (링크 · .claude가 있으면 열지 않고 알림 — /self-review 0c3b7e3514 C2 · C3)
        string? attachDir = null;
        var folder = Attachments.Folder(o.DataFolder, room.Id);
        // 쉬는 실행: 첨부 폴더를 미리 만든다 — 쉬는 동안 첫 첨부로 폴더가 생기면 인자(--add-dir)가 달라져 작업을 끝내야 했다
        if (rest && !Directory.Exists(folder))
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (Attachments.WhyNotOpen(folder) is not { } why) attachDir = folder;
        else if (why != "없음")
            lock (live.Gate)
            {
                live.Notice = why;
                Broadcast(live, new LiveEvent("notice") { Text = why });
            }
        // 갈래 방: 이어받은 방들의 첨부 폴더도 읽기로 (편집 금지는 ClaudeArgs가 같이 단다). 열 수 없는 · 없는 폴더는 조용히 뺀다 (원래 방이 지워졌을 수 있음)
        IReadOnlyList<string> inheritedDirs = [.. room.AttachFrom.Where(RoomStore.IsRoomId).Select(id => Attachments.Folder(o.DataFolder, id)).Where(f => Attachments.WhyNotOpen(f) is null)];
        // 명령 방: 편집 금지 규칙에 쓸 수 없는 글자가 든 경로면 그 첨부 폴더는 열지 않는다 — 금지가 안 걸린 채 열리는 것보다 닫는 쪽
        //. 데이터 폴더 경로에서 오므로 다 같이 걸린다
        if (ClaudeArgs.RunsCommands(room.Level)
            && (attachDir is not null && !ClaudeArgs.IsRuleSafePath(attachDir) || inheritedDirs.Any(d => !ClaudeArgs.IsRuleSafePath(d))))
        {
            attachDir = attachDir is not null && ClaudeArgs.IsRuleSafePath(attachDir) ? attachDir : null;
            inheritedDirs = [.. inheritedDirs.Where(ClaudeArgs.IsRuleSafePath)];
            Notice(live, "데이터 폴더 경로에 괄호 · * ? [ ] { } 글자가 있어 이번 실행에서는 첨부를 열지 않습니다 (편집 금지 규칙에 쓸 수 없음) — 설정 Chat:DataDir을 바꾸세요.");
        }

        // 실행 하나 동안 같은 값 — 규칙 글과 ask 규칙이 어긋나지 않게 한 번 읽는다
        var askWrites = o.AskAppWrites;
        // 규칙 재료는 쉬는 실행이 이어 쓰는 턴에서도 같은 규칙을 만들게 남긴다 (RulesFor — 기본 서버 · 스티커 · 단톡방 규칙 포함)
        var setup = new RunSetup(Terminal: terminalConfig is not null, Unity: unityName, Extras: extraServers, McpOn: mcpConfig is not null,
            AskWrites: room.Level == PermissionLevel.App && askWrites, Workspace: workspace, AppAskWrites: askWrites);
        var rules = RulesFor(room, setup);
        // 이어 쓰는 세션은 CLI가 압축 전까지 처음 시스템 프롬프트를 쓴다 (단계 실측: resume에 넣은 새 규칙은 안 보임).
        // 지휘자가 마지막으로 받은 규칙과 다르면 이번 말 앞에 새 규칙을 붙인다 — 갈래 첫 실행 · 이 칸 전에 만든 방도 한 번 받는다.
        // '/명령'은 앞에 글이 붙으면 CLI가 명령으로 알아보지 못한다 — 그 말에는 붙이지 않고 다음 말로 미룬다.
        // 지문은 규칙을 실은 실행(새 세션 = 시스템 프롬프트, 또는 안내를 붙임)이 세션 기록을 늘렸을 때만 저장한다 — init 뒤 기록 전에 실패하면 다시 붙게
        var rulesHash = RoomRules.Hash(rules);
        var continued = resume || room.ForkOf is not null;
        var noted = continued && room.RulesHash != rulesHash && !text.TrimStart().StartsWith('/');
        var rulesDelivered = !continued || noted;
        var prompt = noted ? RoomRules.ChangedNote(rules) + text : text;
        // stdin 한 줄 상한: 한 번에 써도 약 300KB 줄에서는 CLI가 시작조차 하지 않았다(131KB 정상, 2026-10-29 실측) — 띄우기 전에 막고 말은 입력칸으로
        // 쉬는 실행은 uuid가 붙은 줄을 쓴다 — 그 모양으로 잰다
        if (System.Text.Encoding.UTF8.GetByteCount(ClaudeRunner.UserLine(prompt, Guid.Empty.ToString("D"))) is var lineBytes && lineBytes > ClaudeRunner.MaxStdinLineBytes)
            return Record(room, new RunResult(false, false,
                $"말이 너무 깁니다 ({lineBytes / 1024:N0}KB, {ClaudeRunner.MaxStdinLineBytes / 1024}KB까지) — 긴 글은 파일로 첨부해 주세요.", null, null, null, 0, 0, null)
            {
                NotStarted = true,
            });
        var sizeBefore = transcripts.Size(room.SessionId) ?? -1;
        ModePick? runPick;
        lock (live.Gate) runPick = live.CurrentPick;
        var spec = new RunSpec(room.Level, room.Model, room.SessionId, resume, mcpConfig, o.McpServer, rules)
        {
            ExtraMcpServers = extraServers, // 방이 더한 서버 · unity는 묻지 않고
            ForkFrom = resume ? null : room.ForkOf, // 갈래 방의 첫 실행 (2-2 c)
            // 그 방 첨부 폴더가 있으면 Read로 열 수 있게 (2-2 d)
            AttachDir = attachDir,
            InheritedAttachDirs = inheritedDirs, // 갈래 방: 원래 방들의 첨부
            Effort = room.Effort, // 곁칸에서 바꾸면 다음 실행부터 (Claude 앱 기능 B)
            Plan = room.PlanNext && ClaudeArgs.PlanMode(room.Level) is not null && !playground, // "계획 먼저" (C) — 명령 단계만
            Playground = playground, // 놀이터 구성원: 판 폴더 안 편집 · 웹 검색 · 웹 페이지 읽기
            AskRunMode = room.AskRunMode || runPick is not null, // 외부 요청 전에 묻기 · + 요청 창 말
            ProtectedWorkspaces = room.Level == PermissionLevel.App ? [.. o.AllWorkspaces.Select(w => w.Path)] : [],
            AskWrites = room.Level == PermissionLevel.App && askWrites, // 앱 단계 폴더 밖 쓰기 묻기 (설정 · 규칙 글과 같은 값)
            TerminalMcpConfigPath = terminalConfig, // 명령 방: 앱 MCP(터미널 도구)
        };
        ProcessSpec process;
        try
        {
            process = ProcessFactory?.Invoke(room, spec, workspace)
                      ?? new ProcessSpec(o.ResolveClaude(), ClaudeArgs.Build(spec, [.. o.ModelList]), workspace);
            // 서브에이전트 강도 (claude 2.1.316) — 시험용 ProcessFactory에도 붙여 같은 길을 본다
            if (ClaudeArgs.RunEnvironment(spec.Level, o.SubagentEffort) is { Count: > 0 } env)
            {
                var merged = new Dictionary<string, string>(process.Environment);
                foreach (var (k, v) in env) merged[k] = v;
                process = process with { Environment = merged };
            }
        }
        catch (ArgumentException ex)
        {
            return Record(room, new RunResult(false, false, ex.Message, null, null, null, 0, 0, null));
        }

        store.Update(room.Id, r => r with { RunStartedAt = DateTimeOffset.Now });
        using var stopOnMismatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 화면 처리: 쉬는 실행이면 프로세스가 사는 동안(다음 턴 · 자동 턴) 같은 처리를 쓴다
        var state = new RunView(room, live, generation, unityName, stopOnMismatch);
        Action<StreamEvent> view = ev => ShowRunEvent(state, ev);
        Func<ToolPermission, CancellationToken, Task<string>> decide =
            (ask, aborted) => DecideToolAsync(room, live, generation, ask, aborted, extraServers, unityName, workspace, spec.AskWrites);
        var result = rest
            ? await StartRunAsync(room.Id, live, generation, RunKey(room), spec.Plan, spec.AskRunMode, setup, room, process, prompt, view, decide, resources, r => state.StartedRun = r,
                stopOnMismatch.Token)
            : await ClaudeRunner.RunAsync(process, prompt, view, decide, stopOnMismatch.Token, wrapUp);

        // 턴 끝에 한 번 더 — 마지막 변경이 출력 뒤에 써졌거나 중지된 경우 (실제 확인). 중지는 세대를 올리므로 세대 · 간격 검사 없이.
        if (RefreshSteps(room, live, generation, turnEnd: true) is { } last)
            lock (live.Gate) Broadcast(live, new LiveEvent("steps") { Steps = last });
        lock (live.Gate) live.StepsWatch = false;

        if (state.Mismatch is not null) result = result with { Ok = false, Cancelled = false, Error = state.Mismatch };
        // 규칙을 실은 말이 세션 기록에 들어갔으면 지휘자가 받은 규칙으로 적는다 (위 noted · rulesDelivered)
        if (rulesDelivered && state.Mismatch is null && (transcripts.Size(room.SessionId) ?? -1) > sizeBefore)
        {
            try { store.Update(room.Id, r => r with { RulesHash = rulesHash }); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { } // 다음 말에 다시 붙을 뿐
        }
        // 답 뒤 백그라운드 기다림을 정리했으면 방 기록 줄로 남긴다 — 기록 화면이 그 자리에 그린다 ("끝남"이 몇 초뿐이던 것, 사용자 확인 2026-10-29)
        if (BackgroundNote(result) is { } note) Log?.Note(room.Id, RoomNotes.BackgroundTool, note);
        return Record(room, result, forked: spec.ForkFrom is not null && result.Ok);
    }

    /// <summary>실행 하나의 화면 처리(<see cref="ShowRunEvent"/>)가 이벤트 사이에 들고 가는 것.</summary>
    private sealed class RunView(Room room, Live live, int generation, string? unityName, CancellationTokenSource stopOnMismatch)
    {
        public readonly Room Room = room;
        public readonly Live Live = live;
        public readonly int Generation = generation;
        public readonly string? UnityName = unityName;
        public readonly CancellationTokenSource StopOnMismatch = stopOnMismatch;
        /// <summary>CLI가 다른 세션으로 시작했을 때의 까닭 — 턴 끝에 결과 오류로 싣는다.</summary>
        public string? Mismatch;
        /// <summary>쉬는 실행으로 띄웠으면 그 실행 (세션이 다르면 까닭을 남기고 멈춘다 — 이어 쓰는 턴 결과에도 실리게).</summary>
        public RoomRun? StartedRun;
        /// <summary>Unity 캡처 도구 호출 id → 이름 (결과 그림을 기록에 남길 것만, 결정 3).</summary>
        public readonly Dictionary<string, string> Captures = new(StringComparer.Ordinal);
        /// <summary>도구 호출 id → 대상 (CLI가 거절한 호출의 기록 줄에 파일 · 명령을 보인다).</summary>
        public readonly Dictionary<string, string?> ToolTargets = new(StringComparer.Ordinal);
        /// <summary>결과를 기다리는 run_mode 호출 id.</summary>
        public readonly HashSet<string> RunUses = new(StringComparer.Ordinal);
        /// <summary>백그라운드로 넘어간 run_mode의 작업 id.</summary>
        public readonly HashSet<string> RunTasks = new(StringComparer.Ordinal);
    }

    /// <summary>실행이 낸 이벤트 하나를 화면 · 방 상태에 반영한다 (<see cref="RunWithConfigAsync"/>의 view).</summary>
    private void ShowRunEvent(RunView view, StreamEvent ev)
    {
        var (room, live, generation) = (view.Room, view.Live, view.Generation);
        if (ev is InitEvent init && init.SessionId is { } sid && !string.Equals(sid, room.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            // CLI가 다른 세션으로 이어 갔다 — 이 방의 세션이 아니므로 멈춘다 (CodePet NativeSessionLink.observe와 같음).
            view.Mismatch = $"CLI가 다른 세션({sid})으로 시작해 멈췄습니다. 이 방의 세션은 {room.SessionId}입니다.";
            try { view.StopOnMismatch.Cancel(); } catch (ObjectDisposedException) { } // 쉬는 실행의 다음 턴이면 이 턴의 토큰은 이미 없다
            if (view.StartedRun is { } sr)
            {
                sr.StopReason = view.Mismatch;
                sr.Proc.Stop();
            }
            return;
        }
        // 입력칸 '/' 자동 완성 (Claude 앱 기능 A): 이 방 실행이 알려 준 명령 · 스킬 · 플러그인을 기억한다 (앱을 다시 켜면 첫 실행 전에는 없음)
        if (ev is InitEvent commands)
        {
            _commandSets[room.Id] = commands;
            KeepMcpStatus(room.Id, live, generation, commands);
            if (Cli?.Note(commands.Version) == true) TouchAll(); // CLI 판이 바뀌면 사이드바 띠
        }
        if (view.UnityName is not null) KeepUnityShots(room.Id, ev, view.Captures);
        if (ev is RateLimitEvent limits) Limits?.SaveClaude(limits); // 사이드바 한도 줄은 목록을 다시 그릴 때 읽는다
        if (ev is UsageEvent calls)
            foreach (var call in calls.Tools)
            {
                if (call.Id is { } callId) view.ToolTargets[callId] = call.Detail;
                if (call.Id is { } runCall && IsRunModeTool(call.Name)) view.RunUses.Add(runCall);
            }
        var runArrived = RunResultArrived(ev, view.RunUses, view.RunTasks);
        if (ev is DeniedEvent denied) CliDenied(room, live, generation, denied, view.ToolTargets);
        // 할 일 도구를 쓴 뒤의 출력이면 그 사이 도구가 돌아 파일이 바뀌었을 수 있다 — 잠금 밖에서 읽는다 (3차 b).
        // 잦은 stream_event(글 · 도구 입력 · 생각 조각)에서는 읽지 않고, 300ms 안에 다시 읽지 않는다 — 마지막 상태는 턴 끝 읽기가 맞춘다.
        var fragment = ev is DeltaEvent or TextBlockStartEvent || (ev is OtherEvent other && other.Type.StartsWith("stream_event", StringComparison.Ordinal));
        var steps = fragment ? null : RefreshSteps(room, live, generation);
        lock (live.Gate)
        {
            if (live.Generation != generation) return;
            if (steps is not null) Broadcast(live, new LiveEvent("steps") { Steps = steps });
            // 외부 에이전트 말풍선은 세션 기록 · 호출 기록에서 그린다 — 결과가 온 순간 화면이 새 말풍선만 받아 가게 알린다
            if (runArrived) Broadcast(live, new LiveEvent("runs"));
            if (ev is DeltaEvent) SetFace(live, Face.Writing);
            else if (ev is ToolEvent te) SetFace(live, Face.Tool, te.Name); // 화면은 표정을 face 알림으로만 바꾼다
            // 칩은 tool_use 입력이 다 온 assistant 줄에서 — 대상(파일 이름 · 명령 첫 줄)까지 보인다 (3차 b, 0번 실측 C)
            if (ev is UsageEvent)
            {
                // assistant 줄 = 여기까지의 글과 이 턴의 말이 세션 기록에 있다
                live.PartialLogged = live.Partial.Length;
                live.TurnLogged = true;
            }
            if (ev is UsageEvent u)
                foreach (var tool in u.Tools)
                {
                    Broadcast(live, SetTool(live, tool));
                    LogAppCall(room, tool);
                    if (tool.Name is "TaskCreate" or "TaskUpdate") live.StepsWatch = true; // 목록을 바꾸는 도구만
                }
            if (ev is SubagentToolsEvent subagent)
            {
                foreach (var tool in subagent.Tools) LogAppCall(room, tool);
                SubagentSaidLines(live, subagent);
            }
            if (ev is TaskEvent task) TrackSubagent(live, task);
            if (ev is UsageEvent blocks)
            {
                // 서버가 요약한 중간 글 — Partial에 넣지 않는다: 일반 방은 다시 열면 세션 기록으로 그려진다 (단톡방은 ReplayLive의 also로 다시 보낸다)
                foreach (var said in blocks.Narrations) Broadcast(live, new LiveEvent("narration") { Text = said });
                foreach (var text in blocks.Texts)
                    try { TextBlock?.Invoke(live.Id, text); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            }
            var shown = ev switch
            {
                DeltaEvent d => AppendPartial(live, d.Text),
                TextBlockStartEvent => BlockGap(live),
                RetryEvent r => new LiveEvent("retry") { Text = $"API 재시도 {r.Attempt}회 ({r.Error})" },
                StatusEvent s => new LiveEvent("status") { Status = new RoomStatus(s.Category, s.Detail, s.NeedsAction) },
                TaskEvent t => new LiveEvent("agent") { Task = ShownTask(live, t) }, // 서브에이전트 · 백그라운드 명령 진행 줄 (Claude 앱 기능 D)
                BackgroundWaitEvent b => Background(live, b), // 메인 답 뒤 기다림 — "지금 끝내기" 줄
                _ => null,
            };
            if (shown is not null) Broadcast(live, shown);
        }
    }

    /// <summary>
    /// 정리 표시 글 (방 기록 줄). 정리하지 않았으면 null. 남은 작업이 0개면 작업이 다 끝나고 지휘자가 이어 답하던 중이다.
    /// "stop"은 중지 · 끼어들기 둘 다라 "멈춰"로.
    /// </summary>
    public static string? BackgroundNote(RunResult r) => (r.BackgroundClosedBy, r.BackgroundLeft > 0) switch
    {
        ("wrapup", true) => $"백그라운드 작업 {r.BackgroundLeft}개를 '지금 끝내기'로 정리했습니다",
        ("wrapup", false) => "지휘자가 이어 답하던 중 '지금 끝내기'로 정리했습니다",
        ("cap", true) => $"기다림 상한이 지나 백그라운드 작업 {r.BackgroundLeft}개를 정리했습니다",
        ("cap", false) => "지휘자가 이어 답하던 중 기다림 상한이 지나 정리했습니다",
        ("stop", true) => $"기다리는 중 멈춰 백그라운드 작업 {r.BackgroundLeft}개를 정리했습니다",
        ("stop", false) => "지휘자가 이어 답하던 중 멈췄습니다",
        ("settings", true) => $"방 설정이 바뀌어 백그라운드 작업 {r.BackgroundLeft}개를 정리했습니다",
        _ => null,
    };

    /// <summary>
    /// 기다림 상태를 기억하고(snapshot) 알림을 만든다. 정리 시각은 처음 알린 값을 실행 끝까지 지킨다 — 작업 수만 바뀐 알림은 Until이 없고,
    /// 수가 0이어도 두 번째 답을 기다리는 동안은 기다림 상태다. 실행이 끝나면 LoopAsync가 지운다. 잠금 안에서 부른다.
    /// </summary>
    private static LiveEvent Background(Live live, BackgroundWaitEvent b)
    {
        live.BackgroundCount = b.Count;
        if (b.Until is { } until) live.BackgroundUntil = until;
        return new LiveEvent("background") { BackgroundCount = b.Count, BackgroundUntil = live.BackgroundUntil };
    }

    private int? ForkReadUpTo(string sessionId) => RoomList.ReadUpToBeforeLastUser(transcripts.Read(sessionId));

    /// <summary>실행 결과를 방에 남긴다. 세션 기록 크기는 이 앱이 쓴 뒤의 값으로 (다음 말 때 밖에서 바뀌었는지 볼 기준).</summary>
    private RunResult Record(Room room, RunResult result, bool forked = false)
    {
        // 앱을 끄느라 끊긴 실행은 사용자 중지처럼 적지 않는다 — RunStartedAt · 세션 기록 크기를 그대로 둬 다음 말에 "앞 실행이 앱 종료로 끊겼습니다"가 뜨게
        // (강제 종료와 같은 길, 서버 재시작 실측 2026-10-30: 정상 종료면 중지로 적혀 아무 안내가 없었다)
        if (_disposed && result.Cancelled) return result;
        var size = transcripts.Size(room.SessionId);
        var forkRead = forked ? ForkReadUpTo(room.SessionId) : null;
        try
        {
            store.Update(room.Id, r => r with
            {
                LastRunAt = DateTimeOffset.Now,
                // 갈래는 한 번만: 성공했거나 이 방의 세션 기록이 생겼으면(중지돼도 CLI가 이미 갈라 썼다) 다음부터는 resume이다
                ForkOf = forked || size is not null ? null : r.ForkOf,
                // 갈래가 끝나면 이어받은 말은 읽은 것으로 — 이번 턴의 답만 안 읽음에 세진다
                ReadUpTo = forkRead is { } f && r.ReadUpTo is null ? f : r.ReadUpTo,
                // 사용자가 멈춘 실행은 실패가 아니다 — 목록의 "실패" 표시 · 방의 "마지막 실행 실패" 줄에 남기지 않는다 (2-2 a 실제 확인에서 발견).
                LastError = result.Ok || result.Cancelled ? null : result.Error,
                TotalCostUsd = result.CostUsd ?? r.TotalCostUsd,
                ContextTokens = result.ContextTokens ?? r.ContextTokens,
                ContextWindow = result.ContextWindow ?? r.ContextWindow,
                KnownSize = size ?? r.KnownSize,
                RunStartedAt = null,
                // 지휘자 상태 한 줄은 이번 턴 것으로 — 없으면(실패 · 중지 등) 앞 것을 그대로 (3차 b)
                Status = result.Status is { } s ? new RoomStatus(s.Category, s.Detail, s.NeedsAction) : r.Status,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 방 색인이 깨졌거나 저장하지 못했다 — 결과는 화면에 그대로 보낸다 (RoomStore는 저장 실패 때 메모리도 되돌린다).
        }
        return result;
    }

    private static LiveEvent AppendPartial(Live live, string text)
    {
        live.Partial.Append(text);
        if (live.Partial.Length > MaxPartialChars)
        {
            var cut = live.Partial.Length - MaxPartialChars;
            live.Partial.Remove(0, cut);
            live.PartialLogged = Math.Max(0, live.PartialLogged - cut);
        }
        return new LiveEvent("delta") { Text = text };
    }

    /// <summary>
    /// 글 블록 사이를 빈 줄로 띄운다 — 진행 중 말풍선에서 블록이 "…주세요.진행 칸…"처럼 붙어 긴 작업을 읽기 어려웠다 (사용자 보고 2026-10-30).
    /// 끝난 턴은 세션 기록으로 다시 그려 블록이 나뉜다.
    /// </summary>
    private static LiveEvent? BlockGap(Live live) =>
        live.Partial.Length == 0 || live.Partial[live.Partial.Length - 1] == '\n' ? null : AppendPartial(live, "\n\n");

    private static void ClearPartial(Live live)
    {
        live.Partial.Clear();
        live.PartialLogged = 0;
        live.TurnLogged = false;
    }

    /// <summary>
    /// 앱 단계의 도구 호출을 승인 기록에 남긴다 (서브에이전트 호출 포함). 호출 시점의 기록이다 — 판정이 거절한 호출도 남고, 거절은 denied 줄로 따로 남는다.
    /// 읽기 · 할 일 · 질문 · 계획은 뺀다.
    /// </summary>
    private void LogAppCall(Room room, ToolUse tool)
    {
        if (room.Level == PermissionLevel.App && !ClaudeRunner.ReadTools.Contains(tool.Name)
            && tool.Name is not (ClaudeArgs.AskTool or ApprovalRequest.PlanTool or "TaskCreate" or "TaskGet" or "TaskList" or "TaskUpdate"))
            Log?.Write(room.Id, new ApprovalRequest(tool.Name, tool.Detail ?? "", null), AppCallLabel);
    }

    private static LiveEvent SetTool(Live live, ToolUse tool)
    {
        live.Tool = tool;
        return new LiveEvent("tool") { Text = tool.Name, Detail = tool.Detail };
    }

    /// <summary>할 일 폴더를 실행 중 다시 읽는 최소 간격.</summary>
    private static readonly TimeSpan StepsReadGap = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 이번 턴에 할 일 도구를 썼으면(StepsWatch) 할 일 목록을 다시 읽어, 바뀌었으면 새 목록을 돌려준다 (잠금 밖에서 파일을 읽고 잠금 안에서 바꾼다).
    /// 실행 중에는 그 세대일 때만 · 간격(<see cref="StepsReadGap"/>)을 두고, 턴 끝(turnEnd)은 둘 다 보지 않는다. 바뀐 것이 없으면 null.
    /// </summary>
    private IReadOnlyList<StepItem>? RefreshSteps(Room room, Live live, int generation, bool turnEnd = false)
    {
        lock (live.Gate)
        {
            if (!live.StepsWatch) return null;
            if (!turnEnd && (live.Generation != generation || DateTimeOffset.Now - live.StepsReadAt < StepsReadGap)) return null;
            live.StepsReadAt = DateTimeOffset.Now;
        }
        var fresh = Steps.Read(options.Value.TasksRoot, room.SessionId);
        lock (live.Gate)
        {
            if (live.Steps is { } before && before.SequenceEqual(fresh)) return null;
            live.Steps = fresh;
            return fresh;
        }
    }

    /// <summary>
    /// 목록에 보일 진행 글 ("2/5") — 실행 중이고 이번 턴에 할 일 도구를 썼을 때만 (3차 b). 지난 턴의 끝난 목록이 진행처럼 보이지 않게.
    /// </summary>
    public string? StepProgress(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return null;
        lock (live.Gate) return live.Busy && live.StepsWatch && live.Steps is { } steps ? Steps.Progress(steps) : null;
    }

    private void Broadcast(Live live, LiveEvent ev)
    {
        foreach (var ch in live.Subscribers) ch.Writer.TryWrite(ev);
        if (CardKinds.Contains(ev.Kind))
            try { CardChanged?.Invoke(live.Id, ev); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        if (StreamKinds.Contains(ev.Kind))
            try { Streamed?.Invoke(live.Id, ev); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        if (ListKinds.Contains(ev.Kind))
        {
            Touch(live.Id);
            try { ActivityChanged?.Invoke(live.Id, ev.Kind); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }
    }

    /// <summary>
    /// 방 실행 상태가 바뀜 (시작 · 끝 · 승인 · 질문 …, 방 목록 신호와 같은 때) — 단톡방이 구성원 진행 줄을 갱신한다.
    /// 방 잠금 안에서 부르므로 받는 쪽은 오래 걸리는 일을 넘겨서 할 것.
    /// </summary>
    public event Action<string, string>? ActivityChanged;

    /// <summary>한 턴이 끝남 (end 알림 뒤, 잠금 밖) — 단톡방이 구성원 답을 대화 줄에 올린다.</summary>
    public event Action<string, RunResult>? TurnEnded;

    private static readonly HashSet<string> CardKinds = ["approval", "approval-done", "question", "question-done", "trust"];

    /// <summary>
    /// 승인 · 질문 카드 알림 (나옴 · 끝남) — 단톡방이 구성원 카드를 단톡방 화면에도 보인다 (⑤).
    /// 방 잠금 안에서 부르므로 받는 쪽은 짧게(다른 방 잠금을 오래 잡지 않게).
    /// </summary>
    public event Action<string, LiveEvent>? CardChanged;

    private static readonly HashSet<string> StreamKinds = ["delta", "tool", "narration"];

    /// <summary>
    /// 글 조각 · 도구 알림 — 단톡방이 구성원 임시 말풍선에 흘린다. 방 잠금 안에서 부르므로 받는 쪽은 짧게 (CardChanged와 같음).
    /// </summary>
    public event Action<string, LiveEvent>? Streamed;

    /// <summary>완성된 글 블록 하나 (assistant 줄) — 단톡방이 답 앞 중간 글로 모은다. 화면은 글 조각으로 받으므로 알림은 없다. 방 잠금 안에서 부른다.</summary>
    public event Action<string, string>? TextBlock;

    /// <summary>그 방이 답을 기다리는 승인 · 질문 카드 (단톡방 화면이 다시 붙을 때).</summary>
    public (IReadOnlyList<ApprovalView> Approvals, IReadOnlyList<QuestionView> Questions) WaitingCards(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return ([], []);
        lock (live.Gate)
            return ([.. live.Approvals.Values.Where(p => !p.Answer.Task.IsCompleted).Select(p => p.View)],
                    [.. live.Questions.Values.Where(q => !q.Answer.Task.IsCompleted).Select(q => q.View)]);
    }

    /// <summary>실행 없는 방(단톡방)에 알림을 흘린다 — 그 방을 열어 둔 화면이 받는다.</summary>
    public void Announce(string roomId, LiveEvent ev)
    {
        if (_disposed) return;
        var live = _rooms.GetOrAdd(roomId, id => new Live(id));
        lock (live.Gate)
            foreach (var ch in live.Subscribers) ch.Writer.TryWrite(ev);
    }

    /// <summary>
    /// 앱을 끌 때: 모든 실행을 끝내고(프로세스 트리가 남지 않게) SSE 구독을 닫는다 — 열린 방 탭이 정상 종료를 붙잡지 않게.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (var live in _rooms.Values)
        {
            lock (live.Gate)
            {
                live.Generation++;
                live.Pending.Clear();
                EndApprovals(live, "앱 종료");
                live.Cts?.Cancel();
                live.Session?.Proc.Stop(); // 쉬는 실행 · 그 백그라운드 작업도 (앱을 끄면 같이 끝난다 — 계획 위험)
                if (live.Shell is { } shell) { shell.Stopped = true; lock (shell.Sync) shell.Process?.Kill(); } // '!' 명령도
                foreach (var ch in live.Subscribers) ch.Writer.TryComplete();
                live.Subscribers.Clear();
            }
        }
        lock (_appGate)
        {
            foreach (var ch in _appSubscribers) ch.Writer.TryComplete();
            _appSubscribers.Clear();
            _roomListeners.Clear(); // 방 구독 채널은 위에서 방마다 닫았다
        }
    }
}

/// <summary>큐에서 기다리는 말 하나 (Id로 취소한다).</summary>
public sealed record QueuedMessage(string Id, string Text)
{
    /// <summary>+ 요청 창에서 고른 값. 보통 말은 null.</summary>
    public ModePick? Pick { get; init; }
}

/// <summary>방 목록에 보일 실행 상태.</summary>
public enum RoomActivity
{
    Idle,
    Busy,
    /// <summary>실행 중이고 승인 · 질문 카드가 답을 기다린다 (데스크톱 앱이 이 이름을 읽는다 — 질문도 같은 값).</summary>
    Approval,
}
