using System.Text.Json;
using HB.Chat;
using HB.Orchestration.ChatUi;
using Microsoft.Extensions.Options;

// 설정은 켤 때 한 번만 읽는다 (/self-review security b19234c862 C1): 편집 방이 설정 파일을 고쳐도(ClaudePath · Workspaces · AllowedHosts)
// 돌고 있는 앱에는 반영되지 않는다. 설정 파일은 편집 금지 경로(ClaudeArgs.EditDenied)이기도 하다 — 두 겹.
var builder = WebApplication.CreateBuilder([.. args, "--hostBuilder:reloadConfigOnChange=false"]);

// 개인 값은 git 제외 파일에 둔다. 명령줄 인자가 여전히 가장 우선하게 그 앞에 끼운다 (대시보드와 같은 방식).
var localSettings = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = "appsettings.local.json", Optional = true, ReloadOnChange = false,
};
localSettings.ResolveFileProvider();
var commandLineIndex = builder.Configuration.Sources.ToList().FindLastIndex(s => s is Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationSource);
builder.Configuration.Sources.Insert(commandLineIndex < 0 ? builder.Configuration.Sources.Count : commandLineIndex, localSettings);
// 다시 읽기를 끄는 키가 듣지 않는 판이면 켜지 않는다 (fail-closed).
if (builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.FileConfigurationSource>().FirstOrDefault(s => s.ReloadOnChange) is { } reloading)
    throw new InvalidOperationException($"설정 파일 다시 읽기가 켜져 있습니다: {reloading.Path}");

builder.Services.AddRazorPages();
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection("Chat"));

// 스티커 설정은 대시보드 설정 파일(Dashboard:Stickers)을 그대로 읽는다 — 목록 · 그림 폴더를 두 벌 두지 않게.
// 이 앱 설정과 섞이지 않게 따로 읽는다 (대시보드의 Urls 같은 값이 들어오지 않게).
var stickerConfig = new ConfigurationBuilder();
if (builder.Configuration["Chat:StickerSettingsDir"] is { Length: > 0 } stickerDir && Directory.Exists(stickerDir))
{
    stickerConfig.SetBasePath(Path.GetFullPath(stickerDir))
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
}
builder.Services.Configure<StickerOptions>(stickerConfig.Build().GetSection("Dashboard:Stickers"));
builder.Services.AddSingleton(sp => new StickerCatalog(() => sp.GetRequiredService<IOptionsMonitor<StickerOptions>>().CurrentValue));

builder.Services.AddSingleton(sp => new RoomStore(Path.Combine(sp.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder, "rooms.json")));
builder.Services.AddSingleton(sp => new ChatTranscripts(() => sp.GetRequiredService<IOptions<ChatOptions>>().Value.ProjectsRoot));
builder.Services.AddSingleton<RoomHub>();

var app = builder.Build();
app.UseSameOriginGuard();
app.MapRazorPages();
app.MapStickerImages();

// 방 실시간 알림 (SSE). 흘림 글 · 도구 상태 · 거절 · 끝. 끝나면 화면이 세션 기록으로 말풍선을 다시 받는다.
var sseJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
app.MapGet("/events/{id}", async (string id, RoomStore store, RoomHub hub, HttpContext ctx) =>
{
    if (!RoomStore.IsRoomId(id) || store.Get(id) is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-store";
    var ch = hub.Subscribe(id);
    try
    {
        await foreach (var ev in ch.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(ev, sseJson)}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        hub.Unsubscribe(id, ch);
    }
});

// 앱을 끌 때 돌던 claude 프로세스 트리를 끝낸다.
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<RoomHub>().Dispose());
app.Run();
