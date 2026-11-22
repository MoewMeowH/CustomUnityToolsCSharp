using System.Collections;
using System.Diagnostics;
using System.Text;

namespace HB.Chat;

/// <summary>터미널 하나의 프로세스 — 내 셸(ConPTY, <see cref="PseudoConsole"/>) · 지휘자 프로세스(파이프, <see cref="PipedProcess"/>).</summary>
public interface ITerminalProcess : IDisposable
{
    /// <summary>출력을 끝까지 읽어 조각마다 넘긴다 (한 번에 하나씩).</summary>
    Task PumpAsync(Action<byte[]> onChunk);

    Task<int> WaitForExitAsync();

    /// <summary>입력 (내 셸만). 받지 않으면 false.</summary>
    bool Write(ReadOnlySpan<byte> data);

    void Resize(short cols, short rows);

    /// <summary>프로세스 트리를 끝낸다.</summary>
    void Kill();

    /// <summary>프로세스가 끝난 뒤: 남은 자손을 끝내고 읽기가 끝나게 한다 (ConPTY는 콘솔을 닫아야 읽기가 끝난다).</summary>
    void Finish();
}

/// <summary>터미널 프로세스 환경: 앱 환경에서 이 앱의 주소 설정만 뺀다 — 터미널로 띄운 다른 ASP.NET 서버가 같은 포트에 붙지 않게.</summary>
public static class TerminalEnvironment
{
    public static readonly IReadOnlyList<string> Removed =
        ["ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_URLS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS"];

    public static SortedDictionary<string, string> Current(IReadOnlyDictionary<string, string>? extra = null)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v && !Removed.Contains(k, StringComparer.OrdinalIgnoreCase)) env[k] = v;
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) env[k] = v;
        return env;
    }

    /// <summary>CreateProcess 환경 블록 (UTF-16, "k=v\0…\0", 이름 차례).</summary>
    public static string Block(SortedDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in env) sb.Append(k).Append('=').Append(v).Append('\0');
        return sb.Append('\0').ToString();
    }
}

/// <summary>
/// 지휘자 프로세스 (결정 3 "나눠서 둘 다"): ConPTY가 아니라 파이프로 띄운다 — ConPTY 출력은 화면 다시 그리기(커서 이동 · 지우기)라
/// 줄로 쌓을 수 없어 terminal_read가 앞 출력을 잃었다 (사용자 방 지휘자 보고). stdout · stderr를 섞어 읽고, 입력은 바로 닫는다(EOF).
/// 끝내기는 프로세스별 Job째(<see cref="ProcessJob.Turn"/> — 중간 부모가 먼저 끝난 손자도) + 프로세스 트리째, 앱이 끝나면 앱 Job이 모두 끝낸다.
/// 파이프면 파이썬이 출력을 모아 두므로 PYTHONUNBUFFERED · PYTHONIOENCODING을 넣는다.
/// </summary>
public sealed class PipedProcess : ITerminalProcess
{
    private readonly Process _process;
    private readonly ProcessJob.Turn? _job;
    // 끝내기 · 해제는 한 잠금으로 — Process는 스레드 안전하지 않다 (중지의 Kill과 읽기 끝의 Dispose가 겹칠 수 있음)
    private readonly Lock _gate = new();
    private bool _disposed;

    private PipedProcess(Process process, ProcessJob.Turn? job)
    {
        _process = process;
        _job = job;
    }

    /// <param name="extraEnv">더 넣을 환경 변수 (앱 기본 위에 덮어씀).</param>
    public static PipedProcess Start(string command, string workingDirectory, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        // ComSpec이 없으면 시스템 폴더의 cmd — 작업 폴더 · PATH의 같은 이름이 대신 뜨지 않게 (OpenShell과 같음)
        var shell = Environment.GetEnvironmentVariable("ComSpec") is { } c && !string.IsNullOrWhiteSpace(c)
            ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var info = new ProcessStartInfo(shell)
        {
            // /s: 바깥 따옴표 한 쌍만 벗긴다 — 명령 안의 따옴표는 그대로
            Arguments = $"/d /s /c \"{command}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment.Clear();
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PYTHONUNBUFFERED"] = "1", ["PYTHONIOENCODING"] = "utf-8" };
        foreach (var (k, v) in extraEnv ?? new Dictionary<string, string>()) extra[k] = v;
        foreach (var (k, v) in TerminalEnvironment.Current(extra))
            info.Environment[k] = v;
        var process = Process.Start(info) ?? throw new InvalidOperationException("프로세스를 띄우지 못했습니다.");
        // 띄운 직후 Job에 — 그 뒤 셸이 띄우는 자손은 Job을 물려받는다 (넣지 못하면 null — 트리 종료만 남음)
        var job = ProcessJob.Turn.Attach(process);
        try { process.StandardInput.Close(); } catch (IOException) { }
        return new PipedProcess(process, job);
    }

    /// <summary>stdout · stderr 바이트를 섞어 넘긴다 — 글로 읽을 때는 스트림마다 디코더를 두는 <see cref="PumpAsync(Action{int, byte[]})"/>를 쓴다.</summary>
    public Task PumpAsync(Action<byte[]> onChunk) => PumpAsync((_, chunk) => onChunk(chunk));

    /// <summary>스트림을 가려 넘긴다 (0 = stdout, 1 = stderr) — 글자 디코더를 스트림마다 둘 때 ('!' 명령 · 지휘자 터미널).</summary>
    public Task PumpAsync(Action<int, byte[]> onChunk)
    {
        var gate = new Lock();
        async Task Read(Stream stream, int index)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (await stream.ReadAsync(buffer) is > 0 and var n)
                    lock (gate) onChunk(index, buffer[..n]);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        return Task.WhenAll(Read(_process.StandardOutput.BaseStream, 0), Read(_process.StandardError.BaseStream, 1));
    }

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync();
        return _process.ExitCode;
    }

    public bool Write(ReadOnlySpan<byte> data) => false; // 읽기 전용 — 지휘자에게도 입력 도구는 없다

    public void Resize(short cols, short rows) { }

    public void Kill()
    {
        lock (_gate)
        {
            if (!_disposed) KillLocked();
        }
    }

    public void Finish() => Kill(); // 셸이 끝나도 자손(분리된 자식)이 파이프를 잡고 있을 수 있다

    /// <summary>
    /// 트리째 먼저(Job에 넣기 전에 생긴 자손은 살아 있는 부모로만 찾는다 — /self-review feb02b1439 C1), 그다음 Job째(부모가 끝난 손자까지).
    /// 트리 일부를 못 끝내면 AggregateException이 온다.
    /// </summary>
    private void KillLocked()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
        _job?.Terminate();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            KillLocked();
            // Process.Close()는 한 번이라도 접근한 표준 스트림을 닫지 않는다 — 읽기 핸들이 GC 때까지 남지 않게 직접 닫는다
            foreach (var close in new Action[] { () => _process.StandardOutput.Dispose(), () => _process.StandardError.Dispose() })
            {
                try { close(); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            }
            _process.Dispose();
            _job?.Dispose();
        }
    }
}
