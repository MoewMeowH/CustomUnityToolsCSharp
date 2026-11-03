using System.Runtime.InteropServices;

namespace HB.Chat.Desktop;

/// <summary>
/// 다른 프로그램의 포그라운드 창이 바뀔 때 알린다 (SetWinEventHook EVENT_SYSTEM_FOREGROUND, 이 프로세스 창은 빼고).
/// 펫이 늘 위 자리를 잃는 때(사진 앱을 열고 닫음 — 2026-11-03 사용자 확인)에 다시 세우려고. 만든 UI 스레드의 메시지 루프에서 부른다.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003, WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly WinEventProc _proc; // 훅이 사는 동안 GC가 거두지 않게 붙잡는다
    private readonly Action _changed;
    private IntPtr _hook;

    public ForegroundWatcher(Action changed)
    {
        _changed = changed;
        _proc = (_, _, _, _, _, _, _) => _changed();
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
