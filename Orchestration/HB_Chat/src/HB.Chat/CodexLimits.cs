using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>
/// Codex 구독 한도를 계정에서 바로 읽는다: <c>codex app-server</c>(stdio JSON-RPC)의 <c>account/rateLimits/read</c> 한 번.
/// 모델을 부르지 않아 사용량을 쓰지 않고, ChatGPT 로그인일 때만 값이 온다(API 키 로그인이면 오류 응답 → null).
/// 프로토콜은 codex 0.165.0에서 experimental — 모양이 바뀌면 null이 되고 부르는 쪽이 세션 기록 값으로 넘어간다.
/// </summary>
public static class CodexLimits
{
    /// <summary>띄우기부터 응답까지 상한 (0.165.0 실측 1초 안).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// app-server 인자. plugins를 끄지 않으면 시작할 때 플러그인 마켓 확인으로 <c>git ls-remote</c>를 띄운다 (0.165.0 실측) —
    /// 한도 조회에는 필요 없다. 사용자 config의 MCP 서버는 스레드를 만들 때만 띄워 여기서는 돌지 않는다 (실측).
    /// </summary>
    public static readonly IReadOnlyList<string> Args = ["app-server", "--disable", "plugins", "--disable", "remote_plugin"];

    /// <summary>한 번 조회. 실행 파일이 없거나 · 시간 초과 · 오류 응답 · 모양이 다르면 null (예외를 내지 않는다).</summary>
    public static async Task<LimitSnapshot?> ReadAsync(string? exe, string? codexHome, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in Args) psi.ArgumentList.Add(a);
        psi.Environment["CODEX_MANAGED_BY_NPM"] = "1"; // CodexRunner와 같게 — 업데이트 안내 판단용
        if (!string.IsNullOrWhiteSpace(codexHome)) psi.Environment["CODEX_HOME"] = codexHome;

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { return null; }

        // 자손까지 묶는다 — 끝(Dispose)에 남은 프로세스를 모두 끝낸다 (CodexRunner와 같은 방식)
        using var job = ProcessJob.Turn.Attach(proc);
        void KillAll()
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            job?.Terminate();
        }
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var kill = linked.Token.Register(KillAll);
        _ = DrainAsync(proc.StandardError); // 막히지 않게 비우기만

        JsonNode? response = null;
        try
        {
            await SendAsync(proc, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "hb_chat", ["version"] = "1" } },
            });
            if (await WaitResponseAsync(proc.StandardOutput, 1, linked.Token) is { } init && init["result"] is not null)
            {
                await SendAsync(proc, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" });
                await SendAsync(proc, new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "account/rateLimits/read",
                    ["params"] = new JsonObject { ["excludeResetCreditDetails"] = true },
                });
                response = await WaitResponseAsync(proc.StandardOutput, 2, linked.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }

        // stdin을 닫으면 스스로 끝난다 (실측) — 끝나지 않으면 트리째
        try { proc.StandardInput.Close(); } catch (IOException) { }
        try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { KillAll(); }
        return Parse(response, DateTimeOffset.Now);
    }

    private static async Task SendAsync(Process proc, JsonObject message)
    {
        await proc.StandardInput.WriteAsync(message.ToJsonString() + "\n");
        await proc.StandardInput.FlushAsync();
    }

    /// <summary>id가 맞는 응답 줄까지 읽는다 — 사이에 알림(account/updated 등)이 섞여 온다. 출력이 끝나면 null.</summary>
    private static async Task<JsonObject?> WaitResponseAsync(StreamReader output, int id, CancellationToken ct)
    {
        while (await output.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonObject? node;
            try { node = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (node is null || node.ContainsKey("method")) continue; // 알림 · 서버가 보낸 요청 (요청 id는 우리 것과 따로 매김 — C5)
            if (node["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id) return node;
        }
        return null;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            var buf = new char[4096];
            while (await reader.ReadAsync(buf) > 0) { }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>
    /// <c>account/rateLimits/read</c> 응답 → 한도. <c>rateLimitsByLimitId.codex</c>를 먼저, 없으면 <c>rateLimits</c>(limitId가 codex거나 없을 때만).
    /// 오류 응답 · 창이 없으면 null.
    /// </summary>
    public static LimitSnapshot? Parse(JsonNode? response, DateTimeOffset at)
    {
        if (response?["result"] is not JsonObject result) return null;
        var snap = result["rateLimitsByLimitId"] is JsonObject byId && byId["codex"] is JsonObject codex ? codex
            : result["rateLimits"] is JsonObject single && UsageLimits.Text(single["limitId"]) is null or "codex" ? single
            : null;
        if (snap is null) return null;
        var windows = new List<LimitWindow>();
        foreach (var key in new[] { "primary", "secondary" })
            if (snap[key] is JsonObject w && UsageLimits.Num(w["usedPercent"]) is { } used)
                windows.Add(new LimitWindow(UsageLimits.MinutesName((int)Math.Clamp(UsageLimits.Num(w["windowDurationMins"]) ?? 0, 0, int.MaxValue), key), used,
                    UsageLimits.Num(w["resetsAt"]) is double r and > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)r) : null,
                    null));
        if (windows.Count == 0) return null;
        var credits = UsageLimits.Num((result["rateLimitResetCredits"] as JsonObject)?["availableCount"]);
        return new LimitSnapshot("codex", at, windows)
        {
            Plan = UsageLimits.Text(snap["planType"]),
            ResetCredits = credits is >= 0 and <= int.MaxValue ? (int)credits : null,
            From = LimitSnapshot.FromAccount,
        };
    }
}
