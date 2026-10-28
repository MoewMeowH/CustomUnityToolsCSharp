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
        // 끼어들기는 기다리던 승인을 "끼어들기"로 거절하고 지금 실행을 끝낸다 (계획서 2-2 d)
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
            Assert.Contains(@"Write C:\outside\x.txt", seen.Single(e => e.Kind == "denied").Text);
            var end = seen.Single(e => e.Kind == "end");
            Assert.False(end.Ok);
            Assert.Contains(ClaudeRunner.NoApprover, end.Text);
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
}
