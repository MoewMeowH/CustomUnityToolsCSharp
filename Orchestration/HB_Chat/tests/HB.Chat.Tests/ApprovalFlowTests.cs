using System.Text.Json.Nodes;
using System.Threading.Channels;
using HB.Chat;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 승인 · 질문 흐름 끝까지 (3차 a — stdio 호스트): 방 → 가짜 claude가 control_request can_use_tool을 냄 → 카드(approval · question 알림)
/// → 결정 · 답 → 가짜가 받은 control_response로 결과를 냄.
/// </summary>
public sealed class ApprovalFlowTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-approve-" + Guid.NewGuid().ToString("N"));
    private readonly List<RunSpec> _specs = [];
    private readonly ChatOptions _options;
    private readonly RoomStore _store;
    private readonly RoomHub _hub;
    private readonly Terminals _terminals = new();
    private AgentBrowser? _browser;

    public ApprovalFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        Directory.CreateDirectory(Path.Combine(_dir, "projects"));
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        _options = new ChatOptions
        {
            DataDir = Path.Combine(_dir, "data"),
            Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws") },
            ApprovalTimeoutSeconds = 20,
        };
        _store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        _hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(_options))
        {
            Log = new ApprovalLog(() => _options.DataFolder),
            Terminals = _terminals,
            // 사이트 카드만 본다 — 브라우저는 띄우지 않는다
            Browser = _browser = new AgentBrowser(() => _options.DataFolder, () => null),
            ProcessFactory = (room, spec, workspace) =>
            {
                lock (_specs) _specs.Add(spec);
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId], workspace);
            },
        };
    }

    public void Dispose()
    {
        _hub.Dispose();
        _terminals.Dispose();
        _browser?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<List<LiveEvent>> Until(Channel<LiveEvent> ch, Func<LiveEvent, bool> last)
    {
        var seen = new List<LiveEvent>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var ev in ch.Reader.ReadAllAsync(cts.Token))
        {
            if (ev.Kind is "rooms" or "face") continue; // 방 목록 신호 · 표정은 이 방 흐름 검사 밖 (RoomHubTests에서 따로 본다)
            seen.Add(ev);
            if (last(ev)) break;
        }
        return seen;
    }

    private Room CommandRoom() => _store.Create("ws", PermissionLevel.Command, "opus", null);

    private string Log() => File.ReadAllText(Path.Combine(_options.DataFolder, "approvals.jsonl"));

    [Fact]
    public async Task Allow_RunsWithSameInputAndLogs()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Equal("Bash", card.Tool);
        Assert.Equal("git status", card.Summary);
        Assert.Equal("Show status", card.Description);
        Assert.Equal("Bash(git status)", card.Rule);
        Assert.True(card.ExpiresAt > DateTimeOffset.Now);

        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("허용", seen.Single(e => e.Kind == "approval-done").Text);
        var end = seen.Single(e => e.Kind == "end");
        Assert.True(end.Ok);
        Assert.Empty(_store.Get(room.Id)!.AllowRules); // 이번만 허용은 규칙을 남기지 않는다
        Assert.Contains("\"decision\":\"허용\"", Log());
    }

    [Fact]
    public async Task Deny_PassesReasonToClaude()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, "지금은 안 돼"));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("거절", seen.Single(e => e.Kind == "approval-done").Text);
        var end = seen.Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("지금은 안 돼", end.Text); // 가짜 claude가 받은 거절 문구를 결과로 돌려준다
    }

    [Fact]
    public async Task AlwaysAllow_SavesRule_AndNextRunGetsIt()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.AlwaysAllow, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal(["Bash(git status)"], _store.Get(room.Id)!.AllowRules);
        Assert.Equal(["Bash(git status)"], seen.Single(e => e.Kind == "rules").Rules); // 화면 목록이 바로 바뀐다

        _hub.Send(room.Id, "hello");
        await Until(ch, e => e.Kind == "idle");
        Assert.Equal(["Bash(git status)"], _specs.Last().AllowRules);

        Assert.Null(_hub.RemoveRule(room.Id, "Bash(git status)"));
        Assert.Empty(_store.Get(room.Id)!.AllowRules);
        Assert.Empty((await Until(ch, e => e.Kind == "rules")).Last().Rules!);
    }

    [Theory]
    [InlineData(PermissionLevel.Edit, "acceptEdits")]
    [InlineData(PermissionLevel.Command, "default")]
    public async Task PlanCard_AllowRestoresLevelMode_AndTurnsPlanNextOff(PermissionLevel level, string mode)
    {
        // Claude 앱 기능 C: 계획 먼저 → 실행이 계획 모드 → ExitPlanMode 계획 카드 → 승인 = setMode(단계 모드) · 계획 먼저 꺼짐
        var room = _store.Create("ws", level, "opus", null);
        _store.Update(room.Id, r => r with { PlanNext = true });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "plan " + mode); // 가짜 claude는 허용 답의 setMode가 이 모드일 때만 성공으로 끝낸다
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.True(card.Plan);
        Assert.Equal("ExitPlanMode", card.Tool);
        Assert.Equal("# Plan\n1. edit notes", card.Summary);
        Assert.Null(card.Rule); // 항상 허용 없음
        Assert.Equal("allowedPrompts", card.Extra); // 보이지 않는 입력은 이름을 보이고 허용 답에서 뺀다 (가짜 claude가 확인)
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.AlwaysAllow, null));

        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.True(end.Ok, end.Text);
        Assert.False(_store.Get(room.Id)!.PlanNext);
        lock (_specs) Assert.True(_specs.Last().Plan);
    }

    [Fact]
    public async Task Subagent_ProgressLines_AndCardMarkedFromAgent()
    {
        // Claude 앱 기능 D: 서브에이전트 진행(task_*) → agent 알림, 서브에이전트의 명령 승인(agent_id) → 카드에 서브에이전트 표시, 같은 허용 길
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "agent please");
        var before = await Until(ch, e => e.Kind == "approval");
        var tasks = before.Where(e => e.Kind == "agent").Select(e => e.Task!).ToList();
        Assert.Equal(["started", "progress"], tasks.Select(t => t.Phase));
        Assert.All(tasks, t => Assert.Equal(("t_sub1", "general-purpose"), (t.TaskId, t.Agent)));
        Assert.Equal(1, tasks[1].ToolUses);
        var card = before.Last().Approval!;
        Assert.True(card.FromAgent);
        Assert.Equal("mkdir subdir", card.Summary);

        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var after = await Until(ch, e => e.Kind == "idle");
        var done = after.Single(e => e.Kind == "agent").Task!;
        Assert.Equal(("done", "completed"), (done.Phase, done.Status));
        Assert.True(after.Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task BackgroundSubagent_ApprovalAfterMainResult_StillAnswered()
    {
        // 실제 확인(2026-10-28): 백그라운드 서브에이전트는 메인 result 뒤에 승인을 묻는다 — stdin을 닫지 않아야 카드 답이 간다
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bgagent please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.True(card.FromAgent);
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var after = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("completed", after.Single(e => e.Kind == "agent").Task!.Status);
        Assert.True(after.Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task BackgroundSubagent_NeverDone_StdinClosedAfterCap()
    {
        // /self-review security 42f9d47dc4 C2: 끝 알림이 오지 않아도 상한 뒤에는 stdin을 닫아 실행이 끝난다.
        // 상한은 전역이라 같은 클래스(차례로 도는) 안에서만 줄였다가 되돌린다
        var before = ClaudeRunner.BackgroundAgentWait;
        ClaudeRunner.BackgroundAgentWait = TimeSpan.FromSeconds(1);
        try
        {
            var room = CommandRoom();
            var ch = _hub.Subscribe(room.Id);
            _hub.Send(room.Id, "bghang please");
            var seen = await Until(ch, e => e.Kind == "idle");
            Assert.True(seen.Single(e => e.Kind == "end").Ok);
        }
        finally { ClaudeRunner.BackgroundAgentWait = before; }
    }

    [Fact]
    public async Task BackgroundBash_CardShowsMinutes_SecondAnswerReceived()
    {
        // 카드에 백그라운드 표시, 메인 답 뒤 기다림 알림, 끝 알림 뒤 두 번째 답까지 받고 끝난다
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bgbash please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Equal(ClaudeRunner.BackgroundBashMinutes, card.BackgroundMinutes);
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        var waits = seen.Where(e => e.Kind == "background").ToList();
        Assert.Equal(1, waits[0].BackgroundCount); // 첫 답 뒤 1개 기다림
        Assert.NotNull(waits[0].BackgroundUntil);
        Assert.Equal(0, waits[^1].BackgroundCount); // 끝 알림 뒤 0
        Assert.Contains(seen, e => e.Kind == "agent" && e.Task!.Kind == TaskEvent.BashKind);
        var end = seen.Single(e => e.Kind == "end");
        Assert.True(end.Ok);
    }

    [Fact]
    public async Task BackgroundServer_ClosedAfterCap_KilledAfterGrace_EndsOk()
    {
        // 안 끝나는 백그라운드 명령: 상한 뒤 stdin을 닫아도 CLI가 안 끝난다 → 유예 뒤 정리하고 정상 끝 (멈춤 결함, 실제 확인)
        // 상한 · 유예는 전역이라 같은 클래스(차례로 도는) 안에서만 줄였다가 되돌린다
        var (wait, grace) = (ClaudeRunner.BackgroundBashWait, ClaudeRunner.CloseGrace);
        ClaudeRunner.BackgroundBashWait = TimeSpan.FromSeconds(1);
        ClaudeRunner.CloseGrace = TimeSpan.FromSeconds(1);
        try
        {
            var room = CommandRoom();
            var ch = _hub.Subscribe(room.Id);
            _hub.Send(room.Id, "bgserver please");
            var seen = await Until(ch, e => e.Kind == "idle");
            Assert.True(seen.Single(e => e.Kind == "end").Ok);
        }
        finally { (ClaudeRunner.BackgroundBashWait, ClaudeRunner.CloseGrace) = (wait, grace); }
    }

    [Fact]
    public async Task BackgroundServer_WrapUp_EndsWithoutWaitingForCap()
    {
        // "지금 끝내기": 상한(10분)을 기다리지 않고 바로 정리 — 기다림 알림이 온 뒤에만 받는다
        var grace = ClaudeRunner.CloseGrace;
        ClaudeRunner.CloseGrace = TimeSpan.FromSeconds(1);
        try
        {
            var room = CommandRoom();
            var ch = _hub.Subscribe(room.Id);
            _hub.Send(room.Id, "bgserver please");
            var waiting = (await Until(ch, e => e.Kind == "background")).Last();
            Assert.Equal(1, waiting.BackgroundCount);
            _hub.WrapUp(room.Id);
            var seen = await Until(ch, e => e.Kind == "idle");
            Assert.True(seen.Single(e => e.Kind == "end").Ok);
            // 정리 표시가 방 기록 줄로 남는다
            Assert.Contains("백그라운드 작업 1개를 '지금 끝내기'로 정리했습니다", Log());
        }
        finally { ClaudeRunner.CloseGrace = grace; }
    }

    [Fact]
    public async Task ResumedSession_GhostResultBeforeTurn_DoesNotCloseStdin()
    {
        // 지난 실행에서 정리된 백그라운드 작업이 있는 세션: CLI가 사용자 말 전에 끝 알림 + 빈 result를 낸다 (실측).
        // 그 result에서 stdin을 닫으면 이번 턴의 승인이 "Stream closed"로 실패했다 (실제 확인) — 승인이 가고 답까지 받아야 한다
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ghost please");
        var before = await Until(ch, e => e.Kind == "approval");
        Assert.DoesNotContain(before, e => e.Kind == "end"); // 빈 result가 턴 끝으로 새지 않는다 (6f64f4b039 C8)
        var card = before.Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.True(end.Ok, end.Text);
    }

    [Fact]
    public async Task UnknownNotification_ThenErrorResultWithoutText_EndsTurn()
    {
        // 시작 못 본 끝 알림 뒤 글 없는 실패 result는 거르지 않고 턴을 끝낸다 — 삼키면 stdin이 안 닫혀 방이 멈춘다 (6f64f4b039 C1 · C8)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ghostfail please");
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
    }

    [Fact]
    public async Task BackgroundServer_StopWhileWaiting_EndsWithAnswer()
    {
        // 답을 받은 뒤 기다리는 중에 중지하면 "중지됨"이 아니라 그 답으로 끝난다 (6f64f4b039 C4)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bgserver please");
        await Until(ch, e => e.Kind == "background");
        _hub.Stop(room.Id);
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.True(end.Ok, end.Text);
        Assert.Contains("기다리는 중 멈춰 백그라운드 작업 1개를 정리했습니다", Log());
    }

    [Fact]
    public async Task BackgroundRequestFromSubagent_CardShowsSubagentCap()
    {
        // 서브에이전트가 낸 백그라운드 요청은 서브에이전트 기다림 상한(30분)까지 돈다 (6f64f4b039 C5)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bgfromagent please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.True(card.FromAgent);
        Assert.Equal(ClaudeRunner.BackgroundAgentMinutes, card.BackgroundMinutes);
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task Tasks_PlainOrLateProgress_DoNotHoldStdin()
    {
        // C2: subagent_type 없는 task는 기다리지 않고, 끝난 뒤 늦게 온 진행 줄은 다시 열지 않는다 (상한 30분 — 기다리면 시험 시간 초과)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "bgmisc please");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.True(seen.Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task PlanRequest_WithoutPlanFirst_DeniedWithoutCard()
    {
        // 지휘자 검토 S1: 계획 모드로 시작하지 않은 실행의 ExitPlanMode는 카드 없이 거절
        var room = _store.Create("ws", PermissionLevel.Edit, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "plan acceptEdits");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.DoesNotContain(seen, e => e.Kind == "approval");
        Assert.Contains(seen, e => e.Kind == "denied" && e.Text!.Contains("계획 모드로 시작한 실행이 아니라") && e.Logged == true);
        // 카드 없는 거절도 방 기록 줄로 남는다 (/self-review e464ec29ba C6)
        Assert.Contains("\"decision\":\"거절 (계획 모드로 시작한 실행이 아님)\"", Log());
    }

    [Fact]
    public async Task TerminalStart_CardWithoutAlwaysAllow_LeavesOneTicket()
    {
        // 터미널 곁칸 (결정 "늘 카드"): terminal_start는 카드로 묻고 항상 허용은 받지 않는다.
        // 허용하면 name · command만 돌려주고(가짜가 extra 키를 확인) 앱 MCP가 쓸 표를 하나 남긴다
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "terminal please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Equal(RoomHub.TerminalTool, card.Tool);
        Assert.Equal("python -m http.server 8000", card.Summary);
        Assert.Null(card.Rule);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.AlwaysAllow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.True(end.Ok, end.Text);
        Assert.True(_terminals.TakeApproval(room.Id, "web", "python -m http.server 8000"));
        Assert.False(_terminals.TakeApproval(room.Id, "web", "python -m http.server 8000")); // 한 번만
        Assert.Contains("\"tool\":\"터미널\"", Log());
    }

    [Fact]
    public async Task BrowserOpen_OutsideSiteCardOnce_LocalNoCard()
    {
        // 지휘자 브라우저 (결정 "localhost만 묻지 않음"): 밖 사이트는 사이트마다 한 번 카드,
        // localhost · 허용한 사이트의 다른 쪽은 카드 없이. 허용 답은 url만 (가짜가 extra 키를 확인)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "browse please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Equal(RoomHub.BrowserTool, card.Tool);
        Assert.Equal("https://example.com/a", card.Summary);
        Assert.Null(card.Rule);
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.True(seen.Single(e => e.Kind == "end").Ok);
        Assert.DoesNotContain(seen, e => e.Kind == "approval"); // localhost · 같은 사이트는 다시 묻지 않음
        Assert.True(_browser!.IsAllowed(room.Id, new Uri("https://example.com/zzz")));
        Assert.False(_browser.IsAllowed(room.Id, new Uri("https://other.example/")));
    }

    [Fact]
    public async Task TerminalStart_Denied_LeavesNoTicket()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "terminal please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        Assert.False((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
        Assert.False(_terminals.TakeApproval(room.Id, "web", "python -m http.server 8000"));
    }

    [Fact]
    public async Task CardLeftAtRunEnd_LoggedAndDoneBeforeEnd()
    {
        // 실행이 끝날 때 남은 카드: 기록 · approval-done이 end보다 먼저 — 화면이 턴 끝에 기록을 다시 받을 때 그 줄이 있게 (/self-review e464ec29ba C3)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var extra = _hub.RequestApprovalAsync(room.Id, ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"dir"}}""")!, CancellationToken.None);
        var card2 = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        var done = seen.FindIndex(e => e.Kind == "approval-done" && e.Approval!.Id == card2.Id);
        Assert.InRange(done, 0, seen.FindIndex(e => e.Kind == "end"));
        Assert.Equal("실행 끝남", seen[done].Text);
        Assert.False((await extra)!.Allow);
        Assert.Contains("\"decision\":\"실행 끝남\"", Log());
    }

    [Fact]
    public async Task PlanCard_DenyPassesReasonToReplan()
    {
        var room = _store.Create("ws", PermissionLevel.Edit, "opus", null);
        _store.Update(room.Id, r => r with { PlanNext = true });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "plan please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, "테스트를 먼저"));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("테스트를 먼저", end.Text);
        Assert.Contains("계획을 고친 뒤", end.Text); // 지휘자에게 다시 계획하라고 전한다
        Assert.True(_store.Get(room.Id)!.PlanNext); // 승인 전까지 그대로
    }

    [Fact]
    public async Task PlanCard_LoweredBelowEdit_CanOnlyBeDenied()
    {
        var room = _store.Create("ws", PermissionLevel.Edit, "opus", null);
        _store.Update(room.Id, r => r with { PlanNext = true });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "plan please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        _store.Update(room.Id, r => r with { Level = PermissionLevel.Read });
        Assert.Contains("거절만", _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task LoweredLevel_PendingCardCanOnlyBeDenied()
    {
        // /self-review security 8ccecae84d C1: 카드가 뜬 뒤 곁칸에서 명령 아래로 내리면 허용 · 항상 허용을 받지 않는다
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        _store.Update(room.Id, r => r with { Level = PermissionLevel.Read });
        Assert.Contains("거절만", _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        Assert.Contains("거절만", _hub.Decide(room.Id, card.Id, ApprovalChoice.AlwaysAllow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        await Until(ch, e => e.Kind == "idle");
        Assert.Empty(_store.Get(room.Id)!.AllowRules);
    }

    [Fact]
    public async Task LoweredLevel_DeniesNewAskWithoutCard()
    {
        // 내리기는 실행 중인 턴에도 바로: 그 뒤 오는 명령 승인 요청은 카드 없이 거절 (시작 때 단계를 읽었든 아니든 같은 결과)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        _store.Update(room.Id, r => r with { Level = PermissionLevel.Read });
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.DoesNotContain(seen, e => e.Kind == "approval");
        Assert.Contains(seen, e => e.Kind == "denied");
    }

    [Fact]
    public async Task AbortedRequest_ClosesCardAsCut()
    {
        // 실행 쪽이 먼저 끊긴 경우 (요청 취소) — 카드는 "끊김"으로 닫히고 거절로 끝난다.
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        await Until(ch, e => e.Kind == "approval");
        using var abort = new CancellationTokenSource();
        var second = _hub.RequestApprovalAsync(room.Id, ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"dir"}}""")!, abort.Token);
        var card2 = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Equal("dir", card2.Summary);
        abort.Cancel();
        var answer = await second;
        Assert.False(answer!.Allow);
        Assert.Equal("끊김", (await Until(ch, e => e.Kind == "approval-done" && e.Approval!.Id == card2.Id)).Last().Text);
        _hub.Stop(room.Id);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task WithdrawnRequest_ClosesCardAsCut()
    {
        // CLI가 control_cancel_request로 요청을 거두면 카드는 "끊김"으로 닫히고 실행은 이어진다 (/self-review 7d839f4aa5 C1)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "cancel it");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var done = (await Until(ch, e => e.Kind == "approval-done")).Last();
        Assert.Equal(card.Id, done.Approval!.Id);
        Assert.Equal("끊김", done.Text);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null)); // 거둔 요청은 허용으로 기록되지 않는다
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
        Assert.Contains("\"decision\":\"끊김\"", Log());
    }

    [Fact]
    public async Task RiskyExtraInput_CanOnlyBeDenied()
    {
        // 명령 밖의 입력은 카드에 보이고, 위험한 이름(dangerously · bypass)이 있으면 거절만 (/self-review 5e747ff851 C1)
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        await Until(ch, e => e.Kind == "approval");
        var request = ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"dir","run_in_background":true,"dangerouslyDisableSandbox":true}}""")!;
        var task = _hub.RequestApprovalAsync(room.Id, request, CancellationToken.None);
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.True(card.Risky);
        Assert.Equal("run_in_background=true, dangerouslyDisableSandbox=true", card.Extra);
        Assert.Null(card.Rule);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        Assert.False((await task)!.Allow);
        _hub.Stop(room.Id);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task TwoPendingApprovals_AreDecidedSeparately()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var first = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var secondTask = _hub.RequestApprovalAsync(room.Id, ApprovalRequest.Parse("""{"tool_name":"PowerShell","input":{"command":"Get-Date"}}""")!, CancellationToken.None);
        var second = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.NotEqual(first.Id, second.Id);

        Assert.Null(_hub.Decide(room.Id, second.Id, ApprovalChoice.Deny, null));
        Assert.False((await secondTask)!.Allow);
        Assert.Null(_hub.Decide(room.Id, first.Id, ApprovalChoice.Allow, null)); // 다른 카드는 그대로 남아 있다
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task LateRequestAfterRunEnds_GetsNoCard()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null);
        await Until(ch, e => e.Kind == "idle");

        // 실행이 끝난 뒤의 승인 요청은 카드 없이 거절된다 (그 세대의 실행이 아님)
        Assert.Null(await _hub.RequestApprovalAsync(room.Id, ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"whoami"}}""")!, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedCommand_CanOnlyBeDenied()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        await Until(ch, e => e.Kind == "approval");
        var longCommand = new string('x', ApprovalRequest.MaxSummaryChars + 10);
        var request = ApprovalRequest.Parse($$$"""{"tool_name":"Bash","input":{"command":"{{{longCommand}}}"}}""")!;
        var task = _hub.RequestApprovalAsync(room.Id, request, CancellationToken.None);
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.True(card.Truncated);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null)); // 다 보지 못한 명령은 허용 못 함
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        Assert.False((await task)!.Allow);
        _hub.Stop(room.Id);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public void InheritedCommandRules_AreListed()
    {
        var room = CommandRoom();
        Directory.CreateDirectory(Path.Combine(_dir, "ws", ".claude"));
        File.WriteAllText(Path.Combine(_dir, "ws", ".claude", "settings.json"),
            """{"permissions":{"allow":["Bash(unity test:*)","Read","mcp__x","PowerShell(Get-Date)"]}}""");
        File.WriteAllText(Path.Combine(_dir, "ws", ".claude", "settings.local.json"), "{ broken");
        var rules = _hub.InheritedCommandRules(room);
        Assert.Contains(("작업 폴더", "Bash(unity test:*)"), rules);
        Assert.Contains(("작업 폴더", "PowerShell(Get-Date)"), rules);
        Assert.DoesNotContain(rules, r => r.Rule == "Read" || r.Rule == "mcp__x");
    }

    [Fact]
    public async Task NoAnswer_TimesOutAsDeny()
    {
        _options.ApprovalTimeoutSeconds = 2;
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("시간 초과", seen.Single(e => e.Kind == "approval-done").Text);
        var end = seen.Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("시간 안에", end.Text);
    }

    [Fact]
    public async Task Stop_DeniesPendingApproval()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        _hub.Stop(room.Id);
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("중지됨", seen.Single(e => e.Kind == "approval-done").Text);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null)); // 끝난 카드는 받지 않는다
    }

    [Fact]
    public async Task Interrupt_DeniesPendingCard()
    {
        // 끼어들기는 기다리던 승인을 "끼어들기"로 거절하고 지금 실행을 끝낸다
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Interrupt(room.Id, "hello instead"));
        var done = await Until(ch, e => e.Kind == "approval-done");
        Assert.Equal(card.Id, done.Last().Approval!.Id);
        Assert.Equal("끼어들기", done.Last().Text);
        var seen = await Until(ch, e => e.Kind == "start");
        Assert.Equal("hello instead", seen.Last().Prompt);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task SnapshotShowsPendingCard()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var late = _hub.Subscribe(room.Id); // 화면을 새로 연 경우
        var first = await Until(late, e => e.Kind == "approval");
        Assert.Equal(card.Id, first.Last().Approval!.Id);
        // 새로 연 화면의 표정은 승인 기다림 (tool 재전송이 덮지 않게 화면은 tool로 표정을 바꾸지 않는다 — 83b453653b C1)
        Assert.Equal("Approval", first.Single(e => e.Kind == "snapshot").Face);
        _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task OtherApprovalRequests_AreDeniedWithoutCard()
    {
        // 카드로 받지 않는 승인 요청(작업 폴더 밖 쓰기 등)은 묻지 않고 거절 — 그 전 --permission-prompts none과 같은 동작
        foreach (var level in new[] { PermissionLevel.Edit, PermissionLevel.Command })
        {
            var room = _store.Create("ws", level, "opus", null);
            var ch = _hub.Subscribe(room.Id);
            _hub.Send(room.Id, "write it");
            var seen = await Until(ch, e => e.Kind == "idle");
            Assert.DoesNotContain(seen, e => e.Kind is "approval" or "question");
            // 호스트 거절에는 CLI의 permission_denied 줄이 없어 앱이 "거절됨" 줄을 낸다 (3차 a 실제 확인)
            var denied = seen.Single(e => e.Kind == "denied").Text;
            Assert.Contains(@"Write C:\outside\x.txt", denied);
            Assert.Contains("작업 폴더 밖", denied); // 쓰기는 까닭을 알린다 (사용자 보고 2026-10-30)
            var end = seen.Single(e => e.Kind == "end");
            Assert.False(end.Ok);
            Assert.Contains(ClaudeRunner.NoWriteApprover, end.Text);
        }
    }

    [Fact]
    public async Task CommandOutsideCommandRoom_IsDeniedWithoutCard()
    {
        // 편집 방에는 명령 도구가 없지만, 혹시 승인 요청이 와도 카드 없이 거절한다
        var room = _store.Create("ws", PermissionLevel.Edit, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.DoesNotContain(seen, e => e.Kind == "approval");
        Assert.Contains(ClaudeRunner.NoApprover, seen.Single(e => e.Kind == "end").Text);
    }

    // ---- 질문 카드 (3차 a) ----

    [Fact]
    public async Task Question_AnswersGoBackAsUpdatedInput()
    {
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ask me");
        var q = (await Until(ch, e => e.Kind == "question")).Last().Question!;
        Assert.Equal(2, q.Questions.Count);
        Assert.Equal("Which color?", q.Questions[0].Question);
        Assert.Equal("Color", q.Questions[0].Header);
        Assert.False(q.Questions[0].MultiSelect);
        Assert.Equal(["Red", "Blue"], q.Questions[0].Options.Select(o => o.Label));
        Assert.Equal("warm", q.Questions[0].Options[0].Description);
        Assert.True(q.Questions[1].MultiSelect);
        Assert.Equal(RoomActivity.Approval, _hub.ActivityOf(room.Id)); // 목록 · 데스크톱은 답 기다림으로 본다
        Assert.Equal(Face.Question, _hub.FaceOf(room));

        // 빠진 답 · 없는 질문은 받지 않는다
        Assert.NotNull(_hub.Answer(room.Id, q.Id, new Dictionary<string, string> { ["Which color?"] = "Red" }));
        Assert.NotNull(_hub.Answer(room.Id, q.Id, new Dictionary<string, string> { ["Which color?"] = "Red", ["Which sides?"] = "Soup", ["Nope?"] = "x" }));

        Assert.Null(_hub.Answer(room.Id, q.Id, new Dictionary<string, string> { ["Which color?"] = "Red", ["Which sides?"] = "Fries, 직접 쓴 답" }));
        var seen = await Until(ch, e => e.Kind == "idle");
        var done = seen.Single(e => e.Kind == "question-done");
        Assert.Equal("Which color? → Red / Which sides? → Fries, 직접 쓴 답", done.Text);
        Assert.True(done.Ok); // 화면은 이 값으로 답함 · 답 없음을 가른다
        var end = seen.Single(e => e.Kind == "end");
        Assert.True(end.Ok, end.Text);
        Assert.Contains("\"decision\":\"답: Which color? → Red", Log());
        Assert.NotNull(_hub.Answer(room.Id, q.Id, new Dictionary<string, string> { ["Which color?"] = "Blue", ["Which sides?"] = "Soup" })); // 끝난 질문
    }

    [Fact]
    public async Task Question_InEditRoom_AnswersReachClaude()
    {
        // 가짜 claude는 updatedInput에 질문 둘과 answers가 없으면 거절로 끝난다 — 성공이면 답이 그대로 갔다
        var room = _store.Create("ws", PermissionLevel.Edit, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ask me");
        var q = (await Until(ch, e => e.Kind == "question")).Last().Question!;
        _hub.Answer(room.Id, q.Id, new Dictionary<string, string> { ["Which color?"] = "Blue", ["Which sides?"] = "Salad, Soup" });
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task Question_SkipDeniesWithReason()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ask me");
        var q = (await Until(ch, e => e.Kind == "question")).Last().Question!;
        Assert.Null(_hub.Answer(room.Id, q.Id, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("답하지 않음", seen.Single(e => e.Kind == "question-done").Text);
        Assert.False(seen.Single(e => e.Kind == "question-done").Ok);
        var end = seen.Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("답하지 않았습니다", end.Text);
        Assert.Contains("채팅 글로", end.Text);
    }

    [Fact]
    public async Task Question_TimesOutAndStopEnds()
    {
        _options.ApprovalTimeoutSeconds = 2;
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ask me");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("시간 초과", seen.Single(e => e.Kind == "question-done").Text);

        _options.ApprovalTimeoutSeconds = 20;
        _hub.Send(room.Id, "ask me");
        await Until(ch, e => e.Kind == "question");
        var late = _hub.Subscribe(room.Id); // 새로 연 화면도 기다리는 질문을 받는다
        var first = await Until(late, e => e.Kind == "question");
        Assert.Equal("Question", first.Single(e => e.Kind == "snapshot").Face);
        _hub.Stop(room.Id);
        Assert.Equal("중지됨", (await Until(ch, e => e.Kind == "question-done")).Last().Text);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task Question_InChatRoom_IsDenied()
    {
        // 대화 단계는 도구가 없다 — 혹시 질문 요청이 와도 카드 없이 거절
        var room = _store.Create("ws", PermissionLevel.Chat, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "ask me");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.DoesNotContain(seen, e => e.Kind == "question");
        Assert.Contains(ClaudeRunner.NoApprover, seen.Single(e => e.Kind == "end").Text);
    }

    // ---- 방별 서버 도구 ----

    [Fact]
    public async Task UnityTools_NoCardButEval_AndRecheckedAfterLowering()
    {
        var project = Path.Combine(_dir, "Game");
        Directory.CreateDirectory(project);
        var cli = Path.Combine(_dir, "unity.exe");
        File.WriteAllText(cli, "");
        _options.UnityProjects["Game"] = project;
        _options.UnityCliPath = cli;
        var room = CommandRoom();
        _store.Update(room.Id, r => r with { UnityProject = "Game" });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "roomtools");

        // editor_status는 카드 없이 지나가고 eval만 카드 ("항상 허용" 없음)
        var before = await Until(ch, e => e.Kind == "approval");
        var card = before.Single(e => e.Kind == "approval").Approval!;
        Assert.Equal("mcp__unity__eval", card.Tool);
        Assert.Contains("Debug.Log(1);", card.Summary);
        Assert.Null(card.Rule);

        // 카드를 기다리는 사이 권한을 내리면 카드 허용도 막히고(기존 장치), 그 뒤 Unity 호출은 카드 없이 거절된다 (미리 허용하지 않으므로)
        _store.Update(room.Id, r => r with { Level = PermissionLevel.Edit });
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        var end = seen.Single(e => e.Kind == "end");
        Assert.True(end.Ok, end.Text);
        Assert.Contains("Unity 연결을 끊었거나 권한을 명령 아래로 내림", Log());
        Assert.DoesNotContain(_specs.Single().ExtraMcpServers, s => s != "unity");
    }

    /// <summary>가짜 claude의 runmode 허가 결과 ("ALLOWED " + 받은 updatedInput JSON — 오류 결과로 와야 끝 알림에 글이 실린다).</summary>
    private static JsonObject Allowed(LiveEvent end)
    {
        var at = end.Text!.IndexOf("ALLOWED ", StringComparison.Ordinal); // 앞에 "실행 오류 (…): "가 붙는다
        Assert.True(at >= 0, end.Text);
        return JsonNode.Parse(end.Text[(at + "ALLOWED ".Length)..])!.AsObject();
    }

    /// <summary>모델 선택창: 가짜 MCP 서버 폴더를 만들고 작업 폴더 .mcp.json이 그 dll을 가리키게 한다.</summary>
    private Room ModeRoom(PermissionLevel level = PermissionLevel.Read, bool ask = true)
    {
        ModeCardTests.WriteServer(Path.Combine(_dir, "srv"), ws: Path.Combine(_dir, "ws"));
        var room = _store.Create("ws", level, "opus", null);
        return _store.Update(room.Id, r => r with { AskRunMode = ask });
    }

    [Fact]
    public async Task RunModeCard_AllowWithPicks_RewritesAgentsAndReasoning()
    {
        // 외부 요청 전에 묻기 → run_mode를 미리 허용하지 않음 → 모델 선택 카드 → 고른 값으로 agents · reasoning을 다시 써서 허가
        var room = ModeRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "runmode");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        lock (_specs) Assert.True(_specs.Last().AskRunMode);
        var mode = card.Mode!;
        Assert.Equal("design_main", mode.Mode);
        Assert.Equal("pick one", mode.Reason);
        Assert.Equal(["gpt_main_sol", "gemini_main_flash"], mode.Rows.Select(r => r.Agent));
        Assert.All(mode.Rows, r => Assert.Equal("low", r.Effort)); // 지휘자가 넣은 '*=low'가 처음 값
        Assert.Null(card.Rule);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.AlwaysAllow, null)); // 항상 허용 없음
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null,
            new Dictionary<string, ModeRowPick> { ["gpt_main_sol"] = new("off_agent", null) })); // 꺼진 에이전트는 받지 않는다

        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null, new Dictionary<string, ModeRowPick>
        {
            ["gpt_main_sol"] = new("codex_cli_sol", "high"),
            ["gemini_main_flash"] = new("gemini_main_flash", null),
        }));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("허용 (gpt_main_sol→codex_cli_sol · high)", seen.Single(e => e.Kind == "approval-done").Text);
        var end = seen.Single(e => e.Kind == "end");
        var sent = Allowed(end); // 가짜 claude가 받은 updatedInput을 그대로 돌려준다
        Assert.Equal("check this design", (string?)sent["input"]);
        Assert.Equal(["gpt_main_sol=codex_cli_sol"], sent["agents"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["codex_cli_sol=high"], sent["reasoning"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("design_main", Log());
    }

    [Fact]
    public async Task RunModeCard_Deny_PassesReason_SwitchOffKeepsServerAllowed()
    {
        var room = ModeRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "runmode");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, "지금은 외부 말고"));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("지금은 외부 말고", end.Text);

        _store.Update(room.Id, r => r with { AskRunMode = false });
        _hub.Send(room.Id, "hello");
        await Until(ch, e => e.Kind == "idle");
        lock (_specs) Assert.False(_specs.Last().AskRunMode); // 끄면 다음 실행부터 서버 전체 미리 허용
    }

    [Fact]
    public async Task ModePick_RidesItsOwnMessage_PrefillsOnce_EvenWithSwitchOff()
    {
        // + /self-review 785ebb5e97 C1: + 요청 창 값은 그 말에 붙는다 — 앞에 기다리던 보통 말의 실행은 카드 모드가 아니고,
        // 그 말의 실행만 스위치가 꺼져 있어도 카드로 묻고 미리 채운다
        var room = ModeRoom(ask: false);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "runmode"); // 보통 말 — 가짜는 그래도 run_mode를 묻는다(미리 허용이면 진짜 CLI는 묻지 않음): 카드가 떠 있는 동안 실행 중
        var first = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.False(first.Mode!.Prefilled);
        _hub.Send(room.Id, "hello");
        var pick = new ModePick("design_main", new Dictionary<string, ModeRowPick> { ["gemini_main_flash"] = new("gpt_main_sol", "xhigh") });
        Assert.Null(_hub.Send(room.Id, "runmode", pick, interrupt: false));
        Assert.Null(_hub.Decide(room.Id, first.Id, ApprovalChoice.Deny, null));

        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        lock (_specs)
        {
            Assert.Equal([false, false, true], _specs.Select(s => s.AskRunMode)); // 앞 두 실행은 값을 싣지 않았다
        }
        Assert.True(card.Mode!.Prefilled);
        Assert.Equal(("gpt_main_sol", "xhigh"), (card.Mode.Rows[1].Pick, card.Mode.Rows[1].Effort));
        // gpt_main_sol 줄은 '*=low', 교체로 gpt_main_sol이 된 줄은 xhigh — 서버는 이름마다 강도 하나라 맞추라고 돌려준다 (C4)
        Assert.Contains("강도가 다릅니다", _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null, new Dictionary<string, ModeRowPick>
        {
            ["gpt_main_sol"] = new("gpt_main_sol", "xhigh"),
            ["gemini_main_flash"] = new("gpt_main_sol", "xhigh"),
        }));
        var sent = Allowed((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end"));
        Assert.Equal(["gemini_main_flash=gpt_main_sol"], sent["agents"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["gpt_main_sol=xhigh"], sent["reasoning"]!.AsArray().Select(n => (string)n!));

        _hub.Send(room.Id, "hello");
        await Until(ch, e => e.Kind == "idle");
        lock (_specs) Assert.False(_specs.Last().AskRunMode); // 값은 그 말 하나에만
    }

    [Fact]
    public async Task RunModeCard_UnchangedAllow_KeepsConductorInputAsIs()
    {
        // 785ebb5e97 C2 · C3: 아무것도 바꾸지 않고 허가하면(화면은 모든 줄을 보낸다) 지휘자 입력 그대로 — '*=low'도 그대로 간다
        var room = ModeRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "runmode");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null,
            card.Mode!.Rows.ToDictionary(r => r.Agent, r => new ModeRowPick(r.Pick, r.Effort))));
        var sent = Allowed((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end"));
        Assert.False(sent.ContainsKey("agents"));
        Assert.Equal(["*=low"], sent["reasoning"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public async Task RunModeCard_LoweredToChat_CanOnlyBeDenied()
    {
        var room = ModeRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "runmode");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        _store.Update(room.Id, r => r with { Level = PermissionLevel.Chat });
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        Assert.False((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
    }
}
