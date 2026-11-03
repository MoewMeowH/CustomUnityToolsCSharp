using System.Collections.Concurrent;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// 쉬는 실행 (ClaudeSession) — 진짜 claude 대신 TestData/fake-claude-session.ps1 (한 프로세스 여러 턴, 2.1.316 실측 모양).
/// 시간 값은 세션마다 넘긴다 (전역 기본값은 시험 어셈블리 시작 때 정한 값 그대로).
/// </summary>
public sealed class ClaudeSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-session-" + Guid.NewGuid().ToString("N"));
    private ClaudeSession.SessionTimings _timings = new() { IdleGrace = TimeSpan.FromMilliseconds(700), CloseGrace = TimeSpan.FromSeconds(1) };
    private readonly ConcurrentQueue<StreamEvent> _events = new();
    private readonly List<ToolPermission> _asked = [];
    private SessionState? _askedIn;
    private ClaudeSession? _session;

    public ClaudeSessionTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _session?.Stop();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ProcessSpec Fake(params string[] extra) => new("powershell.exe",
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
         Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude-session.ps1"), Guid.NewGuid().ToString("D"), .. extra], Path.GetTempPath());

    private async Task<ClaudeSession> Start(string prompt)
    {
        var (session, error) = await ClaudeSession.StartAsync(Fake(ClaudeSession.ReplayFlag), prompt, ev => _events.Enqueue(ev), (p, _) =>
        {
            lock (_asked)
            {
                _asked.Add(p);
                _askedIn = _session?.State;
            }
            return Task.FromResult(PermissionDecision.Allow(p.InputJson));
        }, _timings);
        Assert.Null(error);
        return _session = session!;
    }

    /// <summary>조건에 맞는 이벤트가 올 때까지 (앞의 이벤트는 버린다).</summary>
    private async Task<T> Next<T>(Func<T, bool>? match = null, int seconds = 20) where T : StreamEvent
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            while (_events.TryDequeue(out var ev))
                if (ev is T t && (match?.Invoke(t) ?? true)) return t;
            await Task.Delay(20);
        }
        throw new TimeoutException($"{typeof(T).Name}가 오지 않았다");
    }

    private async Task<SessionExit> Exit(int seconds = 20) => await _session!.Completion.WaitAsync(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task MissingReplayFlag_IsRefused()
    {
        var (session, error) = await ClaudeSession.StartAsync(Fake(), "say hi", _ => { }, null);
        Assert.Null(session);
        Assert.Contains(ClaudeSession.ReplayFlag, error);
    }

    /// <summary>
    /// 기본 (2.1.316 실측 1): 작업 시작 → 쉼 → 쉬는 중 말 → 그 말의 턴 → 작업 끝 → 자동 턴 → 작업 없음 → 잠깐 뒤 닫힘.
    /// </summary>
    [Fact]
    public async Task Rest_Message_AutoTurn_ThenCloses()
    {
        var session = await Start("bg 2500 b1");
        var first = await Next<TurnEndedEvent>();
        Assert.Equal((true, "started b1", SessionState.Idle), (first.User, first.Result.Text, first.Next));
        Assert.Equal("b1", Assert.Single((await Next<SessionIdleEvent>()).Tasks).TaskId);
        Assert.NotNull(session.CapAt);

        Assert.Equal(SendStatus.Sent, await session.SendAsync("say hi"));
        Assert.False((await Next<TurnBeganEvent>()).Auto);
        var reply = await Next<TurnEndedEvent>();
        Assert.Equal((true, "hi", SessionState.Idle), (reply.User, reply.Result.Text, reply.Next));

        Assert.True((await Next<TurnBeganEvent>()).Auto);
        var auto = await Next<TurnEndedEvent>();
        Assert.Equal((false, "auto b1"), (auto.User, auto.Result.Text));
        Assert.Equal("done", (await Next<SessionClosingEvent>()).By);
        var exit = await Exit();
        Assert.Equal((false, "done", 0, 0), (exit.Cut, exit.ClosedBy, exit.Left, exit.ExitCode));
        Assert.Equal(SendStatus.Closed, await session.SendAsync("say late"));
    }

    /// <summary>작업이 없는 보통 턴: result 뒤 잠깐 쉬었다가(그사이 말이면 같은 프로세스) 닫힌다.</summary>
    [Fact]
    public async Task NoTasks_ClosesAfterGrace_ButTakesMessageMeanwhile()
    {
        var session = await Start("say one");
        Assert.Equal(SessionState.Idle, (await Next<TurnEndedEvent>()).Next);
        Assert.Equal(SendStatus.Sent, await session.SendAsync("say two"));
        Assert.Equal("two", (await Next<TurnEndedEvent>(e => e.User)).Result.Text);
        Assert.Equal("done", (await Next<SessionClosingEvent>()).By);
        Assert.False((await Exit()).Cut);
    }

    /// <summary>IdleGrace 0: 작업 없는 턴은 result에서 바로 닫기로 정한다 — 그 알림 뒤의 말은 새 프로세스로 (Closed).</summary>
    [Fact]
    public async Task ZeroGrace_ClosesAtResult()
    {
        _timings = _timings with { IdleGrace = TimeSpan.Zero };
        var session = await Start("say one");
        Assert.Equal(SessionState.Closing, (await Next<TurnEndedEvent>()).Next);
        Assert.Equal(SendStatus.Closed, await session.SendAsync("say two"));
        Assert.Equal("done", (await Next<SessionClosingEvent>()).By);
        Assert.False((await Exit()).Cut);
    }

    /// <summary>턴 도중 작업이 끝나면 그 턴 답에 들어가고 자동 턴은 없다 (2.1.316 실측 2). 응답 중의 말은 Busy.</summary>
    [Fact]
    public async Task TaskEndsDuringTurn_IsFolded_NoAutoTurn()
    {
        var session = await Start("bg 1000 b2");
        await Next<SessionIdleEvent>();
        Assert.Equal(SendStatus.Sent, await session.SendAsync("fg 2500"));
        await Next<TurnBeganEvent>();
        Assert.Equal(SendStatus.Busy, await session.SendAsync("say queued"));
        var fg = await Next<TurnEndedEvent>();
        Assert.Equal((true, "fg done (also ended: b2)"), (fg.User, fg.Result.Text));
        Assert.Equal("done", (await Next<SessionClosingEvent>()).By);
        await Exit();
        Assert.DoesNotContain(_events, e => e is TurnBeganEvent);
    }

    /// <summary>
    /// 끝 알림과 동시에 넣은 말 (2.1.316 실측 3): 자동 턴이 먼저 돌고(되돌림 없음 → User=false, 보낸 말은 아직 기다림) 그 뒤 말의 턴.
    /// </summary>
    [Fact]
    public async Task MessageRacesNotification_AutoTurnFirst_ThenUserTurn()
    {
        var session = await Start("bgnow b3");
        await Next<SessionIdleEvent>();
        Assert.Equal(SendStatus.Sent, await session.SendAsync("say mine"));
        Assert.False((await Next<TurnBeganEvent>()).Auto); // 보낸 뒤라 응답 중 — 아직 누구 턴인지 모른다
        var auto = await Next<TurnEndedEvent>();
        Assert.Equal((false, "auto b3", SessionState.Responding), (auto.User, auto.Result.Text, auto.Next));
        Assert.Equal(SendStatus.Busy, await session.SendAsync("say other"));
        var mine = await Next<TurnEndedEvent>();
        Assert.Equal((true, "mine", SessionState.Idle), (mine.User, mine.Result.Text, mine.Next));
        await Exit();
    }

    /// <summary>작업 하나 끝내기 (stop_task): 프로세스는 살고 자동 턴 없이, 작업이 없어지면 닫힌다. 없는 작업은 false.</summary>
    [Fact]
    public async Task StopTask_StopsWithoutAutoTurn()
    {
        var session = await Start("server s1");
        await Next<SessionIdleEvent>();
        Assert.False(await session.StopTaskAsync("nope"));
        Assert.True(await session.StopTaskAsync("s1"));
        Assert.Empty((await Next<SessionIdleEvent>(e => e.Tasks.Count == 0)).Tasks);
        Assert.Equal("done", (await Next<SessionClosingEvent>()).By);
        await Exit();
        Assert.DoesNotContain(_events, e => e is TurnBeganEvent);
    }

    /// <summary>모두 끝내기: 쉬는 중에만. 안 끝나는 작업이 있어 CLI가 안 끝나면 유예 뒤 트리 정리.</summary>
    [Fact]
    public async Task WrapUp_KillsServerAfterGrace()
    {
        var session = await Start("server s2");
        await Next<SessionIdleEvent>();
        Assert.True(session.WrapUp());
        Assert.False(session.WrapUp());
        Assert.Equal(("wrapup", 1), ((await Next<SessionClosingEvent>()).By, (await Exit()).Left));
        Assert.Equal("wrapup", (await session.Completion).ClosedBy);
        Assert.False((await session.Completion).Cut);
    }

    /// <summary>응답 중 "지금 끝내기"는 턴을 자르지 않고 그 턴 끝에 닫는다 (/cross-review 52ac1810e1 self C6).</summary>
    [Fact]
    public async Task WrapUp_WhileResponding_ClosesAtTurnEnd()
    {
        var session = await Start("server s6");
        await Next<SessionIdleEvent>();
        Assert.Equal(SendStatus.Sent, await session.SendAsync("fg 1500"));
        await Next<TurnBeganEvent>();
        Assert.True(session.WrapUp());
        var fg = await Next<TurnEndedEvent>(e => e.User);
        Assert.Equal(("fg done", SessionState.Closing), (fg.Result.Text, fg.Next));
        Assert.Equal("wrapup", (await Next<SessionClosingEvent>()).By);
        Assert.Equal(("wrapup", 1), ((await Exit()).ClosedBy, (await session.Completion).Left));
    }

    /// <summary>설정 확인 · 권한 낮춤의 끝내기: 응답 중이면 쓰던 답을 끝까지 받고 그 턴 끝에 닫는다 (/self-review af6ffa3da5 C1).</summary>
    [Fact]
    public async Task EndAfterTurn_WhileResponding_KeepsAnswer()
    {
        var session = await Start("server s9");
        await Next<SessionIdleEvent>();
        Assert.Equal(SendStatus.Sent, await session.SendAsync("fg 1500"));
        await Next<TurnBeganEvent>();
        session.EndAfterTurn("settings");
        var fg = await Next<TurnEndedEvent>(e => e.User);
        Assert.Equal(("fg done", true, SessionState.Closing), (fg.Result.Text, fg.Result.Ok, fg.Next));
        var exit = await Exit();
        Assert.Equal(("settings", 1, false), (exit.ClosedBy, exit.Left, exit.Cut));
    }

    /// <summary>쉬는 중이면 끝내기는 바로 정리.</summary>
    [Fact]
    public async Task EndAfterTurn_WhileIdle_StopsNow()
    {
        var session = await Start("server s10");
        await Next<SessionIdleEvent>();
        session.EndAfterTurn("settings");
        Assert.Equal(("settings", false), ((await Exit(10)).ClosedBy, (await session.Completion).Cut));
    }

    /// <summary>상한: 쉬는 중이면 바로 닫는다.</summary>
    [Fact]
    public async Task Cap_WhileIdle_Closes()
    {
        _timings = _timings with { Cap = TimeSpan.FromSeconds(1.5) };
        var session = await Start("server s3");
        await Next<SessionIdleEvent>();
        Assert.Equal("cap", (await Next<SessionClosingEvent>()).By);
        var exit = await Exit();
        Assert.Equal(("cap", 1, false), (exit.ClosedBy, exit.Left, exit.Cut));
    }

    /// <summary>상한이 응답 중에 지나면 턴을 자르지 않고 그 턴 끝에 닫는다.</summary>
    [Fact]
    public async Task Cap_WhileResponding_ClosesAtTurnEnd()
    {
        // 상한은 첫 작업을 본 때부터 — 쉼에 들어선 뒤 바로 보낸 긴 턴 도중에 지나가게. 전체 시험 부하에서도 보내기 전에 지나지 않게 넉넉히
        _timings = _timings with { Cap = TimeSpan.FromSeconds(5) };
        var session = await Start("server s4");
        await Next<SessionIdleEvent>();
        Assert.Equal(SendStatus.Sent, await session.SendAsync("fg 9000"));
        var fg = await Next<TurnEndedEvent>(e => e.User && e.Result.Text == "fg done");
        Assert.Equal(SessionState.Closing, fg.Next);
        Assert.Equal("cap", (await Next<SessionClosingEvent>()).By);
        Assert.False((await Exit()).Cut);
    }

    /// <summary>쉬는 중 모델 · 강도 변경 (set_model · apply_flag_settings): 프로세스와 작업을 살린 채.</summary>
    [Fact]
    public async Task SetModelAndEffort_WhileResting()
    {
        var session = await Start("server s5");
        await Next<SessionIdleEvent>();
        Assert.True(await session.SetModelAsync("haiku"));
        Assert.True(await session.SetEffortAsync("low"));
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Single(session.Tasks);
    }

    /// <summary>쉬는 중 백그라운드 작업(서브에이전트)의 승인 요청도 받는다 — 허용 뒤 작업이 끝나 자동 턴.</summary>
    [Fact]
    public async Task PermissionWhileResting_IsAnswered()
    {
        await Start("bgask 500 a1");
        await Next<SessionIdleEvent>();
        var auto = await Next<TurnEndedEvent>(e => !e.User);
        Assert.Equal("auto a1", auto.Result.Text);
        lock (_asked)
        {
            Assert.Equal("Bash", Assert.Single(_asked).Tool);
            Assert.Equal(SessionState.Idle, _askedIn);
        }
    }

    /// <summary>턴 도중 프로세스가 죽으면 Cut.</summary>
    [Fact]
    public async Task DiesMidTurn_IsCut()
    {
        await Start("die");
        var exit = await Exit();
        Assert.Equal((true, 3, null), (exit.Cut, exit.ExitCode, exit.ClosedBy));
        Assert.Contains("fake died", exit.Stderr);
    }

    /// <summary>응답 중 중지: 바로 트리를 끝내고 Cut · stop.</summary>
    [Fact]
    public async Task StopWhileResponding_IsCut()
    {
        var session = await Start("fg 30000");
        await Next<TurnBeganEvent>();
        session.Stop();
        var exit = await Exit(10);
        Assert.Equal((true, "stop"), (exit.Cut, exit.ClosedBy));
    }
}
