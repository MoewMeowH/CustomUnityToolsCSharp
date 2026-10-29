using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// 실행 하나의 자손 프로세스까지 끝내는 Job Object (HB_Chat ProcessJob.Turn에서 옮김 — 계획서 2026-10-29_Codex-외부-리뷰어).
/// Codex는 셸 명령을 자식으로 띄운다. Kill(entireProcessTree)는 부모가 이미 끝났으면 자손을 찾지 못하고,
/// 남은 자손이 출력 파이프를 물고 있으면 읽기가 시간 상한까지 막힌다. 띄운 직후 넣고 <see cref="Terminate"/>나 Dispose(KILL_ON_JOB_CLOSE)로 모두 끝낸다.
/// 넣지 못하면(Windows가 아님 · 실패) null — 그때는 트리 종료만 남는다.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly Lock _gate = new();
    private IntPtr _handle;

    private ProcessJob(IntPtr handle) => _handle = handle;

    public static ProcessJob? Attach(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;

        var info = new JobObjectExtendedLimit { Basic = new JobObjectBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<JobObjectExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size) && AssignProcessToJobObject(job, process.Handle))
                return new ProcessJob(job);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } // 이미 끝난 프로세스
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
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
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
