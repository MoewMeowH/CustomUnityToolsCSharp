using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>
/// Chrome DevTools 프로토콜(CDP) 연결 하나 — .NET 기본 WebSocket, 외부 패키지 없음 (계획서 2026-10-29_HB_Chat-지휘자-브라우저, 0단계 실측).
/// 명령은 id로 답을 짝짓고, 이벤트(id 없는 메시지)는 <see cref="Event"/>로 넘긴다. 연결이 끊기면 기다리던 명령은 모두 실패한다.
/// </summary>
public sealed class CdpClient : IAsyncDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _id;
    private Task? _reader;

    /// <summary>이벤트 (method, params). 읽기 스레드에서 차례대로 부른다 — 오래 걸리는 일은 넘겨서 할 것.</summary>
    public event Action<string, JsonNode?>? Event;

    /// <summary>연결이 끊김.</summary>
    public event Action? Closed;

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri url, CancellationToken ct)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await _ws.ConnectAsync(url, ct);
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>명령을 보내고 결과(result)를 받는다. CDP 오류면 <see cref="CdpException"/>.</summary>
    public async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _id);
        var done = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = done;
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString());
        try
        {
            await _send.WaitAsync(ct);
            try { await _ws.SendAsync(body, WebSocketMessageType.Text, true, ct); }
            finally { _send.Release(); }
            var reply = await done.Task.WaitAsync(CallTimeout, ct);
            if (reply["error"] is { } error) throw new CdpException(method, (string?)error["message"] ?? error.ToJsonString());
            return reply["result"] ?? new JsonObject();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)); }
                catch (System.Text.Json.JsonException) { msg = null; }
                message.SetLength(0);
                if (msg is null) continue;
                if (msg["id"] is { } id && _pending.TryRemove((int)id, out var waiting)) waiting.TrySetResult(msg);
                else if ((string?)msg["method"] is { } method)
                {
                    try { Event?.Invoke(method, msg["params"]); } catch { /* 처리기 쪽 실패가 읽기를 멈추지 않게 */ }
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            // 어떤 이유로 끝나든 기다리던 명령을 끝내고 끊김을 알린다 — 그 밖의 예외로 끝나면 IsOpen인 채 남았다 (/self-review 822a55c0cf C8)
            foreach (var (_, waiting) in _pending) waiting.TrySetException(new CdpException("연결", "브라우저 연결이 끊겼습니다."));
            if (_ws.State == WebSocketState.Open)
                try { _ws.Abort(); } catch (ObjectDisposedException) { }
            try { Closed?.Invoke(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is WebSocketException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        if (_reader is not null) await Task.WhenAny(_reader, Task.Delay(2000));
        _ws.Dispose();
        _stop.Dispose();
    }
}

public sealed class CdpException(string method, string message) : Exception($"{method}: {message}");
