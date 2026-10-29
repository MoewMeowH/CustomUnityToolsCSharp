using HB.Orchestration.ChatUi;
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
// 스티커 폴더가 상대 경로면 content root 기준 (기본 "IconPack" — csproj가 exe 옆으로도 복사, HB_Chat 계획서 2026-10-29_바탕화면-펫)
builder.Services.PostConfigure<DashboardOptions>(o => o.Stickers.Dir = StickerOptions.Resolve(o.Stickers.Dir, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<ModesConfigService>();
builder.Services.AddSingleton<CallLogCache>();
builder.Services.AddSingleton<VerdictLogCache>();
builder.Services.AddSingleton<RiderProjects>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddSingleton<ClaudeSessionsService>();
builder.Services.AddSingleton<SubagentResultsService>();
builder.Services.AddSingleton<BillingStore>();
builder.Services.AddSingleton(sp => new StickerCatalog(() => sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DashboardOptions>>().CurrentValue.Stickers));
builder.Services.AddSingleton<ChatRoomService>();
builder.Services.AddSingleton<ChatAppRooms>();
builder.Services.AddHttpClient<BillingApi>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .AddTypedClient((http, sp) => new BillingApi(http, () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardOptions>>().Value.ReadAdminKey()));

var app = builder.Build();
app.MapRazorPages();
app.MapStickerImages();
app.Run();
