using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Claude Code는 이 서버를 Unity 프로젝트 폴더에서 실행한다.
// appsettings.json은 그 폴더가 아니라 dll 옆에서 읽어야 하므로 ContentRoot를 실행 파일 위치로 고정한다.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// stdio MCP 서버는 stdout이 프로토콜 채널이라, 로그는 반드시 stderr로 보낸다.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var options = builder.Configuration.GetSection("Orchestration").Get<OrchestrationOptions>() ?? new();
builder.Services.AddSingleton(options);

// 제공사 구현. 전용 SDK가 필요한 제공사가 생기면 여기에 추가.
builder.Services.AddSingleton<IChatClientProvider, OpenAICompatibleProvider>();

builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<ModeStore>();
builder.Services.AddSingleton<ModeRunner>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ModeTools>();

var app = builder.Build();

// 에이전트 설정 오류를 첫 호출이 아니라 시작 시점 로그에서 바로 보이도록 미리 만든다.
app.Services.GetRequiredService<AgentCatalog>();

await app.RunAsync();
