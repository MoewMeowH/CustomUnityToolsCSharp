using HB.Orchestration.Dashboard;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
builder.Services.AddSingleton<ModesConfigService>();
builder.Services.AddSingleton<PricingService>();

var app = builder.Build();
app.MapRazorPages();
app.Run();
