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
}
