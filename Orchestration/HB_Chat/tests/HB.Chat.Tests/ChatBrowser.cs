using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// 화면 시험용 브라우저: 설치된 Chrome(없으면 Edge)을 화면 없이 · 임시 프로필로 띄우고
/// 앱의 <see cref="CdpClient"/>로 조종한다 — npm · Playwright 없이. 시험 클래스마다 한 번 띄우고(IClassFixture), 시험마다 새 탭(<see cref="OpenAsync"/>).
/// 브라우저가 없는 PC면 <see cref="Available"/>가 false — 시험은 그냥 돌아간다(AgentBrowserTests와 같은 방식, xUnit 2라 통과로 보인다).
/// 띄우기 · 연결이 실패하면 그 시험 클래스는 실패한다(건너뛰지 않음).
/// </summary>
public sealed class ChatBrowser : IAsyncLifetime
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "hbchat-chrome-" + Guid.NewGuid().ToString("N")[..12]);
    private Process? _process;
    private CdpClient? _root;
    private int _port;

    public bool Available => _root is not null;

    public async Task InitializeAsync()
    {
        if (AgentBrowser.FindInstalled() is not { } exe) return;
        try
        {
            await LaunchAsync(exe);
        }
        catch
        {
            // 띄운 브라우저 · 프로필을 남기지 않는다 — xUnit 2가 초기화에서 던진 픽스처를 정리하는지에 기대지 않는다 (지휘자 K1)
            await DisposeAsync();
            throw;
        }
    }

    private async Task LaunchAsync(string exe)
    {
        Directory.CreateDirectory(_profile);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--headless=new", "--remote-debugging-port=0", "--user-data-dir=" + _profile, "--no-first-run",
                     "--no-default-browser-check", "--disable-extensions", "--window-size=1280,800", "about:blank" })
            info.ArgumentList.Add(a);
        _process = Process.Start(info) ?? throw new InvalidOperationException("브라우저를 띄우지 못했습니다.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // DevToolsActivePort: 첫 줄 포트, 둘째 줄 브라우저 연결 경로 (지휘자 브라우저와 같다)
        var portFile = Path.Combine(_profile, "DevToolsActivePort");
        var sw = Stopwatch.StartNew();
        string[] lines = [];
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && !_process.HasExited)
        {
            try { lines = File.Exists(portFile) ? await File.ReadAllLinesAsync(portFile) : []; }
            catch (IOException) { lines = []; }
            if (lines.Length >= 2) break;
            await Task.Delay(50);
        }
        if (lines.Length < 2 || !int.TryParse(lines[0], out _port)) throw new InvalidOperationException("브라우저 디버깅 포트를 읽지 못했습니다.");
        _root = new CdpClient();
        await _root.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}{lines[1]}"), default);
    }

    /// <summary>새 탭에서 주소를 열고 방 칸(#chat)이 그려질 때까지 기다린다. 방이 아닌 화면(첫 화면 등)은 <paramref name="ready"/>로 기다릴 요소를 준다.</summary>
    public async Task<ChatPage> OpenAsync(Uri url, int width = 1280, int height = 800, string ready = "#chat")
    {
        if (_root is null) throw new InvalidOperationException("브라우저가 없습니다.");
        var target = (string)(await _root.CallAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }))["targetId"]!;
        var cdp = new CdpClient();
        var page = new ChatPage(cdp, async () =>
        {
            await cdp.DisposeAsync();
            try { await _root.CallAsync("Target.closeTarget", new JsonObject { ["targetId"] = target }); } catch (Exception ex) when (ex is CdpException or InvalidOperationException or OperationCanceledException) { }
        }, _port, target);
        try
        {
            await cdp.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/devtools/page/{target}"), default);
            await cdp.CallAsync("Runtime.enable");
            await cdp.CallAsync("Log.enable");
            await cdp.CallAsync("Page.enable");
            await page.ResizeAsync(width, height);
            await cdp.CallAsync("Page.navigate", new JsonObject { ["url"] = url.ToString() });
            await page.WaitForAsync($"document.readyState === 'complete' && !!document.querySelector({JsonSerializer.Serialize(ready)})",
                ready == "#chat" ? "방 화면" : ready);
            return page;
        }
        catch
        {
            await page.DisposeAsync(); // 탭 · 연결 · 앱으로 가는 SSE를 남기지 않는다 (c8752a0b82 C4)
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_root is not null) await _root.DisposeAsync();
        _root = null;
        if (_process is not null)
        {
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
            _process = null;
        }
        ChatApp.DeleteFolder(_profile); // 렌더러 · GPU 자식이 프로필을 잠깐 더 잡고 있을 수 있다 — 다시 시도
    }
}

/// <summary>탭 하나: 스크립트 실행 · 조건 기다리기 · 콘솔 오류 모음 (시험 끝에 <see cref="Errors"/>가 비어 있어야 한다).</summary>
public sealed class ChatPage : IAsyncDisposable
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 턴 끝을 기다리는 시간 — 한 턴에 Windows PowerShell 시작 + 가짜 claude + 끝 처리 + 새 말 받기가 들어가고, 시험이 병렬로 돌아 PC가 바쁘면
    /// PowerShell 시작이 수 초 걸릴 수 있다 (c8752a0b82 C5).
    /// </summary>
    public static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(30);
    private readonly CdpClient _cdp;
    private readonly Func<Task> _close;
    private readonly int _port;
    private readonly string _targetId;
    private readonly List<string> _errors = [];

    internal ChatPage(CdpClient cdp, Func<Task> close, int port, string targetId)
    {
        _cdp = cdp;
        _close = close;
        _port = port;
        _targetId = targetId;
        cdp.Event += (method, p) =>
        {
            var error = method switch
            {
                "Runtime.exceptionThrown" => "예외: " + (p?["exceptionDetails"]?["exception"]?["description"] ?? p?["exceptionDetails"]?["text"])?.ToString(),
                "Runtime.consoleAPICalled" when (string?)p?["type"] == "error" => "console.error: " + p?["args"]?.ToJsonString(),
                // 브라우저가 저절로 묻는 favicon.ico는 뺀다 (앱에 아이콘 파일이 없어 404 — 화면 스크립트와 무관, 실측)
                "Log.entryAdded" when (string?)p?["entry"]?["level"] == "error" && !((string?)p?["entry"]?["url"] ?? "").EndsWith("/favicon.ico", StringComparison.Ordinal)
                    => "로그: " + (string?)p?["entry"]?["text"] + " " + (string?)p?["entry"]?["url"],
                _ => null,
            };
            // 대화 상자(alert · confirm)는 화면 없는 브라우저에서 스크립트 실행을 막는다 — 오류로 남기고 닫는다 (c8752a0b82 C3, AgentBrowser와 같은 방식).
            // 이벤트는 읽기 스레드라 명령은 넘겨서 보낸다
            if (method == "Page.javascriptDialogOpening")
            {
                error = $"대화 상자({(string?)p?["type"]}): {(string?)p?["message"]}";
                _ = Task.Run(async () =>
                {
                    try { await cdp.CallAsync("Page.handleJavaScriptDialog", new JsonObject { ["accept"] = false }); }
                    catch (Exception ex) when (ex is CdpException or InvalidOperationException or OperationCanceledException) { }
                });
            }
            if (error is not null) lock (_errors) _errors.Add(error);
        };
    }

    /// <summary>페이지 예외 · console.error · 브라우저 오류 로그(불러오기 실패 · CSP 위반 등).</summary>
    public IReadOnlyList<string> Errors { get { lock (_errors) return [.. _errors]; } }

    /// <summary>오류가 없어야 — 있으면 전부 메시지에 (Assert.Empty는 앞부분만 보인다).</summary>
    public void AssertNoErrors()
    {
        var errors = Errors;
        Assert.True(errors.Count == 0, "페이지 오류:\n" + string.Join("\n", errors));
    }

    /// <summary>식을 실행해 값을 받는다 (Promise면 기다린다). 페이지에서 던지면 시험 실패.</summary>
    public async Task<JsonNode?> EvalAsync(string expression)
    {
        var r = await _cdp.CallAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true });
        if (r["exceptionDetails"] is { } ex)
            throw new InvalidOperationException($"페이지 스크립트 실패: {ex["exception"]?["description"] ?? ex["text"]}\n식: {expression}");
        return r["result"]?["value"];
    }

    public async Task<T> EvalAsync<T>(string expression) =>
        (await EvalAsync(expression)) is { } v ? v.GetValue<T>() : throw new InvalidOperationException("값이 없습니다: " + expression);

    /// <summary>조건 식이 참이 될 때까지 (고정 대기 없이). 시간이 넘으면 방 칸 HTML 앞부분을 실패 메시지에 붙인다.</summary>
    public async Task WaitForAsync(string condition, string what, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await EvalAsync($"!!({condition})") is { } v && v.GetValue<bool>()) return;
            if (sw.Elapsed > (timeout ?? DefaultWait))
            {
                var html = (await EvalAsync("(document.querySelector('#pane') || document.body)?.outerHTML.slice(0, 3000)"))?.ToString();
                throw new TimeoutException($"{what}: {(timeout ?? DefaultWait).TotalSeconds}초 안에 되지 않았습니다 ({condition}).\n오류: {string.Join(" | ", Errors)}\n화면: {html}");
            }
            await Task.Delay(50);
        }
    }

    /// <summary>지금 화면을 PNG로 저장 (실패 조사용).</summary>
    public async Task ScreenshotAsync(string path)
    {
        var shot = await _cdp.CallAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" });
        await File.WriteAllBytesAsync(path, Convert.FromBase64String((string)shot["data"]!));
    }

    /// <summary>틀을 못 찾았을 때의 틀 목록 (실패 메시지용).</summary>
    public string? LastFrameTree { get; private set; }

    /// <summary>창 크기 바꾸기 (미니 창 · 좁은 창 흉내).</summary>
    public Task ResizeAsync(int width, int height) =>
        _cdp.CallAsync("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = false });

    /// <summary>
    /// 격리 틀(srcdoc, 다른 출처라 페이지 스크립트로는 안을 못 봄) 안에서 식을 실행한다. Chrome은 sandbox 틀을 따로 떨어진 대상(OOPIF)으로 띄워
    /// 페이지의 틀 목록에 없다(실측) — 디버깅 목록에서 이 탭에 딸린 iframe 대상을 찾아 거기에 붙는다. 틀은 index번째 (srcdoc만 셈). 아직 없으면 null.
    /// 틀 안 예외는 이 탭의 <see cref="Errors"/>에 잡히지 않는다 (다른 대상).
    /// </summary>
    public async Task<JsonNode?> EvalInFrameAsync(string expression, int index = 0)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{_port}/json/list"))!.AsArray();
        var frames = targets.Where(t => (string?)t?["type"] == "iframe" && (string?)t?["parentId"] == _targetId
            && ((string?)t?["url"])?.StartsWith("about:srcdoc", StringComparison.Ordinal) == true).ToList();
        if (index >= frames.Count) { LastFrameTree = targets.ToJsonString(); return null; }
        await using var frame = new CdpClient();
        await frame.ConnectAsync(new Uri((string)frames[index]!["webSocketDebuggerUrl"]!), default);
        var r = await frame.CallAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true });
        if (r["exceptionDetails"] is { } ex) throw new InvalidOperationException($"틀 안 스크립트 실패: {ex["exception"]?["description"] ?? ex["text"]}\n식: {expression}");
        return r["result"]?["value"];
    }

    /// <summary>화면에서 보내기 (입력칸 + 보내기 — 화면 스크립트의 Send 길 그대로).</summary>
    public Task SendAsync(string text) =>
        EvalAsync($"(() => {{ const t = document.querySelector('#text'); t.value = {System.Text.Json.JsonSerializer.Serialize(text)}; " +
                  "t.dispatchEvent(new Event('input', { bubbles: true })); document.querySelector('#send-form').requestSubmit(); })()");

    public async ValueTask DisposeAsync() => await _close();
}
