using System.Diagnostics;
using HB.Orchestration.Agents.Providers;

namespace HB.Orchestration.Tests;

public sealed class ClaudeCliProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-claude-process-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("normal")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task Descendant_is_reaped_after_parent_exit_or_cancellation(string ending)
    {
        if (!OperatingSystem.IsWindows()) return;
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var pidFile = Path.Combine(_dir, "child.pid");
        // ping은 부모와 출력 파이프를 공유한다. 정상 경로는 부모가 먼저 끝나게 한다.
        var script = "$psi = New-Object Diagnostics.ProcessStartInfo; $psi.FileName = 'ping.exe'; " +
            "$psi.Arguments = '-n 40 127.0.0.1'; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; " +
            "$p = [Diagnostics.Process]::Start($psi); " +
            "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "', [string]$p.Id); " +
            (ending == "normal" ? "Write-Output 'parent-done'" : "Start-Sleep 40");
        var inv = new ClaudeCliInvocation(powershell, ["-NoProfile", "-Command", script], _dir, new Dictionary<string, string?>(), "");
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var run = ClaudeCliProcess.RunAsync(inv, TimeSpan.FromSeconds(ending == "timeout" ? 5 : 20), cts.Token);
        Process? child = null;
        try
        {
            while (!File.Exists(pidFile) && !run.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);
            Assert.True(File.Exists(pidFile), "CLI 자손이 시작되지 않음");
            var pid = int.Parse(File.ReadAllText(pidFile));
            try { child = Process.GetProcessById(pid); } catch (ArgumentException) { }
            if (ending == "cancel") cts.Cancel();
            if (ending == "normal") Assert.Contains("parent-done", (await run).Stdout);
            else if (ending == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => run);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            if (child is not null)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.HasExited);
            }
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"걸린 시간 {sw.Elapsed}");
            Directory.Delete(_dir, recursive: true);
            Directory.CreateDirectory(_dir); // 종료 후 작업 폴더를 지울 수 있다.
        }
        finally
        {
            cts.Cancel();
            try { await run; } catch (Exception) { }
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                child.Dispose();
            }
        }
    }
}
