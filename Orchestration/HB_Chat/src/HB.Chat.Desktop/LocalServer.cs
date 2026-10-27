using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HB.Chat.Desktop;

public enum ServerKind { Chat, Dashboard }

public enum ServerState
{
    /// <summary>그 포트에 아무것도 없다 (연결 거부).</summary>
    Missing,
    /// <summary>무언가 듣고 있지만 시간 안에 답하지 않았다 — 켜는 중이거나 바쁜 우리 서버일 수 있어 새로 띄우지 않는다.</summary>
    Busy,
    /// <summary>HB Chat(또는 대시보드)이 답한다.</summary>
    Ours,
    /// <summary>`/api/rooms`가 없는 옛 HB Chat (2-3 전 빌드) — 다시 켜야 한다.</summary>
    OldChat,
    /// <summary>다른 무언가가 그 포트를 쓴다.</summary>
    Other,
}

/// <summary>
/// 서버 하나 (계획서 2-3): 이미 떠 있으면 붙기만, 없으면 빌드된 exe를 창 없이 띄워 앱 Job에 넣는다.
/// 확인: 채팅은 `GET /api/rooms`가 JSON 배열, 대시보드는 `GET /`의 제목이 HB Orchestration (서버 쪽에 새 끝점을 두지 않음).
/// 띄운 서버는 답할 때까지(또는 끝날 때까지) 기다린다 — 늦게 떠도 탭이 오류에 멈추지 않게 (/self-review 26d478170f C2).
/// 출력은 로그 파일에 (켤 때마다 새로).
/// </summary>
public sealed class LocalServer(ServerKind kind, Uri baseUrl, string exePath, string contentRoot, IReadOnlyList<string> extraArgs, string logPath,
    Func<int, bool>? isListening = null)
{
    /// <summary>
    /// 그 포트를 듣는 것이 있는지 OS 목록으로 본다. Windows는 아무도 듣지 않는 localhost 포트에 붙어도 거부가 몇 초 늦게 와서,
    /// HTTP 시간 초과만으로는 "없음"과 "느린 서버"를 구별하지 못했다 (리뷰 반영 뒤 실제 확인).
    /// </summary>
    public static bool IsListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private readonly Func<int, bool> _isListening = isListening ?? IsListening;

    public ServerKind Kind => kind;
    public Uri BaseUrl => baseUrl;

    /// <summary>이 앱이 띄운 서버인지 (끝낼 때 같이 끝남).</summary>
    public bool Owned { get; private set; }

    /// <summary>이 시간이 지나도 답이 없으면 "아직 켜는 중"을 알린다 (기다리기는 계속).</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(40);

    /// <summary>켤 때 이미 무언가 듣는데 답이 늦으면 몇 번 더 물어본다 (켠 직후 /api/rooms가 세션 기록을 처음 읽느라 느릴 수 있음 — 26d478170f C1).</summary>
    public const int BusyRetries = 4;

    public static bool IsChatRooms(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsDashboard(string body) => body.Contains("<title>HB Orchestration", StringComparison.Ordinal);

    public static bool IsChatPage(string body) => body.Contains("<title>HB Chat", StringComparison.Ordinal);

    public async Task<ServerState> ProbeAsync(HttpClient http, CancellationToken ct)
    {
        if (!_isListening(baseUrl.Port)) return ServerState.Missing;
        try
        {
            using var res = await http.GetAsync(new Uri(baseUrl, kind == ServerKind.Chat ? "/api/rooms" : "/"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode && (kind == ServerKind.Chat ? IsChatRooms(body) : IsDashboard(body))) return ServerState.Ours;
            if (kind == ServerKind.Chat && res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 2-3 전 빌드의 HB Chat이면 /api/rooms만 없다 — "다른 프로그램"이 아니라 다시 켜라고 알린다 (26d478170f C8)
                using var page = await http.GetAsync(baseUrl, ct);
                if (page.IsSuccessStatusCode && IsChatPage(await page.Content.ReadAsStringAsync(ct))) return ServerState.OldChat;
            }
            return ServerState.Other;
        }
        catch (HttpRequestException)
        {
            // 듣는 것이 있는데 HTTP로 답하지 못함 — 방금 끝났으면 다음 확인에서 Missing
            return _isListening(baseUrl.Port) ? ServerState.Other : ServerState.Missing;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServerState.Busy; // 듣고는 있지만 답이 늦다 — 새로 띄우지 않는다
        }
    }

    /// <summary>서버를 쓸 수 있게 한다. 성공이면 null, 아니면 화면에 보일 이유. 느릴 때 progress로 알린다.</summary>
    public async Task<string?> EnsureAsync(DesktopJob job, HttpClient http, IProgress<string>? progress, CancellationToken ct)
    {
        var state = await ProbeAsync(http, ct);
        for (var i = 0; state == ServerState.Busy && i < BusyRetries; i++)
        {
            await Task.Delay(1500, ct);
            state = await ProbeAsync(http, ct);
        }
        switch (state)
        {
            case ServerState.Ours: return null; // 배치로 띄운 것 — 붙기만
            case ServerState.Busy: return $"{baseUrl}에서 무언가 듣고 있지만 답하지 않습니다 — 그 프로그램을 확인하세요.";
            case ServerState.OldChat: return $"{baseUrl}에 이전 판 HB Chat이 떠 있습니다 — 그 창(HB_Chat.bat)을 닫고 이 앱을 다시 켜세요.";
            case ServerState.Other: return $"{baseUrl}을 다른 프로그램이 쓰고 있습니다 ({Name}가 아님).";
        }
        if (!File.Exists(exePath))
            return $"{Name} 서버 파일이 없습니다: {exePath} — HB_Chat_Desktop.bat으로 빌드하거나 desktop.json에 경로를 적으세요.";

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        // 창 없는 자식도 숨은 콘솔(OEM 코드 페이지)로 쓴다 — 그 코드 페이지로 읽어야 한국어 로그가 깨지지 않는다 (26d478170f C14)
        var console = ConsoleEncoding();
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = contentRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = console,
            StandardErrorEncoding = console,
        };
        foreach (var a in Args) psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start가 null");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await log.DisposeAsync();
            return $"{Name} 서버를 띄우지 못했습니다: {ex.Message}";
        }
        // Job에 못 넣으면 앱이 끝나도 남으므로 띄우지 않은 것으로 한다 (d459e3af9b C5)
        if (job.Assign(process) is { } jobError)
        {
            Write(log, "[desktop] " + jobError);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            await log.DisposeAsync();
            return $"{Name} 서버를 앱 수명에 묶지 못해 띄우지 않았습니다: {jobError}";
        }
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Write(log, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Write(log, e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var slowAt = DateTime.UtcNow + SlowAfter;
        var warned = false;
        while (true)
        {
            if (process.HasExited)
            {
                var code = process.ExitCode;
                process.Dispose();
                await log.DisposeAsync();
                return $"{Name} 서버가 켜지다 끝났습니다 (종료 코드 {code}) — 로그: {logPath}";
            }
            if (await ProbeAsync(http, ct) == ServerState.Ours)
            {
                // 그 사이 다른 우리 서버가 답했을 수 있다 — 띄운 것이 살아 있을 때만 소유 (26d478170f C1)
                await Task.Delay(300, ct);
                Owned = !process.HasExited;
                return null;
            }
            if (!warned && DateTime.UtcNow > slowAt)
            {
                warned = true;
                progress?.Report($"{Name} 서버가 늦게 뜹니다 — 계속 기다립니다 (로그: {logPath})");
            }
            await Task.Delay(400, ct);
        }
    }

    /// <summary>띄울 때 인자: content root(wwwroot · appsettings) · 주소 + 서버별 인자.</summary>
    public IReadOnlyList<string> Args => ["--contentRoot", contentRoot, "--urls=" + baseUrl.GetLeftPart(UriPartial.Authority), .. extraArgs];

    private string Name => kind == ServerKind.Chat ? "HB Chat" : "대시보드";

    private static Encoding ConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static void Write(StreamWriter log, string line)
    {
        lock (log)
        {
            try { log.WriteLine(line); } catch (ObjectDisposedException) { } catch (IOException) { }
        }
    }
}
