using System.Diagnostics;
using System.Net;
using HBLint.App;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var options = AppOptions.Parse(args);
if (options == null)
{
    Console.Error.WriteLine(AppOptions.Usage);
    return 2;
}

var app = AppHost.Build(options, webBuilder => webBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port)));
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
// 토큰은 주소 조각(#)으로 — 서버 로그 · Referer에 남지 않는다
var url = $"{address.Replace("[::1]", "127.0.0.1")}/#token={options.Token}";
Console.WriteLine($"hblint-app: {url}");
Console.WriteLine("끝내려면 Ctrl+C");
if (options.OpenBrowser)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.WriteLine("브라우저를 열지 못했습니다 — 위 주소를 직접 여세요");
    }
}
await app.WaitForShutdownAsync();
return 0;

namespace HBLint.App
{
    /// <summary>앱 구성 (시험은 WebApplicationFactory 대신 이것으로 TestServer를 만든다).</summary>
    internal static class AppHost
    {
        public static WebApplication Build(AppOptions options, Action<WebApplicationBuilder>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            });
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<AppState>();
            configure?.Invoke(builder);

            var app = builder.Build();
            app.UseLocalGuard(options);
            app.UseDefaultFiles();
            app.UseStaticFiles();
            // 라우팅을 정적 파일 뒤에 — 앞에 있으면 "/"가 MapFallback에 먼저 잡혀 index.html을 주지 않는다
            app.UseRouting();
            Api.Map(app);
            app.MapFallback(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/plain; charset=utf-8";
                return context.Response.WriteAsync(File.Exists(Path.Combine(app.Environment.WebRootPath, "index.html"))
                    ? "없는 주소입니다"
                    : "웹 UI가 빌드되지 않았습니다 — tools/Run-App.ps1로 실행하세요 (npm run build → wwwroot)");
            });
            return app;
        }
    }
}
