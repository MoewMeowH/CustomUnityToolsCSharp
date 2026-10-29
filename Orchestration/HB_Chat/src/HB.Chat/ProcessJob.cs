using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HB.Chat;

/// <summary>
/// 앱이 비정상 종료돼도 claude 프로세스 트리가 남지 않게 한다 (/self-review 0a3b1d5155 C9, 2차에서 하기로 했던 것).
/// 켤 때 이 앱 프로세스를 "핸들이 닫히면 모두 끝냄"(KILL_ON_JOB_CLOSE) Job Object에 넣는다. 이 앱이 띄우는 claude와
/// 그 아래 MCP 서버 · 훅 프로세스는 Job을 물려받는다. 그래서 앱이 어떻게 끝나든(작업 관리자 · 충돌) OS가 Job 핸들을 닫으면서 전부 끝낸다.
/// claude를 띄운 뒤에 하나씩 넣으면, 넣기 전에 claude가 띄운 자식이 빠질 수 있다. 앱 자신을 넣으면 그 틈이 없다.
/// 실패하면(이미 다른 Job 안에서 중첩이 막힌 경우 등) 예외 대신 이유를 돌려준다 — 정상 종료 · 중지의 트리 종료는 그대로 돈다.
/// </summary>
public static class ProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    /// <summary>앱이 끝날 때까지 닫지 않는다 (닫히는 순간 Job 안의 프로세스가 모두 끝난다).</summary>
    private static IntPtr _job;

    /// <summary>이 프로세스를 Job에 넣는다. 성공이면 null, 실패면 이유. Windows가 아니면 아무것도 하지 않고 null.</summary>
    public static string? AttachCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (_job != IntPtr.Zero) return null;

        var job = NewKillOnCloseJob(out var error);
        if (job == IntPtr.Zero) return error;
        if (!AssignProcessToJobObject(job, System.Diagnostics.Process.GetCurrentProcess().Handle))
            return Fail(job, "AssignProcessToJobObject");
        _job = job;
        return null;
    }

    /// <summary>"핸들이 닫히면 모두 끝냄" Job을 만든다. 실패면 IntPtr.Zero와 이유.</summary>
    private static IntPtr NewKillOnCloseJob(out string? error)
    {
        error = null;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            error = $"CreateJobObject 실패: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return IntPtr.Zero;
        }

        var info = new JobObjectExtendedLimit { Basic = new JobObjectBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<JobObjectExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                error = Fail(job, "SetInformationJobObject");
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    /// <summary>
    /// 실행 하나(Codex 구성원 한 턴)의 자손까지 끝내는 Job — 앱 Job 안의 중첩 Job이다 (sol 리뷰 ②-P2).
    /// Kill(entireProcessTree)는 부모가 이미 끝났으면 자손을 찾지 못한다. 띄운 직후 넣고, <see cref="Terminate"/>나 Dispose(KILL_ON_JOB_CLOSE)로
    /// 남은 프로세스를 모두 끝낸다. 넣지 못하면(Windows가 아님 · 실패) null — 그때는 트리 종료만 남는다.
    /// </summary>
    public sealed class Turn : IDisposable
    {
        private readonly Lock _gate = new();
        private IntPtr _handle;

        private Turn(IntPtr handle) => _handle = handle;

        public static Turn? Attach(System.Diagnostics.Process process)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var job = NewKillOnCloseJob(out _);
            if (job == IntPtr.Zero) return null;
            try
            {
                if (AssignProcessToJobObject(job, process.Handle)) return new Turn(job);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } // 이미 끝난 프로세스
            CloseHandle(job);
            return null;
        }

        /// <summary>Job 안의 프로세스를 모두 끝낸다 (부모가 먼저 끝났어도). 닫힌 뒤면 아무것도 하지 않는다.</summary>
        public void Terminate()
        {
            lock (_gate)
            {
                if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero) return;
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    /// <summary>이 프로세스가 이 앱의 Job 안에 있는지 (테스트 · 확인용).</summary>
    public static bool IsAttached() => Contains(System.Diagnostics.Process.GetCurrentProcess());

    /// <summary>그 프로세스가 이 앱의 Job 안에 있는지 — 띄운 자식이 Job을 물려받았는지 테스트가 본다.</summary>
    internal static bool Contains(System.Diagnostics.Process process) =>
        _job != IntPtr.Zero && IsProcessInJob(process.Handle, _job, out var inJob) && inJob;

    private static string Fail(IntPtr job, string call)
    {
        var message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        CloseHandle(job);
        return $"{call} 실패: {message}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
