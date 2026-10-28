using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace HB.Chat;

/// <summary>연결 칸 "확인" 결과: 연결됐는지 · 도구 이름과 설명 · 시각 · 오류.</summary>
public sealed record ProbeResult(bool Ok, IReadOnlyList<(string Name, string? Description)> Tools, DateTimeOffset At, string? Error, long Millis)
{
    /// <summary>
    /// http 서버가 401 · 403으로 답함 — OAuth로 로그인하는 서버(Notion 등)다. claude는 대화형 세션에서 받아 둔 토큰으로 붙지만 앱은 그 토큰이 없어
    /// 직접 확인할 수 없다 (실패가 아님). 화면은 실행 상태(init)를 보라고 안내한다.
    /// </summary>
    public bool NeedsLogin { get; init; }
}

/// <summary>
/// 곁칸 "연결" 탭의 "확인" 단추 (계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결): 앱이 MCP 클라이언트로 그 서버를 직접 띄워 tools/list만 받고 닫는다
/// (0단계 실측: unity mcp 연결 149ms · 도구 186개 62ms, 설명 포함 — init에는 이름만). 띄우는 명령은 그 방 실행이 승인 없이 띄우는 것과 같다.
/// 결과는 방 · 서버 이름마다 기억한다 (앱을 다시 켜면 비어 있음). 한 방 · 서버에 한 번에 하나만.
/// </summary>
public sealed partial class McpProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, ProbeResult> _results = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    public ProbeResult? Last(string roomId, string server) => _results.TryGetValue(Key(roomId, server), out var r) ? r : null;

    public bool IsRunning(string roomId, string server) => _running.ContainsKey(Key(roomId, server));

    public void Forget(string roomId)
    {
        foreach (var key in _results.Keys.Where(k => k.StartsWith(roomId + "|", StringComparison.Ordinal))) _results.TryRemove(key, out _);
    }

    /// <summary>
    /// .mcp.json 모양 항목(command · args · env 또는 url · headers)으로 띄워 도구 목록을 받는다. 이미 도는 중이면 null.
    /// workingDir: stdio 서버의 작업 폴더 (CLI처럼 작업 폴더에서 띄운다).
    /// </summary>
    public async Task<ProbeResult?> ProbeAsync(string roomId, string server, JsonObject entry, string workingDir, CancellationToken ct)
    {
        var key = Key(roomId, server);
        if (!_running.TryAdd(key, 0)) return null;
        var started = DateTimeOffset.Now;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ProbeResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var client = await McpClient.CreateAsync(Transport(server, entry, workingDir), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            result = new ProbeResult(true, [.. tools.Select(t => (t.Name, t.Description)).OrderBy(t => t.Name, StringComparer.Ordinal)], started, null,
                watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new ProbeResult(false, [], started, $"{Timeout.TotalSeconds:0}초 안에 답이 없었습니다", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsUnauthorized(ex))
        {
            result = new ProbeResult(false, [], started, "OAuth 로그인 서버라 앱이 직접 확인할 수 없습니다 — 실제 연결 상태는 마지막 실행 상태를 보세요",
                watch.ElapsedMilliseconds) { NeedsLogin = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ProbeResult(false, [], started, Cut($"{ex.GetType().Name}: {ex.Message}", 300), watch.ElapsedMilliseconds);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
        _results[key] = result;
        return result;
    }

    /// <summary>앱 MCP(hb-chat) 도구: 이 앱 안의 도구 형식에서 이름 · 설명을 읽는다 (띄울 것이 없다).</summary>
    public static IReadOnlyList<(string Name, string? Description)> AppTools() =>
    [
        .. new[] { typeof(TerminalTools), typeof(BrowserTools) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Attr: m.GetCustomAttribute<McpServerToolAttribute>(), Desc: m.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Where(x => x.Attr?.Name is not null)
            .Select(x => (x.Attr!.Name!, x.Desc))
            .OrderBy(x => x.Item1, StringComparer.Ordinal),
    ];

    private static IClientTransport Transport(string server, JsonObject entry, string workingDir)
    {
        if (entry["url"]?.GetValue<string>() is { } url)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry["headers"] is JsonObject h)
                foreach (var (name, value) in h)
                    if (value is not null) headers[name] = Expand(value.ToString());
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server,
                Endpoint = new Uri(Expand(url)),
                AdditionalHeaders = headers,
            });
        }
        if (entry["command"]?.GetValue<string>() is not { Length: > 0 } command)
            throw new InvalidOperationException("command · url이 없는 항목입니다.");
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (entry["env"] is JsonObject e)
            foreach (var (name, value) in e)
                env[name] = value is null ? null : Expand(value.ToString());
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server,
            Command = Expand(command),
            Arguments = entry["args"] is JsonArray a ? [.. a.Select(x => Expand(x?.ToString() ?? ""))] : [],
            EnvironmentVariables = env,
            WorkingDirectory = workingDir,
        });
    }

    /// <summary>.mcp.json 자리표시 <c>${VAR}</c> · <c>${VAR:-기본}</c>를 이 앱 환경 변수로 푼다 (CLI와 같은 모양).</summary>
    public static string Expand(string text) =>
        Placeholder().Replace(text, m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) is { Length: > 0 } v ? v
            : m.Groups[2].Success ? m.Groups[2].Value : "");

    /// <summary>예외(안쪽 예외 포함)에 401 · 403 http 응답이 있는지.</summary>
    private static bool IsUnauthorized(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }) return true;
        return false;
    }

    private static string Key(string roomId, string server) => roomId + "|" + server;

    private static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex Placeholder();
}
