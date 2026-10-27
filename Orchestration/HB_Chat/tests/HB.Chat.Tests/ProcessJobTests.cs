using System.Diagnostics;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// Job Object: 앱(여기서는 테스트 호스트)을 넣으면 띄운 자식도 같은 Job에 들어간다.
/// "앱이 죽으면 자식이 끝난다"는 테스트 호스트를 죽일 수 없어 실제 확인(작업 로그)으로 본다.
/// </summary>
public sealed class ProcessJobTests
{
    [Fact]
    public void ChildProcessInheritsTheJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(ProcessJob.AttachCurrentProcess());
        Assert.True(ProcessJob.IsAttached());
        Assert.Null(ProcessJob.AttachCurrentProcess()); // 두 번 불러도 같은 Job

        using var child = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.True(ProcessJob.Contains(child));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }
}
