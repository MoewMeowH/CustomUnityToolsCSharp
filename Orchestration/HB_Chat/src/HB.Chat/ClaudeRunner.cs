using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>띄울 프로세스. Args는 <see cref="ClaudeArgs"/>가 만든 값 (테스트는 가짜 claude 스크립트 앞 인자를 붙인다).</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory);

/// <summary>
/// 실행 하나의 결과. Ok는 result 이벤트가 성공으로 왔을 때만. SessionId는 init에서 받은 실제 ID.
/// UnknownLines는 화면에 쓰지 않은 줄 수 (형식이 바뀌었는지 볼 때).
/// </summary>
public sealed record RunResult(
    bool Ok, bool Cancelled, string? Error, string? Text, double? CostUsd, string? SessionId, int Denials, int OtherLines, string? Stderr)
{
    /// <summary>마지막 assistant 요청의 컨텍스트 크기 (토큰). 중지돼도 그때까지 본 값.</summary>
    public long? ContextTokens { get; init; }

    /// <summary>모델의 컨텍스트 창 (result modelUsage). result가 없으면 null.</summary>
    public int? ContextWindow { get; init; }
}

/// <summary>
/// CLI가 묻는 도구 승인 하나 (control_request can_use_tool, 3차 0번 실측). InputJson은 도구 입력 원문, RequestJson은 요청 원문
/// (description · permission_suggestions · decision_reason 포함).
/// </summary>
public sealed record ToolPermission(string RequestId, string Tool, string InputJson, string RequestJson);

/// <summary>
/// claude -p를 숨긴 프로세스로 한 번 띄우고, stdout stream-json을 줄마다 읽어 넘긴다 (CodePet chat-agent-runner와 같은 구조).
/// 입력은 SDK식 stream-json (3차 a): initialize 제어 요청 → 사용자 말 한 줄. 실행 중 승인 요청(control_request can_use_tool)은
/// onPermission에 묻고 그 답을 control_response로 stdin에 쓴다. result가 오면 stdin을 닫아 프로세스가 끝나게 한다.
/// 셸을 거치지 않고 인자를 하나씩 넘긴다. 취소하면 프로세스 트리를 끝낸다 — 진행 중 턴은 세션에 결과 없이 남고 다음 말이 이어 간다 (문서).
/// 기본 시간 제한은 없다 (긴 작업). 예외를 던지지 않고 실패는 Error로 돌려준다 (onEvent가 던진 예외는 삼킨다).
/// </summary>
public static class ClaudeRunner
{
    /// <summary>stdout 한 줄 상한 (글자). 넘으면 실행을 끝낸다.</summary>
    public const int MaxLineChars = 8 * 1024 * 1024;

    /// <summary>stderr는 끝부분만 이만큼 남긴다.</summary>
    public const int MaxStderrChars = 16 * 1024;

    /// <summary>승인을 물을 곳이 없을 때(onPermission 없음)의 거절 문구 — 그 전 --permission-prompts none과 같은 뜻.</summary>
    public const string NoApprover = "승인할 사람이 없어 자동으로 거절했습니다. 다시 시도하지 말고, 무엇이 필요한지 사용자에게 말하세요.";

    public static Task<RunResult> RunAsync(ProcessSpec spec, string prompt, Action<StreamEvent> onEvent, CancellationToken ct) =>
        RunAsync(spec, prompt, onEvent, null, ct);

    /// <summary>
    /// onPermission은 승인 요청마다 불리고 답(<see cref="PermissionDecision"/>의 JSON)을 돌려준다. 사용자를 기다리는 동안 출력 읽기는 계속된다.
    /// 실행이 끝난 뒤 늦게 온 답은 쓰지 않고 버린다 (프로세스가 없음).
    /// </summary>
    public static async Task<RunResult> RunAsync(ProcessSpec spec, string prompt, Action<StreamEvent> onEvent,
        Func<ToolPermission, CancellationToken, Task<string>>? onPermission, CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        // 이 앱을 Claude Code 세션 안에서 띄웠을 때 물려받는 표시. 띄운 claude가 자기를 하위 세션으로 보지 않게 뺀다.
        psi.Environment.Remove("CLAUDECODE");

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return Fail("프로세스를 시작하지 못했습니다.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Fail($"실행 실패: {ex.Message}");
        }

        var stderrTask = ReadTail(proc.StandardError, MaxStderrChars);
        string? error = null;
        // stdin 쓰기는 하나씩 (승인 답은 다른 스레드에서 온다). 닫은 뒤의 쓰기 · 끝난 프로세스에 쓰기는 조용히 버린다.
        // 이 잠금은 Dispose하지 않는다 — 실행이 끝난 뒤에 늦게 끝나는 승인 답이 들어올 수 있다.
        var input = new StdinWriter(proc.StandardInput);
        // 답을 기다리는 승인 요청마다 취소 토큰 — CLI가 control_cancel_request로 거두면 그 요청만 끊는다 (/self-review 7d839f4aa5 C1)
        var asks = new ConcurrentDictionary<string, CancellationTokenSource>();
        using (ct.Register(() => Kill(proc)))
        {
            // 프로세스가 먼저 끝났으면 쓰기가 버려지고 아래에서 결과 없음으로 보인다.
            await input.WriteAsync(InitializeLine);
            await input.WriteAsync(UserLine(prompt));

            ResultEvent? result = null;
            string? sessionId = null;
            long? context = null;
            string? contextModel = null;
            var other = 0;
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > MaxLineChars)
                {
                    error = "출력 한 줄이 너무 길어 실행을 끝냈습니다.";
                    Kill(proc);
                    break;
                }
                var ev = StreamParser.Parse(line);
                switch (ev)
                {
                    case null: continue;
                    case ControlRequestEvent control:
                        // 답을 기다리는 동안에도 출력은 계속 읽는다 (여러 승인이 겹칠 수 있다). 화면에는 넘기지 않는다.
                        var ask = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        if (!asks.TryAdd(control.RequestId, ask))
                        {
                            ask.Dispose(); // 같은 ID가 아직 답을 기다린다 — 두 번째 요청은 버린다 (먼저 온 요청의 답이 간다)
                            continue;
                        }
                        _ = AnswerAsync(input, control, onPermission, asks, ask, ct);
                        continue;
                    case ControlCancelEvent cancel:
                        if (asks.TryGetValue(cancel.RequestId, out var withdrawn))
                        {
                            try { withdrawn.Cancel(); } catch (ObjectDisposedException) { } // 그사이 답이 끝났다
                        }
                        continue;
                    case OtherEvent: other++; break;
                    case InitEvent init: sessionId = init.SessionId; break;
                    case ResultEvent r:
                        result = r;
                        await input.CloseAsync(); // 턴 끝 — 다음 입력이 없으므로 CLI가 끝난다 (0번 실측)
                        break;
                    case UsageEvent u: (context, contextModel) = (u.ContextTokens, u.Model ?? contextModel); break;
                }
                try { onEvent(ev); } catch { /* 화면 쪽 실패가 실행을 멈추지 않게 */ }
            }
            // result 없이 끝났어도(중지 · 끼어들기 · 결과 없음 · 한 줄 초과) 닫는다 — 늦은 승인 답은 닫힌 쓰기로 버려진다 (7d839f4aa5 C2)
            await input.CloseAsync();

            await proc.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrTask;
            if (ct.IsCancellationRequested)
                return new RunResult(false, true, "중지됨", null, result?.CostUsd, sessionId, result?.Denials ?? 0, other, stderr)
                    { ContextTokens = context, ContextWindow = result?.WindowFor(contextModel) };
            if (error is null && result is null)
                error = $"결과 없이 끝났습니다 (종료 코드 {proc.ExitCode}).";
            if (error is null && !result!.Ok)
                error = $"실행 오류 ({result.Subtype}): {result.Text}";
            return new RunResult(error is null, false, error, result?.Text, result?.CostUsd, sessionId, result?.Denials ?? 0, other,
                string.IsNullOrWhiteSpace(stderr) ? null : stderr) { ContextTokens = context, ContextWindow = result?.WindowFor(contextModel) };
        }
    }

    private static RunResult Fail(string error) => new(false, false, error, null, null, null, 0, 0, null);

    /// <summary>SDK가 처음 보내는 제어 요청 (0번 실측에서 쓴 모양 그대로 — 훅은 넘기지 않는다).</summary>
    internal const string InitializeLine = """{"type":"control_request","request_id":"hbchat_init","request":{"subtype":"initialize","hooks":null}}""";

    internal static string UserLine(string prompt) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = prompt },
        parent_tool_use_id = (string?)null,
        session_id = "",
    });

    /// <summary>
    /// 제어 요청 하나에 답한다. can_use_tool이면 onPermission(없으면 거절), 그 밖의 종류는 오류 답 (initialize에 훅을 넘기지 않아 올 일이 없다).
    /// onPermission이 던지거나 답 JSON이 깨졌으면 거절로 답한다 — 답이 없으면 CLI가 멈춰 있게 된다 (지휘자 검토 C2).
    /// CLI가 그 요청을 거뒀으면(control_cancel_request — 요청 토큰만 끊기고 실행 토큰은 그대로) 답을 쓰지 않는다.
    /// </summary>
    private static async Task AnswerAsync(StdinWriter input, ControlRequestEvent control,
        Func<ToolPermission, CancellationToken, Task<string>>? onPermission,
        ConcurrentDictionary<string, CancellationTokenSource> asks, CancellationTokenSource ask, CancellationToken run)
    {
        try
        {
            string response;
            if (control.Subtype == "can_use_tool")
            {
                try
                {
                    var permission = ParsePermission(control);
                    var decision = permission is null ? PermissionDecision.Deny("승인 요청 모양을 읽지 못해 거절했습니다.")
                        : onPermission is null ? PermissionDecision.Deny(NoApprover)
                        : await onPermission(permission, ask.Token);
                    response = SuccessLine(control.RequestId, decision);
                }
                catch (Exception ex)
                {
                    response = SuccessLine(control.RequestId, PermissionDecision.Deny($"승인 처리 중 오류로 거절했습니다: {ex.GetType().Name}"));
                }
                if (ask.IsCancellationRequested && !run.IsCancellationRequested) return; // CLI가 거둔 요청
            }
            else
            {
                response = JsonSerializer.Serialize(new
                {
                    type = "control_response",
                    response = new { subtype = "error", request_id = control.RequestId, error = $"HB_Chat은 {control.Subtype} 요청을 받지 않습니다." },
                });
            }
            await input.WriteAsync(response);
        }
        finally
        {
            asks.TryRemove(new KeyValuePair<string, CancellationTokenSource>(control.RequestId, ask));
            ask.Dispose();
        }
    }

    private static string SuccessLine(string requestId, string decision) => JsonSerializer.Serialize(new
    {
        type = "control_response",
        response = new { subtype = "success", request_id = requestId, response = JsonNode.Parse(decision) },
    });

    private static ToolPermission? ParsePermission(ControlRequestEvent control)
    {
        try
        {
            using var doc = JsonDocument.Parse(control.RequestJson);
            var e = doc.RootElement;
            if (!e.TryGetProperty("tool_name", out var tool) || tool.ValueKind != JsonValueKind.String || tool.GetString() is not { Length: > 0 } name)
                return null;
            var inputJson = e.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i.GetRawText() : "{}";
            return new ToolPermission(control.RequestId, name, inputJson, control.RequestJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>stdin 줄 쓰기 (한 번에 하나). 닫은 뒤 · 프로세스가 끝난 뒤의 쓰기는 버린다.</summary>
    private sealed class StdinWriter(StreamWriter writer)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _closed;

        public async Task WriteAsync(string line)
        {
            await _gate.WaitAsync();
            try
            {
                if (_closed) return;
                await writer.WriteAsync(line + "\n"); // \r\n 대신 \n — 줄 끝 모양을 실측과 같게
                await writer.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { _closed = true; }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CloseAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_closed) return;
                _closed = true;
                writer.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                _gate.Release();
            }
        }
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static async Task<string> ReadTail(StreamReader reader, int limit)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            sb.Append(buf, 0, n);
            if (sb.Length > limit * 2) sb.Remove(0, sb.Length - limit);
        }
        return sb.Length > limit ? sb.ToString(sb.Length - limit, limit) : sb.ToString();
    }
}
