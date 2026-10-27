using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace HB.Chat;

/// <summary>
/// 방 화면에 보내는 실시간 알림 (SSE data 한 줄 = JSON 하나).
/// kind: snapshot · start · delta · tool · denied · retry · end · queued · notice · idle.
/// Prompt는 지금 처리 중인 사용자 말, Pending은 큐에서 기다리는 말들.
/// </summary>
public sealed record LiveEvent(string Kind)
{
    public string? Text { get; init; }
    public string? Prompt { get; init; }
    public bool? Ok { get; init; }
    public bool? Busy { get; init; }
    public int? Queued { get; init; }
    public double? CostUsd { get; init; }
    public IReadOnlyList<string>? Pending { get; init; }
}

/// <summary>
/// 방마다 한 번에 한 턴 (계획서 3번). 실행 중에 온 말은 큐에 넣고 끝나면 차례로 보낸다.
/// 중지는 세대(generation)를 올려 큐를 비우고 실행을 끝낸다 — 늦게 끝난 실행은 화면 · 큐에 손대지 않는다.
/// 진행 중 턴의 흘림 글 · 도구 상태는 여기서만 들고 SSE로 흘린다. 끝난 말은 세션 기록에서 다시 읽는다.
/// </summary>
public sealed class RoomHub(RoomStore store, ChatTranscripts transcripts, IOptions<ChatOptions> options) : IDisposable
{
    /// <summary>한 번에 보낼 수 있는 글 길이 상한 (글자).</summary>
    public const int MaxPromptChars = 100_000;

    /// <summary>진행 중 흘림 글을 화면용으로 들고 있는 상한 (글자). 넘으면 앞을 버린다 (끝나면 세션 기록으로 바뀐다).</summary>
    private const int MaxPartialChars = 200_000;

    private sealed class Live
    {
        public readonly Lock Gate = new();
        public int Generation;
        public bool Busy;
        public readonly Queue<string> Pending = new();
        public CancellationTokenSource? Cts;
        public string? Current;
        public readonly System.Text.StringBuilder Partial = new();
        public string? Tool;
        public string? Notice;
        public readonly List<Channel<LiveEvent>> Subscribers = [];
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Live> _rooms = new();
    private bool _disposed;

    /// <summary>
    /// 이 실행기를 바꿀 때 (테스트: 가짜 claude). 기본은 claude 실행 파일 + <see cref="ClaudeArgs"/> 인자.
    /// </summary>
    public Func<Room, RunSpec, string, ProcessSpec>? ProcessFactory { get; init; }

    public bool IsBusy(string roomId) => _rooms.TryGetValue(roomId, out var live) && live.Busy;

    /// <summary>말 보내기. 실행 중이면 큐에 넣는다. 오류 문구(null이면 받음).</summary>
    public string? Send(string roomId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "빈 글은 보내지 않습니다.";
        if (text.Length > MaxPromptChars) return $"글이 너무 깁니다 ({text.Length:N0}자, 상한 {MaxPromptChars:N0}자).";
        if (store.Get(roomId) is null) return "없는 방입니다.";
        if (store.LoadError is not null) return "방 색인을 읽지 못해 보내지 않습니다.";

        var live = _rooms.GetOrAdd(roomId, _ => new Live());
        lock (live.Gate)
        {
            live.Pending.Enqueue(text);
            if (live.Busy)
            {
                Broadcast(live, new LiveEvent("queued") { Queued = live.Pending.Count, Pending = [.. live.Pending] });
                return null;
            }
            live.Busy = true;
            var generation = live.Generation;
            _ = Task.Run(() => LoopAsync(roomId, live, generation));
        }
        return null;
    }

    /// <summary>중지: 큐를 비우고 진행 중 실행을 끝낸다.</summary>
    public void Stop(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate)
        {
            live.Generation++;
            live.Pending.Clear();
            live.Cts?.Cancel();
        }
    }

    /// <summary>SSE 구독. 먼저 지금 상태(snapshot)를 넣어 준다. 끝나면 <see cref="Unsubscribe"/>.</summary>
    public Channel<LiveEvent> Subscribe(string roomId)
    {
        var live = _rooms.GetOrAdd(roomId, _ => new Live());
        var ch = Channel.CreateUnbounded<LiveEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (live.Gate)
        {
            ch.Writer.TryWrite(new LiveEvent("snapshot")
            {
                Busy = live.Busy,
                Text = live.Busy ? live.Partial.ToString() : null,
                Prompt = live.Current,
                Pending = [.. live.Pending],
                Queued = live.Pending.Count,
            });
            if (live.Busy && live.Tool is { } tool) ch.Writer.TryWrite(new LiveEvent("tool") { Text = tool });
            if (live.Notice is { } notice) ch.Writer.TryWrite(new LiveEvent("notice") { Text = notice });
            live.Subscribers.Add(ch);
        }
        return ch;
    }

    public void Unsubscribe(string roomId, Channel<LiveEvent> ch)
    {
        if (!_rooms.TryGetValue(roomId, out var live)) return;
        lock (live.Gate) live.Subscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    private async Task LoopAsync(string roomId, Live live, int generation)
    {
        while (true)
        {
            string text;
            CancellationTokenSource cts;
            lock (live.Gate)
            {
                if (live.Generation != generation || live.Pending.Count == 0 || _disposed)
                {
                    live.Busy = false;
                    live.Current = null;
                    live.Cts = null;
                    live.Tool = null;
                    live.Partial.Clear();
                    // 중지로 끝난 경우 새 세대에서 이미 받은 말이 있으면 이어서 돈다.
                    if (live.Pending.Count > 0 && !_disposed)
                    {
                        live.Busy = true;
                        generation = live.Generation;
                        continue;
                    }
                    Broadcast(live, new LiveEvent("idle") { Busy = false });
                    return;
                }
                text = live.Pending.Dequeue();
                live.Current = text;
                live.Partial.Clear();
                live.Tool = null;
                // 지난 턴의 "앱 밖에서 바뀜" 경고는 지운다 — 이번 턴에도 그렇다면 RunRoomAsync가 다시 낸다 (0a3b1d5155 C7). 화면은 start에서 숨긴다.
                live.Notice = null;
                cts = live.Cts = new CancellationTokenSource();
                Broadcast(live, new LiveEvent("start") { Prompt = text, Queued = live.Pending.Count, Pending = [.. live.Pending] });
            }

            RunResult result;
            try
            {
                result = await RunOneAsync(roomId, live, text, generation, cts.Token);
            }
            catch (Exception ex)
            {
                result = new RunResult(false, false, $"내부 오류: {ex.Message}", null, null, null, 0, 0, null);
            }

            // live.Cts를 잠금 안에서 먼저 비운 뒤 Dispose한다: Stop · Dispose가 해제된 CTS에 Cancel하지 않게 (0a3b1d5155 C4).
            lock (live.Gate)
            {
                live.Cts = null;
                Broadcast(live, new LiveEvent("end") { Ok = result.Ok, Text = result.Error, CostUsd = result.CostUsd });
            }
            cts.Dispose();
        }
    }

    private async Task<RunResult> RunOneAsync(string roomId, Live live, string text, int generation, CancellationToken ct)
    {
        if (store.Get(roomId) is not { } room) return new RunResult(false, false, "없는 방입니다.", null, null, null, 0, 0, null);
        try
        {
            return await RunRoomAsync(room, live, text, generation, ct);
        }
        catch (Exception ex)
        {
            // 예상 밖 실패(IO 등)도 방에 남긴다 — 안 남기면 KnownSize가 낡아 다음 말에 잘못된 "앱 밖에서 바뀜" 경고가 뜬다 (0a3b1d5155 C6).
            return Record(room, new RunResult(false, false, $"내부 오류: {ex.GetType().Name}: {ex.Message}", null, null, null, 0, 0, null));
        }
    }

    private async Task<RunResult> RunRoomAsync(Room room, Live live, string text, int generation, CancellationToken ct)
    {
        var o = options.Value;
        if (o.WorkspacePath(room.Workspace) is not { } workspace || !Directory.Exists(workspace))
            return Record(room, new RunResult(false, false, $"작업 폴더 '{room.Workspace}'가 허용 목록에 없거나 없는 폴더입니다.", null, null, null, 0, 0, null));

        var size = transcripts.Size(room.SessionId);
        var resume = size is not null;
        if (resume && room.KnownSize is { } known && known != size)
        {
            // 다른 곳(Claude 앱 · CLI)에서 같은 세션을 이어 쓴 것으로 보인다 — 갈래가 생겼을 수 있다 (계획서 위험 첫 항목).
            var notice = $"세션 기록이 이 앱 밖에서 바뀌었습니다 ({known:N0} → {size:N0}바이트). 다른 곳에서 이 세션을 이어 쓰면 대화가 갈라질 수 있습니다.";
            lock (live.Gate)
            {
                live.Notice = notice;
                Broadcast(live, new LiveEvent("notice") { Text = notice });
            }
        }

        string? mcpConfig = null;
        if (room.Level is PermissionLevel.Read or PermissionLevel.Edit)
        {
            try
            {
                mcpConfig = McpConfigFile.Write(workspace, o.McpServer, o.DataFolder);
            }
            catch (InvalidOperationException ex)
            {
                return Record(room, new RunResult(false, false, ex.Message, null, null, null, 0, 0, null));
            }
        }

        var spec = new RunSpec(room.Level, room.Model, room.SessionId, resume, mcpConfig, o.McpServer, RoomRules.For(room.Level));
        ProcessSpec process;
        try
        {
            process = ProcessFactory?.Invoke(room, spec, workspace)
                      ?? new ProcessSpec(o.ResolveClaude(), ClaudeArgs.Build(spec, [.. o.ModelList]), workspace);
        }
        catch (ArgumentException ex)
        {
            return Record(room, new RunResult(false, false, ex.Message, null, null, null, 0, 0, null));
        }

        string? mismatch = null;
        using var stopOnMismatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var result = await ClaudeRunner.RunAsync(process, text, ev =>
        {
            if (ev is InitEvent init && init.SessionId is { } sid && !string.Equals(sid, room.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                // CLI가 다른 세션으로 이어 갔다 — 이 방의 세션이 아니므로 멈춘다 (CodePet NativeSessionLink.observe와 같음).
                mismatch = $"CLI가 다른 세션({sid})으로 시작해 멈췄습니다. 이 방의 세션은 {room.SessionId}입니다.";
                stopOnMismatch.Cancel();
                return;
            }
            lock (live.Gate)
            {
                if (live.Generation != generation) return;
                var shown = ev switch
                {
                    DeltaEvent d => AppendPartial(live, d.Text),
                    ToolEvent t => SetTool(live, t.Name),
                    DeniedEvent d => new LiveEvent("denied") { Text = $"{d.Tool}: {d.Reason}" },
                    RetryEvent r => new LiveEvent("retry") { Text = $"API 재시도 {r.Attempt}회 ({r.Error})" },
                    _ => null,
                };
                if (shown is not null) Broadcast(live, shown);
            }
        }, stopOnMismatch.Token);

        if (mismatch is not null) result = result with { Ok = false, Cancelled = false, Error = mismatch };
        return Record(room, result);
    }

    /// <summary>실행 결과를 방에 남긴다. 세션 기록 크기는 이 앱이 쓴 뒤의 값으로 (다음 말 때 밖에서 바뀌었는지 볼 기준).</summary>
    private RunResult Record(Room room, RunResult result)
    {
        var size = transcripts.Size(room.SessionId);
        try
        {
            store.Update(room.Id, r => r with
            {
                LastRunAt = DateTimeOffset.Now,
                LastError = result.Ok ? null : result.Error,
                TotalCostUsd = result.CostUsd ?? r.TotalCostUsd,
                KnownSize = size ?? r.KnownSize,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 방 색인이 깨졌거나 저장하지 못했다 — 결과는 화면에 그대로 보낸다 (RoomStore는 저장 실패 때 메모리도 되돌린다).
        }
        return result;
    }

    private static LiveEvent AppendPartial(Live live, string text)
    {
        live.Partial.Append(text);
        if (live.Partial.Length > MaxPartialChars) live.Partial.Remove(0, live.Partial.Length - MaxPartialChars);
        return new LiveEvent("delta") { Text = text };
    }

    private static LiveEvent SetTool(Live live, string name)
    {
        live.Tool = name;
        return new LiveEvent("tool") { Text = name };
    }

    private static void Broadcast(Live live, LiveEvent ev)
    {
        foreach (var ch in live.Subscribers) ch.Writer.TryWrite(ev);
    }

    /// <summary>
    /// 앱을 끌 때: 모든 실행을 끝내고(프로세스 트리가 남지 않게) SSE 구독을 닫는다 — 열린 방 탭이 정상 종료를 붙잡지 않게 (0a3b1d5155 C8).
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (var live in _rooms.Values)
        {
            lock (live.Gate)
            {
                live.Generation++;
                live.Pending.Clear();
                live.Cts?.Cancel();
                foreach (var ch in live.Subscribers) ch.Writer.TryComplete();
                live.Subscribers.Clear();
            }
        }
    }
}

/// <summary>방 규칙: 첫 말 때 --append-system-prompt로 한 번 넣는다 (압축 전까지 바뀌지 않는다).</summary>
public static class RoomRules
{
    public static string For(PermissionLevel level) =>
        "이 대화는 HB_Chat 채팅 앱의 방이다. 사용자는 브라우저 채팅 화면으로 읽고 말한다. " +
        $"이 방의 권한: {Describe(level)}. " +
        "승인을 물을 곳이 없어서 허용되지 않은 도구 호출은 자동으로 거절된다 — 거절되면 다시 시도하지 말고, 무엇이 필요한지 사용자에게 말한다.";

    public static string LevelName(PermissionLevel level) => level switch
    {
        PermissionLevel.Chat => "대화",
        PermissionLevel.Read => "읽기",
        PermissionLevel.Edit => "편집",
        _ => level.ToString(),
    };

    /// <summary>
    /// 단계 설명 (화면 · 방 규칙). MCP는 사용자 결정대로 읽기 · 편집 모두 허용하되 하는 일을 그대로 적는다
    /// (/self-review 0a3b1d5155 C10 · b19234c862 C3: run_mode는 작업 폴더 파일을 외부 에이전트로 보낼 수 있고, save_result · record_verdicts는 기록 파일을 쓴다).
    /// </summary>
    public static string Describe(PermissionLevel level) => level switch
    {
        PermissionLevel.Chat => "대화만 (도구 · MCP 없음)",
        PermissionLevel.Read =>
            "작업 폴더 읽기 · 검색과 hb-orchestration MCP (run_mode는 작업 폴더 파일을 외부 에이전트로 보낼 수 있고 비용이 든다, " +
            "save_result · record_verdicts는 기록 파일을 쓴다). 작업 폴더 파일 편집 · 명령 실행은 없음",
        PermissionLevel.Edit =>
            "읽기 단계 + 작업 폴더 파일 편집. 이 앱 · MCP 서버 설정, appsettings, CLAUDE.md 같은 지시 파일, 스크립트(.bat · .cmd · .ps1), " +
            ".claude · .mcp.json은 편집이 막힌다. 명령 실행은 없고 작업 폴더 밖 쓰기는 거절",
        _ => level.ToString(),
    };
}
