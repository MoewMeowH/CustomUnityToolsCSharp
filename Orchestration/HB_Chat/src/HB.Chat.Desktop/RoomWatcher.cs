namespace HB.Chat.Desktop;

/// <summary>
/// 방 상태 듣기: 앱이 채팅 서버의 앱 전체 SSE `/events`를 직접 듣고, 신호마다(잠깐 모아서) `/api/rooms`를 받아
/// UI 스레드로 넘긴다. 페이지 → 앱 메시지를 쓰지 않는다 (페이지 · 위젯이 앱에 닿는 다리가 없게).
/// 받기는 일꾼 하나가 차례로 한다 — 겹친 받기의 옛 응답이 새 응답 뒤에 적용되지 않게 (/self-review 0f0b6007a4 C1).
/// 끊기면(서버를 다시 켬 등) 알리고 잠시 뒤 다시 붙는다. 붙을 때마다 서버가 hello를 보내 목록을 다시 받는다.
/// </summary>
public sealed class RoomWatcher(Uri chatUrl, Action<IReadOnlyList<RoomState>> onRooms, Action<bool> onConnection, SynchronizationContext ui)
    : IDisposable
{
    public static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(300), Reconnect = TimeSpan.FromSeconds(3);

    private readonly HttpClient _stream = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private volatile bool _dirty;

    public void Start()
    {
        var ct = _cts.Token;
        _ = Task.Run(() => ListenAsync(ct));
        _ = Task.Run(() => FetchLoopAsync(ct));
    }

    /// <summary>SSE 줄 중 신호(data:)인지. 빈 줄 · 주석(:) · 다른 필드는 아니다.</summary>
    public static bool IsSignal(string line) => line.StartsWith("data:", StringComparison.Ordinal);

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var res = await _stream.GetAsync(new Uri(chatUrl, "/events"), HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                ui.Post(_ => onConnection(true), null);
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (IsSignal(line)) Signal();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // 서버가 끝났거나 다시 켜는 중 — 알리고 잠시 뒤 다시 붙는다
            }
            if (!ct.IsCancellationRequested) ui.Post(_ => onConnection(false), null);
            try
            {
                await Task.Delay(Reconnect, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Signal()
    {
        _dirty = true;
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>받기 일꾼: 신호가 오면 잠깐 모은 뒤, 그 사이 또 온 신호가 없어질 때까지 차례로 받는다.</summary>
    private async Task FetchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(ct);
                await Task.Delay(Gather, ct);
                while (_dirty)
                {
                    _dirty = false;
                    try
                    {
                        var json = await _http.GetStringAsync(new Uri(chatUrl, "/api/rooms"), ct);
                        if (RoomStates.Parse(json) is { } rooms && !ct.IsCancellationRequested) ui.Post(_ => onRooms(rooms), null);
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        // 이번 받기는 건너뛴다 — 다음 신호 때 다시
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 끝
        }
    }

    /// <summary>멈춘다. 토큰 소스는 해제하지 않는다 — 아직 도는 작업이 취소된 토큰을 볼 수 있게.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
        _http.Dispose();
    }
}
