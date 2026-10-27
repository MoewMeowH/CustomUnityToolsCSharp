using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HB.Chat;

/// <summary>띄울 프로세스. Args는 <see cref="ClaudeArgs"/>가 만든 값 (테스트는 가짜 claude 스크립트 앞 인자를 붙인다).</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args, string WorkingDirectory);

/// <summary>
/// 실행 하나의 결과. Ok는 result 이벤트가 성공으로 왔을 때만. SessionId는 init에서 받은 실제 ID.
/// UnknownLines는 화면에 쓰지 않은 줄 수 (형식이 바뀌었는지 볼 때).
/// </summary>
public sealed record RunResult(
    bool Ok, bool Cancelled, string? Error, string? Text, double? CostUsd, string? SessionId, int Denials, int OtherLines, string? Stderr);

/// <summary>
/// claude -p를 숨긴 프로세스로 한 번 띄워 프롬프트를 stdin으로 넣고, stdout stream-json을 줄마다 읽어 넘긴다 (CodePet chat-agent-runner와 같은 구조).
/// 셸을 거치지 않고 인자를 하나씩 넘긴다. 취소하면 프로세스 트리를 끝낸다 — 진행 중 턴은 세션에 결과 없이 남고 다음 말이 이어 간다 (문서).
/// 기본 시간 제한은 없다 (긴 작업). 예외를 던지지 않고 실패는 Error로 돌려준다 (onEvent가 던진 예외는 삼킨다).
/// </summary>
public static class ClaudeRunner
{
    /// <summary>stdout 한 줄 상한 (글자). 넘으면 실행을 끝낸다.</summary>
    public const int MaxLineChars = 8 * 1024 * 1024;

    /// <summary>stderr는 끝부분만 이만큼 남긴다.</summary>
    public const int MaxStderrChars = 16 * 1024;

    public static async Task<RunResult> RunAsync(ProcessSpec spec, string prompt, Action<StreamEvent> onEvent, CancellationToken ct)
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
        using (ct.Register(() => Kill(proc)))
        {
            try
            {
                await proc.StandardInput.WriteAsync(prompt);
                proc.StandardInput.Close();
            }
            catch (IOException) { } // 프로세스가 먼저 끝났다 — 아래에서 결과 없음으로 보인다.

            ResultEvent? result = null;
            string? sessionId = null;
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
                    case OtherEvent: other++; break;
                    case InitEvent init: sessionId = init.SessionId; break;
                    case ResultEvent r: result = r; break;
                }
                try { onEvent(ev); } catch { /* 화면 쪽 실패가 실행을 멈추지 않게 */ }
            }

            await proc.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrTask;
            if (ct.IsCancellationRequested)
                return new RunResult(false, true, "중지됨", null, result?.CostUsd, sessionId, result?.Denials ?? 0, other, stderr);
            if (error is null && result is null)
                error = $"결과 없이 끝났습니다 (종료 코드 {proc.ExitCode}).";
            if (error is null && !result!.Ok)
                error = $"실행 오류 ({result.Subtype}): {result.Text}";
            return new RunResult(error is null, false, error, result?.Text, result?.CostUsd, sessionId, result?.Denials ?? 0, other,
                string.IsNullOrWhiteSpace(stderr) ? null : stderr);
        }
    }

    private static RunResult Fail(string error) => new(false, false, error, null, null, null, 0, 0, null);

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
