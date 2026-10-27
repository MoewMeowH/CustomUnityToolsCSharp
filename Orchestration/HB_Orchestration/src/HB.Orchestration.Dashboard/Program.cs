using HB.Orchestration.Dashboard;

var builder = WebApplication.CreateBuilder(args);

// 개인 값(스티커 폴더 등)은 git 제외 파일에 둔다. 명령줄 인자가 여전히 가장 우선하게 그 앞에 끼운다.
var localSettings = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = "appsettings.local.json", Optional = true, ReloadOnChange = true,
};
localSettings.ResolveFileProvider();
var commandLineIndex = builder.Configuration.Sources.ToList().FindLastIndex(s => s is Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationSource);
builder.Configuration.Sources.Insert(commandLineIndex < 0 ? builder.Configuration.Sources.Count : commandLineIndex, localSettings);

builder.Services.AddRazorPages();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddSingleton<ModesConfigService>();
builder.Services.AddSingleton<CallLogCache>();
builder.Services.AddSingleton<VerdictLogCache>();
builder.Services.AddSingleton<RiderProjects>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddSingleton<ClaudeSessionsService>();
builder.Services.AddSingleton<SubagentResultsService>();
builder.Services.AddSingleton<BillingStore>();
builder.Services.AddSingleton<StickerCatalog>();
builder.Services.AddSingleton<ChatRoomService>();
builder.Services.AddHttpClient<BillingApi>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .AddTypedClient((http, sp) => new BillingApi(http, () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardOptions>>().Value.ReadAdminKey()));

var app = builder.Build();
app.MapRazorPages();

// 채팅방 스티커 그림. 설정 목록(Files)에 있는 태그만, 스티커 폴더 안의 파일만 내보낸다.
// 그림이 커서(2026-10-27 기본 팩: 1254px, 장당 약 1.4MB) 하루 동안 브라우저 캐시에 둔다.
app.MapGet("/sticker/{tag}", (string tag, StickerCatalog catalog, HttpResponse response) =>
{
    if (catalog.FileFor(tag) is not { } file) return Results.NotFound();
    response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(file.Path, file.ContentType, lastModified: File.GetLastWriteTimeUtc(file.Path));
});
app.Run();
