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
builder.Services.AddHttpClient<BillingApi>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .AddTypedClient((http, sp) => new BillingApi(http, () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardOptions>>().Value.ReadAdminKey()));

var app = builder.Build();
app.MapRazorPages();
app.MapStickerImages();
app.Run();
