using System.Net;
using System.Threading.Channels;
using HB.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 승인 흐름 끝까지: 명령 방 → 가짜 claude(approve 모드)가 진짜 /approve로 훅을 부름 → 카드(approval 알림) → 결정 → 가짜가 결과를 냄.
/// /approve는 테스트 안에서 띄운 Kestrel이 받는다 (앱과 같은 MapApprovals).
/// </summary>
public sealed class ApprovalFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-approve-" + Guid.NewGuid().ToString("N"));
    private readonly List<RunSpec> _specs = [];
    private readonly ChatOptions _options;
    private RoomStore _store = null!;
    private RoomHub _hub = null!;
    private WebApplication _web = null!;
    private string _base = null!;

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
    }

    public async Task InitializeAsync()
    {
        _store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        _hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(_options))
        {
            Log = new ApprovalLog(() => _options.DataFolder),
            ProcessFactory = (room, spec, workspace) =>
            {
                lock (_specs) _specs.Add(spec);
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId, spec.ApprovalHookUrl ?? ""], workspace);
            },
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_hub);
        _web = builder.Build();
        _web.MapApprovals();
        await _web.StartAsync();
        _base = _web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _hub.ApprovalBaseUrl = _base;
    }

    public async Task DisposeAsync()
    {
        _hub.Dispose();
        await _web.StopAsync();
        await _web.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<List<LiveEvent>> Until(Channel<LiveEvent> ch, Func<LiveEvent, bool> last)
    {
        var seen = new List<LiveEvent>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var ev in ch.Reader.ReadAllAsync(cts.Token))
        {
            if (ev.Kind == "rooms") continue; // 방 목록 신호는 이 방의 흐름이 아니다 (RoomHubTests에서 따로 본다)
            seen.Add(ev);
            if (last(ev)) break;
        }
        return seen;
    }

    private Room CommandRoom() => _store.Create("ws", PermissionLevel.Command, "opus", null);

    [Fact]
    public async Task Allow_RunsAndLogs()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.Equal("Bash", card.Tool);
        Assert.Equal("git status", card.Summary);
        Assert.Equal("Bash(git status)", card.Rule);
        Assert.True(card.ExpiresAt > DateTimeOffset.Now);

        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("허용", seen.Single(e => e.Kind == "approval-done").Text);
        Assert.True(seen.Single(e => e.Kind == "end").Ok);
        Assert.Empty(_store.Get(room.Id)!.AllowRules); // 이번만 허용은 규칙을 남기지 않는다
        Assert.Contains("\"decision\":\"허용\"", File.ReadAllText(Path.Combine(_options.DataFolder, "approvals.jsonl")));

        // 훅 주소는 실행마다 다른 토큰을 쓴다.
        var spec = Assert.Single(_specs);
        Assert.StartsWith($"{_base}/approve?room={room.Id}&token=", spec.ApprovalHookUrl);
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
    public async Task HookSideAbort_ClosesCardAsCut()
    {
        // claude 쪽 훅이 먼저 끊긴 경우 (요청 취소) — 카드는 "끊김"으로 닫히고 거절로 끝난다.
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        await Until(ch, e => e.Kind == "approval");
        var token = _hub.CurrentToken(room.Id)!;
        using var abort = new CancellationTokenSource();
        var second = _hub.RequestApprovalAsync(room.Id, token, ApprovalRequest.Parse("""{"tool_name":"Bash","tool_input":{"command":"dir"}}""")!, abort.Token);
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
    public async Task TwoPendingApprovals_AreDecidedSeparately()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var first = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var token = _hub.CurrentToken(room.Id)!;
        var secondTask = _hub.RequestApprovalAsync(room.Id, token, ApprovalRequest.Parse("""{"tool_name":"PowerShell","tool_input":{"command":"Get-Date"}}""")!, CancellationToken.None);
        var second = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.NotEqual(first.Id, second.Id);

        Assert.Null(_hub.Decide(room.Id, second.Id, ApprovalChoice.Deny, null));
        Assert.False((await secondTask)!.Allow);
        Assert.Null(_hub.Decide(room.Id, first.Id, ApprovalChoice.Allow, null)); // 다른 카드는 그대로 남아 있다
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task LateRequestAfterRunEnds_IsForbidden()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var token = _hub.CurrentToken(room.Id)!;
        _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null);
        await Until(ch, e => e.Kind == "idle");

        Assert.Null(_hub.CurrentToken(room.Id)); // 실행이 끝나면 토큰을 비운다
        using var http = new HttpClient();
        var res = await http.PostAsync($"{_base}/approve?room={room.Id}&token={token}",
            new StringContent("""{"tool_name":"Bash","tool_input":{"command":"whoami"}}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task TruncatedCommand_CanOnlyBeDenied()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        await Until(ch, e => e.Kind == "approval");
        var token = _hub.CurrentToken(room.Id)!;
        var longCommand = new string('x', ApprovalRequest.MaxSummaryChars + 10);
        var request = ApprovalRequest.Parse($$$"""{"tool_name":"Bash","tool_input":{"command":"{{{longCommand}}}"}}""")!;
        var task = _hub.RequestApprovalAsync(room.Id, token, request, CancellationToken.None);
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        Assert.True(card.Truncated);
        Assert.NotNull(_hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null)); // 다 보지 못한 명령은 허용 못 함
        Assert.Null(_hub.Decide(room.Id, card.Id, ApprovalChoice.Deny, null));
        Assert.False((await task)!.Allow);
        _hub.Stop(room.Id);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task BadToken_IsRejectedBeforeReadingBody()
    {
        var room = CommandRoom();
        using var http = new HttpClient();
        // 토큰이 없으면 큰 본문(상한 2배)이어도 읽기 전에 403.
        var big = new string('a', ApprovalEndpoint.MaxBodyBytes * 2);
        var res = await http.PostAsync($"{_base}/approve?room={room.Id}&token=nope", new StringContent(big));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
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
    public async Task SnapshotShowsPendingCard()
    {
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var card = (await Until(ch, e => e.Kind == "approval")).Last().Approval!;
        var late = _hub.Subscribe(room.Id); // 화면을 새로 연 경우
        var first = await Until(late, e => e.Kind == "approval");
        Assert.Equal(card.Id, first.Last().Approval!.Id);
        _hub.Decide(room.Id, card.Id, ApprovalChoice.Allow, null);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task WrongToken_IsForbidden()
    {
        var room = CommandRoom();
        using var http = new HttpClient();
        var body = new StringContent("""{"tool_name":"Bash","tool_input":{"command":"whoami"}}""", System.Text.Encoding.UTF8, "application/json");
        var res = await http.PostAsync($"{_base}/approve?room={room.Id}&token=nope", body);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode); // 실행 중이 아니거나 토큰이 다름

        Assert.Null(await _hub.RequestApprovalAsync(room.Id, "nope", ApprovalRequest.Parse("""{"tool_name":"Bash","tool_input":{}}""")!, CancellationToken.None));
    }

    [Fact]
    public async Task NoBaseUrl_CommandRoomFailsClearly()
    {
        _hub.ApprovalBaseUrl = null;
        var room = CommandRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "approve please");
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("승인 주소", end.Text);
        Assert.Empty(_specs);
    }
}
