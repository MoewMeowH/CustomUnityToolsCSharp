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
    private readonly ChatOptions _options;

    public RoomHubTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "ws"));
        Directory.CreateDirectory(Path.Combine(_dir, "projects"));
        var options = _options = new ChatOptions
        {
            DataDir = Path.Combine(_dir, "data"),
            ClaudeProjectsDir = Path.Combine(_dir, "projects"), // 할 일 폴더는 그 옆 tasks (3차 b)
            Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws") },
        };
        _store = new RoomStore(Path.Combine(_dir, "data", "rooms.json"));
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        _hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(options))
        {
            ProcessFactory = (room, spec, workspace) =>
            {
                lock (_specs)
                {
                    _specs.Add(spec);
                    // 방별 설정 파일은 실행이 끝나면 지워진다 — 띄울 때의 내용을 남긴다
                    _configTexts.Add(spec.McpConfigPath is { } p && File.Exists(p) ? File.ReadAllText(p) : null);
                }
                // echo 모드는 세션 기록에 한 줄을 덧붙인다 (진짜 CLI처럼 — 규칙 지문은 기록이 늘 때만 저장)
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId,
                    Path.Combine(_dir, "tasks"), Path.Combine(_dir, "projects", "F--ws", spec.SessionId + ".jsonl")], workspace);
            },
        };
    }

    private readonly List<string?> _configTexts = [];

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
    public async Task BlockedBeforeRun_EndCarriesPrompt()
    {
        // 4차 C2: claude를 띄우기 전에 막힌 말은 end에 Prompt를 실어 화면이 남긴다 (세션 기록에 없으므로). 돈 턴은 싣지 않는다.
        var blocked = _store.Create("없는폴더", PermissionLevel.Chat, "opus", null);
        var ch = _hub.Subscribe(blocked.Id);
        Assert.Null(_hub.Send(blocked.Id, "여기 말"));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("허용 목록에 없거나", end.Text);
        Assert.Equal("여기 말", end.Prompt);

        var room = NewRoom();
        var ok = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello there"));
        Assert.Null((await Until(ok, e => e.Kind == "idle")).Single(e => e.Kind == "end").Prompt);
    }

    [Fact]
    public async Task ClaudeMissing_EndCarriesPrompt()
    {
        // /self-review bd52c6f0d1 C1: 실행 파일이 없어 띄우지 못한 실패도 세션 기록이 그대로라 Prompt를 싣는다
        var options = new ChatOptions
        {
            DataDir = Path.Combine(_dir, "data"),
            ClaudeProjectsDir = Path.Combine(_dir, "projects"),
            Workspaces = new(StringComparer.OrdinalIgnoreCase) { ["ws"] = Path.Combine(_dir, "ws") },
        };
        using var hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(options))
        {
            ProcessFactory = (_, _, workspace) => new ProcessSpec(Path.Combine(_dir, "no-such-claude.exe"), [], workspace),
        };
        var room = NewRoom();
        var ch = hub.Subscribe(room.Id);
        Assert.Null(hub.Send(room.Id, "사라지면 안 되는 말"));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Equal("사라지면 안 되는 말", end.Prompt);
    }

    [Fact]
    public async Task OneTurn_FacesAndContext()
    {
        var room = NewRoom();
        Assert.Equal(Face.Idle, _hub.FaceOf(room));
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello there"));
        var seen = await Until(ch, e => e.Kind == "idle", withRooms: true);

        // 생각 중 → 도구(Read) → 답 쓰는 중, 끝은 end에 실린다 (계획서 2-2 b). 도구 표정은 이름을 싣는다 (83b453653b C1)
        Assert.Equal(["Thinking", "Tool", "Writing"], seen.Where(e => e.Kind == "face").Select(e => e.Face));
        Assert.Equal("Read", seen.Single(e => e.Kind == "face" && e.Face == "Tool").Text);
        Assert.NotNull(_hub.EndFaceLeft(room.Id)); // 끝난 직후 — 화면 타이머용 남은 시간
        var end = seen.Single(e => e.Kind == "end");
        Assert.Equal("Done", end.Face);
        // 마지막 assistant usage 2 + 100 + 900 + 8 (서브에이전트 메시지는 뺌), modelUsage contextWindow
        Assert.Equal(1010, end.ContextTokens);
        Assert.Equal(200000, end.ContextWindow);
        var saved = _store.Get(room.Id)!;
        Assert.Equal(1010, saved.ContextTokens);
        Assert.Equal(200000, saved.ContextWindow);

        Assert.Equal(Face.Done, _hub.FaceOf(saved)); // 끝난 직후 잠깐
        Assert.Equal(Face.Idle, _hub.FaceOf(saved, transient: false)); // 목록은 쉼
    }

    [Fact]
    public async Task ForkRoom_FirstRunForks_ThenClears()
    {
        const string source = "2ac92fd3-e819-408f-a834-c33490b7c038";
        var room = _store.Create("ws", PermissionLevel.Chat, "opus", "갈래", forkOf: source);
        Assert.Null(room.ReadUpTo); // 이어받은 말은 읽은 것으로
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello fork"));
        await Until(ch, e => e.Kind == "idle");
        Assert.Equal(source, Assert.Single(_specs).ForkFrom);
        Assert.Null(_store.Get(room.Id)!.ForkOf); // 성공하면 한 번으로 끝

        Assert.Null(_hub.Send(room.Id, "hello again"));
        await Until(ch, e => e.Kind == "idle");
        Assert.Null(_specs[1].ForkFrom);
    }

    [Fact]
    public async Task ForkRoom_RecordAlreadyThere_ResumesAndClearsFork()
    {
        // 중지 등으로 갈래 기록이 이미 생긴 방: 다음 실행은 resume이고 갈래 표시는 비운다 (중복 갈래 없음)
        var room = _store.Create("ws", PermissionLevel.Chat, "opus", null, forkOf: "2ac92fd3-e819-408f-a834-c33490b7c038");
        var dir = Path.Combine(_dir, "projects", "ws");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, room.SessionId + ".jsonl"),
            """{"type":"user","timestamp":"2026-10-28T00:00:00Z","message":{"role":"user","content":"이어받은 질문"}}""" + "\n");
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello fork"));
        await Until(ch, e => e.Kind == "idle");
        Assert.True(Assert.Single(_specs).Resume);
        Assert.Null(_specs[0].ForkFrom);
        Assert.Null(_store.Get(room.Id)!.ForkOf);
    }

    [Fact]
    public async Task CancelQueued_RemovesOnlyThatMessage()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "sleep please"));
        await Until(ch, e => e.Kind == "delta");
        Assert.Null(_hub.Send(room.Id, "hello one"));
        Assert.Null(_hub.Send(room.Id, "hello two"));
        var items = (await Until(ch, e => e.Kind == "queued" && e.PendingItems!.Count == 2)).Last().PendingItems!;

        Assert.Null(_hub.CancelQueued(room.Id, items[0].Id));
        var after = (await Until(ch, e => e.Kind == "queued")).Last();
        Assert.Equal(["hello two"], after.Pending);
        Assert.Equal(items[1].Id, Assert.Single(after.PendingItems!).Id);
        Assert.NotNull(_hub.CancelQueued(room.Id, items[0].Id)); // 이미 취소함
        Assert.NotNull(_hub.CancelQueued(room.Id, "nope"));

        _hub.Stop(room.Id);
        await Until(ch, e => e.Kind == "idle");
    }

    [Fact]
    public async Task Interrupt_StopsCurrent_RunsFirst_KeepsQueue()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "sleep please"));
        await Until(ch, e => e.Kind == "delta");
        Assert.Null(_hub.Send(room.Id, "hello later"));
        await Until(ch, e => e.Kind == "queued");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(_hub.Interrupt(room.Id, "hello now"));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"끼어들기가 {sw.Elapsed} 걸렸다");

        // 지금 턴은 중지로 끝나고(끼어든 말이 큐 맨 앞), 그다음 기다리던 말이 돈다
        Assert.Equal("Stopped", seen.First(e => e.Kind == "end").Face);
        Assert.Equal(["hello now", "hello later"], seen.Where(e => e.Kind == "start").Select(e => e.Prompt));
        Assert.Equal(3, seen.Count(e => e.Kind == "end")); // 끊긴 턴 · 끼어든 말 · 기다리던 말
    }

    [Fact]
    public async Task SendNow_MovesThatMessageFirst_AndCutsCurrentTurn()
    {
        // 기다리는 말의 "지금" (3차 b): 그 말이 먼저, 다른 기다리는 말은 남아 뒤에 돈다
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "sleep please"));
        await Until(ch, e => e.Kind == "delta");
        Assert.Null(_hub.Send(room.Id, "hello one"));
        Assert.Null(_hub.Send(room.Id, "hello two"));
        var items = (await Until(ch, e => e.Kind == "queued" && e.PendingItems!.Count == 2)).Last().PendingItems!;

        Assert.Null(_hub.SendNow(room.Id, items[1].Id));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("Stopped", seen.First(e => e.Kind == "end").Face);
        Assert.Equal("끼어들기로 멈춤", seen.First(e => e.Kind == "end").Text);
        Assert.Equal(["hello two", "hello one"], seen.Where(e => e.Kind == "start").Select(e => e.Prompt));
        Assert.NotNull(_hub.SendNow(room.Id, items[1].Id)); // 이미 보냄
    }

    [Fact]
    public async Task TaskTools_SendStepsFromTasksFolder()
    {
        // 할 일 도구 뒤 출력 · 턴 끝에 폴더를 다시 읽어 steps 알림 (3차 b, 0번 실측 B 모양) — 칩은 할 일 제목 · 번호 → 상태.
        // 할 일 도구가 있는 단계(편집)로 — 대화 단계는 도구가 없다 (/self-review ea28ba9185 C7)
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        var room = _store.Create("ws", PermissionLevel.Edit, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        Assert.Empty((await Until(ch, e => e.Kind == "snapshot")).Last().Steps!); // 아직 할 일 없음
        Assert.Null(_hub.Send(room.Id, "tasks please"));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal(PermissionLevel.Edit, _specs.Last().Level);
        Assert.Equal(["alpha", "beta", "#1 → in_progress"], seen.Where(e => e.Kind == "tool").Select(e => e.Detail));
        Assert.Null(_hub.StepProgress(room.Id)); // 쉬는 방은 목록에 진행 글을 붙이지 않는다
        var last = seen.Last(e => e.Kind == "steps").Steps!;
        Assert.Equal([new StepItem("1", "alpha", "in_progress"), new StepItem("2", "beta", "pending")], last);
        Assert.Equal("blocked", _store.Get(room.Id)!.Status!.Category);
        Assert.Equal("check alpha", _store.Get(room.Id)!.Status!.NeedsAction);

        // 다시 연 화면은 snapshot으로 같은 목록 · 상태를 받는다
        var again = (await Until(_hub.Subscribe(room.Id), e => e.Kind == "snapshot")).Last();
        Assert.Equal(last, again.Steps);
        Assert.Equal("check alpha", again.Status!.NeedsAction);
    }

    [Fact]
    public async Task Interrupt_WhenIdle_IsPlainSend()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Interrupt(room.Id, "hello idle"));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("hello idle", seen.Single(e => e.Kind == "start").Prompt);
    }

    [Fact]
    public async Task AttachDir_InSpecWhenFolderExists()
    {
        // 첨부는 읽기 단계 이상 (대화 단계는 받지 않음) — 읽기 방은 작업 폴더 .mcp.json이 있어야 돈다
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet","args":["x.dll"]}}}""");
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello no attach"));
        await Until(ch, e => e.Kind == "idle");
        Assert.Null(_specs[0].AttachDir);

        var folder = Attachments.Folder(Path.Combine(_dir, "data"), room.Id);
        Directory.CreateDirectory(folder);
        Assert.Null(_hub.Send(room.Id, "hello attach"));
        await Until(ch, e => e.Kind == "idle");
        Assert.Equal(Path.GetFullPath(folder), _specs[1].AttachDir);

        // .claude가 생긴 첨부 폴더는 열지 않고 알린다 (/self-review 0c3b7e3514 C2 — CLI가 추가 폴더의 스킬을 불러옴)
        Directory.CreateDirectory(Path.Combine(folder, ".claude"));
        Assert.Null(_hub.Send(room.Id, "hello claude dir"));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Null(_specs[2].AttachDir);
        Assert.Contains(".claude", seen.Single(e => e.Kind == "notice").Text);
    }

    [Fact]
    public async Task Interrupt_EndTextSaysInterrupted()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "sleep please"));
        await Until(ch, e => e.Kind == "delta");
        Assert.Null(_hub.Interrupt(room.Id, "hello now"));
        var seen = await Until(ch, e => e.Kind == "idle");
        Assert.Equal("끼어들기로 멈춤", seen.First(e => e.Kind == "end").Text); // "중지됨"이 아니라 (지휘자 검토 R5)
    }

    [Fact]
    public void Search_SkipsTrash()
    {
        var kept = _store.Create("ws", PermissionLevel.Chat, "opus", "빌드 방 하나");
        var trashed = _store.Create("ws", PermissionLevel.Chat, "opus", "빌드 방 둘");
        _store.Update(trashed.Id, r => r with { DeletedAt = DateTimeOffset.Now });
        var hits = new RoomList(_store, _hub, new ChatTranscripts(() => Path.Combine(_dir, "projects"))).Search("빌드");
        Assert.Equal([kept.Id], hits.Select(h => h.RoomId));
    }

    [Fact]
    public async Task ForkRoom_SourceBusy_RejectsFirstRun()
    {
        var source = NewRoom();
        var sourceCh = _hub.Subscribe(source.Id);
        Assert.Null(_hub.Send(source.Id, "sleep please"));
        await Until(sourceCh, e => e.Kind == "delta");

        var fork = _store.Create("ws", PermissionLevel.Chat, "opus", null, forkOf: source.SessionId);
        var ch = _hub.Subscribe(fork.Id);
        Assert.Null(_hub.Send(fork.Id, "hello fork"));
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("원래 방이 실행 중", end.Text);
        Assert.NotNull(_store.Get(fork.Id)!.ForkOf); // 끝난 뒤 다시 보내면 갈래로
        _hub.Stop(source.Id);
        await Until(sourceCh, e => e.Kind == "idle");
    }

    [Fact]
    public async Task ForkRoom_FailedFirstRun_KeepsFork()
    {
        var room = _store.Create("ws", PermissionLevel.Chat, "opus", null, forkOf: "2ac92fd3-e819-408f-a834-c33490b7c038");
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "fail now"));
        await Until(ch, e => e.Kind == "idle");
        Assert.NotNull(_store.Get(room.Id)!.ForkOf); // 다시 보내면 또 갈래로 시도
    }

    [Fact]
    public void FaceOf_IdleFailedSleep()
    {
        var room = NewRoom();
        Assert.Equal(Face.Failed, _hub.FaceOf(room with { LastError = "boom" }));
        Assert.Equal(Face.Sleep, _hub.FaceOf(room with { CreatedAt = DateTimeOffset.Now.AddHours(-2) }));
        Assert.Equal(Face.Idle, _hub.FaceOf(room with { CreatedAt = DateTimeOffset.Now.AddHours(-2), LastRunAt = DateTimeOffset.Now.AddMinutes(-5) }));
    }

    [Fact]
    public async Task Stop_EndsWithStoppedFace_AndKeepsKnownWindow()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        Assert.Null(_hub.Send(room.Id, "hello first")); // 창(200000)을 방 색인에 남긴다
        await Until(ch, e => e.Kind == "idle");
        Assert.Null(_hub.Send(room.Id, "sleep please"));
        await Until(ch, e => e.Kind == "delta");
        _hub.Stop(room.Id);
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.Equal("Stopped", end.Face);
        Assert.Equal(200000, end.ContextWindow); // result 없는 턴도 앞 창으로 (83b453653b C5)
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
            // 방 목록 신호 · 표정 알림은 따로 본다 (RoomSubscriber_GetsListSignals · OneTurn_FacesAndContext)
            if (ev.Kind is "rooms" or "face" && !withRooms) continue;
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
        // mcp: 실행 시작(init)의 연결 상태를 저장했다는 알림 — 곁칸 연결 탭이 다시 받는다
        Assert.Equal(["snapshot", "start", "mcp", "tool", "denied", "delta", "delta", "status", "end", "idle"], kinds);
        Assert.Equal("hello there", seen[1].Prompt);
        Assert.Equal("Read", seen[3].Text);
        Assert.Equal("notes.md", seen[3].Detail); // 칩 대상은 파일 이름만 (3차 b)
        Assert.Equal("said hello", seen.Single(e => e.Kind == "status").Status!.Detail);
        Assert.Null(seen.Single(e => e.Kind == "status").Status!.NeedsAction); // 빈 글은 없음으로
        Assert.StartsWith("Write:", seen[4].Text);
        Assert.Equal("Hello", string.Concat(seen.Where(e => e.Kind == "delta").Select(e => e.Text)));
        var end = seen.Single(e => e.Kind == "end");
        Assert.True(end.Ok);
        Assert.Equal(0.0125, end.CostUsd);

        var saved = _store.Get(room.Id)!;
        Assert.Null(saved.LastError);
        Assert.Equal(0.0125, saved.TotalCostUsd);
        Assert.NotNull(saved.LastRunAt);
        Assert.Equal(new RoomStatus("completed", "said hello", null), saved.Status); // 방 색인에 남아 목록 · 다음 화면에 보인다
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

    // ---- 방별 연결 (계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결) ----

    [Fact]
    public async Task RoomExtraServer_WritesRoomConfig_AllowsIt_AndDeletesAfterRun()
    {
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"),
            """{"mcpServers":{"hb-orchestration":{"command":"dotnet","args":["x.dll"]},"notion":{"url":"https://example.com/mcp"},"other":{"command":"evil"}}}""");
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        _store.Update(room.Id, r => r with { McpExtra = ["notion", "gone"] });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        var events = await Until(ch, e => e.Kind == "idle");
        Assert.True(events.Single(e => e.Kind == "end").Ok);
        Assert.Contains(events, e => e.Kind == "notice" && e.Text!.Contains("gone"));

        var spec = Assert.Single(_specs);
        Assert.Equal(McpConfigFile.RoomPath(Path.Combine(_dir, "data"), room.Id), spec.McpConfigPath);
        Assert.False(File.Exists(spec.McpConfigPath)); // 실행이 끝나면 지운다
        Assert.Equal(["notion"], spec.ExtraMcpServers);
        var config = Assert.Single(_configTexts);
        Assert.Contains("hb-orchestration", config);
        Assert.Contains("example.com/mcp", config);
        Assert.DoesNotContain("evil", config);
    }

    [Fact]
    public async Task RoomBaseServerOff_LeavesItOut()
    {
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"notion":{"url":"https://example.com/mcp"}}}""");
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        _store.Update(room.Id, r => r with { McpOff = ["hb-orchestration"], McpExtra = ["notion"] });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok); // 기본 서버가 .mcp.json에 없어도 꺼 두면 돈다
        var config = Assert.Single(_configTexts);
        Assert.Contains("notion", config);
        Assert.DoesNotContain("hb-orchestration", config);
    }

    [Fact]
    public async Task CommandRoomWithUnity_AddsUnityServer_AndKeepsCaptureImage()
    {
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        var project = Path.Combine(_dir, "MyGame");
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        var cli = Path.Combine(_dir, "unity.exe");
        File.WriteAllText(cli, "");
        _options.UnityProjects["MyGame"] = project;
        _options.UnityCliPath = cli;
        var log = new ApprovalLog(() => Path.Combine(_dir, "data"));
        var notes = new RoomNotes(() => Path.Combine(_dir, "data"));
        using var hub = new RoomHub(_store, new ChatTranscripts(() => Path.Combine(_dir, "projects")), Options.Create(_options))
        {
            Log = log,
            ProcessFactory = (r, spec, workspace) =>
            {
                lock (_specs) _specs.Add(spec);
                return new ProcessSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                    Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1"), spec.SessionId, Path.Combine(_dir, "tasks")], workspace);
            },
        };
        var room = _store.Create("ws", PermissionLevel.Command, "opus", null);
        _store.Update(room.Id, r => r with { UnityProject = "MyGame" });
        var ch = hub.Subscribe(room.Id);
        hub.Send(room.Id, "unityshot");
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);

        var spec = Assert.Single(_specs);
        Assert.Contains(McpConfigFile.UnityServer, spec.ExtraMcpServers);
        Assert.Contains("MyGame", spec.AppendSystemPrompt);
        var shots = notes.Notes(room.Id).Where(n => n.Image is not null).ToList();
        Assert.Equal(2, shots.Count); // 캡처 결과의 두 장(png · jpg) 다 — editor_status 그림은 남기지 않는다 (/self-review 0bcf01451b C7)
        Assert.All(shots, shot => Assert.Contains("capture_game_view", shot.Text));
        Assert.All(shots, shot => Assert.NotNull(RoomShots.PathOf(Path.Combine(_dir, "data"), room.Id, shot.Image)));
        Assert.Contains(shots, shot => shot.Image!.EndsWith(".jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Init_SavesMcpStatus_AndTellsScreen()
    {
        // 연결 탭: 실행 시작(init)의 상태를 방에 남기고 "mcp" 알림 — 첫 턴 도중 · 앱을 다시 켠 뒤에도 보이게 (사용자 확인 2026-10-29)
        var room = NewRoom();
        Assert.Null(room.McpStatusAt);
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        var seen = await Until(ch, e => e.Kind == "idle");
        var mcp = seen.FindIndex(e => e.Kind == "mcp");
        Assert.True(mcp >= 0);
        Assert.True(mcp < seen.FindIndex(e => e.Kind == "end")); // 턴 끝 전에
        Assert.NotNull(_store.Get(room.Id)!.McpStatusAt);
    }

    /// <summary>fake echo 모드가 돌려준 받은 말 (실패 결과 글의 "실행 오류 (…): " 뒤).</summary>
    private static async Task<string> Echoed(Channel<LiveEvent> ch)
    {
        var text = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Text!;
        return text[(text.IndexOf("): ", StringComparison.Ordinal) + 3)..];
    }

    [Fact]
    public async Task ChangedRules_ArePrependedOnce_WhenResuming()
    {
        // 이어 쓰는 세션은 CLI가 처음 시스템 프롬프트를 쓴다 (0단계 실측) — 규칙이 바뀌면 말 앞에 system-reminder로 새 규칙 (계획서 2026-10-29_HB_Chat-2-2-작은-한계-정리)
        var room = NewRoom();
        var projectDir = Path.Combine(_dir, "projects", "F--ws");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, room.SessionId + ".jsonl"), "{}\n"); // 이어 쓰는 세션
        var ch = _hub.Subscribe(room.Id);

        // 이 칸 전에 만든 방(RulesHash 없음): 한 번 받는다
        _hub.Send(room.Id, "echo first");
        var first = await Echoed(ch);
        Assert.StartsWith("<system-reminder>", first);
        Assert.Contains(RoomRules.For(PermissionLevel.Chat), first);
        Assert.EndsWith("echo first", first);
        Assert.Equal(RoomRules.Hash(RoomRules.For(PermissionLevel.Chat)), _store.Get(room.Id)!.RulesHash);

        // 그대로면 붙이지 않는다
        _hub.Send(room.Id, "echo second");
        Assert.Equal("echo second", await Echoed(ch));

        // 단계를 바꾸면 다시 한 번 — 새 단계의 규칙으로
        _store.Update(room.Id, r => r with { Level = PermissionLevel.Read });
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        _hub.Send(room.Id, "echo third");
        var third = await Echoed(ch);
        Assert.Contains(RoomRules.Describe(PermissionLevel.Read), third);
        Assert.EndsWith("echo third", third);
    }

    [Fact]
    public async Task ChangedRules_WaitForNonCommandMessage_AndNotSavedWithoutTranscript()
    {
        var room = NewRoom();
        var projectDir = Path.Combine(_dir, "projects", "F--ws");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, room.SessionId + ".jsonl"), "{}\n");
        var ch = _hub.Subscribe(room.Id);

        // '/명령' 앞에는 안내를 붙이지 않는다 — 명령으로 알아보지 못하게 되므로 (/self-review 70170a8801 C2). 지문도 그대로
        _hub.Send(room.Id, "/echo now");
        Assert.Equal("/echo now", await Echoed(ch));
        Assert.Null(_store.Get(room.Id)!.RulesHash);

        // 기록이 늘지 않은 실행(default 모드는 기록을 안 씀)은 안내를 붙여도 지문을 저장하지 않는다 (4c300badae C4 · 70170a8801 C3)
        _hub.Send(room.Id, "hello");
        await Until(ch, e => e.Kind == "idle");
        Assert.Null(_store.Get(room.Id)!.RulesHash);

        // 다음 보통 말에 붙는다
        _hub.Send(room.Id, "echo later");
        Assert.StartsWith("<system-reminder>", await Echoed(ch));
        Assert.NotNull(_store.Get(room.Id)!.RulesHash);
    }

    [Fact]
    public async Task TooLongLine_IsBlockedBeforeStart()
    {
        // stdin 한 줄 120KB 상한 (2026-10-29 실측: 한 번에 써도 약 300KB에서 CLI가 시작하지 않음, 70170a8801 C4)
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "echo " + new string('가', 50_000)); // UTF-8 약 150KB
        var end = (await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end");
        Assert.False(end.Ok);
        Assert.Contains("말이 너무 깁니다", end.Text);
        Assert.Empty(_specs); // claude를 띄우지 않았다
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(ClaudeRunner.UserLine(new string('가', 30_000))) < ClaudeRunner.MaxStdinLineBytes); // 한글 그대로 — 약 90KB
        Assert.Contains("가", ClaudeRunner.UserLine("가"));
    }

    [Fact]
    public async Task NewSession_GetsRulesBySystemPrompt_NotNote()
    {
        var room = NewRoom();
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "echo fresh");
        Assert.Equal("echo fresh", await Echoed(ch));
        Assert.NotNull(_store.Get(room.Id)!.RulesHash);
    }

    [Fact]
    public async Task ForkRoom_ReadsInheritedAttachFolders_SkipsMissing()
    {
        var parent = Path.Combine(_dir, "data", "attachments", "aaaaaaaaaaaa");
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        _store.Update(room.Id, r => r with { AttachFrom = ["bbbbbbbbbbbb", "aaaaaaaaaaaa", "../x"] }); // 지워진 조상 · 이상한 값은 뺀다
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        await Until(ch, e => e.Kind == "idle");
        Assert.Equal([parent], Assert.Single(_specs).InheritedAttachDirs);
    }

    [Fact]
    public void ChangedNote_IsHiddenByTranscriptReader()
    {
        // 공용 기록 읽기가 system-reminder를 걷어 내므로 화면 · 미리보기 · 검색에는 사용자 글만 (ClaudeChat.CleanUserText)
        var path = Path.Combine(_dir, "t.jsonl");
        var content = RoomRules.ChangedNote("규칙 글") + "사용자 글";
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user", timestamp = "2026-10-29T00:00:00Z", message = new { role = "user", content },
        }) + "\n");
        var chat = HB.Orchestration.Shared.ClaudeChatReader.ReadMore(path, null).Chat;
        Assert.Equal("사용자 글", Assert.Single(chat.Entries).Text.Trim());
    }

    [Fact]
    public async Task UnityProjectOnReadRoom_IsNotAttached()
    {
        File.WriteAllText(Path.Combine(_dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet"}}}""");
        var project = Path.Combine(_dir, "MyGame");
        Directory.CreateDirectory(project);
        var cli = Path.Combine(_dir, "unity.exe");
        File.WriteAllText(cli, "");
        _options.UnityProjects["MyGame"] = project;
        _options.UnityCliPath = cli;
        var room = _store.Create("ws", PermissionLevel.Read, "opus", null);
        _store.Update(room.Id, r => r with { UnityProject = "MyGame" });
        var ch = _hub.Subscribe(room.Id);
        _hub.Send(room.Id, "hello");
        Assert.True((await Until(ch, e => e.Kind == "idle")).Single(e => e.Kind == "end").Ok);
        var spec = Assert.Single(_specs);
        Assert.Empty(spec.ExtraMcpServers);
        Assert.DoesNotContain("unity", File.ReadAllText(spec.McpConfigPath!)); // 공용 파일 (실행 뒤에도 남음)
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
