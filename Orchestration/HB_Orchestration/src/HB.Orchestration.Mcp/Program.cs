using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// publish 뒤 HB_Orchestration.bat이 부르는 설정 비교. 서버(Host)를 만들기 전에 출력하고 끝낸다.
// 인자 없이 실행되면(Claude Code가 띄울 때) 이 분기를 지나쳐 기존 서버 그대로다.
if (args.Length > 0 && args[0] == ConfigDriftReport.Flag)
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    return ConfigDriftReport.Run(args, Console.Out, Console.Error);
}

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

// 설정은 고정값으로 넣지 않고 "읽는 방법"을 넘긴다. appsettings.json이 바뀌면 AgentCatalog가 다시 읽는다.
var configuration = builder.Configuration;
builder.Services.AddSingleton(new OrchestrationSource(
    Load: () =>
    {
        ((IConfigurationRoot)configuration).Reload();
        return configuration.GetSection("Orchestration").Get<OrchestrationOptions>() ?? new();
    },
    SettingsPath: Path.Combine(AppContext.BaseDirectory, "appsettings.json")));

// 제공사 구현. 전용 SDK가 필요한 제공사가 생기면 여기에 추가.
builder.Services.AddSingleton<IChatClientProvider, OpenAICompatibleProvider>();
builder.Services.AddSingleton<IChatClientProvider, OpenAIResponsesProvider>(); // 웹검색 단계 (Responses API)

builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<ModeStore>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<ModeRunner>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ModeTools>();

var app = builder.Build();

// 에이전트 설정 오류를 첫 호출이 아니라 시작 시점 로그에서 바로 보이도록 미리 만든다.
app.Services.GetRequiredService<AgentCatalog>();

// 세션 상태 파일을 시작하자마자 남기고(대시보드 '세션' 화면), 정상 종료 때 지운다.
var sessions = app.Services.GetRequiredService<SessionTracker>();
app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(sessions.Remove);

await app.RunAsync();
return 0;
