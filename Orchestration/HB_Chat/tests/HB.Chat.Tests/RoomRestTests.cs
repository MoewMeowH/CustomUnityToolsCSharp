using System.Threading.Channels;
using HB.Chat;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 방의 쉬는 실행 (백그라운드 명령 — Claude 앱처럼): RoomHub + ClaudeSession, 진짜 claude 대신 TestData/fake-claude-session.ps1
/// (한 프로세스 여러 턴 · 2.1.316 실측 모양). 시간 값은 이 허브에만 준다.
/// </summary>
public sealed class RoomRestTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-rest-" + Guid.NewGuid().ToString("N"));
    private readonly RoomStore _store;
    private readonly RoomHub _hub;
    private readonly ChatOptions _options;
    private int _launched;

    public RoomRestTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        Directory.CreateDirectory(Path.Combine(_dir, "projects"));
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        _options = new ChatOptions
        {
            DataDir = Path.Combine(_dir, "data"),
            ClaudeProjectsDir = Path.Combine(_dir, "projects"),
            Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws") },
            ApprovalTimeoutSeconds = 20,
        };
        _store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude-session.ps1");
        _hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(_options))
        {
            Log = new ApprovalLog(() => _options.DataFolder),
            ProcessFactory = (room, spec, workspace) =>
            {
                Interlocked.Increment(ref _launched);
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId], workspace);
            },
            SessionTimings = new() { IdleGrace = TimeSpan.FromSeconds(1.5), CloseGrace = TimeSpan.FromSeconds(1) },
        };
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Room CommandRoom() => _store.Create("ws", PermissionLevel.Command, "opus", null);

    /// <summary>모든 조건을 본 뒤까지 읽는다 (턴 끝과 작업 줄은 차례가 정해져 있지 않다).</summary>
    private static async Task<List<LiveEvent>> UntilAll(Channel<LiveEvent> ch, params Func<LiveEvent, bool>[] all)
    {
        var seen = new List<LiveEvent>();
        var left = all.ToList();
        using var cts = new CancellationTokenSource(Wait);
        try
        {
            await foreach (var ev in ch.Reader.ReadAllAsync(cts.Token))
            {
                if (ev.Kind is "rooms" or "face") continue;
                seen.Add(ev);
                left.RemoveAll(f => f(ev));
                if (left.Count == 0) break;
            }
        }
        catch (OperationCanceledException)
        {
            // 시간 초과면 그때까지 본 알림을 보인다
            throw new TimeoutException("기다리던 알림이 오지 않음. 본 알림: " + string.Join(", ",
                seen.Select(e => e.Kind + (e.BackgroundCount is { } n ? $"[{n}]" : "") + (e.Text is { } t ? "(" + t[..Math.Min(60, t.Length)] + ")" : ""))));
        }
        return seen;
    }

    /// <summary>'!' 명령: 앱이 작업 폴더에서 돌리고 끝나면 결과를 bash 모드 꼴의 말 하나로 지휘자에게 보낸다.</summary>
    [Fact]
    public async Task Shell_RunsInWorkspace_ThenSendsResultToConductor()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.RunShell(room.Id, "echo SHELL_OK& cd", Path.Combine(_dir, "ws")));
        var seen = await UntilAll(ch, e => e.Kind == "shell-end" && e.Ok == true,
            e => e.Kind == "start" && e.Prompt?.StartsWith("<bash-input>echo SHELL_OK& cd</bash-input>\n<bash-stdout>SHELL_OK", StringComparison.Ordinal) == true,
            e => e.Kind == "end");
        Assert.Contains(seen, e => e.Kind == "shell-start" && e.Shell?.Command == "echo SHELL_OK& cd");
        var prompt = seen.First(e => e.Kind == "start").Prompt!;
        Assert.Contains(Path.Combine(_dir, "ws"), prompt);   // cd = 작업 폴더
        Assert.DoesNotContain("<bash-stderr>", prompt);       // 종료 0
        Assert.Equal(1, _launched);
    }

    /// <summary>도는 중에는 하나만, [중지]는 트리째 끄고 "사용자가 중지"로 결과를 보낸다.</summary>
    [Fact]
    public async Task Shell_OneAtATime_StopKillsAndReports()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        var ws = Path.Combine(_dir, "ws");
        Assert.Null(_hub.RunShell(room.Id, "ping -n 60 127.0.0.1", ws));
        await UntilAll(ch, e => e.Kind == "shell-start");
        Assert.NotNull(_hub.RunShell(room.Id, "echo again", ws));
        Assert.True(_hub.StopShell(room.Id));
        await UntilAll(ch, e => e.Kind == "shell-end" && e.Ok == false && e.Detail!.StartsWith("사용자가 중지"),
            e => e.Kind == "start" && e.Prompt!.Contains("<bash-stderr>사용자가 중지</bash-stderr>"));
        Assert.False(_hub.StopShell(room.Id));
    }

    /// <summary>작업 폴더의 같은 이름 스크립트가 PATH의 명령보다 먼저 돌지 않는다 (보안 C1 — cmd 실측과 같은 꼴).</summary>
    [Fact]
    public async Task Shell_DoesNotRunWorkspaceScriptForBareName()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        var ws = Path.Combine(_dir, "ws");
        File.WriteAllText(Path.Combine(ws, "hostname.bat"), "@echo HIJACKED");
        Assert.Null(_hub.RunShell(room.Id, "hostname", ws));
        var seen = await UntilAll(ch, e => e.Kind == "start" && e.Prompt!.StartsWith("<bash-input>hostname", StringComparison.Ordinal));
        Assert.DoesNotContain("HIJACKED", seen.Last().Prompt);
        Assert.Contains(Environment.MachineName, seen.Last().Prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>지휘자용 출력: 앞 · 뒤 15,000자 + 가운데 생략, 화면 snapshot은 보낸 것까지만, 서로게이트 쌍은 가르지 않는다 (C3 · C8 · C10).</summary>
    [Fact]
    public void ShellRun_KeepsHeadAndTail_AndSnapshotExcludesUnsent()
    {
        var run = new RoomHub.ShellRun("x");
        run.Keep(new string('a', RoomHub.ShellKeepChars - 1) + "😀" + new string('b', 40_000) + "끝");
        var output = run.Output();
        Assert.StartsWith(new string('a', RoomHub.ShellKeepChars - 1) + "\n… (가운데 ", output); // 😀 앞에서 잘림 (반쪽 없음)
        Assert.EndsWith("b끝", output);
        Assert.DoesNotContain('\uD83D', output.Split('\n')[0]);
        Assert.Equal("", run.SentView());               // 아직 안 보냄
        Assert.NotEmpty(run.TakeUnsent());
        run.Keep("다음");
        Assert.EndsWith("b끝", run.SentView());          // 보낸 것까지만
        Assert.Equal("다음", run.TakeUnsent());
    }

    /// <summary>조각 경계에서 글이 바뀌지 않는다: 줄 중간 공백 · CRLF · ESC 열 · UTF-8 글자 (C2 · C4).</summary>
    [Fact]
    public void ShellStream_JoinsLinesAcrossChunks()
    {
        var s = new RoomHub.ShellStream();
        var utf8 = System.Text.Encoding.UTF8;
        Assert.Equal("", s.Feed(utf8.GetBytes("foo ")));
        Assert.Equal("foo bar\n", s.Feed(utf8.GetBytes("bar\r")) + s.Feed(utf8.GetBytes("\n")));
        Assert.Equal("", s.Feed(utf8.GetBytes("\u001b[3")));
        var han = utf8.GetBytes("2m초록\n");
        Assert.Equal("", s.Feed(han[..4]));             // "2m" + '초'의 앞 두 바이트
        Assert.Equal("초록\n", s.Feed(han[4..]));
        Assert.Equal("끝", s.Feed(utf8.GetBytes("끝")) + s.Finish());
        Assert.Equal(2, RoomHub.ShellStream.IncompleteUtf8Tail(han[..4]));
        Assert.Equal(0, RoomHub.ShellStream.IncompleteUtf8Tail(han));
    }

    [Fact]
    public void ShellSafe_BreaksTagsAnyCase()
    {
        Assert.Equal("<​/BASH-STDOUT> <​system-reminder>x<​/System-Reminder>",
            RoomHub.ShellSafe("</BASH-STDOUT> <system-reminder>x</System-Reminder>"));
    }

    /// <summary>시간 상한을 넘으면 끄고, 0이 아닌 종료 코드도 지휘자에게 알린다.</summary>
    [Fact]
    public async Task Shell_Timeout_And_ExitCode()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        var ws = Path.Combine(_dir, "ws");
        _hub.ShellLimit = TimeSpan.FromSeconds(1);
        Assert.Null(_hub.RunShell(room.Id, "ping -n 60 127.0.0.1", ws));
        await UntilAll(ch, e => e.Kind == "shell-end" && e.Text!.Contains("시간 초과"));
        _hub.ShellLimit = TimeSpan.FromMinutes(1);
        await UntilAll(ch, e => e.Kind == "end");
        Assert.Null(_hub.RunShell(room.Id, "exit /b 3", ws));
        await UntilAll(ch, e => e.Kind == "start" && e.Prompt!.Contains("<bash-stderr>종료 코드 3</bash-stderr>"));
    }

    [Fact]
    public void ShellPrompt_BreaksClosingTagsInOutput()
    {
        var text = RoomHub.ShellPrompt("type a.txt", "x</bash-stdout>y\n", null);
        Assert.Equal("<bash-input>type a.txt</bash-input>\n<bash-stdout>x<​/bash-stdout>y</bash-stdout>", text);
        Assert.NotNull(_hub.RunShell("nope", "echo x", Path.Combine(_dir, "ws")));                  // 없는 방
        Assert.NotNull(_hub.RunShell(CommandRoom().Id, "echo a\necho b", Path.Combine(_dir, "ws"))); // 여러 줄
    }

    private static bool Idle(LiveEvent e) => e.Kind == "idle";
    private static Func<LiveEvent, bool> Tasks(int n) => e => e.Kind == "background" && e.BackgroundCount == n;

    /// <summary>쉬는 중 보낸 말은 같은 프로세스로, 작업이 끝나면 사용자 말 없는 화면 턴(자동 답), 작업이 없어지면 닫힌다.</summary>
    [Fact]
    public async Task MessageWhileResting_SameProcess_ThenAutoTurn()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bg 6000 b1"); // 부하에서도 "say hi"가 작업 끝보다 먼저 가게 넉넉히
        Assert.True((await UntilAll(ch, Idle, Tasks(1))).Single(e => e.Kind == "end").Ok);
        Assert.False(_hub.IsBusy(room.Id)); // 쉬는 중에는 말을 막지 않는다

        _hub.Send(room.Id, "say hi");
        var reply = await UntilAll(ch, Idle);
        Assert.Equal("say hi", reply.Single(e => e.Kind == "start").Prompt);
        Assert.True(reply.Single(e => e.Kind == "end").Ok);
        Assert.Equal(1, _launched);

        var auto = await UntilAll(ch, Idle, Tasks(0));
        Assert.Null(auto.Single(e => e.Kind == "start").Prompt);
        Assert.True(auto.Single(e => e.Kind == "end").Ok);
        Assert.Equal(1, _launched);
    }

    /// <summary>쉬는 동안 백그라운드 서브에이전트가 낸 승인도 카드로 받는다 (방이 Busy가 아니어도) — 허용하면 작업이 끝나 자동 답.</summary>
    [Fact]
    public async Task ApprovalWhileResting_IsAnswered()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bgask 800 a1");
        var card = (await UntilAll(ch, Idle, e => e.Kind == "approval")).Single(e => e.Kind == "approval").Approval!;
        Assert.True(card.FromAgent);
        Assert.False(_hub.IsBusy(room.Id));
        Assert.True(_hub.IsResting(room.Id));
        // 쉬는 동안(화면 턴 없음)에는 "이 턴 동안 묻지 않기"가 없다 — 턴 경계가 없어 쉬는 시간 전체로 넓어진다 (self C8)
        Assert.False(card.TurnAllow);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.AllowTurn, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var auto = await UntilAll(ch, e => e.Kind == "approval-done", Idle);
        Assert.Equal("허용", auto.Single(e => e.Kind == "approval-done").Text);
        Assert.Null(auto.Single(e => e.Kind == "start").Prompt);
        Assert.True(auto.Single(e => e.Kind == "end").Ok);
    }

    /// <summary>쉬는 중 모델 · 강도만 바꾸면 프로세스를 살린 채 (set_model · apply_flag_settings) 같은 프로세스로 보낸다.</summary>
    [Fact]
    public async Task ModelChangedWhileResting_SameProcess()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s1");
        await UntilAll(ch, Idle, Tasks(1));
        _store.Update(room.Id, r => r with { Model = "haiku", Effort = "low" });
        _hub.Send(room.Id, "say after");
        Assert.True((await UntilAll(ch, Idle)).Single(e => e.Kind == "end").Ok);
        Assert.Equal(1, _launched);
    }

    /// <summary>
    /// 쉬는 중 프로세스 인자가 바뀌는 설정(예: 외부 요청 전에 묻기)을 바꾸면 알리고 작업을 끝낸 뒤 새로 띄운다 (임시 — 확인 창 전).
    /// </summary>
    [Fact]
    public async Task SettingsChangedWhileResting_RestartsWithNotice()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s2");
        await UntilAll(ch, Idle, Tasks(1));
        _store.Update(room.Id, r => r with { AskRunMode = !r.AskRunMode });
        _hub.Send(room.Id, "say fresh");
        var seen = await UntilAll(ch, Idle, e => e.Kind == "notice");
        Assert.Contains("백그라운드 작업 1개를 끝내고 새로 시작합니다", seen.Single(e => e.Kind == "notice").Text);
        Assert.True(seen.Single(e => e.Kind == "end").Ok);
        Assert.Equal(2, _launched);
    }

    /// <summary>쉬는 중 설정(인자)이 바뀐 뒤 작업이 끝나면, 옛 인자로 자동 턴을 돌리지 않고 실행을 끝낸다 (security C1).</summary>
    [Fact]
    public async Task SettingsChangedWhileResting_AutoTurnNotRun()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bg 2000 b2");
        await UntilAll(ch, Idle, Tasks(1));
        _store.Update(room.Id, r => r with { AskRunMode = !r.AskRunMode });
        var after = await UntilAll(ch, Tasks(0));
        for (var i = 0; i < 40 && _hub.IsResting(room.Id); i++) await Task.Delay(50);
        Assert.False(_hub.IsResting(room.Id));
        Assert.DoesNotContain(after, e => e.Kind == "start");
        Assert.False(_hub.IsBusy(room.Id));
    }

    /// <summary>휴지통 · 권한 낮춤이 쓰는 EndRest: 쉬는 실행을 작업째 끝내고 기록 줄을 남긴다 (1-F4).</summary>
    [Fact]
    public async Task EndRest_StopsRestingRun()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s4");
        await UntilAll(ch, Idle, Tasks(1));
        Assert.True(_hub.IsResting(room.Id));
        _hub.EndRest(room.Id);
        await UntilAll(ch, Tasks(0));
        Assert.False(_hub.IsResting(room.Id));
        Assert.Contains("기다리는 중 멈춰 백그라운드 작업 1개를 정리했습니다", File.ReadAllText(Path.Combine(_options.DataFolder, "approvals.jsonl")));
    }

    /// <summary>작업 줄에 작업 목록이 실리고, 하나만 끝내면 프로세스는 살고 자동 턴도 없다 (stop_task). 방 목록 수도 따라간다.</summary>
    [Fact]
    public async Task TaskList_StopOneTask_KeepsOthers()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s7");
        await UntilAll(ch, Idle, Tasks(1));
        _hub.Send(room.Id, "server s8");
        var two = (await UntilAll(ch, Idle, Tasks(2))).Last(e => e.Kind == "background" && e.BackgroundCount == 2);
        Assert.Equal(["s7", "s8"], two.Tasks!.Select(t => t.Id).Order());
        Assert.All(two.Tasks!, t => Assert.Equal("command", t.Kind));
        Assert.Equal(2, _hub.RestingTasks(room.Id));

        Assert.NotNull(await _hub.StopTaskAsync(room.Id, "nope"));
        Assert.Null(await _hub.StopTaskAsync(room.Id, "s7"));
        var one = (await UntilAll(ch, Tasks(1))).Last(e => e.Kind == "background");
        Assert.Equal("s8", Assert.Single(one.Tasks!).Id);
        Assert.True(_hub.IsResting(room.Id));
        Assert.Equal(1, _hub.RestingTasks(room.Id));
        Assert.Equal(1, _launched);
    }

    /// <summary>
    /// + 요청 창 말(고른 값) 뒤의 보통 말은 같은 프로세스로 — 고른 값은 말마다 바뀌어 열쇠에 넣지 않는다 (/self-review af6ffa3da5 C2).
    /// 묻지 않는 인자로 띄운 실행에 고른 말이 오면 그때만 새로.
    /// </summary>
    [Fact]
    public async Task PickedMessage_ThenPlain_SameProcess()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server p1");
        await UntilAll(ch, Idle, Tasks(1));
        var pick = new ModePick("design_main", new Dictionary<string, ModeRowPick>());
        _hub.Send(room.Id, "server p2", pick, interrupt: false);
        await UntilAll(ch, Idle, Tasks(1));
        Assert.Equal(2, _launched); // 묻지 않는 인자로 띄운 실행 — 고른 말은 새로
        _hub.Send(room.Id, "say plain");
        Assert.True((await UntilAll(ch, Idle)).Single(e => e.Kind == "end").Ok);
        Assert.Equal(2, _launched); // 묻는 인자로 띄운 실행은 보통 말도 이어 쓴다
        Assert.True(_hub.IsResting(room.Id));
    }

    /// <summary>쉬는 동안 첫 첨부가 생겨도 같은 프로세스 — 첨부 폴더는 띄우기 전에 만들어 둔다 (af6ffa3da5 C3).</summary>
    [Fact]
    public async Task FirstAttachmentWhileResting_SameProcess()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server a1");
        await UntilAll(ch, Idle, Tasks(1));
        var folder = Attachments.Folder(_options.DataFolder, room.Id);
        Assert.True(Directory.Exists(folder));
        File.WriteAllText(Path.Combine(folder, "note.txt"), "x");
        _hub.Send(room.Id, "say attached");
        var seen = await UntilAll(ch, Idle);
        Assert.True(seen.Single(e => e.Kind == "end").Ok);
        Assert.DoesNotContain(seen, e => e.Kind == "notice");
        Assert.Equal(1, _launched);
    }

    /// <summary>방 규칙: 쉬는 실행 방은 "답을 끝내도 계속 돈다 · 120분", 단톡방 구성원은 예전 문구.</summary>
    [Fact]
    public void Rules_RestingRoomsSayTasksOutliveTurn()
    {
        var rest = RoomRules.For(PermissionLevel.Command, terminal: true, rest: true);
        Assert.Contains("답을 끝내도 계속 돈다", rest);
        Assert.Contains($"최대 {ClaudeSession.RestCapMinutes}분", rest);
        Assert.DoesNotContain("그 턴에서만 산다", rest);
        var group = RoomRules.For(PermissionLevel.Command, terminal: true);
        Assert.Contains("그 턴에서만 산다", group);
    }

    /// <summary>쉬는 중 "지금 끝내기"는 안 끝나는 작업째 정리하고 기록 줄을 남긴다. 그 뒤 말은 새 프로세스로.</summary>
    [Fact]
    public async Task WrapUpWhileResting_ThenNewProcess()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s3");
        await UntilAll(ch, Idle, Tasks(1));
        _hub.WrapUp(room.Id);
        await UntilAll(ch, Tasks(0));
        Assert.Contains("'지금 끝내기'로 정리했습니다", File.ReadAllText(Path.Combine(_options.DataFolder, "approvals.jsonl")));
        _hub.Send(room.Id, "say next");
        Assert.True((await UntilAll(ch, Idle)).Single(e => e.Kind == "end").Ok);
        Assert.Equal(2, _launched);
    }

    /// <summary>
    /// 쉬는 실행은 마지막 턴이 끝난 뒤에야 프로세스가 끝나고 CLI가 그때 세션 기록에 줄을 더 쓴다 — 프로세스가 끝나면 그 크기를 이 앱이 본 크기로 적어,
    /// 다음 말에 "이 앱 밖에서 바뀌었습니다" 경고가 잘못 뜨지 않게 (사용자 화면 2026-11-04). 가짜 CLI는 기록을 쓰지 않아 시험이 턴 끝 뒤에 덧붙인다.
    /// </summary>
    [Fact]
    public async Task TranscriptGrownAfterTurn_ByRestingProcess_IsNotOutsideChange()
    {
        var room = CommandRoom();
        var transcript = Path.Combine(_dir, "projects", "F--ws", room.SessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "{\"type\":\"user\"}\n");
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "say hi");
        await UntilAll(ch, Idle);
        Assert.True(_hub.IsResting(room.Id));
        File.AppendAllText(transcript, "{\"type\":\"last-prompt\"}\n"); // 프로세스가 끝나며 쓰는 줄
        for (var i = 0; i < 100 && _hub.IsResting(room.Id); i++) await Task.Delay(50);
        Assert.False(_hub.IsResting(room.Id));
        for (var i = 0; i < 40 && _store.Get(room.Id)!.KnownSize != new FileInfo(transcript).Length; i++) await Task.Delay(50);
        Assert.Equal(new FileInfo(transcript).Length, _store.Get(room.Id)!.KnownSize);

        _hub.Send(room.Id, "say again");
        var next = await UntilAll(ch, Idle);
        Assert.DoesNotContain(next, e => e.Kind == "notice" && e.Text!.Contains("앱 밖에서"));
    }

    /// <summary>
    /// 새 말이 쉬는 실행을 끝내고 새로 띄울 때: 끝나며 CLI가 쓴 줄은 이 앱이 본 크기로 적지만(사용자 화면 2026-11-04: 1,171,421 → 1,173,341),
    /// 끝내기 전에 이미 늘어 있던 것(쉬는 동안 다른 곳에서 이어 씀)은 덮지 않고 경고한다. 가짜 CLI는 죽을 때 기록을 쓸 수 없어 앞쪽은 이 시험으로 재현하지 못한다.
    /// </summary>
    [Fact]
    public async Task TranscriptGrownWhileResting_ThenRetired_StillWarnsOutsideChange()
    {
        var room = CommandRoom();
        var transcript = Path.Combine(_dir, "projects", "F--ws", room.SessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "{\"type\":\"user\"}\n");
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s4");
        await UntilAll(ch, Idle, Tasks(1));
        File.AppendAllText(transcript, "{\"type\":\"user\"}\n"); // 쉬는 동안 다른 곳에서 이어 쓴 줄
        _store.Update(room.Id, r => r with { AskRunMode = !r.AskRunMode }); // 새 말이 쉬는 실행을 끝내고 새로 띄우게
        _hub.Send(room.Id, "say fresh");
        var seen = await UntilAll(ch, Idle);
        Assert.True(seen.Single(e => e.Kind == "end").Ok);
        Assert.Equal(2, _launched);
        Assert.Contains(seen, e => e.Kind == "notice" && e.Text!.Contains("앱 밖에서"));
    }

    /// <summary>
    /// 쉬는 실행에서 답하는 도중 끼어들기: 프로세스를 끊지 않고 interrupt만 보낸다 — 그 턴은 "끼어들기로 멈춤", 백그라운드 작업은 살고,
    /// 새 말은 같은 프로세스로 답한다 (결정 "끼어들기만 살림", 2.1.316 실측 2026-11-04).
    /// </summary>
    [Fact]
    public async Task Interrupt_KeepsBackgroundTasks_SameProcess()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s1");
        await UntilAll(ch, Idle, Tasks(1));
        _hub.Send(room.Id, "fg 8000");
        await UntilAll(ch, e => e.Kind == "start" && e.Prompt == "fg 8000");
        await UntilAnsweringUser(room.Id); // 프로세스가 그 말을 받아 답하는 중이 되게
        Assert.Null(_hub.Interrupt(room.Id, "say after"));
        var seen = await UntilAll(ch, e => e.Kind == "end" && e.Text == "끼어들기로 멈춤", e => e.Kind == "end" && e.Ok == true, Idle);
        Assert.Equal(1, _hub.InterruptsSent);
        Assert.Equal(1, _launched);
        Assert.True(_hub.IsResting(room.Id));
        Assert.Equal(1, _hub.RestingTasks(room.Id));
        Assert.Null(_store.Get(room.Id)!.LastError); // 끼어들기는 실패가 아니다
        Assert.DoesNotContain(seen, e => e.Kind == "background" && e.BackgroundCount == 0);
    }

    /// <summary>interrupt가 실패하면(오류 답) 예전처럼 실행을 끝내고 새 프로세스로 새 말을 보낸다 — 닫는 쪽으로.</summary>
    [Fact]
    public async Task Interrupt_Refused_FallsBackToEndingProcess()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "nointerrupt");
        await UntilAll(ch, Idle);
        _hub.Send(room.Id, "server s1");
        await UntilAll(ch, Idle, Tasks(1));
        _hub.Send(room.Id, "fg 8000");
        await UntilAll(ch, e => e.Kind == "start" && e.Prompt == "fg 8000");
        await UntilAnsweringUser(room.Id);
        Assert.Null(_hub.Interrupt(room.Id, "say after"));
        await UntilAll(ch, e => e.Kind == "end" && e.Text == "끼어들기로 멈춤", e => e.Kind == "end" && e.Ok == true, Idle);
        Assert.Equal(1, _hub.InterruptsSent); // interrupt를 보냈고, 거절돼 예전 길로
        Assert.Equal(2, _launched);
    }

    /// <summary>받기 전(되돌림 전)에 끼어들면 interrupt를 보내지 않고 예전 길 — 받기 전 interrupt는 CLI가 무시한다.</summary>
    [Fact]
    public async Task Interrupt_BeforeAccepted_UsesOldPath()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "server s1");
        await UntilAll(ch, Idle, Tasks(1));
        Assert.False(_hub.AnsweringUser(room.Id));
        _hub.Send(room.Id, "fg 8000");
        Assert.Null(_hub.Interrupt(room.Id, "say after")); // 같은 순간 — 아직 받지 않음
        await UntilAll(ch, e => e.Kind == "end" && e.Ok == true, Idle);
        Assert.Equal(0, _hub.InterruptsSent);
    }

    private async Task UntilAnsweringUser(string roomId)
    {
        for (var i = 0; i < 200 && !_hub.AnsweringUser(roomId); i++) await Task.Delay(25);
        Assert.True(_hub.AnsweringUser(roomId));
    }
}
