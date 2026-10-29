using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HB.Chat.Desktop;

internal static class Program
{
    /// <summary>`--exit`가 떠 있는 앱이 끝나기를 기다리는 시간 (도는 턴이 있으면 그 창이 묻는다).</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 하나만 실행: 데이터 폴더마다 이름 있는 Mutex. 두 번째 실행은 이름 있는 이벤트로 첫 창을 앞으로 부르고 끝난다.
    /// `--exit`로 실행하면 떠 있는 앱을 끝내고(트레이 메뉴 끝내기와 같음) 끝날 때까지 기다린다 — 끝났거나 떠 있지 않으면 0,
    /// 사용자가 끄지 않기로 했거나 시간 안에 끝나지 않으면 1 (/self-review 0f0b6007a4 C5).
    /// 데이터 폴더를 이름에 넣어 시험용 실행(--data=)이 평소 앱과 부딪치지 않게 한다.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // WebView2는 이 환경 변수의 브라우저 인자(원격 디버깅 등)를 그대로 받는다 — 앱이 정한 인자만 쓰게 지운다.
        // 띄우는 서버도 물려받지 않는다. 시험용 원격 디버깅은 --debug-port=로만 (지휘자 검토 R4).
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);
        ApplicationConfiguration.Initialize();
        var (settings, warning) = DesktopSettings.Load(args);
        var key = "HB_Chat_Desktop_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.DataFolder.ToLowerInvariant())))[..12];
        var mutexName = @"Local\" + key;
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "_show");
        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "_exit");
        if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase))) return RequestExit(mutexName, exitSignal);

        using var mutex = new Mutex(true, mutexName, out var first);
        if (!first)
        {
            // 첫 창이 앞으로 나올 수 있게 전경 권한을 넘긴다 — 안 그러면 작업 표시줄만 깜빡인다 (26d478170f C7)
            AllowSetForegroundWindow(AsfwAny);
            show.Set();
            return 0;
        }

        // 데이터 폴더 쓰기가 Claude 데스크톱 앱(MSIX) 패키지 캐시로 옮겨지면 켜지 않는다 (4차 A) — 띄우는 서버도 기본 폴더를 쓰므로
        // 방 목록 · 기록이 밖에서 띄운 앱과 두 벌로 갈린다. --data=로 준 폴더도 같이 본다(옮겨지지 않으면 null — dc27a724bf C1 · C3).
        // 첫 실행만 — 두 번째 실행은 첫 창을 부르기만 하고 쓰지 않는다 (C2).
        if (HB.Chat.RedirectedFolder.Probe(settings.DataFolder) is { } redirected)
        {
            MessageBox.Show(HB.Chat.RedirectedFolder.Message(settings.DataFolder, redirected), "HB Chat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        Application.Run(new MainForm(settings, warning, show, exitSignal));
        GC.KeepAlive(mutex);
        return 0;
    }

    private static int RequestExit(string mutexName, EventWaitHandle exitSignal)
    {
        if (!Mutex.TryOpenExisting(mutexName, out var running)) return 0; // 떠 있는 앱이 없다
        using (running)
        {
            AllowSetForegroundWindow(AsfwAny); // 숨긴 창이 확인 창을 띄울 때 앞으로 나오게
            exitSignal.Set();
            try
            {
                if (!running.WaitOne(ExitWait)) return 1;
            }
            catch (AbandonedMutexException)
            {
                return 0; // 앱이 놓지 않고 끝났다 — 끝난 것은 같다
            }
            running.ReleaseMutex();
            return 0;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
