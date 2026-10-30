using System.Diagnostics;
using System.Threading.Channels;
using HB.Chat;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace HB.Chat.Tests;

/// <summary>
/// 서버가 턴 도중 꺼졌다 켜질 때 (사용자 목록 2번): 같은 데이터 폴더로 서버를 다시 만들면 방이 "실행 중"에 멈춰 있지 않고, 기다리던 카드는 사라지고,
/// claude 프로세스는 끝나 있고, 다음 말이 그대로 돈다. 정상 종료(RoomHub.Dispose — 호스트가 끌 때 부름)만 본다 — 강제 종료는 Job Object가 프로세스 트리를 끝낸다.
/// </summary>
public sealed class RestartTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-restart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ChatOptions Options() => new()
    {
        DataDir = Path.Combine(_dir, "data"),
        ClaudeProjectsDir = Path.Combine(_dir, "projects"),
        Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws") },
    };

    private string TranscriptPath(string sessionId) => Path.Combine(_dir, "projects", "F--ws", sessionId + ".jsonl");

    /// <summary>서버 한 벌 — 같은 데이터 폴더 · 세션 기록 폴더를 읽는다 (다시 켜기 = 새로 만들기).</summary>
    private (RoomStore Store, RoomHub Hub, GroupChat Groups) Server()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        Directory.CreateDirectory(Path.Combine(_dir, "projects", "F--ws")); // 가짜 claude 'say'는 세션 기록 폴더를 만들지 않는다
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        var options = Options();
        var store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        var hub = new RoomHub(store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Microsoft.Extensions.Options.Options.Create(options))
        {
            Log = new ApprovalLog(() => options.DataFolder),
            ProcessFactory = (room, spec, workspace) => new ProcessSpec("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId, Path.Combine(_dir, "tasks"), TranscriptPath(spec.SessionId)], workspace),
        };
        var groups = new GroupChat(store, hub, new GroupLog(() => options.DataFolder), null, Microsoft.Extensions.Options.Options.Create(options));
        return (store, hub, groups);
    }

    private static async Task<List<LiveEvent>> Until(Channel<LiveEvent> ch, Func<LiveEvent, bool> last)
    {
        var seen = new List<LiveEvent>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var ev in ch.Reader.ReadAllAsync(cts.Token))
        {
            seen.Add(ev);
            if (last(ev)) break;
        }
        return seen;
    }

    /// <summary>가짜 claude가 적은 프로세스 ID가 끝날 때까지 (10초). 끝났으면 true.</summary>
    private async Task<bool> ProcessGone(string sessionId)
    {
        var pidFile = Path.Combine(_dir, "tasks", "pid-" + sessionId);
        var sw = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && sw.Elapsed < Wait) await Task.Delay(50);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        while (sw.Elapsed < Wait + TimeSpan.FromSeconds(10))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; } // 없는 프로세스
            await Task.Delay(100);
        }
        return false;
    }

    [Fact]
    public async Task Room_ShutdownMidTurn_RestartClean()
    {
        string roomId, sessionId;
        {
            var (store, hub, _) = Server();
            var room = store.Create("ws", PermissionLevel.Command, "opus", "재시작");
            (roomId, sessionId) = (room.Id, room.SessionId);
            var ch = hub.Subscribe(room.Id);
            Assert.Null(hub.Send(room.Id, "say 1"));
            Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok); // 끝까지 돈 턴 하나 (세션 기록 크기가 적힘)
            Assert.Null(hub.Send(room.Id, "hangpid"));
            await Until(ch, e => e.Kind == "approval");
            Assert.Single(hub.WaitingCards(room.Id).Approvals);
            hub.Dispose(); // 서버를 끔 (호스트가 끌 때)
            Assert.True(await ProcessGone(sessionId), "claude 프로세스가 끝나지 않음");
        }

        var (store2, hub2, _) = Server(); // 다시 켬
        try
        {
            var after = store2.Get(roomId)!;
            output.WriteLine($"다시 켠 뒤: RunStartedAt={after.RunStartedAt?.ToString() ?? "null"} · LastError={after.LastError ?? "null"} · KnownSize={after.KnownSize}");
            // 끊긴 실행은 중지로 적히지 않는다 — 시작 표시가 남고 실패도 아님 (다음 말에 안내)
            Assert.NotNull(after.RunStartedAt);
            Assert.Null(after.LastError);
            Assert.Equal(RoomActivity.Idle, hub2.ActivityOf(roomId));
            Assert.False(hub2.IsBusy(roomId));
            Assert.Empty(hub2.WaitingCards(roomId).Approvals);

            // 다음 말이 그대로 돈다
            var ch2 = hub2.Subscribe(roomId);
            Assert.Null(hub2.Send(roomId, "say 1"));
            var seen = await Until(ch2, e => e.Kind == "idle");
            Assert.True(seen.Single(e => e.Kind == "end").Ok);
            foreach (var n in seen.Where(e => e.Kind == "notice")) output.WriteLine($"다음 말의 안내: {n.Text}");
            Assert.Contains(seen, e => e.Kind == "notice" && e.Text!.Contains("앱 종료로 끊겼습니다"));
            output.WriteLine($"다음 말 뒤: LastError={store2.Get(roomId)!.LastError ?? "null"} · RunStartedAt={store2.Get(roomId)!.RunStartedAt?.ToString() ?? "null"}");
            Assert.Null(store2.Get(roomId)!.RunStartedAt);
        }
        finally { hub2.Dispose(); }
    }

    [Fact]
    public async Task Group_ShutdownWhileMemberWaits_RestartClean()
    {
        string groupId, childId, childSession;
        {
            var (store, hub, groups) = Server();
            var (group, error) = groups.Create("ws", PermissionLevel.Read, "opus", null, "재시작 단톡방",
                [new GroupMemberSpec("A", "", Level: PermissionLevel.Command), new GroupMemberSpec("B", "")]);
            Assert.Null(error);
            groupId = group!.Id;
            childId = group.Members[0].RoomId;
            childSession = store.Get(childId)!.SessionId;
            var events = hub.Subscribe(groupId);
            Assert.Null(groups.Send(groupId, "hangpid @A"));
            await Until(events, e => e.Kind == "approval");
            Assert.True(groups.Waiting(childId));
            hub.Dispose();
            Assert.True(await ProcessGone(childSession), "구성원 claude 프로세스가 끝나지 않음");
        }

        var (store2, hub2, groups2) = Server();
        try
        {
            var group = store2.Get(groupId)!;
            Assert.All(groups2.States(group), s => Assert.Null(s.State)); // 칩이 "생각 중" · "승인 기다림"에 멈춰 있지 않음
            Assert.Empty(groups2.Cards(group));
            Assert.False(groups2.Waiting(childId));
            var log = new GroupLog(() => Options().DataFolder).Read(groupId);
            output.WriteLine("다시 켠 뒤 대화 줄: " + string.Join(" | ", log.Select(e => $"{e.Kind}:{e.Text}")));
            // 사용자 말은 남고 답은 없다 — 화면은 "답이 없습니다 (실행이 끊김)"으로 그린다 (Answered · Waiting 둘 다 아님)
            var user = Assert.Single(log, e => e.Kind == GroupEntryKind.User);
            Assert.DoesNotContain((user.Line, group.Members[0].Id), GroupChat.AnsweredBy(log));

            // 새 말을 막지 않는다 ("답하는 중" 아님)
            Assert.Null(groups2.Send(groupId, "@B 다시"));
        }
        finally { hub2.Dispose(); }
    }
}
