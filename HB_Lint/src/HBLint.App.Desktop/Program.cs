using System.Diagnostics;
using System.Net;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace HBLint.App.Desktop;

/// <summary>
/// hblint-app-desktop — 인자는 hblint-app과 같다 (--rules · --out · --project · --port, 브라우저는 열지 않음).
/// 서버를 이 프로세스에서 띄우고 창을 연다. 창이 닫히면 서버도 멈춘다.
/// </summary>
internal static class Program
{
    private const string Title = "HB_Lint 규칙 편집";

    [STAThread]
    private static int Main(string[] args)
    {
        // WebView2는 이 환경 변수의 브라우저 인자(원격 디버깅 등)를 그대로 받는다 — 앱이 정한 인자만 쓰게 지운다 (HB.Chat.Desktop과 같음)
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);
        ApplicationConfiguration.Initialize();

        var options = AppOptions.Parse(args);
        if (options == null)
        {
            MessageBox.Show(AppOptions.Usage, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        WebApplication app;
        string address;
        try
        {
            app = AppHost.Build(options, builder => builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port)));
            app.StartAsync().GetAwaiter().GetResult();
            address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        }
        catch (IOException e)
        {
            MessageBox.Show("서버를 시작하지 못했습니다 (포트가 쓰이는 중일 수 있음 — --port를 바꾸세요):\n" + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // 토큰은 주소 조각(#)으로 — 서버 로그 · Referer에 남지 않는다
        var startUrl = $"{address}/#token={options.Token}";
        try
        {
            if (!HasWebView2())
            {
                // 런타임이 없으면 hblint-app처럼 기본 브라우저로 — 이 알림을 닫을 때까지 서버가 돈다
                if (MessageBox.Show("화면 엔진(WebView2 런타임)이 없어 창을 열 수 없습니다. 기본 브라우저로 열까요?\n(설치: https://developer.microsoft.com/microsoft-edge/webview2/)",
                        Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(startUrl) { UseShellExecute = true });
                    MessageBox.Show("브라우저로 열었습니다. 이 창을 닫으면 서버가 끝납니다.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }
            Application.Run(new MainForm(new Uri(address), startUrl, options.DataFolder));
            return 0;
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static bool HasWebView2()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }
}
