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

// 호출 기록 압축 (사람이 가끔 돌림): 예전 기록의 큰 프롬프트를 blobs로 옮기고 원래 파일은 백업으로 남긴다. 서버를 띄우지 않는다.
// 사용: HB.Orchestration.Mcp.exe --compact-log [calls.jsonl 경로]
if (args.Length > 0 && args[0] == "--compact-log")
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    var compacted = HB.Orchestration.Shared.CallLog.CompactPrompts(args.Length > 1 ? args[1] : null);
    Console.WriteLine($"줄 {compacted.Lines:N0} (옮김 {compacted.Moved:N0} · 그대로 {compacted.Kept:N0}) · " +
                      $"{compacted.BytesBefore:N0} → {compacted.BytesAfter:N0}바이트 · 백업 {compacted.Backup}");
    foreach (var e in compacted.Errors) Console.Error.WriteLine($"[저장 실패] {e}");
    return compacted.Errors.Count == 0 ? 0 : 1;
}

// 골든 세트 회귀 평가 (사람이 가끔 돌림). 아래와 같은 설정·서비스로 에이전트를 부르지만 MCP 서버는 띄우지 않는다.
var golden = args.Length > 0 && args[0] == GoldenCli.Flag;

// Claude Code는 이 서버를 Unity 프로젝트 폴더에서 실행한다.
// appsettings.json은 그 폴더가 아니라 dll 옆에서 읽어야 하므로 ContentRoot를 실행 파일 위치로 고정한다.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // --golden 인자는 설정 키가 아니다 (명령줄 설정 공급자가 "--yes"를 값 없는 키로 보고 예외를 낸다).
    Args = golden ? [] : args,
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
builder.Services.AddSingleton<IChatClientProvider, ClaudeCliProvider>(); // claude -p 읽기 전용 검토 (sub_* 모드)
builder.Services.AddSingleton<IChatClientProvider, CodexCliProvider>(); // codex exec 읽기 전용 리뷰, ChatGPT 구독 (review_main)

builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<ModeStore>();
builder.Services.AddSingleton<ModeRunner>();

if (golden)
{
    // 세션 상태 파일을 남기지 않는다 (대시보드 '세션' 화면에 Claude Code 세션처럼 보이지 않게).
    builder.Services.AddSingleton(sp => SessionTracker.Unsaved(sp.GetRequiredService<ILogger<SessionTracker>>()));
    // 에이전트 등록 같은 정보 로그는 점수 출력에 섞이지 않게 뺀다 (재시도·실패 경고는 남김). 인코딩은 첫 로그 전에.
    // (SetMinimumLevel은 appsettings의 Logging:LogLevel:Default에 덮이므로, 그보다 구체적인 범주 규칙으로 건다)
    builder.Logging.AddFilter("HB.Orchestration", LogLevel.Warning);
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    using var goldenHost = builder.Build();
    var catalog = goldenHost.Services.GetRequiredService<AgentCatalog>();
    using var cts = new CancellationTokenSource();
    // 첫 Ctrl+C는 취소(끝난 사례의 결과는 이미 파일에 있음), 두 번째는 그대로 종료.
    Console.CancelKeyPress += (_, e) => { e.Cancel = !cts.IsCancellationRequested; cts.Cancel(); };
    return await GoldenCli.RunAsync(args, catalog.Options, goldenHost.Services.GetRequiredService<ModeRunner>,
        logPath: null, Console.Out, Console.Error, cts.Token);
}

builder.Services.AddSingleton<SessionTracker>();

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

// 검토형 서브에이전트 결과를 호출 기록 옆으로 보관한다 (Claude Code가 세션 기록을 지우기 전에). 시작을 막지 않고, 실패해도 서버는 돈다.
// 대시보드도 읽을 때 같은 Sync를 하므로 여기는 대시보드를 안 켜는 날의 보험이다.
var archiveLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SubagentArchive");
var archiveStopping = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
_ = Task.Run(() =>
{
    try
    {
        var report = HB.Orchestration.Shared.SubagentArchive.Sync(HB.Orchestration.Shared.ClaudeTranscriptReader.DefaultProjectsRoot(),
            HB.Orchestration.Shared.SubagentArchive.FolderFor(app.Services.GetRequiredService<AgentRunner>().LogPath), archiveStopping);
        if (report.Errors.Count > 0)
            archiveLog.LogWarning("서브에이전트 결과 보관: 새로 {Added}건 · 읽은 기록 {Scanned} · 결과 없음 {NoResult} · 오류 {Errors}",
                report.Added, report.Scanned, report.NoResult, string.Join(" | ", report.Errors));
        else if (report.Added > 0 || report.NoResult > 0)
            archiveLog.LogInformation("서브에이전트 결과 보관: 새로 {Added}건 · 읽은 기록 {Scanned} · 결과 없음 {NoResult}", report.Added, report.Scanned, report.NoResult);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { archiveLog.LogWarning(ex, "서브에이전트 결과 보관 실패"); }
});

await app.RunAsync();
return 0;
