using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HB.Chat.Desktop;

/// <summary>
/// 앱이 띄운 서버를 넣는 Job Object (KILL_ON_JOB_CLOSE): 앱이 끝나거나 죽으면 OS가 핸들을 닫으면서 서버와 그 아래 claude 트리를 끝낸다.
/// 서버도 켤 때 자기를 자기 Job에 넣는다 (HB.Chat ProcessJob) — 중첩 Job은 Windows 8부터 되고 0번 실측에서 확인했다.
/// 붙기만 한 서버(배치로 띄운 것)는 넣지 않는다.
/// </summary>
public sealed class DesktopJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private IntPtr _handle;

    public DesktopJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
                throw new Win32Exception(error, "SetInformationJobObject");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>프로세스를 Job에 넣는다. 실패면 이유 (그 서버는 앱이 죽어도 남을 수 있다).</summary>
    public string? Assign(Process process) =>
        _handle != IntPtr.Zero && AssignProcessToJobObject(_handle, process.Handle)
            ? null
            : $"AssignProcessToJobObject 실패: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";

    /// <summary>핸들을 닫는다 — 안의 프로세스(앱이 띄운 서버 · claude)가 모두 끝난다.</summary>
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
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
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
