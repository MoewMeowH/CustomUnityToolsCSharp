using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace HB.Chat;

/// <summary>
/// 방 화면에 보내는 실시간 알림 (SSE data 한 줄 = JSON 하나).
/// kind: snapshot · start · delta · tool · denied · retry · end · queued · notice · idle · approval · approval-done · rules ·
/// rooms(방 목록 줄이 바뀌었을 수 있음, Text = 그 방 ID — 어느 방이든).
/// Prompt는 지금 처리 중인 사용자 말, Pending은 큐에서 기다리는 말들. Approval은 승인 카드(approval) · 끝난 카드의 ID(approval-done, Text = 결정).
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
    public ApprovalView? Approval { get; init; }

    /// <summary>rules 알림: 이 방의 "항상 허용" 규칙 전체 (바뀐 뒤 화면 목록을 다시 그린다).</summary>
    public IReadOnlyList<string>? Rules { get; init; }

    /// <summary>face 알림 · snapshot: 지휘자 표정 상태 (<see cref="HB.Chat.Face"/> 이름, 계획서 2-2 b).</summary>
    public string? Face { get; init; }

    /// <summary>end 알림: 이번 턴이 끝날 때의 컨텍스트 크기 · 창 (토큰).</summary>
    public long? ContextTokens { get; init; }
    public int? ContextWindow { get; init; }
}

/// <summary>
/// 화면의 승인 카드 하나. Rule이 있으면 "이 방에서 항상 허용" 단추와 저장될 규칙을 보인다.
/// Truncated면 명령이 너무 길어 잘린 것 — 거절만 보인다 (서버도 허용을 받지 않는다).
/// </summary>
public sealed record ApprovalView(string Id, string Tool, string Summary, string? Description, string? Rule, DateTimeOffset ExpiresAt, bool Truncated);

/// <summary>
/// 방마다 한 번에 한 턴 (계획서 3번). 실행 중에 온 말은 큐에 넣고 끝나면 차례로 보낸다.
/// 중지는 세대(generation)를 올려 큐를 비우고 실행을 끝낸다 — 늦게 끝난 실행은 화면 · 큐에 손대지 않는다.
/// 진행 중 턴의 흘림 글 · 도구 상태는 여기서만 들고 SSE로 흘린다. 끝난 말은 세션 기록에서 다시 읽는다.
/// </summary>
public sealed class RoomHub(RoomStore store, ChatTranscripts transcripts, IOptions<ChatOptions> options) : IDisposable
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
        public readonly Queue<string> Pending = new();
        public CancellationTokenSource? Cts;
        public string? Current;
        public readonly System.Text.StringBuilder Partial = new();
        public string? Tool;
        public string? Notice;
        public readonly List<Channel<LiveEvent>> Subscribers = [];

        /// <summary>실행 중 표정 (생각 중 → 답 쓰는 중 · 도구 · 승인 기다림). 쉴 때는 LastEnd · 방 색인으로 정한다 (<see cref="FaceOf"/>).</summary>
        public Face Phase = Face.Idle;
        public Face? LastEnd;
        public DateTimeOffset LastEndAt;

        /// <summary>지금 명령 단계 실행의 승인 토큰 (실행마다 새로, 끝나거나 중지하면 비움). 이 값과 맞는 승인 요청만 받는다.</summary>
        public string? RunToken;
        public readonly Dictionary<string, PendingApproval> Approvals = [];
    }

    private sealed class PendingApproval(ApprovalRequest request, DateTimeOffset expiresAt)
    {
        public readonly string Id = Guid.NewGuid().ToString("N")[..10];
        public readonly ApprovalRequest Request = request;
        public readonly DateTimeOffset ExpiresAt = expiresAt;
        public readonly TaskCompletionSource<ApprovalAnswer> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>카드에 남길 결정 (허용 · 항상 허용 · 거절 …). 답을 정한 쪽이 적는다.</summary>
        public string? Label;

        public ApprovalView View => new(Id, Request.Tool, Request.Summary, Request.Description, Request.Rule, ExpiresAt, Request.Truncated);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Live> _rooms = new();
    private bool _disposed;

    /// <summary>앱 전체 구독 (방 목록 화면 · 2-3 알림): 목록 줄이 바뀌었을 수 있는 방 ID를 받는다.</summary>
    private readonly Lock _appGate = new();
    private readonly List<Channel<string>> _appSubscribers = [];

    /// <summary>
    /// 방 구독도 목록 신호(rooms)를 같이 받는다 — 탭 하나가 SSE를 하나만 열게 (HTTP/1.1 한 호스트 연결 6개 상한, /self-review 5225b1ffaf C1).
    /// 잠금은 앱 잠금만 잡는다: 방 잠금을 잡은 채 Touch가 불려도 다른 방 잠금으로 가지 않는다.
    /// </summary>
    private readonly List<Channel<LiveEvent>> _roomListeners = [];

    /// <summary>방 목록 줄을 바꾸는 알림 (바쁨 · 승인 · 끝). 흘림 글(delta) 같은 잦은 알림은 넣지 않는다.</summary>
    private static readonly HashSet<string> ListKinds = ["start", "end", "idle", "approval", "approval-done", "queued"];

    /// <summary>
    /// 이 실행기를 바꿀 때 (테스트: 가짜 claude). 기본은 claude 실행 파일 + <see cref="ClaudeArgs"/> 인자.
    /// </summary>
    public Func<Room, RunSpec, string, ProcessSpec>? ProcessFactory { get; init; }

    /// <summary>승인 기록 (없으면 남기지 않음).</summary>
    public ApprovalLog? Log { get; init; }

    /// <summary>
    /// 승인 훅이 부를 이 앱의 주소 (예: http://127.0.0.1:5290). 앱이 켜진 뒤 실제 주소로 채운다.
    /// 비어 있으면 명령 단계 실행을 시작하지 않는다.
    /// </summary>
    public string? ApprovalBaseUrl { get; set; }

    public bool IsBusy(string roomId) => _rooms.TryGetValue(roomId, out var live) && live.Busy;

    /// <summary>방 목록에 보일 실행 상태: 쉼 · 실행 중 · 승인 기다림 (실패는 방 색인의 LastError로 따로 본다).</summary>
    public RoomActivity ActivityOf(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return RoomActivity.Idle;
        lock (live.Gate)
            return live.Approvals.Values.Any(p => !p.Answer.Task.IsCompleted) ? RoomActivity.Approval
                : live.Busy ? RoomActivity.Busy
                : RoomActivity.Idle;
    }

    /// <summary>끝난 턴의 표정(끝남 · 실패 · 중지)을 보이는 시간. 그 뒤에는 쉼 · 실패 · 오래 쉼.</summary>
    public static readonly TimeSpan EndFaceFor = TimeSpan.FromSeconds(10);

    /// <summary>마지막 활동에서 이만큼 지나면 "오래 쉼" 표정.</summary>
    public static readonly TimeSpan SleepAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// 지휘자 표정 (계획서 2-2 b). 실행 중이면 그 단계, 쉬면 방금 끝난 턴(transient일 때만, <see cref="EndFaceFor"/> 안) → 마지막 실패 → 오래 쉼 → 쉼.
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
        if (live.Busy) return live.Approvals.Values.Any(p => !p.Answer.Task.IsCompleted) ? Face.Approval : live.Phase;
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

    /// <summary>말 보내기. 실행 중이면 큐에 넣는다. 오류 문구(null이면 받음).</summary>
    public string? Send(string roomId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "빈 글은 보내지 않습니다.";
        if (text.Length > MaxPromptChars) return $"글이 너무 깁니다 ({text.Length:N0}자, 상한 {MaxPromptChars:N0}자).";
        if (store.Get(roomId) is not { } room) return "없는 방입니다.";
        if (room.DeletedAt is not null) return "휴지통에 있는 방입니다 — 되살린 뒤 보내세요.";
        if (store.LoadError is not null) return "방 색인을 읽지 못해 보내지 않습니다.";

        var live = _rooms.GetOrAdd(roomId, id => new Live(id));
        lock (live.Gate)
        {
            live.Pending.Enqueue(text);
            if (live.Busy)
            {
                Broadcast(live, new LiveEvent("queued") { Queued = live.Pending.Count, Pending = [.. live.Pending] });
                return null;
            }
            live.Busy = true;
            var generation = live.Generation;
            _ = Task.Run(() => LoopAsync(roomId, live, generation));
        }
        return null;
    }

    /// <summary>중지: 큐를 비우고 진행 중 실행을 끝낸다. 기다리던 승인은 거절로 돌려준다.</summary>
    public void Stop(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
        {
            live.Generation++;
            live.Pending.Clear();
            EndApprovals(live, "중지됨");
            live.Cts?.Cancel();
        }
    }

    /// <summary>
    /// 승인 요청 (PermissionRequest 훅 → /approve). 토큰이 그 방의 지금 실행과 맞지 않으면 null (엔드포인트가 403).
    /// 카드를 띄우고 답 · 시간 초과 · 훅 쪽 끊김(aborted) · 중지 중 먼저 오는 것으로 끝낸다. 답은 기록에도 남긴다.
    /// </summary>
    public async Task<ApprovalAnswer?> RequestApprovalAsync(string roomId, string token, ApprovalRequest request, CancellationToken aborted)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return null;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ApprovalTimeoutSeconds));
        PendingApproval pending;
        lock (live.Gate)
        {
            if (live.RunToken is not { } current || !TokenEquals(current, token)) return null;
            pending = new PendingApproval(request, DateTimeOffset.Now + timeout);
            live.Approvals[pending.Id] = pending;
            Broadcast(live, new LiveEvent("approval") { Approval = pending.View });
            SetFace(live, Face.Approval);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        wait.CancelAfter(timeout);
        try
        {
            await pending.Answer.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            // claude 쪽 훅이 먼저 끊겼으면 답을 돌려줘도 버려진다 (0번 실측). 아니면 시간 초과 — 둘 다 거절.
            var (label, message) = aborted.IsCancellationRequested
                ? ("끊김", "승인 요청이 claude 쪽에서 먼저 끊겼습니다.")
                : ("시간 초과", "사용자가 시간 안에 답하지 않아 거절했습니다. 필요하면 다시 요청하세요.");
            lock (live.Gate)
            {
                if (pending.Answer.TrySetResult(new ApprovalAnswer(false, message))) pending.Label = label;
            }
        }

        var answer = await pending.Answer.Task;
        lock (live.Gate)
        {
            live.Approvals.Remove(pending.Id);
            Broadcast(live, new LiveEvent("approval-done") { Approval = pending.View, Text = pending.Label });
            // 다른 카드가 남았으면 그대로 승인 기다림, 아니면 도구 단계로 (허용이면 명령이 돈다, 거절이면 곧 다음 글).
            // Busy가 아니라 Cts로 본다: 실행이 끝나는 잠금에서 Cts를 먼저 비우므로 end 뒤에 face가 나가지 않는다 (/self-review 83b453653b C3).
            if (live.Cts is not null && live.Approvals.Count == 0) SetFace(live, Face.Tool);
        }
        Log?.Write(roomId, request, pending.Label ?? (answer.Allow ? "허용" : "거절"));
        return answer;
    }

    /// <summary>
    /// 카드 단추. 오류 문구(null이면 받음). 잘린 명령은 허용을 받지 않는다 (사용자가 다 보지 못함).
    /// 항상 허용은 답이 정해진 뒤에만 규칙을 저장한다 — 같은 순간 시간 초과로 거절됐는데 규칙만 남는 일이 없게 (지휘자 검토 R3).
    /// 저장에 실패하면 이번 허용은 그대로 두고 오류를 알린다.
    /// </summary>
    public string? Decide(string roomId, string approvalId, ApprovalChoice choice, string? reason)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return "없는 방입니다.";
        string? rule = null;
        lock (live.Gate)
        {
            if (!live.Approvals.TryGetValue(approvalId, out var pending) || pending.Answer.Task.IsCompleted)
                return "이미 끝난 승인 요청입니다.";
            if (choice != ApprovalChoice.Deny && pending.Request.Truncated)
                return "명령이 너무 길어 다 보이지 않습니다 — 거절만 할 수 있습니다.";
            if (choice == ApprovalChoice.AlwaysAllow && (rule = pending.Request.Rule) is null)
                return "이 요청에는 항상 허용할 규칙이 없습니다.";
            var label = choice switch { ApprovalChoice.Allow => "허용", ApprovalChoice.AlwaysAllow => "항상 허용", _ => "거절" };
            var message = string.IsNullOrWhiteSpace(reason) ? "사용자가 거절했습니다." : $"사용자가 거절했습니다: {reason.Trim()}";
            if (!pending.Answer.TrySetResult(new ApprovalAnswer(choice != ApprovalChoice.Deny, choice == ApprovalChoice.Deny ? message : null)))
                return "이미 끝난 승인 요청입니다.";
            pending.Label = label;
        }
        if (rule is null) return null;
        try
        {
            var room = store.Update(roomId, r => r with { AllowRules = r.AllowRules.Contains(rule) ? r.AllowRules : [.. r.AllowRules, rule] });
            BroadcastRules(live, room);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"이번은 허용했지만 규칙을 저장하지 못했습니다: {ex.Message}";
        }
    }

    /// <summary>"항상 허용" 규칙 지우기. 오류 문구(null이면 지움).</summary>
    public string? RemoveRule(string roomId, string rule)
    {
        try
        {
            var room = store.Update(roomId, r => r with { AllowRules = [.. r.AllowRules.Where(x => x != rule)] });
            if (room is null) return "없는 방입니다.";
            if (_rooms.TryGetValue(roomId, out var live)) BroadcastRules(live, room);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"규칙을 지우지 못했습니다: {ex.Message}"; // 저장 실패를 500 대신 이유로 (/self-review 36e4e20e04 C8)
        }
    }

    /// <summary>규칙 목록이 바뀌면 열린 화면에 새 목록을 보낸다 (새로고침 없이 — /self-review 36e4e20e04 C9).</summary>
    private void BroadcastRules(Live live, Room? room)
    {
        if (room is null) return;
        lock (live.Gate) Broadcast(live, new LiveEvent("rules") { Rules = room.AllowRules });
    }

    /// <summary>엔드포인트가 본문을 읽기 전에 보는 토큰 검사: 그 방의 지금 실행 토큰과 같은지.</summary>
    public bool IsCurrentToken(string roomId, string token)
    {
        if (token.Length == 0 || !_rooms.TryGetValue(roomId, out var live)) return false;
        lock (live.Gate) return live.RunToken is { } current && TokenEquals(current, token);
    }

    /// <summary>테스트용: 그 방의 지금 실행 토큰.</summary>
    internal string? CurrentToken(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return null;
        lock (live.Gate) return live.RunToken;
    }

    /// <summary>
    /// 작업 폴더 · 사용자 설정이 이미 미리 허용한 명령 규칙 (Bash · PowerShell). 명령 방에서는 이 규칙과 맞는 명령이 카드 없이 실행되므로
    /// 방 화면에 보인다 (/self-review ab79e61df5 C3 — 프로젝트 훅이 필요해 설정 출처를 줄이지는 않는다). 읽지 못한 파일은 건너뛴다.
    /// </summary>
    public IReadOnlyList<(string Source, string Rule)> InheritedCommandRules(Room room)
    {
        var files = new List<(string Source, string Path)>
        {
            ("사용자", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")),
        };
        if (options.Value.WorkspacePath(room.Workspace) is { } ws)
        {
            files.Add(("작업 폴더", System.IO.Path.Combine(ws, ".claude", "settings.json")));
            files.Add(("작업 폴더 local", System.IO.Path.Combine(ws, ".claude", "settings.local.json")));
        }
        var rules = new List<(string, string)>();
        foreach (var (source, path) in files)
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("permissions", out var p) || !p.TryGetProperty("allow", out var allow) ||
                    allow.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var a in allow.EnumerateArray())
                    if (a.GetString() is { } rule && (rule.StartsWith("Bash", StringComparison.Ordinal) || rule.StartsWith("PowerShell", StringComparison.Ordinal)))
                        rules.Add((source, rule));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        return rules;
    }

    private static bool TokenEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>실행이 끝나거나 중지될 때: 토큰을 비우고 기다리던 승인을 모두 거절로 끝낸다 (잠금 안에서 부른다).</summary>
    private static void EndApprovals(Live live, string label)
    {
        live.RunToken = null;
        foreach (var p in live.Approvals.Values)
            if (p.Answer.TrySetResult(new ApprovalAnswer(false, $"{label} — 승인 요청을 거절했습니다."))) p.Label = label;
    }

    /// <summary>SSE 구독. 먼저 지금 상태(snapshot)를 넣어 준다. 끝나면 <see cref="Unsubscribe"/>.</summary>
    public Channel<LiveEvent> Subscribe(string roomId)
    {
        var live = _rooms.GetOrAdd(roomId, id => new Live(id));
        var ch = Channel.CreateUnbounded<LiveEvent>(new UnboundedChannelOptions { SingleReader = true });
        var room = store.Get(roomId);
        lock (live.Gate)
        {
            // 표정도 Busy · Partial과 같은 잠금에서 정한다 (/self-review 83b453653b C4)
            var face = LiveFace(live, transient: true) ?? (room is null ? Face.Idle : RestFace(room));
            ch.Writer.TryWrite(new LiveEvent("snapshot")
            {
                Face = face.ToString(),
                Busy = live.Busy,
                Text = live.Busy ? live.Partial.ToString() : null,
                Prompt = live.Current,
                Pending = [.. live.Pending],
                Queued = live.Pending.Count,
            });
            if (live.Busy && live.Tool is { } tool) ch.Writer.TryWrite(new LiveEvent("tool") { Text = tool });
            if (live.Notice is { } notice) ch.Writer.TryWrite(new LiveEvent("notice") { Text = notice });
            foreach (var p in live.Approvals.Values.Where(p => !p.Answer.Task.IsCompleted))
                ch.Writer.TryWrite(new LiveEvent("approval") { Approval = p.View });
            live.Subscribers.Add(ch);
        }
        lock (_appGate)
        {
            if (_disposed) ch.Writer.TryComplete();
            else _roomListeners.Add(ch);
        }
        return ch;
    }

    public void Unsubscribe(string roomId, Channel<LiveEvent> ch)
    {
        lock (_appGate) _roomListeners.Remove(ch);
        if (_rooms.TryGetValue(roomId, out var live))
            lock (live.Gate) live.Subscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    private async Task LoopAsync(string roomId, Live live, int generation)
    {
        while (true)
        {
            string text;
            CancellationTokenSource cts;
            lock (live.Gate)
            {
                if (live.Generation != generation || live.Pending.Count == 0 || _disposed)
                {
                    live.Busy = false;
                    live.Current = null;
                    live.Cts = null;
                    live.Tool = null;
                    live.Partial.Clear();
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
                text = live.Pending.Dequeue();
                live.Current = text;
                live.Partial.Clear();
                live.Tool = null;
                // 지난 턴의 "앱 밖에서 바뀜" 경고는 지운다 — 이번 턴에도 그렇다면 RunRoomAsync가 다시 낸다 (0a3b1d5155 C7). 화면은 start에서 숨긴다.
                live.Notice = null;
                cts = live.Cts = new CancellationTokenSource();
                Broadcast(live, new LiveEvent("start") { Prompt = text, Queued = live.Pending.Count, Pending = [.. live.Pending] });
                SetFace(live, Face.Thinking);
            }

            RunResult result;
            try
            {
                result = await RunOneAsync(roomId, live, text, generation, cts.Token);
            }
            catch (Exception ex)
            {
                result = new RunResult(false, false, $"내부 오류: {ex.Message}", null, null, null, 0, 0, null);
            }

            // 창을 모르는 턴(중지 · result 없음)은 방 색인에 남은 창으로 — 화면 막대가 0%로 떨어지지 않게 (/self-review 83b453653b C5)
            var window = result.ContextWindow ?? store.Get(roomId)?.ContextWindow;

            // live.Cts를 잠금 안에서 먼저 비운 뒤 Dispose한다: Stop · Dispose가 해제된 CTS에 Cancel하지 않게 (0a3b1d5155 C4).
            lock (live.Gate)
            {
                live.Cts = null;
                EndApprovals(live, "실행 끝남");
                live.LastEnd = result.Ok ? Face.Done : result.Cancelled ? Face.Stopped : Face.Failed;
                live.LastEndAt = DateTimeOffset.Now;
                live.Phase = Face.Idle; // 다음 턴의 start가 생각 중으로 바꾼다
                Broadcast(live, new LiveEvent("end")
                {
                    Ok = result.Ok, Text = result.Error, CostUsd = result.CostUsd, Face = live.LastEnd.ToString(),
                    ContextTokens = result.ContextTokens, ContextWindow = window,
                });
            }
            cts.Dispose();
        }
    }

    private async Task<RunResult> RunOneAsync(string roomId, Live live, string text, int generation, CancellationToken ct)
    {
        if (store.Get(roomId) is not { } room) return new RunResult(false, false, "없는 방입니다.", null, null, null, 0, 0, null);
        try
        {
            return await RunRoomAsync(room, live, text, generation, ct);
        }
        catch (Exception ex)
        {
            // 예상 밖 실패(IO 등)도 방에 남긴다 — 안 남기면 KnownSize가 낡아 다음 말에 잘못된 "앱 밖에서 바뀜" 경고가 뜬다 (0a3b1d5155 C6).
            return Record(room, new RunResult(false, false, $"내부 오류: {ex.GetType().Name}: {ex.Message}", null, null, null, 0, 0, null));
        }
    }

    private async Task<RunResult> RunRoomAsync(Room room, Live live, string text, int generation, CancellationToken ct)
    {
        var o = options.Value;
        if (o.WorkspacePath(room.Workspace) is not { } workspace || !Directory.Exists(workspace))
            return Record(room, new RunResult(false, false, $"작업 폴더 '{room.Workspace}'가 허용 목록에 없거나 없는 폴더입니다.", null, null, null, 0, 0, null));

        var size = transcripts.Size(room.SessionId);
        var resume = size is not null;
        // 갈래 첫 실행: 원래 방이 턴 도중이면(기록을 쓰는 중) 갈라지지 않게 기다리게 한다 (/self-review 95e8b1c242 C2). 갈래 표시는 남는다.
        if (!resume && room.ForkOf is { } source
            && store.All().FirstOrDefault(r => string.Equals(r.SessionId, source, StringComparison.OrdinalIgnoreCase)) is { } sourceRoom
            && IsBusy(sourceRoom.Id))
            return Record(room, new RunResult(false, false, "원래 방이 실행 중입니다 — 그 턴이 끝난 뒤 다시 보내세요 (갈래는 그때까지의 대화를 이어받습니다).", null, null, null, 0, 0, null));
        if (resume && room.KnownSize is { } known && known != size)
        {
            // 앞 실행이 끝을 남기지 못했으면(앱 종료) 그 실행이 쓴 것이다. 아니면 다른 곳(Claude 앱 · CLI)에서
            // 같은 세션을 이어 쓴 것으로 보인다 — 갈래가 생겼을 수 있다 (계획서 위험 첫 항목).
            var notice = room.RunStartedAt is not null
                ? "앞 실행이 앱 종료로 끊겼습니다 — 그 턴의 답은 없거나 일부만 남았을 수 있습니다."
                : $"세션 기록이 이 앱 밖에서 바뀌었습니다 ({known:N0} → {size:N0}바이트). 다른 곳에서 이 세션을 이어 쓰면 대화가 갈라질 수 있습니다.";
            lock (live.Gate)
            {
                live.Notice = notice;
                Broadcast(live, new LiveEvent("notice") { Text = notice });
            }
        }

        string? mcpConfig = null;
        if (room.Level is PermissionLevel.Read or PermissionLevel.Edit or PermissionLevel.Command)
        {
            try
            {
                mcpConfig = McpConfigFile.Write(workspace, o.McpServer, o.DataFolder);
            }
            catch (InvalidOperationException ex)
            {
                return Record(room, new RunResult(false, false, ex.Message, null, null, null, 0, 0, null));
            }
        }

        // 명령 단계: 이 실행에만 맞는 토큰을 만들어 승인 훅 주소에 넣는다. 끝나거나 중지하면 LoopAsync · Stop이 비운다.
        string? hookUrl = null;
        if (room.Level == PermissionLevel.Command)
        {
            if (ApprovalBaseUrl is not { Length: > 0 } baseUrl)
                return Record(room, new RunResult(false, false, "승인 주소를 아직 모릅니다 — 앱이 다 켜진 뒤 다시 보내세요.", null, null, null, 0, 0, null));
            var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            lock (live.Gate)
            {
                if (live.Generation != generation) return new RunResult(false, true, "중지됨", null, null, null, 0, 0, null);
                live.RunToken = token;
            }
            hookUrl = $"{baseUrl.TrimEnd('/')}/approve?room={room.Id}&token={token}";
        }

        var spec = new RunSpec(room.Level, room.Model, room.SessionId, resume, mcpConfig, o.McpServer, RoomRules.For(room.Level),
            hookUrl, Math.Max(1, o.ApprovalTimeoutSeconds) + 30, room.AllowRules)
        {
            ForkFrom = resume ? null : room.ForkOf, // 갈래 방의 첫 실행 (2-2 c)
        };
        ProcessSpec process;
        try
        {
            process = ProcessFactory?.Invoke(room, spec, workspace)
                      ?? new ProcessSpec(o.ResolveClaude(), ClaudeArgs.Build(spec, [.. o.ModelList]), workspace);
        }
        catch (ArgumentException ex)
        {
            return Record(room, new RunResult(false, false, ex.Message, null, null, null, 0, 0, null));
        }

        store.Update(room.Id, r => r with { RunStartedAt = DateTimeOffset.Now });
        string? mismatch = null;
        using var stopOnMismatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var result = await ClaudeRunner.RunAsync(process, text, ev =>
        {
            if (ev is InitEvent init && init.SessionId is { } sid && !string.Equals(sid, room.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                // CLI가 다른 세션으로 이어 갔다 — 이 방의 세션이 아니므로 멈춘다 (CodePet NativeSessionLink.observe와 같음).
                mismatch = $"CLI가 다른 세션({sid})으로 시작해 멈췄습니다. 이 방의 세션은 {room.SessionId}입니다.";
                stopOnMismatch.Cancel();
                return;
            }
            lock (live.Gate)
            {
                if (live.Generation != generation) return;
                if (ev is DeltaEvent) SetFace(live, Face.Writing);
                else if (ev is ToolEvent te) SetFace(live, Face.Tool, te.Name); // 화면은 표정을 face 알림으로만 바꾼다 (83b453653b C1)
                var shown = ev switch
                {
                    DeltaEvent d => AppendPartial(live, d.Text),
                    ToolEvent t => SetTool(live, t.Name),
                    DeniedEvent d => new LiveEvent("denied") { Text = $"{d.Tool}: {d.Reason}" },
                    RetryEvent r => new LiveEvent("retry") { Text = $"API 재시도 {r.Attempt}회 ({r.Error})" },
                    _ => null,
                };
                if (shown is not null) Broadcast(live, shown);
            }
        }, stopOnMismatch.Token);

        if (mismatch is not null) result = result with { Ok = false, Cancelled = false, Error = mismatch };
        return Record(room, result, forked: spec.ForkFrom is not null && result.Ok);
    }

    private int? ForkReadUpTo(string sessionId) => RoomList.ReadUpToBeforeLastUser(transcripts.Read(sessionId));

    /// <summary>실행 결과를 방에 남긴다. 세션 기록 크기는 이 앱이 쓴 뒤의 값으로 (다음 말 때 밖에서 바뀌었는지 볼 기준).</summary>
    private RunResult Record(Room room, RunResult result, bool forked = false)
    {
        var size = transcripts.Size(room.SessionId);
        var forkRead = forked ? ForkReadUpTo(room.SessionId) : null;
        try
        {
            store.Update(room.Id, r => r with
            {
                LastRunAt = DateTimeOffset.Now,
                // 갈래는 한 번만: 성공했거나 이 방의 세션 기록이 생겼으면(중지돼도 CLI가 이미 갈라 썼다) 다음부터는 resume이다
                ForkOf = forked || size is not null ? null : r.ForkOf,
                // 갈래가 끝나면 이어받은 말은 읽은 것으로 — 이번 턴의 답만 안 읽음에 세진다 (/self-review 95e8b1c242 C1)
                ReadUpTo = forkRead is { } f && r.ReadUpTo is null ? f : r.ReadUpTo,
                // 사용자가 멈춘 실행은 실패가 아니다 — 목록의 "실패" 표시 · 방의 "마지막 실행 실패" 줄에 남기지 않는다 (2-2 a 실제 확인에서 발견).
                LastError = result.Ok || result.Cancelled ? null : result.Error,
                TotalCostUsd = result.CostUsd ?? r.TotalCostUsd,
                ContextTokens = result.ContextTokens ?? r.ContextTokens,
                ContextWindow = result.ContextWindow ?? r.ContextWindow,
                KnownSize = size ?? r.KnownSize,
                RunStartedAt = null,
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
        if (live.Partial.Length > MaxPartialChars) live.Partial.Remove(0, live.Partial.Length - MaxPartialChars);
        return new LiveEvent("delta") { Text = text };
    }

    private static LiveEvent SetTool(Live live, string name)
    {
        live.Tool = name;
        return new LiveEvent("tool") { Text = name };
    }

    private void Broadcast(Live live, LiveEvent ev)
    {
        foreach (var ch in live.Subscribers) ch.Writer.TryWrite(ev);
        if (ListKinds.Contains(ev.Kind)) Touch(live.Id);
    }

    /// <summary>
    /// 앱을 끌 때: 모든 실행을 끝내고(프로세스 트리가 남지 않게) SSE 구독을 닫는다 — 열린 방 탭이 정상 종료를 붙잡지 않게 (0a3b1d5155 C8).
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

/// <summary>방 목록에 보일 실행 상태.</summary>
public enum RoomActivity
{
    Idle,
    Busy,
    /// <summary>실행 중이고 승인 카드가 답을 기다린다.</summary>
    Approval,
}

/// <summary>방 규칙: 첫 말 때 --append-system-prompt로 한 번 넣는다 (압축 전까지 바뀌지 않는다).</summary>
public static class RoomRules
{
    public static string For(PermissionLevel level) =>
        "이 대화는 HB_Chat 채팅 앱의 방이다. 사용자는 브라우저 채팅 화면으로 읽고 말한다. " +
        $"이 방의 권한: {Describe(level)}. " +
        (level == PermissionLevel.Command
            ? "명령을 실행하면 사용자가 그 자리에서 카드로 허용 · 거절한다. 거절되면 같은 명령을 다시 시도하지 말고, 거절 이유를 보고 다른 방법을 묻는다."
            : "승인을 물을 곳이 없어서 허용되지 않은 도구 호출은 자동으로 거절된다 — 거절되면 다시 시도하지 말고, 무엇이 필요한지 사용자에게 말한다.");

    public static string LevelName(PermissionLevel level) => level switch
    {
        PermissionLevel.Chat => "대화",
        PermissionLevel.Read => "읽기",
        PermissionLevel.Edit => "편집",
        PermissionLevel.Command => "명령",
        _ => level.ToString(),
    };

    /// <summary>
    /// 단계 설명 (화면 · 방 규칙). MCP는 사용자 결정대로 읽기 · 편집 모두 허용하되 하는 일을 그대로 적는다
    /// (/self-review 0a3b1d5155 C10 · b19234c862 C3: run_mode는 작업 폴더 파일을 외부 에이전트로 보낼 수 있고, save_result · record_verdicts는 기록 파일을 쓴다).
    /// </summary>
    public static string Describe(PermissionLevel level) => level switch
    {
        PermissionLevel.Chat => "대화만 (도구 · MCP 없음)",
        PermissionLevel.Read =>
            "작업 폴더 읽기 · 검색과 hb-orchestration MCP (run_mode는 작업 폴더 파일을 외부 에이전트로 보낼 수 있고 비용이 든다, " +
            "save_result · record_verdicts는 기록 파일을 쓴다). 작업 폴더 파일 편집 · 명령 실행은 없음",
        PermissionLevel.Edit =>
            "읽기 단계 + 작업 폴더 파일 편집. 이 앱 · MCP 서버 설정, appsettings, CLAUDE.md 같은 지시 파일, 스크립트(.bat · .cmd · .ps1), " +
            ".claude · .mcp.json은 편집이 막힌다. 명령 실행은 없고 작업 폴더 밖 쓰기는 거절",
        PermissionLevel.Command =>
            "읽기 단계 + 작업 폴더 파일 편집(묻지 않음) + 명령 실행(Bash · PowerShell). 명령은 실행 전에 방의 카드로 사용자가 허용 · 거절한다 " +
            "(답이 없으면 약 10분 뒤 거절, 읽기 전용 명령과 작업 폴더 설정이 미리 허용한 명령은 묻지 않음). 편집 금지 경로는 Edit · Write 도구에만 걸리고, " +
            "명령으로 쓰는 것은 카드가 막는다. \"이 방에서 항상 허용\" 규칙은 이 방에만 적용되며, 그 명령이 돌리는 파일이 바뀌어도 다시 묻지 않는다",
        _ => level.ToString(),
    };
}
