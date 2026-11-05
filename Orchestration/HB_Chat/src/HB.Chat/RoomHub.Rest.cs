namespace HB.Chat;

/// <summary>화면 작업 줄의 백그라운드 작업 하나: Kind command · agent, FromAgent = 서브에이전트가 띄운 명령, Since = 처음 본 시각.</summary>
public sealed record BgTaskView(string Id, string Kind, string? Description, string? Agent, bool FromAgent, DateTimeOffset Since);

/// <summary>
/// 쉬는 실행 (백그라운드 명령 — Claude 앱처럼): 답 뒤에도 백그라운드 작업이 남으면 claude 프로세스를 닫지 않고 방을 쉬게 둔다.
/// 쉬는 동안 보낸 말은 같은 프로세스로 가고, 작업이 끝나면 지휘자가 스스로 이어 답한다(자동 턴). 단톡방 구성원 방은 예전처럼 한 실행 한 턴.
/// </summary>
public sealed partial class RoomHub
{
    /// <summary>쉬는 실행 시간 값 (시험이 바꾼다). null이면 <see cref="ClaudeSession.DefaultTimings"/>.</summary>
    internal ClaudeSession.SessionTimings? SessionTimings { get; set; }

    /// <summary>이 방 실행 준비 중 규칙 글을 만드는 재료 — 이어 쓰는 턴이 같은 규칙을 다시 만든다.</summary>
    internal sealed record RunSetup(bool Terminal, string? Unity, IReadOnlyList<string> Extras, bool McpOn, bool AskWrites, string Workspace, bool AppAskWrites);

    /// <summary>실행 하나가 만든 자원 (터미널 토큰 · 설정 파일 · 방 MCP 설정 파일). 쉬는 실행이 가져가면(Adopted) 프로세스가 끝날 때 정리한다.</summary>
    private sealed class RunResources(Terminals? terminals)
    {
        public string? TerminalToken;
        public string? TerminalConfig;
        public string? RoomConfig;
        public bool Adopted;
        private int _released;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            if (TerminalToken is not null) terminals?.Revoke(TerminalToken);
            if (TerminalConfig is not null) TerminalMcp.Delete(TerminalConfig);
            if (RoomConfig is not null) McpConfigFile.DeleteRoom(RoomConfig);
        }
    }

    /// <summary>
    /// 화면 턴 하나가 기다리는 CLI 턴. Auto면 쉬는 중 CLI가 스스로 시작한 턴, 아니면 보낸 말의 턴(되돌림 User=true로 끝남).
    /// 컨텍스트 · 상태 · 세션 ID는 이 턴 동안 본 마지막 값 (읽기 고리 한 곳에서만 쓰고, Ended가 끝난 뒤 읽는다).
    /// </summary>
    private sealed class TurnWait(bool auto)
    {
        public readonly bool Auto = auto;

        /// <summary>자동 턴을 화면 턴으로 열었는지 (방 잠금 안에서). 앞 화면 턴이 끝나는 중에 시작한 자동 턴은 그 루프가 이어서 연다.</summary>
        public bool Shown;
        public readonly TaskCompletionSource<TurnEndedEvent?> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? Context;
        public string? ContextModel;
        public StatusEvent? Status;
        public int Other;
        public string? SessionId;

        /// <summary>
        /// 이 화면 턴에 끼어들기로 interrupt를 보냈다 — 성공 아닌 result로 끝나면 중지된 턴("끼어들기로 멈춤")으로 남긴다. 턴마다 따로라
        /// 다른 턴(다음 말 · 자동 턴)의 결과에 번지지 않는다 (/self-review 42c732f7d3 C3).
        /// </summary>
        public volatile bool Interrupting;

        public void Track(StreamEvent ev)
        {
            switch (ev)
            {
                case InitEvent init: SessionId = init.SessionId; break;
                case UsageEvent { ContextTokens: > 0 } u: (Context, ContextModel) = (u.ContextTokens, u.Model ?? ContextModel); break;
                case StatusEvent s: Status = s; break;
                case OtherEvent: Other++; break;
            }
        }
    }

    /// <summary>쉬는 실행 하나 (프로세스 소유 — stdin · 닫기 · 작업 목록 · 실행 자원). Wait · Unshown · 상태는 방 잠금 안에서 바꾼다.</summary>
    private sealed class RoomRun(string key, int generation, string model, string? effort, bool plan, bool askRunMode, RunSetup setup)
    {
        public readonly string Key = key;
        public readonly int Generation = generation;
        public readonly RunSetup Setup = setup;

        /// <summary>지금 계획 모드인지 — 계획을 승인하면(setMode) false. "계획 먼저"가 켜졌는데 아니면 새로 (self C6).</summary>
        public bool Plan = plan;

        /// <summary>run_mode를 묻는 인자로 띄웠는지 — + 요청 창 말(고른 값)은 이 인자가 있어야 카드로 받는다 (self C2).</summary>
        public readonly bool AskRunMode = askRunMode;
        public string Model = model;
        public string? Effort = effort;
        public ClaudeSession Proc = null!;
        public RunResources? Resources;
        public TurnWait? Wait;

        /// <summary>작업을 처음 본 시각 (화면 "언제부터") — 읽기 고리에서만.</summary>
        public readonly Dictionary<string, DateTimeOffset> Seen = new(StringComparer.Ordinal);

        /// <summary>앱이 이 실행을 멈춘 까닭 (세션 ID 불일치 등) — 그 턴 결과 문구로.</summary>
        public string? StopReason;

        /// <summary>프로세스가 끝나고 자원까지 정리됐을 때.</summary>
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>그 세대의 실행이 아직 승인 · 질문을 받을 수 있나: 턴이 돌거나, 같은 세대의 쉬는 실행이 살아 있음 (쉬는 동안 백그라운드 서브에이전트가 낸 승인도 받는다).</summary>
    private static bool RunAlive(Live live, int generation) =>
        live.Generation == generation
        && (live.Cts is not null || live.Session is { } run && run.Generation == generation && run.Proc.State is SessionState.Idle or SessionState.Responding);

    /// <summary>쉬는 실행이면 지금 작업 줄을 다시 알린다 (잠금 안에서 — 턴 시작 · 끝 알림 바로 뒤).</summary>
    private void RestLine(Live live)
    {
        if (Resting(live) && live.BackgroundUntil is not null)
            Broadcast(live, TasksEvent(live));
    }

    /// <summary>작업 줄 알림 (잠금 안에서).</summary>
    private static LiveEvent TasksEvent(Live live) =>
        new("background") { BackgroundCount = live.BackgroundCount, BackgroundUntil = live.BackgroundUntil, Tasks = live.BgTasks };

    /// <summary>쉬는 중(화면 턴 없음)인 방의 백그라운드 작업 수 — 방 목록 "백그라운드 N". 아니면 0.</summary>
    public int RestingTasks(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return 0;
        lock (live.Gate) return !live.Busy && Resting(live) ? live.BgTasks.Count : 0;
    }

    /// <summary>
    /// 백그라운드 작업 하나 끝내기 (화면 작업 줄 — stop_task, 2.1.316 실측: 프로세스 · 다른 작업은 살고 자동 턴도 없음). 오류 문구 (null이면 끝냄).
    /// </summary>
    public async Task<string?> StopTaskAsync(string roomId, string taskId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return "없는 방입니다.";
        RoomRun? run;
        lock (live.Gate)
        {
            run = live.Session;
            if (run is null || !Resting(live)) return "도는 백그라운드 작업이 없습니다.";
            if (!live.BgTasks.Any(t => t.Id == taskId)) return "이미 끝났거나 없는 작업입니다.";
        }
        return await run.Proc.StopTaskAsync(taskId) ? null : "작업을 끝내지 못했습니다 — 다시 누르거나 '지금 끝내기'를 쓰세요.";
    }

    /// <summary>
    /// 끼어들기 · 지금 보내기 (잠금 안에서): 쉬는 실행이 지금 화면 턴을 답하는 중이면 프로세스를 끊지 않고 interrupt만 보낸다 — 지금 답만 멈추고
    /// 백그라운드 작업은 산다 (결정 "끼어들기만 살림", 2.1.316 실측 2026-11-04). 그 턴은 result로 끝나고, 다음 차례(맨 앞의 새 말)가 같은 프로세스로 간다.
    /// interrupt가 실패하면(답 없음 · 오류) 예전처럼 실행을 취소해 프로세스를 끝낸다. 이 길을 탔으면 true — 아니면 부른 쪽이 예전처럼 취소한다.
    /// </summary>
    private bool TryInterruptRest(Live live)
    {
        // 이 화면 턴(사용자 말)의 CLI 턴이 받아져 도는 중일 때만 — 자동 턴 · 받기 전 · 다른 턴 끝을 기다리는 중이면 예전 길 (42c732f7d3 C1 · C2)
        if (live.Session is not { } run || run.Wait is not { Auto: false } wait || live.Cts is not { } running) return false;
        if (run.Proc.InterruptUserTurn() is not { } sent) return false;
        wait.Interrupting = true;
        Interlocked.Increment(ref _interruptsSent);
        _ = FollowInterruptAsync(live, wait, running, sent);
        return true;
    }

    /// <summary>interrupt 답을 기다려, 실패(오류 · 답 없음)면 그 턴이 아직 돌 때 예전처럼 취소한다 (프로세스 끝).</summary>
    private static async Task FollowInterruptAsync(Live live, TurnWait wait, CancellationTokenSource running, Task<bool> sent)
    {
        bool ok;
        try { ok = await sent.ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { ok = false; }
        if (ok) return;
        lock (live.Gate)
        {
            wait.Interrupting = false;
            // live.Cts는 Dispose 전에 비운다 — 같은 것일 때만
            if (live.Cts == running)
                try { running.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>interrupt 길을 탄 수 (시험 — 예전 길과 가린다).</summary>
    private int _interruptsSent;
    internal int InterruptsSent => Volatile.Read(ref _interruptsSent);

    /// <summary>CLI의 백그라운드 서브에이전트 작업 종류 (2.1.316 실측 task_type).</summary>
    private const string AgentKind = "local_agent";

    /// <summary>CLI 작업 목록 → 화면 작업 줄 (처음 본 시각을 붙인다, 읽기 고리에서만).</summary>
    private static IReadOnlyList<BgTaskView> TaskViews(RoomRun run, IReadOnlyList<BackgroundTask> tasks)
    {
        var now = DateTimeOffset.Now;
        foreach (var t in tasks) run.Seen.TryAdd(t.TaskId, now);
        foreach (var gone in run.Seen.Keys.Where(k => tasks.All(t => t.TaskId != k)).ToList()) run.Seen.Remove(gone);
        // 모르는 종류는 그 이름 그대로 (서브에이전트로 잘못 보이지 않게, af6ffa3da5 C7)
        return [.. tasks.Select(t => new BgTaskView(t.TaskId, t.Kind switch { TaskEvent.BashKind => "command", AgentKind => "agent", { Length: > 0 } k => k, _ => "task" },
            t.Description, t.Agent, t.ParentTaskId is not null, run.Seen[t.TaskId]))];
    }

    /// <summary>쉬는 실행이 살아 있나 (잠금 안에서).</summary>
    private static bool Resting(Live live) => live.Session is { } run && run.Proc.State is SessionState.Idle or SessionState.Responding;

    /// <summary>쉬는 실행(답 뒤에도 프로세스 · 백그라운드 작업이 남음)이 있나 — 휴지통 · 갈래 막기 (Busy가 아니어도 프로세스가 산다).</summary>
    public bool IsResting(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return false;
        lock (live.Gate) return Resting(live);
    }

    /// <summary>쉬는 실행이 화면 턴의 사용자 말을 받아 답하는 중인가 (시험 — 끼어들기가 interrupt 길을 탈 때).</summary>
    internal bool AnsweringUser(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return false;
        lock (live.Gate) return live.Session is { Wait: { Auto: false } } run && run.Proc.AnsweringUser;
    }

    /// <summary>
    /// 쉬는 실행을 백그라운드 작업째 끝낸다 (휴지통 · 권한 낮춤 · 설정 바꾸기 확인). 화면 턴은 건드리지 않는다 — 턴째 멈추려면 <see cref="StopTurn"/>.
    /// by는 기록 줄의 까닭 (stop = "기다리는 중 멈춰", settings = "방 설정이 바뀌어").
    /// </summary>
    public void EndRest(string roomId, string by = "stop", bool afterTurn = false)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
        {
            if (live.Session is not { } run) return;
            // 응답 중이면 쓰던 답을 자르지 않고 그 턴 끝에 (설정 확인 · 권한 낮춤 — af6ffa3da5 C1)
            if (afterTurn) run.Proc.EndAfterTurn(by);
            else run.Proc.Stop(by);
        }
    }

    /// <summary>쉬는 실행에 도는 백그라운드 작업이 있나 (화면 턴 여부와 상관없이) — 갈래 막기 (작업 없이 잠깐 쉬는 동안은 막지 않음, af6ffa3da5 C8).</summary>
    public bool HasRestingTasks(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return false;
        lock (live.Gate) return Resting(live) && live.BgTasks.Count > 0;
    }

    /// <summary>
    /// 이어 쓰기 열쇠: 프로세스 인자 · 붙는 자원을 바꾸는 방 설정. 모델 · 강도는 넣지 않는다(쉬는 프로세스에 바로 바꿈), 규칙 글도
    /// 넣지 않는다(지금처럼 말 앞 안내로 전한다). "계획 먼저"는 <see cref="RoomRun.Plan"/>, + 요청 창 말은 <see cref="RoomRun.AskRunMode"/>로 따로 본다
    /// (말마다 바뀌는 값이라 열쇠에 넣으면 설정을 안 바꿔도 작업이 끝났다 — af6ffa3da5 C2). 인자에 쓰이는 단계에서만 넣는다
    /// (작업 폴더 목록 · 폴더 밖 쓰기 묻기는 앱 단계, 서브에이전트 강도는 서브에이전트를 쓰는 단계 — /cross-review 52ac1810e1 self C4).
    /// 첨부 폴더는 쉬는 실행을 띄우기 전에 만들어 두어 첫 첨부로 열쇠가 바뀌지 않는다 (af6ffa3da5 C3).
    /// </summary>
    private string RunKey(Room room)
    {
        var o = options.Value;
        var app = room.Level == PermissionLevel.App;
        return string.Join("\n",
            room.SessionId, room.Level, room.Workspace, room.UnityProject ?? "",
            string.Join(",", room.McpOff.Order(StringComparer.Ordinal)), string.Join(",", room.McpExtra.Order(StringComparer.Ordinal)),
            room.AskRunMode,
            app && o.AskAppWrites, ClaudeArgs.AllowsAgents(room.Level) ? o.SubagentEffort ?? "" : "",
            Directory.Exists(Attachments.Folder(o.DataFolder, room.Id)), string.Join(",", room.AttachFrom),
            app ? string.Join(",", o.AllWorkspaces.Select(w => w.Path)) : "");
    }

    /// <summary>쉬는 실행이 자동 턴으로 응답 중이라 말을 못 보냈을 때 다시 해 보는 간격.</summary>
    private static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>쉬는 실행에 반드시 붙는 되돌림 인자 — 시험용 실행기(ProcessFactory)가 만든 인자에도.</summary>
    private static ProcessSpec WithReplay(ProcessSpec spec) =>
        spec.Args.Contains(ClaudeSession.ReplayFlag, StringComparer.Ordinal) ? spec : spec with { Args = [.. spec.Args, ClaudeSession.ReplayFlag] };

    /// <summary>
    /// 쉬는 실행의 줄 하나: 턴 경계(자동 턴 시작 · 턴 끝)와 작업 목록은 여기서, 나머지는 실행을 띄울 때 만든 화면 처리(view)로.
    /// 읽기 고리 한 곳에서 차례로 온다 (닫기 알림만 다른 스레드).
    /// </summary>
    private void OnRunEvent(string roomId, Live live, RoomRun run, StreamEvent ev, Action<StreamEvent> view)
    {
        TurnWait? wait;
        lock (live.Gate) wait = run.Wait;
        switch (ev)
        {
            case TurnBeganEvent:
                // 닫는 중(지금 끝내기 · 상한 · 설정 바뀜)에 시작한 턴은 화면 턴으로 열지 않는다 — 곧 정리된다 (self C10)
                if (wait is null && run.Proc.State != SessionState.Closing) StartAutoTurn(roomId, live, run);
                return;
            case TurnEndedEvent ended:
                if (wait is null) return;
                if (wait.Auto || ended.User)
                {
                    lock (live.Gate)
                    {
                        if (run.Wait == wait) run.Wait = null;
                        // 화면 턴으로 열기 전에 끝났다 — 버리지 않고 LoopAsync가 다음 차례에 연다 (1-F1)
                        if (wait.Auto && !wait.Shown) live.Unshown.Enqueue((run, wait));
                    }
                    wait.Ended.TrySetResult(ended);
                }
                // 아니면 보낸 말 앞에 끼어든 턴(이어 가기 앞의 빈 결과 · 자동 턴) — 그 말의 턴을 계속 기다린다
                return;
            case SessionIdleEvent idle:
                PublishTasks(live, run, idle.Tasks, idle.CapAt);
                return;
            case BackgroundTasksEvent changed:
                // 응답 중에도 목록이 바뀌면 작업 줄을 고친다 (쉬기 시작 · 쉬는 중에는 SessionIdleEvent가 같은 값으로 한 번 더 온다)
                PublishTasks(live, run, changed.Tasks, run.Proc.CapAt);
                break;
            case SessionClosingEvent:
                return; // 끝은 WatchRunAsync가 알린다
        }
        wait?.Track(ev);
        view(ev);
    }

    /// <summary>작업 목록을 방에 남기고 작업 줄을 알린다. 작업이 없으면 줄을 지운다 — 상한 시각만 남아 "이어 답하는 중 · 지금 끝내기"가 잠깐 뜨지 않게 (self C6).</summary>
    private void PublishTasks(Live live, RoomRun run, IReadOnlyList<BackgroundTask> tasks, DateTimeOffset? capAt)
    {
        var views = TaskViews(run, tasks);
        lock (live.Gate)
        {
            if (live.Session != run || live.Generation != run.Generation) return;
            if (views.Count == 0 && live.BgTasks.Count == 0 && live.BackgroundUntil is null) return; // 작업이 없던 실행 — 알릴 것 없음
            live.BgTasks = views;
            live.BackgroundCount = views.Count;
            live.BackgroundUntil = views.Count > 0 ? capAt : null;
            Broadcast(live, TasksEvent(live));
        }
    }

    /// <summary>
    /// 쉬는 중 CLI가 스스로 턴을 시작함 (작업 끝 알림 뒤): 방이 놀고 있으면 화면 턴을 연다. 보낸 말을 막 꺼내는 중이면(Busy) 그 말이 이 턴 끝을 기다렸다 보낸다.
    /// 방이 휴지통에 들어갔거나 띄운 뒤 설정(단계 등)이 바뀌었으면 옛 인자로 돌리지 않고 실행을 끝낸다 (/cross-review 52ac1810e1 security C1).
    /// </summary>
    private void StartAutoTurn(string roomId, Live live, RoomRun run)
    {
        var current = store.Get(roomId);
        if (current is not { DeletedAt: null } || RunKey(current) != run.Key)
        {
            // 조용히 사라지지 않게 알린다 (af6ffa3da5 C3 — 전역 설정 등 화면 확인을 거치지 않은 변경)
            if (current is { DeletedAt: null }) Notice(live, "방 설정이 바뀌어 백그라운드 작업이 끝난 뒤의 이어 답을 멈췄습니다 — 결과가 필요하면 다시 물어보세요.");
            run.Proc.Stop("settings");
            return;
        }
        lock (live.Gate)
        {
            if (run.Wait is not null) return;
            var auto = new TurnWait(auto: true);
            run.Wait = auto;
            // 앞 화면 턴이 끝나는 중(Busy)이면 그 루프가 다음 차례에 이 턴을 연다 (TakeAutoTurn)
            if (live.Busy || _disposed || live.Session != run || live.Generation != run.Generation) return;
            auto.Shown = true;
            live.Busy = true;
            var generation = live.Generation;
            _ = Task.Run(() => LoopAsync(roomId, live, generation, (run, auto)));
        }
    }

    /// <summary>아직 화면 턴으로 열지 않은 자동 턴이 있으면 맡는다 (잠금 안에서 — LoopAsync가 기다리는 말보다 먼저). 끝난 것부터.</summary>
    private static (RoomRun Run, TurnWait Wait)? TakeAutoTurn(Live live, int generation)
    {
        // 끝난 것부터 — 그 프로세스가 이미 끝나 방에서 떨어졌어도 (중지로 세대가 바뀐 것은 버린다)
        while (live.Unshown.TryDequeue(out var done))
            if (done.Run.Generation == generation)
            {
                done.Wait.Shown = true;
                return done;
            }
        if (live.Session is not { } run || run.Generation != generation) return null;
        if (run.Wait is not { Auto: true, Shown: false } wait) return null;
        wait.Shown = true;
        return (run, wait);
    }

    /// <summary>자동 턴 하나를 화면 턴으로: 끝(result)까지 기다려 방에 남긴다. 중지 · 끼어들기면 프로세스를 정리한다.</summary>
    private async Task<RunResult> AutoTurnAsync(string roomId, Live live, RoomRun run, TurnWait wait, int generation, CancellationToken ct)
    {
        if (store.Get(roomId) is not { } room) return new RunResult(false, false, "없는 방입니다.", null, null, null, 0, 0, null);
        // 턴 도중 앱이 꺼지면 다음 말에 "앞 실행이 앱 종료로 끊겼습니다"가 뜨게 (self C9)
        MarkRunStarted(room.Id);
        TurnEndedEvent? ended;
        using (ct.Register(() => run.Proc.Stop()))
            ended = await wait.Ended.Task;
        var result = await TurnResultAsync(run, wait, ended, ct.IsCancellationRequested);
        if (RefreshSteps(room, live, generation, turnEnd: true) is { } last)
            lock (live.Gate) Broadcast(live, new LiveEvent("steps") { Steps = last });
        lock (live.Gate) live.StepsWatch = false;
        return Record(room, result);
    }

    /// <summary>방 색인에 턴 시작 시각 — 저장 실패는 턴을 막지 않는다 (rooms.json이 잠깐 잠김, self C1).</summary>
    private void MarkRunStarted(string roomId)
    {
        try { store.Update(roomId, r => r with { RunStartedAt = DateTimeOffset.Now }); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>턴 결과 → 방 결과. 프로세스가 턴 도중 끝났으면(ended null) 그 끝 정보로 실패 · 중지 (앱이 멈춘 까닭이 있으면 그 문구).</summary>
    private static async Task<RunResult> TurnResultAsync(RoomRun run, TurnWait wait, TurnEndedEvent? ended, bool cancelled)
    {
        if (ended is { Result: var r })
        {
            // 끼어들기(interrupt)로 멈춘 턴: CLI는 error_during_execution으로 끝낸다 — 실패가 아니라 중지된 턴으로 (화면 "끼어들기로 멈춤")
            var interrupted = wait.Interrupting && !r.Ok;
            return new RunResult(r.Ok, interrupted, r.Ok ? null : interrupted ? "끼어들기로 멈춤" : $"실행 오류 ({r.Subtype}): {r.Text}", r.Text, r.CostUsd,
                wait.SessionId, r.Denials, wait.Other, null)
            {
                ContextTokens = wait.Context, ContextWindow = r.WindowFor(wait.ContextModel), Status = wait.Status,
            };
        }
        var exit = await run.Proc.Completion;
        if (run.StopReason is { } why)
            return new RunResult(false, false, why, null, null, wait.SessionId, 0, wait.Other, exit.Stderr) { ContextTokens = wait.Context, Status = wait.Status };
        if (cancelled)
            return new RunResult(false, true, "중지됨", null, null, wait.SessionId, 0, wait.Other, exit.Stderr) { ContextTokens = wait.Context, Status = wait.Status };
        return new RunResult(false, false, exit.Error ?? $"결과 없이 끝났습니다 (종료 코드 {exit.ExitCode}).", null, null, wait.SessionId, 0, wait.Other, exit.Stderr)
        {
            ContextTokens = wait.Context, Status = wait.Status,
        };
    }

    /// <summary>
    /// 새 프로세스를 쉬는 실행으로 띄우고 첫 말의 턴을 기다린다. 띄웠으면 자원을 가져가 프로세스가 끝날 때 정리한다 (턴이 끝나도 작업이 남으면 산다).
    /// </summary>
    private async Task<RunResult> StartRunAsync(string roomId, Live live, int generation, string key, bool plan, bool askRunMode, RunSetup setup, Room room, ProcessSpec process,
        string prompt, Action<StreamEvent> view, Func<ToolPermission, CancellationToken, Task<string>> decide, RunResources resources, Action<RoomRun> started,
        CancellationToken ct)
    {
        var run = new RoomRun(key, generation, room.Model, room.Effort, plan, askRunMode, setup);
        var wait = new TurnWait(auto: false);
        run.Wait = wait;
        var (proc, error) = await ClaudeSession.StartAsync(WithReplay(process), prompt, ev => OnRunEvent(roomId, live, run, ev, view), decide, SessionTimings);
        if (proc is null) return new RunResult(false, false, error, null, null, null, 0, 0, null);
        run.Proc = proc;
        started(run);
        resources.Adopted = true;
        run.Resources = resources;
        lock (live.Gate) live.Session = run;
        _ = WatchRunAsync(roomId, live, run);
        TurnEndedEvent? ended;
        using (ct.Register(() => proc.Stop()))
            ended = await wait.Ended.Task;
        return await TurnResultAsync(run, wait, ended, ct.IsCancellationRequested);
    }

    /// <summary>쉬는 실행을 띄우기 전에 막힌 말 — 세션 기록에 들어가지 않았다 (화면이 입력칸에 돌려준다).</summary>
    private RunResult NotSent(Room room, string error) => Record(room, new RunResult(false, false, error, null, null, null, 0, 0, null) { NotStarted = true });

    /// <summary>
    /// 쉬는 실행에 이 말을 보낸다 (이어 쓰기). 쓸 수 없으면(없음 · 세대 · 열쇠 · 닫힘) 쉬는 실행을 끝내고 null — 부른 쪽이 새로 띄운다.
    /// 옛 실행이 끝나지 않으면 새로 띄우지 않고 보내지 못함으로 (같은 방 설정 파일을 겹쳐 쓰지 않게 — security C5).
    /// </summary>
    private async Task<RunResult?> TryRestTurnAsync(Room room, Live live, string text, int generation, CancellationToken ct)
    {
        RoomRun? run;
        lock (live.Gate) run = live.Session;
        if (run is null) return null;
        var key = RunKey(room);
        var wantsPlan = room.PlanNext && ClaudeArgs.PlanMode(room.Level) is not null;
        bool picked;
        lock (live.Gate) picked = live.CurrentPick is not null;
        // + 요청 창 말은 run_mode를 묻는 인자가 있어야 카드로 받는다 — 없이 띄운 실행이면 새로 (af6ffa3da5 C2)
        var changed = run.Key != key || wantsPlan && !run.Plan || picked && !run.AskRunMode;
        if (run.Generation != generation || changed || run.Proc.State is SessionState.Closing or SessionState.Exited)
            return await RetireAsync(live, run, changed ? "방 설정이 바뀌어" : null) ? null : NotSent(room, StillRunning);
        // 모델 · 강도도 띄울 때와 같은 검사 — 쉬는 실행에서 허용 목록 밖 값을 그대로 넘기지 않게 (self C7)
        if (!options.Value.ModelList.Contains(room.Model, StringComparer.Ordinal) || !ClaudeArgs.IsSafeName(room.Model))
            return NotSent(room, $"허용 목록에 없는 모델: {room.Model}");
        if (room.Effort is { } level && !RoomEffort.Levels.Contains(level, StringComparer.Ordinal))
            return NotSent(room, $"알 수 없는 추론 강도: {level}");

        var rules = RulesFor(room, run.Setup);
        var rulesHash = RoomRules.Hash(rules);
        var noted = room.RulesHash != rulesHash && !text.TrimStart().StartsWith('/');
        var prompt = noted ? RoomRules.ChangedNote(rules) + text : text;
        // 실제로 쓰는 줄은 uuid가 붙는다 (self C12)
        if (System.Text.Encoding.UTF8.GetByteCount(ClaudeRunner.UserLine(prompt, Guid.Empty.ToString("D"))) is var lineBytes && lineBytes > ClaudeRunner.MaxStdinLineBytes)
            return NotSent(room, $"말이 너무 깁니다 ({lineBytes / 1024:N0}KB, {ClaudeRunner.MaxStdinLineBytes / 1024}KB까지) — 긴 글은 파일로 첨부해 주세요.");

        // 이 말의 턴을 맡는다 — 다른 턴(자동 턴)이 돌고 있으면 그 끝을 기다린다
        var wait = new TurnWait(auto: false);
        while (true)
        {
            TurnWait? other;
            lock (live.Gate)
            {
                other = run.Wait;
                if (other is null) run.Wait = wait;
            }
            if (other is null) break;
            await other.Ended.Task.WaitAsync(ct);
        }
        var sent = SendStatus.Closed;
        var sizeBefore = transcripts.Size(room.SessionId) ?? -1;
        TurnEndedEvent? ended = null;
        try
        {
            // 모델 · 강도는 프로세스를 살린 채 (사용자 결정 "모델 · 강도는 작업 살린 채" — set_model · apply_flag_settings, 2.1.316 실측). 턴을 맡은 뒤(자동 턴 도중이 아님)에 보낸다
            string? failed = null;
            if (run.Model != room.Model)
            {
                if (await run.Proc.SetModelAsync(room.Model)) run.Model = room.Model;
                else failed = "모델을 바꾸지 못해";
            }
            if (failed is null && run.Effort != room.Effort)
            {
                if (await run.Proc.SetEffortAsync(room.Effort)) run.Effort = room.Effort;
                else failed = "추론 강도를 바꾸지 못해";
            }
            if (failed is not null)
            {
                lock (live.Gate)
                    if (run.Wait == wait) run.Wait = null;
                return await RetireAsync(live, run, failed) ? null : NotSent(room, StillRunning);
            }
            MarkRunStarted(room.Id);
            using (ct.Register(() => run.Proc.Stop()))
            {
                sent = await run.Proc.SendAsync(prompt);
                // 그사이 CLI가 자동 턴을 시작했다 — 그 턴이 끝나면 다시 (그 턴은 이 화면 턴 안에 보인다)
                while (sent == SendStatus.Busy && !ct.IsCancellationRequested)
                {
                    await Task.Delay(BusyRetry, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                    sent = await run.Proc.SendAsync(prompt);
                }
                if (sent == SendStatus.Sent) ended = await wait.Ended.Task;
            }
        }
        finally
        {
            // 보내지 못했으면(예외 포함) 맡은 턴을 놓는다 — 남으면 다음 자동 턴 · 말이 프로세스가 끝날 때까지 막혔다 (self C1)
            if (sent != SendStatus.Sent)
                lock (live.Gate)
                    if (run.Wait == wait) run.Wait = null;
        }
        if (sent != SendStatus.Sent)
        {
            if (ct.IsCancellationRequested) return Record(room, new RunResult(false, true, "중지됨", null, null, null, 0, 0, null));
            return await RetireAsync(live, run, null) ? null : NotSent(room, StillRunning);
        }
        var result = await TurnResultAsync(run, wait, ended, ct.IsCancellationRequested);
        if (RefreshSteps(room, live, generation, turnEnd: true) is { } last)
            lock (live.Gate) Broadcast(live, new LiveEvent("steps") { Steps = last });
        lock (live.Gate) live.StepsWatch = false;
        if (noted && (transcripts.Size(room.SessionId) ?? -1) > sizeBefore)
        {
            try { store.Update(room.Id, r => r with { RulesHash = rulesHash }); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        return Record(room, result);
    }

    private const string StillRunning = "앞 실행이 아직 끝나지 않아 보내지 못했습니다 — 잠시 뒤 다시 보내거나 중지하세요.";

    /// <summary>
    /// 쉬는 실행을 끝내고 자원 정리까지 기다린다 (새로 띄우기 전 — 방 설정 파일 경로가 같아 겹치지 않게). 제때 안 끝나면 false.
    /// why가 있으면(설정 바뀜 · 모델 못 바꿈) 작업이 남았을 때 알리고 기록 줄도 그 까닭으로 남긴다.
    /// 끝나며 늘어난 세션 기록 크기는 이 앱이 본 크기(KnownSize)로 적는다 (끝내기 전에 이미 달랐으면 그대로 둔다).
    /// </summary>
    private async Task<bool> RetireAsync(Live live, RoomRun run, string? why)
    {
        var left = run.Proc.Tasks.Count;
        // 화면 확인을 거치지 않은 설정 변경(다른 탭 · 전역 설정)의 뒷받침 — 작업이 남았으면 알리고 끝낸다
        if (why is not null && left > 0) Notice(live, $"{why} 백그라운드 작업 {left}개를 끝내고 새로 시작합니다.");
        // 끝나며 CLI가 쓴 줄도 이 앱이 쓴 것이다 — 새 말의 화면 턴이 돌고 있어 WatchRunAsync는 크기를 적지 않으니 아래에서 적는다 (새로 띄우기 전 검사가
        // "이 앱 밖에서 바뀌었습니다"를 잘못 띄웠다, 사용자 화면 2026-11-04: 1,171,421 → 1,173,341). 끝내기 전에 이미 달랐으면(쉬는 동안 다른 곳에서
        // 이어 씀) 그대로 두어 그 검사가 알리게 한다 (/self-review f7aa28507a C1). 부른 쪽은 방을 다시 읽어 이 값을 쓴다
        bool unchanged;
        try { unchanged = store.Get(live.Id) is { } before && transcripts.Size(before.SessionId) is { } was && was == before.KnownSize; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { unchanged = false; }
        run.Proc.Stop(why is null ? "stop" : "settings");
        try
        {
            await run.Done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (unchanged)
            {
                try
                {
                    if (store.Get(live.Id) is { } room && transcripts.Size(room.SessionId) is { } size && size != room.KnownSize)
                        store.Update(live.Id, r => r with { KnownSize = size });
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
            }
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>프로세스가 끝나면: 자원 정리 · 정리 기록 줄 · 방에서 떼기 · 기다리던 턴 깨우기 · 쉬는 동안 남은 카드 · 이 턴 허용 끄기 · 작업 줄 지우기.</summary>
    private async Task WatchRunAsync(string roomId, Live live, RoomRun run)
    {
        var exit = await run.Proc.Completion;
        run.Resources?.Release();
        // 작업이 남은 채 정리했으면 방 기록 줄 ("지금 끝내기" · 상한 · 멈춤 · 설정 바뀜) — 화면이 작업 줄이 지워진 알림을 받기 전에 남긴다
        if (exit.Left > 0 && exit.ClosedBy is "wrapup" or "cap" or "stop" or "settings"
            && BackgroundNote(new RunResult(true, false, null, null, null, null, 0, 0, null) { BackgroundClosedBy = exit.ClosedBy, BackgroundLeft = exit.Left }) is { } note)
            Log?.Note(roomId, RoomNotes.BackgroundTool, note);
        TurnWait? wait;
        lock (live.Gate)
        {
            wait = run.Wait;
            run.Wait = null;
            if (live.Session == run)
            {
                live.Session = null;
                // 화면 턴이 돌고 있으면 그 끝(LoopAsync)이 카드 · 이 턴 허용을 정리한다
                if (!live.Busy)
                {
                    EndApprovals(live, "실행 끝남");
                    SetTrust(live, false);
                }
                // 작업 줄이 떠 있었으면 지운다 — 중지로 세대가 바뀌었어도 (지우기만 하는 알림). 작업이 없던 실행은 알리지 않는다
                if (live.BackgroundCount > 0 || live.BackgroundUntil is not null)
                    Broadcast(live, new LiveEvent("background") { BackgroundCount = 0 });
                live.BackgroundCount = 0;
                live.BackgroundUntil = null;
                live.BgTasks = [];
                // 프로세스와 함께 끝난 서브에이전트 말풍선: 화면 턴이 돌면 그 끝이 정리한다, 아니면 지금 비운다
                if (!live.Busy && live.Subagents.Count > 0)
                {
                    live.Subagents.Clear();
                    Broadcast(live, SubagentsEvent(live));
                }
            }
        }
        // 쉬는 실행은 마지막 화면 턴이 끝난 뒤(IdleGrace · 작업 끝)에야 프로세스가 끝나고, CLI는 끝날 때 세션 기록에 줄을 더 쓴다 —
        // 턴 끝에 적은 크기와 달라져 다음 말에 "이 앱 밖에서 바뀌었습니다" 경고가 잘못 떴다 (사용자 화면 2026-11-04: 223,272 → 224,183).
        // 화면 턴이 돌지 않을 때 끝났으면 지금 크기를 이 앱이 본 크기로 (돌고 있으면 그 턴 끝 Record가 적는다). 앱을 끄느라 끝난 것은 그대로 둔다
        bool idle;
        lock (live.Gate) idle = !live.Busy;
        if (idle && !_disposed)
        {
            try
            {
                if (store.Get(roomId) is { RunStartedAt: null } room && transcripts.Size(room.SessionId) is { } size && size != room.KnownSize)
                    store.Update(roomId, r => r.RunStartedAt is null ? r with { KnownSize = size } : r);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        wait?.Ended.TrySetResult(null);
        run.Done.TrySetResult();
    }

    /// <summary>방 규칙 글 (실행 준비 값으로) — 처음 띄울 때와 이어 쓰는 턴이 같은 값을 만든다.</summary>
    // 실측 전용 표시는 스티커처럼 지금 값으로 읽는다 — 쉬는 실행이 띄울 때 값에 묶이면 작업용으로 돌린 것이 지휘자에게 안 간다 (/self-review 9078a0fc44 C1)
    internal string RulesFor(Room room, RunSetup s) =>
        RoomRules.For(room.Level, terminal: s.Terminal, unity: s.Unity, unityMeasure: s.Unity is { } u && options.Value.IsMeasureUnity(u),
            askWrites: s.AppAskWrites, rest: room.GroupId is null,
            extras: [.. s.Extras.Where(x => x != McpConfigFile.UnityServer)],
            // 모델 선택창 안내는 기본 서버가 붙고 요청 창 · 스위치가 있는 방에만 (785ebb5e97 C7)
            modeCard: s.McpOn && !room.McpOff.Contains(options.Value.McpServer) && room.GroupId is null)
        + StickerRules.For(Stickers?.Options) // 설정 태그 · Hints가 바뀌면 지문이 바뀌어 다음 말에 다시 붙는다
        + GroupRules.For(store, room, options.Value.WorkspacePath); // 단톡방 구성원 방이면 역할 · 다른 구성원
}
