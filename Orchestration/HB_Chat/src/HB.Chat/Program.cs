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
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ChatOptions>>();
    return new RoomHub(sp.GetRequiredService<RoomStore>(), sp.GetRequiredService<ChatTranscripts>(), options)
    {
        Log = new ApprovalLog(() => options.Value.DataFolder),
    };
});
builder.Services.AddSingleton<RoomList>();
// 주체별 프로필 (3차 e): 데이터 폴더 profiles.json + profiles/ 그림
builder.Services.AddSingleton(sp => new ProfileStore(sp.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder));
builder.Services.AddSingleton<ProfileView>();
builder.Services.AddSingleton<FaceCatalog>();
builder.Services.AddSingleton<RoomRuns>();

var app = builder.Build();

// 앱에서 더한 작업 폴더 (3차 d): 켤 때 한 번 읽어 설정 값에 붙인다 — 설정 파일(Chat:Workspaces)이 먼저다.
// 아래 보호 폴더를 품거나 그 안인 폴더는 더하지 못한다 — 편집 금지 규칙이 작업 폴더 기준 상대 경로라, 이 폴더들을 따로 작업 폴더로 두면
// 앱 · MCP 서버 · Claude 설정을 고칠 수 있게 되므로 (/self-review security eee1adb777 C2 · 460430d647 C1 · C2).
{
    var chat = app.Services.GetRequiredService<IOptions<ChatOptions>>().Value;
    var guarded = new List<string> { app.Environment.ContentRootPath, AppContext.BaseDirectory, chat.DataFolder };
    // 앱이 든 git 저장소 전체 (공유 라이브러리 · 데스크톱 · MCP 게시 폴더 — 다음 빌드 · 실행에 들어간다)
    for (var dir = new DirectoryInfo(app.Environment.ContentRootPath); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) { guarded.Add(dir.FullName); break; }
    // Claude 설정 폴더 (세션 기록 · 할 일 · settings.json 훅): 세션 기록 폴더의 부모(CLAUDE_CONFIG_DIR 반영)와 ~/.claude
    if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(chat.ProjectsRoot))) is { Length: > 0 } claudeDir) guarded.Add(claudeDir);
    guarded.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
    // 설정 파일 작업 폴더 기준 편집 금지 폴더 (Orchestration/HB_Chat · Orchestration/Publish_Output)
    foreach (var ws in chat.Workspaces.Values.Where(v => !string.IsNullOrWhiteSpace(v)))
        foreach (var rule in ClaudeArgs.EditDenied.Where(r => !r.StartsWith("**", StringComparison.Ordinal) && r.EndsWith("/**", StringComparison.Ordinal)))
            guarded.Add(Path.Combine(ws.Trim(), rule[..^3]));
    chat.AppWorkspaces = new WorkspaceStore(Path.Combine(chat.DataFolder, "workspaces.json"), chat.Workspaces, guarded);
    if (chat.AppWorkspaces.LoadError is { } wsError)
        app.Logger.LogWarning("앱에서 더한 작업 폴더(workspaces.json)를 읽지 못했습니다 — 덮어쓰지 않고 설정 파일 것만 씁니다: {Error}", wsError);
    foreach (var warning in chat.AppWorkspaces.LoadWarnings)
        app.Logger.LogWarning("앱에서 더한 작업 폴더를 쓰지 않습니다: {Warning}", warning);
}

// 앱이 비정상 종료돼도 claude 프로세스 트리가 남지 않게 (ProcessJob). 실패해도 켜기는 한다 — 정상 종료 · 중지의 트리 종료는 그대로 돈다.
if (ProcessJob.AttachCurrentProcess() is { } jobError)
    app.Logger.LogWarning("Job Object에 넣지 못했습니다 — 앱이 비정상 종료되면 claude 프로세스가 남을 수 있습니다: {Error}", jobError);

// 이 필드 전에 만든 방은 마지막 줄까지 읽음으로 (안 그러면 업그레이드 직후 기존 방이 전부 안 읽음 — /self-review 5225b1ffaf C4).
try
{
    if (RoomList.MarkLegacyRead(app.Services.GetRequiredService<RoomStore>(), app.Services.GetRequiredService<ChatTranscripts>()) is > 0 and var moved)
        app.Logger.LogInformation("읽음 표시가 없던 방 {Count}개를 마지막 말까지 읽음으로 옮겼습니다.", moved);
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    app.Logger.LogWarning("읽음 표시를 옮기지 못했습니다 — 기존 방의 안 읽은 수가 0으로 보입니다: {Error}", ex.Message);
}

// 휴지통 30일 (계획서 2-2 c): 켤 때 한 번, 그 뒤 하루마다. 세션 기록은 남긴다 (Trash).
void PurgeTrash()
{
    try
    {
        var removed = Trash.Purge(app.Services.GetRequiredService<RoomStore>(),
            app.Services.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder, DateTimeOffset.Now);
        if (removed.Count > 0)
            app.Logger.LogWarning("휴지통에서 30일 지난 방 {Count}개를 지웠습니다 (방 색인 · 첨부, 세션 기록은 남김): {Ids}", removed.Count, string.Join(", ", removed));
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        app.Logger.LogWarning("휴지통을 비우지 못했습니다: {Error}", ex.Message);
    }
}
PurgeTrash();
using var purgeTimer = new Timer(_ => PurgeTrash(), null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));

app.UseSameOriginGuard();
// 위젯 (2-2 f): 말풍선 안 격리 iframe(srcdoc)이 스스로 다른 주소로 이동하지 못하게 (위젯 글을 밖으로 실어 나르는 길).
// srcdoc은 가져오기가 아니라 막히지 않는다. 이 앱 화면에는 다른 iframe이 없다.
app.UseWidgetCsp();
// 첨부 · 프로필 그림 올리기만 본문 상한을 파일 상한 + 여유로 — 폼 바인딩이 본문을 먼저 다 읽으므로 핸들러 검사 전에 막는다
// (지휘자 검토 R1, 프로필은 3차 e /self-review security). 다른 요청은 Kestrel 기본(30MB) 그대로.
app.Use(async (ctx, next) =>
{
    var handler = HttpMethods.IsPost(ctx.Request.Method) ? ctx.Request.Query["handler"].ToString() : "";
    (long Bytes, string Text)? upload =
        string.Equals(handler, "Attach", StringComparison.OrdinalIgnoreCase) ? (Attachments.MaxBytes + 1024 * 1024, $"파일 하나는 {Attachments.MaxBytes / 1024 / 1024}MB까지입니다.")
        : string.Equals(handler, "Profile", StringComparison.OrdinalIgnoreCase) ? (ProfileStore.MaxImageBytes + 64 * 1024, $"그림은 {ProfileStore.MaxImageBytes / 1024}KB까지입니다.")
        : null;
    if (upload is var (limit, text))
    {
        if (ctx.Request.ContentLength is { } length && length > limit)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ctx.Response.WriteAsync(text);
            return;
        }
        if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
            body.MaxRequestBodySize = limit; // chunked도 이 크기에서 끊긴다
    }
    await next();
});
// 화면 스크립트 · CSS (wwwroot). 배치는 프로젝트 폴더에서 dotnet run으로 띄워 content root = 프로젝트 폴더다.
app.UseStaticFiles();
app.MapRazorPages();
app.MapStickerImages();
// 프로필 그림 (3차 e): 앱이 정한 이름 모양만 · 프로필 폴더 안만, nosniff · 같은 출처만 쓰게
app.MapGet("/profile-image/{file}", IResult (string file, ProfileStore profiles, HttpResponse response) =>
{
    if (profiles.ImagePath(file) is not { } path) return Results.NotFound();
    response.Headers.XContentTypeOptions = "nosniff";
    response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    response.Headers.CacheControl = "private, max-age=86400"; // 바꾸면 파일 이름이 바뀐다
    return TypedResults.PhysicalFile(path,ProfileStore.ContentType(path)); // 경로는 GetFullPath로 만든 절대 경로
});
// 승인 · 질문은 claude 프로세스의 stdin/stdout(stdio 호스트)으로 주고받는다 — 승인용 HTTP 경로는 없다 (3차 a, 그 전 2-1의 /approve).

// 방 실시간 알림 (SSE). 흘림 글 · 도구 상태 · 거절 · 끝. 끝나면 화면이 세션 기록으로 말풍선을 다시 받는다.
var sseJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

// 데스크톱 앱 (계획서 2-3): 방 상태 요약 — 서버 확인 · 알림 · 트레이 상태용. 읽기만.
app.MapGet("/api/rooms", (RoomList list, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(RoomApi.Rows(list.All()), RoomApi.Json);
});
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

// 앱 전체 알림 (SSE, 계획서 2-2 a): 방 목록 줄이 바뀌었을 수 있는 방 ID. 화면은 받으면 목록을 다시 받는다.
// 붙자마자 hello를 보낸다 — 끊긴 사이에 바뀐 것이 있을 수 있어 화면이 목록을 한 번 다시 받는다.
app.MapGet("/events", async (RoomHub hub, HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-store";
    var ch = hub.SubscribeAll();
    try
    {
        await ctx.Response.WriteAsync("data: {\"kind\":\"hello\"}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        await foreach (var id in ch.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { kind = "room", id }, sseJson)}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        hub.UnsubscribeAll(ch);
    }
});

// 앱을 끌 때 돌던 claude 프로세스 트리를 끝낸다.
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<RoomHub>().Dispose());
app.Run();
