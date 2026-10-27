using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HB.Chat.Desktop;

internal static class Program
{
    /// <summary>
    /// 하나만 실행 (계획서 2-3): 데이터 폴더마다 이름 있는 Mutex. 두 번째 실행은 이름 있는 이벤트로 첫 창을 앞으로 부르고 끝난다.
    /// 데이터 폴더를 이름에 넣어 시험용 실행(--data=)이 평소 앱과 부딪치지 않게 한다.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // WebView2는 이 환경 변수의 브라우저 인자(원격 디버깅 등)를 그대로 받는다 — 앱이 정한 인자만 쓰게 지운다.
        // 띄우는 서버도 물려받지 않는다. 시험용 원격 디버깅은 --debug-port=로만 (지휘자 검토 R4).
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);
        ApplicationConfiguration.Initialize();
        var (settings, warning) = DesktopSettings.Load(args);
        var key = "HB_Chat_Desktop_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.DataFolder.ToLowerInvariant())))[..12];
        using var mutex = new Mutex(true, @"Local\" + key, out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "_show");
        if (!first)
        {
            // 첫 창이 앞으로 나올 수 있게 전경 권한을 넘긴다 — 안 그러면 작업 표시줄만 깜빡인다 (26d478170f C7)
            AllowSetForegroundWindow(AsfwAny);
            show.Set();
            return;
        }
        Application.Run(new MainForm(settings, warning, show));
        GC.KeepAlive(mutex);
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
