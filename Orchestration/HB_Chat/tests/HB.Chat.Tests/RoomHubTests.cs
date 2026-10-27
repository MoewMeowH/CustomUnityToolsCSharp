using System.Diagnostics;
using System.Threading.Channels;
using HB.Chat;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 방 실행 (RoomHub + ClaudeRunner) — 진짜 claude 대신 TestData/fake-claude.ps1 (프롬프트 첫 낱말로 동작을 고른다).
/// </summary>
public sealed class RoomHubTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-hub-" + Guid.NewGuid().ToString("N"));
    private readonly RoomStore _store;
    private readonly RoomHub _hub;
    private readonly List<RunSpec> _specs = [];

    public RoomHubTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        Directory.CreateDirectory(Path.Combine(_dir, "projects"));
        var options = new ChatOptions
        {
            DataDir = Path.Combine(_dir, "data"),
            Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws") },
        };
        _store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        _hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(options))
        {
            ProcessFactory = (room, spec, workspace) =>
            {
                lock (_specs) _specs.Add(spec);
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId, spec.ApprovalHookUrl ?? ""], workspace);
            },
        };
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Room NewRoom() => _store.Create("ws", PermissionLevel.Chat, "opus", null);

    private static async Task<List<string>> Collect(Channel<string> ch, int count)
    {
        var seen = new List<string>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var id in ch.Reader.ReadAllAsync(cts.Token))
        {
            seen.Add(id);
            if (seen.Count >= count) break;
        }
        return seen;
    }

    [Fact]
    public async Task AppEvents_TurnTouchesRoom_AndActivityFollows()
    {
        var room = NewRoom();
        var other = NewRoom();
        var all = _hub.SubscribeAll();
        var ch = _hub.Subscribe(room.Id);
        Assert.Equal(RoomActivity.Idle, _hub.ActivityOf(room.Id));

        Assert.Null(_hub.Send(room.Id, "sleep now"));
        await Until(ch, e => e.Kind == "start");
        Assert.Equal(RoomActivity.Busy, _hub.ActivityOf(room.Id));
        Assert.Equal(RoomActivity.Idle, _hub.ActivityOf(other.Id));

        _hub.Touch(other.Id); // 이름 바꾸기 · 읽음 같은 화면 쪽 변경
        _hub.Stop(room.Id);
        await Until(ch, e => e.Kind == "idle");
        Assert.Equal(RoomActivity.Idle, _hub.ActivityOf(room.Id));

        // start · (Touch) · end · idle — 흘림 글(delta)은 앱 전체 알림에 넣지 않는다.
        var ids = await Collect(all, 4);
        Assert.Equal([room.Id, other.Id, room.Id, room.Id], ids);
        Assert.False(all.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RoomSubscriber_GetsListSignals_ForAnyRoom()
    {
        // 탭 하나 = SSE 하나: 연 방의 구독이 다른 방의 목록 신호도 받는다 (/self-review 5225b1ffaf C1).
        var room = NewRoom();
        var other = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Touch(other.Id);
        var seen = await Until(ch, e => e.Kind == "rooms", withRooms: true);
        Assert.Equal(other.Id, seen.Last().Text);

        _hub.Unsubscribe(room.Id, ch);
        _hub.Touch(other.Id); // 끊은 구독에는 더 오지 않는다 (닫힌 채널)
        Assert.True(ch.Reader.Completion.IsCompleted || !ch.Reader.TryRead(out _));
    }

    [Fact]
    public void AppEvents_DisposeCompletesSubscribers()
    {
        var all = _hub.SubscribeAll();
        _hub.Dispose();
        Assert.True(all.Reader.Completion.IsCompleted);
        Assert.True(_hub.SubscribeAll().Reader.Completion.IsCompleted); // 끈 뒤 새 구독은 바로 닫힌다
    }

    [Fact]
    public void Send_RejectsRoomInTrash()
    {
        var room = NewRoom();
        _store.Update(room.Id, r => r with { DeletedAt = DateTimeOffset.Now });
        Assert.Contains("휴지통", _hub.Send(room.Id, "hello"));
        Assert.False(_hub.IsBusy(room.Id));
    }

    [Fact]
    public void RoomList_SkipsTrash_NewestFirst()
    {
        var older = NewRoom();
        var newer = NewRoom();
        var trashed = NewRoom();
        _store.Update(older.Id, r => r with { CreatedAt = DateTimeOffset.Now.AddHours(-2) });
        _store.Update(trashed.Id, r => r with { DeletedAt = DateTimeOffset.Now });
        var list = new RoomList(_store, _hub, new ChatTranscripts(() => Path.Combine(_dir, "projects"))).All();
        Assert.Equal([newer.Id, older.Id], list.Select(s => s.Id));
    }

    private static async Task<List<LiveEvent>> Until(Channel<LiveEvent> ch, Func<LiveEvent, bool> last, bool withRooms = false)
    {
        var seen = new List<LiveEvent>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var ev in ch.Reader.ReadAllAsync(cts.Token))
        {
            if (ev.Kind == "rooms" && !withRooms) continue; // 방 목록 신호는 따로 본다 (RoomSubscriber_GetsListSignals)
            seen.Add(ev);
            if (last(ev)) break;
        }
        return seen;
    }

    [Fact]
    public async Task OneTurn_StreamsAndRecords()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello there"));

        var seen = await Until(ch, e => e.Kind == "idle");
        var kinds = seen.Select(e => e.Kind).ToList();
        Assert.Equal(["snapshot", "start", "tool", "denied", "delta", "delta", "end", "idle"], kinds);
        Assert.Equal("hello there", seen[1].Prompt);
        Assert.Equal("Read", seen[2].Text);
        Assert.StartsWith("Write:", seen[3].Text);
        Assert.Equal("Hello", string.Concat(seen.Where(e => e.Kind == "delta").Select(e => e.Text)));
        var end = seen.Single(e => e.Kind == "end");
        Assert.True(end.Ok);
        Assert.Equal(0.0125, end.CostUsd);

        var saved = _store.Get(room.Id)!;
        Assert.Null(saved.LastError);
        Assert.Equal(0.0125, saved.TotalCostUsd);
        Assert.NotNull(saved.LastRunAt);
        // 세션 기록이 아직 없으니 첫 말 (--session-id + 방 규칙).
        var spec = Assert.Single(_specs);
        Assert.False(spec.Resume);
        Assert.Equal(room.SessionId, spec.SessionId);
        Assert.NotNull(spec.AppendSystemPrompt);
        Assert.False(_hub.IsBusy(room.Id));
    }

    [Fact]
    public async Task QueuedWhileBusy_StopClearsQueueAndKills()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "sleep please"));
        await Until(ch, e => e.Kind == "delta"); // 가짜가 init · 글 하나를 내고 60초 잔다
        Assert.Null(_hub.Send(room.Id, "hello later"));
        var queued = (await Until(ch, e => e.Kind == "queued")).Last();
        Assert.Equal(["hello later"], queued.Pending);

        var sw = Stopwatch.StartNew();
        _hub.Stop(room.Id);
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"중지가 {sw.Elapsed} 걸렸다");
        var end = seen.Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Equal("중지됨", end.Text);
        Assert.DoesNotContain(seen, e => e.Kind == "start"); // 큐에 있던 말은 돌지 않는다
        Assert.Null(_store.Get(room.Id)!.LastError); // 중지는 실패로 남기지 않는다 (목록 "실패" 표시 — 2-2 a)

        // 중지 뒤 새 말은 다시 돈다.
        Assert.Null(_hub.Send(room.Id, "hello again"));
        var again = await Until(ch, e => e.Kind == "idle");
        Assert.True(again.Single(e => e.Kind == "end").Ok);
    }

    [Fact]
    public async Task OtherSession_IsStopped()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "other");
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("다른 세션", end.Text);
    }

    [Theory]
    [InlineData("fail", "boom")]
    [InlineData("nores", "결과 없이 끝났습니다")]
    public async Task Failures_AreReported(string prompt, string expected)
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, prompt);
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains(expected, end.Text);
        Assert.Contains(expected, _store.Get(room.Id)!.LastError);
    }

    [Fact]
    public void Send_RejectsBadInput()
    {
        var room = NewRoom();
        Assert.NotNull(_hub.Send(room.Id, "   "));
        Assert.NotNull(_hub.Send(room.Id, new string('x', RoomHub.MaxPromptChars + 1)));
        Assert.NotNull(_hub.Send("aaaaaaaaaaaa", "hi"));
    }

    [Fact]
    public async Task WorkspaceOutsideList_Fails()
    {
        var room = _store.Create("nope", PermissionLevel.Chat, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("허용 목록", end.Text);
        Assert.Empty(_specs);
    }

    [Fact]
    public async Task ResumeWhenTranscriptExists_AndWarnsWhenChangedOutside()
    {
        var room = NewRoom();
        var projectDir = Path.Combine(_dir, "projects", "F--ws");
        Directory.CreateDirectory(projectDir);
        var transcript = Path.Combine(projectDir, room.SessionId + ".jsonl");
        File.WriteAllText(transcript, "{}\n");
        _store.Update(room.Id, r => r with { KnownSize = 1 }); // 앱이 본 크기와 다르다 (밖에서 이어 씀)

        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Contains(seen, e => e.Kind == "notice" && e.Text!.Contains("이 앱 밖에서"));
        var spec = Assert.Single(_specs);
        Assert.True(spec.Resume);
        Assert.Equal(new FileInfo(transcript).Length, _store.Get(room.Id)!.KnownSize);

        // 다음 턴에는 크기가 맞으니 경고가 다시 나오지 않고, 새로 붙은 화면에도 지난 경고가 오지 않는다 (0a3b1d5155 C7).
        _hub.Send(room.Id, "hello again");
        var next = await Until(ch, e => e.Kind == "idle");
        Assert.DoesNotContain(next, e => e.Kind == "notice");
        var fresh = _hub.Subscribe(room.Id);
        Assert.True(fresh.Reader.TryRead(out var first));
        Assert.Equal("snapshot", first.Kind);
        Assert.False(fresh.Reader.TryRead(out _)); // 지난 경고(notice)가 따라오지 않는다
    }

    [Fact]
    public async Task RunCutByAppExit_IsNotReportedAsOutsideChange()
    {
        // 앞 실행이 끝을 남기지 못한 채(앱 종료) 세션 기록이 자랐다 → "앱 밖에서 바뀜"이 아니라 "앞 실행이 끊김".
        var room = NewRoom();
        var projectDir = Path.Combine(_dir, "projects", "F--ws");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, room.SessionId + ".jsonl"), "{}\n{}\n");
        _store.Update(room.Id, r => r with { KnownSize = 3, RunStartedAt = DateTimeOffset.Now.AddMinutes(-5) });

        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        var seen = await Until(ch, e => e.Kind == "idle");
        var notice = seen.Single(e => e.Kind == "notice");
        Assert.Contains("앱 종료로 끊겼습니다", notice.Text);
        Assert.DoesNotContain("이 앱 밖에서", notice.Text);
        Assert.Null(_store.Get(room.Id)!.RunStartedAt); // 이번 실행은 끝을 남겼다
    }

    [Fact]
    public async Task SendRightAfterStop_RunsBeforeIdle()
    {
        // 중지한 뒤 루프가 쉬기 전에 보낸 말은 이어서 돈다 (RoomHub 루프의 새 세대 분기, 0a3b1d5155 C12).
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "sleep please");
        await Until(ch, e => e.Kind == "delta");
        _hub.Stop(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello after stop")); // 보통은 앞 실행이 끝나기 전이라 큐에 들어간다
        var seen = await Until(ch, e => e.Kind == "end" && e.Ok == true);
        var ends = seen.Where(e => e.Kind == "end").ToList();
        Assert.Equal(2, ends.Count);
        Assert.Equal("중지됨", ends[0].Text);
        Assert.Equal("hello after stop", seen.Single(e => e.Kind == "start").Prompt);
        Assert.Equal("idle", (await Until(ch, e => e.Kind == "idle")).Last().Kind);
    }

    [Fact]
    public async Task ReadRoom_WritesOnlyTheMcpServerConfig()
    {
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"),
            """{"mcpServers":{"hb-orchestration":{"command":"dotnet","args":["x.dll"]},"other":{"command":"evil"}}}""");
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);

        var spec = Assert.Single(_specs);
        Assert.Equal(PermissionLevel.Read, spec.Level);
        Assert.NotNull(spec.McpConfigPath);
        Assert.StartsWith(Path.Combine(_dir, "data"), spec.McpConfigPath);
        var text = File.ReadAllText(spec.McpConfigPath);
        Assert.Contains("hb-orchestration", text);
        Assert.DoesNotContain("evil", text);
    }

    [Fact]
    public async Task ReadRoom_WithoutMcpJson_FailsBeforeRunning()
    {
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains(".mcp.json", end.Text);
        Assert.Empty(_specs);
        Assert.Contains(".mcp.json", _store.Get(room.Id)!.LastError);
    }

    [Fact]
    public async Task Dispose_CompletesSubscribers()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        await Until(ch, e => e.Kind == "snapshot");
        _hub.Dispose();
        using var cts = new CancellationTokenSource(Wait);
        Assert.False(await ch.Reader.WaitToReadAsync(cts.Token)); // 닫혀서 더 읽을 것이 없다
    }
}
