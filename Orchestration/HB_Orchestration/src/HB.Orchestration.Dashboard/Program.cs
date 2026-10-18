using HB.Orchestration.Dashboard;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddSingleton<ModesConfigService>();
builder.Services.AddSingleton<CallLogCache>();
builder.Services.AddSingleton<PricingService>();
builder.Services.AddSingleton<ClaudeSessionsService>();
builder.Services.AddSingleton<BillingStore>();
builder.Services.AddHttpClient<BillingApi>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .AddTypedClient((http, sp) => new BillingApi(http, () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardOptions>>().Value.ReadAdminKey()));

var app = builder.Build();
app.MapRazorPages();
app.Run();
