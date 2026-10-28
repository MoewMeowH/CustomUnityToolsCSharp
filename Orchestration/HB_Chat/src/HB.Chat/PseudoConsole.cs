using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HB.Chat;

/// <summary>
/// ConPTY(의사 콘솔)로 띄운 내 셸 하나 (계획서 2026-10-29_HB_Chat-터미널-곁칸, 결정 "입력도 (ConPTY)" · 결정 3 — 지휘자 프로세스는 <see cref="PipedProcess"/>).
/// 출력은 VT 바이트 그대로(색 포함), 입력은 키 바이트 그대로(Ctrl+C = 0x03), 크기는 곁칸 탭을 따른다.
/// 프로세스마다 KILL_ON_JOB_CLOSE Job Object를 두어 끝내기는 트리째(서버가 띄운 자식까지). 일시 정지로 띄워 Job에 넣은 뒤 풀어
/// 넣기 전에 자식이 빠지는 틈을 없앤다 (<see cref="ProcessJob"/>과 같은 이유). 앱 전체 Job 안이라 앱이 끝나면 함께 끝난다.
/// 프로세스가 끝나도 ConPTY 출력 파이프는 ClosePseudoConsole 전에는 끝나지 않는다 — <see cref="CloseConsole"/>을 불러 읽기를 끝낸다.
/// </summary>
public sealed class PseudoConsole : ITerminalProcess
{
    private readonly IntPtr _console;
    private readonly IntPtr _process;
    private readonly IntPtr _job;
    // 콘솔 · Job 핸들을 쓰는 호출(크기 · 끝내기 · 닫기 · 해제)은 한 잠금으로 — 닫힌 핸들에 부르면 다른 터미널의 핸들일 수 있다 (/self-review 57b59ef134 C5 · a3a9dd85c1 C4)
    private readonly Lock _gate = new();
    private bool _consoleClosed;
    private bool _disposed;

    public Stream Output { get; }
    public Stream Input { get; }
    public int ProcessId { get; }

    private PseudoConsole(IntPtr console, IntPtr process, IntPtr job, int pid, SafeFileHandle output, SafeFileHandle input)
    {
        _console = console;
        _process = process;
        _job = job;
        ProcessId = pid;
        Output = new FileStream(output, FileAccess.Read, 1);
        Input = new FileStream(input, FileAccess.Write, 1);
    }

    /// <summary>명령 줄을 작업 폴더에서 띄운다. 환경은 <see cref="TerminalEnvironment"/>. 실패하면 Win32Exception.</summary>
    public static PseudoConsole Start(string commandLine, string workingDirectory, short cols, short rows)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY는 Windows에서만 쓴다.");
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero, job = IntPtr.Zero;
        var environment = Marshal.StringToHGlobalUni(TerminalEnvironment.Block(TerminalEnvironment.Current()));
        var info = default(ProcessInformation);
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0)) throw Win32("CreatePipe");
            if (!CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw Win32("CreatePipe");
            var hr = CreatePseudoConsole(new Coord(cols, rows), inputRead, outputWrite, 0, out console);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole");
            // 콘솔 쪽 끝은 ConPTY가 복제해 들고 있다 — 우리 것은 닫아야 프로세스가 끝났을 때 읽기가 끝난다
            CloseHandle(inputRead); inputRead = IntPtr.Zero;
            CloseHandle(outputWrite); outputWrite = IntPtr.Zero;

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw Win32("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Win32("UpdateProcThreadAttribute");

            job = CreateKillOnCloseJob();
            // "Ctrl+C 무시"는 띄울 때 부모에서 물려받는다 — 이 앱을 백그라운드 셸 · 새 프로세스 그룹으로 띄웠으면 켜져 있어,
            // 곁칸의 Ctrl+C(0x03)가 서버를 멈추지 못했다 (시험 · 5291 실제 확인). 되돌려 둔다 (이 앱에는 ASP.NET의 종료 처리기가 있다).
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // 이 앱의 표준 핸들이 파이프로 돌려져 있으면(시험 · 서비스로 띄움) 자식이 그것을 물려받아 의사 콘솔을 안 쓴다 —
            // 빈 핸들로 USESTDHANDLES를 켜야 콘솔로 붙는다 (ConPTY 알려진 동작, 시험에서 출력이 비고 choice가 바로 끝남)
            startup.StartupInfo.Flags = StartfUseStdHandles;
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateSuspended | CreateUnicodeEnvironment,
                    environment, workingDirectory, ref startup, out info))
                throw Win32("CreateProcess");
            if (!AssignProcessToJobObject(job, info.Process))
            {
                var error = Win32("AssignProcessToJobObject");
                TerminateProcess(info.Process, 1);
                throw error;
            }
            ResumeThread(info.Thread);
            CloseHandle(info.Thread);
            var result = new PseudoConsole(console, info.Process, job, info.ProcessId,
                new SafeFileHandle(outputRead, ownsHandle: true), new SafeFileHandle(inputWrite, ownsHandle: true));
            console = job = outputRead = inputWrite = IntPtr.Zero;
            info = default;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            foreach (var h in new[] { inputRead, inputWrite, outputRead, outputWrite, job, info.Process, info.Thread })
                if (h != IntPtr.Zero) CloseHandle(h);
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
        }
    }

    /// <summary>프로세스가 끝날 때까지 기다려 종료 코드를 준다.</summary>
    public Task<int> WaitForExitAsync()
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_process, ownsHandle: false) };
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) =>
            done.TrySetResult(GetExitCodeProcess(_process, out var code) ? (int)code : -1), null, Timeout.Infinite, executeOnlyOnce: true);
        // 등록을 돌려받은 뒤에 푼다 — 이미 끝난 프로세스면 콜백이 등록 전에 돌 수 있다 (지휘자 검토 K4)
        _ = done.Task.ContinueWith(_ =>
        {
            registration.Unregister(null);
            handle.Dispose();
        }, TaskScheduler.Default);
        return done.Task;
    }

    public async Task PumpAsync(Action<byte[]> onChunk)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (await Output.ReadAsync(buffer) is > 0 and var n) onChunk(buffer[..n]);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        lock (_gate) if (_disposed) return false;
        try
        {
            Input.Write(data);
            Input.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Resize(short cols, short rows)
    {
        lock (_gate) if (!_consoleClosed) ResizePseudoConsole(_console, new Coord(cols, rows));
    }

    /// <summary>프로세스 트리를 끝낸다 (Job째).</summary>
    public void Kill()
    {
        lock (_gate) if (!_disposed) TerminateJobObject(_job, 1);
    }

    /// <summary>
    /// 셸이 끝난 뒤: 남은 자손을 Job째 먼저 끝내고 콘솔을 닫는다 — 콘솔에 붙은 자손이 있으면 닫기가 기다릴 수 있다 (/self-review 57b59ef134 C8).
    /// 닫아야 남은 출력이 흘러나오고 읽기가 끝난다 (ConPTY).
    /// </summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_disposed) return;
            TerminateJobObject(_job, 1);
            CloseConsoleLocked();
        }
    }

    private void CloseConsoleLocked()
    {
        if (_consoleClosed) return;
        _consoleClosed = true;
        ClosePseudoConsole(_console);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            TerminateJobObject(_job, 1);
            CloseConsoleLocked();
            CloseHandle(_job);
        }
        Input.Dispose();
        Output.Dispose();
        CloseHandle(_process);
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw Win32("CreateJobObject");
        var info = new JobObjectExtendedLimit { Basic = new JobObjectBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<JobObjectExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                var error = Win32("SetInformationJobObject");
                CloseHandle(job);
                throw error;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    private static Win32Exception Win32(string call) => new(Marshal.GetLastWin32Error(), call + " 실패");

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
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

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
