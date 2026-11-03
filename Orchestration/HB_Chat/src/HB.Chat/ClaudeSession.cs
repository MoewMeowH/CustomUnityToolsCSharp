using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace HB.Chat;

/// <summary>쉬는 실행의 상태. 응답 중 = 턴이 돌거나 보낸 말이 아직 턴을 받지 못함, 쉼 = 백그라운드 작업만 남아 말을 받음.</summary>
public enum SessionState { Responding, Idle, Closing, Exited }

/// <summary>턴 시작 (CLI가 턴마다 내는 system init — 2.1.316 실측). Auto는 쉬는 중(보낸 말 없음)에 CLI가 스스로 시작한 턴.</summary>
public sealed record TurnBeganEvent(int Turn, bool Auto) : StreamEvent;

/// <summary>
/// 턴 끝 (result). User는 그 턴에서 보낸 말의 되돌림(replay)을 봤다는 뜻 — 아니면 자동 턴이거나 이어 가기 앞의 빈 결과(ghost)다.
/// Next는 이 result 뒤의 상태 (응답 중이면 보낸 말이 아직 턴을 기다린다).
/// </summary>
public sealed record TurnEndedEvent(int Turn, bool User, ResultEvent Result, SessionState Next) : StreamEvent;

/// <summary>쉬는 중 작업 목록 (쉬기 시작 · 쉬는 중 목록이 바뀔 때). CapAt은 상한 시각(첫 작업부터).</summary>
public sealed record SessionIdleEvent(IReadOnlyList<BackgroundTask> Tasks, DateTimeOffset? CapAt) : StreamEvent;

/// <summary>쉬는 실행을 닫기 시작함 — By: wrapup(모두 끝내기) · cap(상한) · done(작업이 다 끝나 할 일 없음). Left는 그때 남은 작업 수.</summary>
public sealed record SessionClosingEvent(string By, int Left) : StreamEvent;

/// <summary>
/// 프로세스가 끝남. Cut은 응답 중(턴 도중 · 보낸 말이 턴을 받기 전)에 끝났다는 뜻 — 그 말의 턴은 결과가 없다.
/// ClosedBy는 <see cref="SessionClosingEvent.By"/> 또는 stop(중지) · null(스스로 끝남), Left는 그때 남은 작업 수.
/// </summary>
public sealed record SessionExit(int? ExitCode, string? Error, string? Stderr, bool Cut, string? ClosedBy, int Left);

/// <summary>말 보내기 결과: 보냄 · 응답 중이라 못 보냄(앱 대기열로) · 닫혔음(새 프로세스로).</summary>
public enum SendStatus { Sent, Busy, Closed }

/// <summary>
/// claude -p 프로세스 하나 = 쉬는 실행 (턴 여러 개). stdin · 닫기 · 작업 목록을 이 객체가 맡는다.
/// <list type="bullet">
/// <item>말은 쉬는 중에만 하나씩 보낸다 — 응답 중에 넣으면 CLI가 다음 말과 합쳐 한 턴으로 돈다 (실측).</item>
/// <item>턴은 init ~ result. 보낸 말의 uuid 되돌림이 그 사이에 왔으면 그 말의 턴이다 (<c>--replay-user-messages</c> 필요). 끝 알림과 동시에 넣은 말은
/// 자동 턴 뒤에 따로 돈다 (2.1.316 실측) — 그래서 result 수 · 보낸 말 수로 가르지 않는다.</item>
/// <item>작업 수는 CLI의 작업 목록(background_tasks_changed)을 따른다. result 뒤 작업이 남으면 쉰다. 작업이 없으면(끝났거나 원래 없음)
/// <see cref="SessionTimings.IdleGrace"/> 동안 자동 턴이 시작되지 않을 때 닫는다 — 끝 알림 뒤 init이 곧 온다 (실측 약 0.1초).</item>
/// <item>상한은 첫 작업을 본 때부터 <see cref="SessionTimings.Cap"/> (새 작업으로 늘지 않음). 넘으면 쉬는 중이면 바로, 응답 중이면 그 턴 끝에 닫는다.</item>
/// <item>닫기 결정과 말 받기는 같은 잠금 아래 — 닫기로 정한 뒤의 말은 <see cref="SendStatus.Closed"/>로 돌려 새 프로세스로 가게 한다.</item>
/// <item>stdin을 닫은 뒤 <see cref="SessionTimings.CloseGrace"/> 안에 안 끝나면 트리를 정리한다 (안 끝나는 백그라운드 명령이 있으면 CLI가 안 끝난다, 실측).</item>
/// </list>
/// 콜백(onEvent)은 읽기 고리 한 곳에서 차례로 부른다 (닫기 알림만 다른 스레드). 예외를 던지지 않는다.
/// </summary>
public sealed class ClaudeSession
{
    /// <summary>
    /// 시간 값 기본 — 시험 어셈블리가 시작할 때 한 번만 줄인다(IdleGrace 0 = 작업 없는 턴은 result에서 바로 닫음, 예전 한 실행 한 턴과 같게).
    /// 시험 하나가 바꿔야 하면 전역 대신 <see cref="StartAsync"/>에 값을 넘긴다 (시험 클래스는 함께 돈다).
    /// </summary>
    internal static SessionTimings DefaultTimings { get; set; } = new();

    /// <summary>
    /// 시간 값. Cap = 쉬는 실행 전체 상한(결정 "2시간"), IdleGrace = 작업이 없을 때 자동 턴을 기다렸다 닫는 시간(0 이하면 result에서 바로 닫음),
    /// WriteTimeout = stdin 한 줄 쓰기 제한(넘으면 CLI가 입력을 안 읽는 것으로 보고 트리 정리 — design_main dbeb07f870),
    /// CloseGrace = stdin을 닫은 뒤 스스로 끝나기를 기다리는 시간, ControlTimeout = 제어 요청 답 기다림.
    /// </summary>
    public sealed record SessionTimings
    {
        public TimeSpan Cap { get; init; } = TimeSpan.FromMinutes(RestCapMinutes);
        public TimeSpan IdleGrace { get; init; } = TimeSpan.FromSeconds(3);
        public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan CloseGrace { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(15);
    }

    /// <summary>작업이 다 끝난 뒤 자동 턴(init)을 기다리는 최소 시간.</summary>
    private static readonly TimeSpan AutoTurnWait = TimeSpan.FromSeconds(1);

    /// <summary>쉬는 실행 상한 (분, 결정 "2시간") — 방 규칙 · 승인 카드에도 보인다.</summary>
    public const int RestCapMinutes = 120;

    private readonly SessionTimings _t;
    private readonly Process _proc;
    private readonly ClaudeRunner.StdinWriter _input;
    private readonly Action<StreamEvent> _onEvent;
    private readonly Func<ToolPermission, CancellationToken, Task<string>>? _onPermission;
    private readonly CancellationTokenSource _life = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _asks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlResponseEvent>> _controls = new();
    private readonly TaskCompletionSource<SessionExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly Timer _capTimer;
    private readonly Timer _idleTimer;

    // 아래는 _gate 안에서만
    private SessionState _state = SessionState.Responding;
    private string? _pending;       // 보냈는데 아직 되돌림을 못 본 말의 uuid
    private int _turn;
    private bool _inTurn;
    private bool _turnUser;
    private IReadOnlyList<BackgroundTask> _tasks = [];
    private DateTimeOffset? _capAt;
    private bool _capPassed;
    private string? _closeAtTurnEnd; // 응답 중 받은 닫기 (지금 끝내기 · 설정 바뀜) — 그 턴 끝에
    private string? _closedBy;
    private int _closedLeft;
    private int _controlSeq;

    private ClaudeSession(Process proc, Action<StreamEvent> onEvent, Func<ToolPermission, CancellationToken, Task<string>>? onPermission, SessionTimings timings)
    {
        _t = timings;
        _proc = proc;
        _input = new ClaudeRunner.StdinWriter(proc.StandardInput);
        _onEvent = onEvent;
        _onPermission = onPermission;
        _capTimer = new Timer(_ => CapReached());
        _idleTimer = new Timer(_ => IdleCheck());
    }

    /// <summary>지금 상태.</summary>
    public SessionState State { get { lock (_gate) return _state; } }

    /// <summary>지금 백그라운드 작업 목록 (CLI가 마지막으로 알린 것).</summary>
    public IReadOnlyList<BackgroundTask> Tasks { get { lock (_gate) return _tasks; } }

    /// <summary>상한 시각 (작업을 본 적이 없으면 null).</summary>
    public DateTimeOffset? CapAt { get { lock (_gate) return _capAt; } }

    /// <summary>프로세스가 끝나면 완료된다.</summary>
    public Task<SessionExit> Completion => _exit.Task;

    /// <summary>
    /// 띄우고 첫 말을 보낸다. 띄우지 못했거나 첫 말을 쓰지 못하면 (null, 오류). 인자에 <c>--replay-user-messages</c>가 있어야 턴을 가른다
    /// (없으면 모든 턴이 User=false로 보인다).
    /// </summary>
    public static async Task<(ClaudeSession? Session, string? Error)> StartAsync(ProcessSpec spec, string prompt, Action<StreamEvent> onEvent,
        Func<ToolPermission, CancellationToken, Task<string>>? onPermission, SessionTimings? timings = null)
    {
        // 되돌림 없이는 보낸 말이 턴을 받았는지 몰라 응답 중에서 나오지 못한다 — 인자를 만드는 쪽의 실수를 여기서 막는다
        if (!spec.Args.Contains(ReplayFlag, StringComparer.Ordinal)) return (null, $"내부 오류: 쉬는 실행에는 {ReplayFlag} 인자가 필요합니다.");
        var proc = new Process { StartInfo = ClaudeRunner.StartInfo(spec) };
        try
        {
            if (!proc.Start())
            {
                proc.Dispose();
                return (null, "프로세스를 시작하지 못했습니다.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            proc.Dispose();
            return (null, $"실행 실패: {ex.Message}");
        }
        var session = new ClaudeSession(proc, onEvent, onPermission, timings ?? DefaultTimings);
        var uuid = Guid.NewGuid().ToString("D");
        lock (session._gate) session._pending = uuid;
        _ = session.ReadLoopAsync();
        // 쓰지 못했으면 프로세스가 끝났거나 정리됐다 — 읽기 고리가 끝(Cut)을 알린다
        if (await session.WriteAsync(ClaudeRunner.InitializeLine)) await session.WriteAsync(ClaudeRunner.UserLine(prompt, uuid));
        return (session, null);
    }

    /// <summary>사용자 말 되돌림 인자 — 쉬는 실행에 반드시 (RoomHub가 띄울 때 붙인다 — <c>WithReplay</c>).</summary>
    public const string ReplayFlag = "--replay-user-messages";

    /// <summary>
    /// 쉬는 중이면 말을 보낸다. 응답 중이면 <see cref="SendStatus.Busy"/>(앱 대기열에 두고 다음 쉼에), 닫는 중 · 끝났으면 <see cref="SendStatus.Closed"/>(새 프로세스로).
    /// 쓰기에 실패하면(제한 시간이면 트리 정리) <see cref="SendStatus.Closed"/> — 받지 않은 말로 치고 끝(Cut)에 세지 않는다.
    /// </summary>
    public async Task<SendStatus> SendAsync(string prompt)
    {
        var uuid = Guid.NewGuid().ToString("D");
        lock (_gate)
        {
            if (_state is SessionState.Closing or SessionState.Exited) return SendStatus.Closed;
            if (_state != SessionState.Idle) return SendStatus.Busy;
            _state = SessionState.Responding;
            _pending = uuid;
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (await WriteAsync(ClaudeRunner.UserLine(prompt, uuid))) return SendStatus.Sent;
        // 받지 않은 말로 친다 — 부른 쪽이 새 프로세스로 다시 보내므로 이 실행의 끝(Cut)에 다시 세지 않는다
        lock (_gate)
        {
            if (_pending == uuid) _pending = null;
        }
        return SendStatus.Closed;
    }

    /// <summary>모델을 바꾼다 — 다음 턴부터 (set_model, 2.1.316 실측: 프로세스를 안 죽이고 작업도 산다). 답이 성공이면 true.</summary>
    public async Task<bool> SetModelAsync(string model) =>
        await ControlAsync(new { subtype = "set_model", model }) is { Ok: true };

    /// <summary>추론 강도를 바꾼다 — 다음 턴부터, 세션 한정 (apply_flag_settings effortLevel, 2.1.316 실측: 출처 flagSettings). null이면 강도 지정을 뺀다(CLI 문구: "string or null").</summary>
    public async Task<bool> SetEffortAsync(string? effort) =>
        await ControlAsync(new { subtype = "apply_flag_settings", settings = new { effortLevel = effort } }) is { Ok: true };

    /// <summary>작업 하나를 끝낸다 (stop_task, 2.1.316 실측: 바로 stopped 끝 알림 · 자동 턴 없음 · 프로세스는 산다).</summary>
    public async Task<bool> StopTaskAsync(string taskId) =>
        await ControlAsync(new { subtype = "stop_task", task_id = taskId }) is { Ok: true };

    /// <summary>
    /// 지금 답만 멈춘다 (interrupt, 2.1.316 실측 2026-11-04: 곧바로 성공 답 · 도는 전경 도구는 거절 · 그 턴은 result error_during_execution,
    /// 백그라운드 작업은 산다 · 프로세스도 산다). 답이 성공이면 true.
    /// </summary>
    public async Task<bool> InterruptAsync() =>
        await ControlAsync(new { subtype = "interrupt" }) is { Ok: true };

    /// <summary>
    /// 보낸 사용자 말을 CLI가 받아(되돌림을 봄) 그 턴을 답하는 중이면 interrupt를 보내기 시작해 그 답을, 아니면 null —
    /// 자동 턴 · 받기 전의 말 · 쉬는 중에는 보내지 않는다 (/self-review 42c732f7d3 C1 · C2: 다른 턴을 멈추거나, 받기 전이라 무시됐다).
    /// 쓰기는 이 호출 안에서 시작된다(입력 줄 잠금을 먼저 잡음) — 그 뒤에 보내는 말은 interrupt 줄 다음에 들어간다.
    /// </summary>
    public Task<bool>? InterruptUserTurn()
    {
        lock (_gate)
            if (!AnsweringUserLocked) return null;
        return InterruptAsync();
    }

    /// <summary>보낸 사용자 말을 CLI가 받아 그 턴을 답하는 중인가 (끼어들기를 interrupt로 할 수 있는 때).</summary>
    public bool AnsweringUser { get { lock (_gate) return AnsweringUserLocked; } }

    private bool AnsweringUserLocked => _state == SessionState.Responding && _inTurn && _turnUser && _pending is null;

    /// <summary>제어 요청 하나 — 답(또는 제한 시간 · 프로세스 끝이면 null).</summary>
    internal async Task<ControlResponseEvent?> ControlAsync(object request, TimeSpan? timeout = null)
    {
        string id;
        lock (_gate)
        {
            if (_state == SessionState.Exited) return null;
            id = "hbchat_c" + ++_controlSeq;
        }
        var answer = new TaskCompletionSource<ControlResponseEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _controls[id] = answer;
        try
        {
            if (!await WriteAsync(ClaudeRunner.ControlLine(id, request))) return null;
            var done = await Task.WhenAny(answer.Task, Task.Delay(timeout ?? _t.ControlTimeout, _life.Token)).ConfigureAwait(false);
            return done == answer.Task ? answer.Task.Result : null;
        }
        catch (OperationCanceledException)
        {
            return answer.Task.IsCompletedSuccessfully ? answer.Task.Result : null;
        }
        finally
        {
            _controls.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 모두 끝낸다 (화면 "지금 끝내기"): 쉬는 중이면 바로 stdin을 닫고 유예 뒤 트리 정리, 응답 중이면 그 턴 끝에 (턴을 자르지 않는다 — 자르려면 <see cref="Stop"/>).
    /// 이미 닫는 중 · 끝났으면 false.
    /// </summary>
    public bool WrapUp()
    {
        lock (_gate)
        {
            if (_state == SessionState.Responding)
            {
                _closeAtTurnEnd ??= "wrapup";
                return true;
            }
        }
        return Close("wrapup", onlyIdle: true);
    }

    /// <summary>
    /// 작업째 끝낸다 — 응답 중이면 그 턴을 자르지 않고 턴 끝에 (설정 바꾸기 확인 · 권한 낮춤, /self-review af6ffa3da5 C1), 아니면 바로 트리 정리.
    /// by는 끝 알림의 ClosedBy.
    /// </summary>
    public void EndAfterTurn(string by)
    {
        lock (_gate)
        {
            if (_state == SessionState.Responding)
            {
                _closeAtTurnEnd = by;
                return;
            }
        }
        Stop(by);
    }

    /// <summary>바로 트리를 끝낸다 (중지 · 끼어들기 · 앱 종료). 응답 중이었으면 끝 알림의 Cut이 참. by는 끝 알림의 ClosedBy (설정 바뀜이면 "settings").</summary>
    public void Stop(string by = "stop")
    {
        lock (_gate)
        {
            if (_state == SessionState.Exited) return;
            if (_closedBy is null)
            {
                _closedBy = by;
                _closedLeft = _tasks.Count;
            }
            if (_state != SessionState.Responding) _state = SessionState.Closing;
        }
        ClaudeRunner.Kill(_proc);
    }

    private bool Close(string by, bool onlyIdle)
    {
        int left;
        lock (_gate)
        {
            if (_state is SessionState.Closing or SessionState.Exited) return false;
            if (onlyIdle && _state != SessionState.Idle) return false;
            left = MarkClosing(by);
        }
        FinishClosing(by, left);
        return true;
    }

    /// <summary>닫기로 정한다 (잠금 안에서) — 그 뒤의 말은 <see cref="SendStatus.Closed"/>. 남은 작업 수를 돌려준다.</summary>
    private int MarkClosing(string by)
    {
        _state = SessionState.Closing;
        _closedBy ??= by;
        _closedLeft = _tasks.Count;
        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _capTimer.Change(Timeout.Infinite, Timeout.Infinite);
        return _closedLeft;
    }

    private void FinishClosing(string by, int left)
    {
        Emit(new SessionClosingEvent(by, left));
        _ = CloseInputAsync(killLater: true);
    }

    private async Task CloseInputAsync(bool killLater)
    {
        await _input.CloseAsync();
        if (!killLater) return;
        try
        {
            await Task.Delay(_t.CloseGrace, _life.Token);
            ClaudeRunner.Kill(_proc);
        }
        catch (OperationCanceledException) { } // 그 전에 끝남
    }

    // 판정과 닫기 결정은 한 잠금 안에서 — 잠금을 놓은 사이 말 · 자동 턴이 응답 중으로 바꾸면 상한이 사라졌다 (/cross-review 52ac1810e1 1-F2)
    private void CapReached()
    {
        int left;
        lock (_gate)
        {
            if (_state == SessionState.Responding)
            {
                _capPassed = true; // 이 턴 끝에 닫는다 (턴 도중 자르지 않는다)
                return;
            }
            if (_state != SessionState.Idle) return;
            left = MarkClosing("cap");
        }
        FinishClosing("cap", left);
    }

    private void IdleCheck()
    {
        int left;
        lock (_gate)
        {
            if (_state != SessionState.Idle || _tasks.Count > 0 || _inTurn) return;
            left = MarkClosing("done");
        }
        FinishClosing("done", left);
    }

    /// <summary>한 줄 쓰기 — 제한 시간 안에 못 쓰면 트리를 정리한다. 닫힌 뒤의 쓰기는 버리고 false.</summary>
    private async Task<bool> WriteAsync(string line)
    {
        var write = _input.WriteAsync(line);
        if (await Task.WhenAny(write, Task.Delay(_t.WriteTimeout)).ConfigureAwait(false) != write)
        {
            ClaudeRunner.Kill(_proc);
            return false;
        }
        return await write;
    }

    private void Emit(StreamEvent ev)
    {
        try { _onEvent(ev); } catch { /* 화면 쪽 실패가 실행을 멈추지 않게 */ }
    }

    private async Task ReadLoopAsync()
    {
        var stderrTask = ClaudeRunner.ReadTail(_proc.StandardError, ClaudeRunner.MaxStderrChars);
        string? error = null;
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > ClaudeRunner.MaxLineChars)
                {
                    error = "출력 한 줄이 너무 길어 실행을 끝냈습니다.";
                    ClaudeRunner.Kill(_proc);
                    break;
                }
                var ev = StreamParser.Parse(line);
                if (ev is null) continue;
                if (!Handle(ev)) continue;
                Emit(ev);
                if (ev is ResultEvent r) AfterResult(r);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            error ??= $"출력 읽기 실패: {ex.Message}";
        }
        await _input.CloseAsync();
        await _proc.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;
        bool cut;
        string? by;
        int left;
        lock (_gate)
        {
            cut = _state == SessionState.Responding && (_inTurn || _pending is not null);
            _state = SessionState.Exited;
            by = _closedBy;
            left = _closedLeft;
            _idleTimer.Dispose();
            _capTimer.Dispose();
        }
        _life.Cancel();
        foreach (var ask in _asks.Values)
            try { ask.Cancel(); } catch (ObjectDisposedException) { }
        int? code = null;
        try { code = _proc.ExitCode; } catch (InvalidOperationException) { }
        _proc.Dispose();
        _exit.TrySetResult(new SessionExit(code, error, string.IsNullOrWhiteSpace(stderr) ? null : stderr, cut, by, left));
    }

    /// <summary>줄 하나의 상태 반영. false면 화면에 넘기지 않는다 (제어 요청 · 앱 제어 요청의 답).</summary>
    private bool Handle(StreamEvent ev)
    {
        switch (ev)
        {
            case ControlRequestEvent control:
                var ask = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                if (!_asks.TryAdd(control.RequestId, ask))
                {
                    ask.Dispose(); // 같은 ID가 아직 답을 기다린다
                    return false;
                }
                _ = ClaudeRunner.AnswerAsync(_input, control, _onPermission, _asks, ask, _life.Token);
                return false;
            case ControlCancelEvent cancel:
                if (_asks.TryGetValue(cancel.RequestId, out var withdrawn))
                    try { withdrawn.Cancel(); } catch (ObjectDisposedException) { }
                return false;
            case ControlResponseEvent response:
                if (_controls.TryGetValue(response.RequestId, out var waiting)) waiting.TrySetResult(response);
                return false;
            case InitEvent:
                bool auto;
                int turn;
                lock (_gate)
                {
                    auto = _state == SessionState.Idle;
                    if (auto) _state = SessionState.Responding;
                    _inTurn = true;
                    _turnUser = false;
                    turn = ++_turn;
                    _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                Emit(new TurnBeganEvent(turn, auto));
                return true;
            case ReplayEvent replay:
                lock (_gate)
                {
                    if (replay.Uuid is not null && replay.Uuid == _pending)
                    {
                        _pending = null;
                        _turnUser = true;
                    }
                }
                return true;
            case BackgroundTasksEvent list:
                IReadOnlyList<BackgroundTask> tasks;
                DateTimeOffset? capAt;
                bool idle;
                lock (_gate)
                {
                    _tasks = list.Tasks;
                    if (_capAt is null && list.Tasks.Count > 0)
                    {
                        _capAt = DateTimeOffset.Now + _t.Cap;
                        _capTimer.Change(_t.Cap, Timeout.InfiniteTimeSpan);
                    }
                    (tasks, capAt, idle) = (_tasks, _capAt, _state == SessionState.Idle);
                    // 쉬는 중 작업이 다 빠졌다 = 끝 알림 — 곧 자동 턴이 온다 (실측 약 0.1초). IdleGrace가 0이어도 그만큼은 기다린다
                    if (idle && tasks.Count == 0) _idleTimer.Change(_t.IdleGrace > AutoTurnWait ? _t.IdleGrace : AutoTurnWait, Timeout.InfiniteTimeSpan);
                }
                if (idle) Emit(new SessionIdleEvent(tasks, capAt));
                return true;
            default:
                return true;
        }
    }

    /// <summary>result 뒤: 보낸 말이 아직 턴을 못 받았으면 응답 중 그대로, 상한이 지났으면 닫기, 아니면 쉼 (작업이 없으면 자동 턴을 잠깐 기다렸다 닫음).</summary>
    private void AfterResult(ResultEvent result)
    {
        int turn;
        bool user;
        SessionState next;
        IReadOnlyList<BackgroundTask> tasks;
        DateTimeOffset? capAt;
        string? closeBy = null;
        var left = 0;
        lock (_gate)
        {
            turn = _turn;
            user = _turnUser;
            _inTurn = false;
            _turnUser = false;
            if (_state == SessionState.Responding && _pending is null)
            {
                // 닫기는 TurnEnded를 내기 전에 정한다 — 그 알림을 받고 바로 보낸 말이 닫히는 프로세스로 가지 않게
                if (_closeAtTurnEnd is { } pendingBy) closeBy = pendingBy;
                else if (_capPassed) closeBy = "cap";
                else if (_tasks.Count == 0 && _t.IdleGrace <= TimeSpan.Zero) closeBy = "done";
                else
                {
                    _state = SessionState.Idle;
                    if (_tasks.Count == 0) _idleTimer.Change(_t.IdleGrace, Timeout.InfiniteTimeSpan);
                }
                if (closeBy is not null) left = MarkClosing(closeBy);
            }
            next = _state;
            (tasks, capAt) = (_tasks, _capAt);
        }
        Emit(new TurnEndedEvent(turn, user, result, next));
        if (closeBy is not null) FinishClosing(closeBy, left);
        else if (next == SessionState.Idle) Emit(new SessionIdleEvent(tasks, capAt));
    }
}
