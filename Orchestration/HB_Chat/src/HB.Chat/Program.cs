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
// 대시보드 폴더 · 그 안의 스티커 폴더는 상대 경로도 받는다 — 이 앱 content root 기준, 스티커 폴더는 그 대시보드 폴더 기준
var stickerConfig = new ConfigurationBuilder();
var stickerBase = StickerOptions.Resolve(builder.Configuration["Chat:StickerSettingsDir"], builder.Environment.ContentRootPath);
if (stickerBase is { Length: > 0 } stickerDir && Directory.Exists(stickerDir))
{
    stickerConfig.SetBasePath(Path.GetFullPath(stickerDir))
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
}
builder.Services.Configure<StickerOptions>(stickerConfig.Build().GetSection("Dashboard:Stickers"));
builder.Services.PostConfigure<StickerOptions>(o => o.Dir = StickerOptions.Resolve(o.Dir, stickerBase is { Length: > 0 } b ? Path.GetFullPath(b) : builder.Environment.ContentRootPath));
builder.Services.AddSingleton(sp => new StickerCatalog(() => sp.GetRequiredService<IOptionsMonitor<StickerOptions>>().CurrentValue));

builder.Services.AddSingleton(sp => new RoomStore(Path.Combine(sp.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder, "rooms.json")));
builder.Services.AddSingleton(sp => new ChatTranscripts(() => sp.GetRequiredService<IOptions<ChatOptions>>().Value.ProjectsRoot));
// 터미널 곁칸: 지휘자가 띄운 서버를 앱이 ConPTY로 붙잡는다 + 앱 MCP(명령 방 실행만, 실행 토큰)
builder.Services.AddSingleton<Terminals>();
builder.Services.AddSingleton<McpProbe>(); // 곁칸 연결 탭 "확인"
builder.Services.AddHttpContextAccessor();
// 지휘자 브라우저: 방마다 화면 없는 Chrome(없으면 Edge) · 별도 프로필 — 같은 앱 MCP
builder.Services.AddSingleton(sp => new AgentBrowser(() => sp.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder,
    blockedPorts: () => TerminalMcp.BlockedPorts(sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(), sp.GetRequiredService<IOptions<ChatOptions>>().Value)));
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<TerminalTools>().WithTools<BrowserTools>();
// 구독 한도: Claude는 실행 때 받은 값을 데이터 폴더에, Codex는 ~/.codex 세션 기록에서
builder.Services.AddSingleton(sp => new UsageLimits(() => sp.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder));
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ChatOptions>>();
    return new RoomHub(sp.GetRequiredService<RoomStore>(), sp.GetRequiredService<ChatTranscripts>(), options)
    {
        Log = new ApprovalLog(() => options.Value.DataFolder),
        Limits = sp.GetRequiredService<UsageLimits>(),
        Terminals = sp.GetRequiredService<Terminals>(),
        Browser = sp.GetRequiredService<AgentBrowser>(),
        SelfUrl = () => TerminalMcp.LoopbackUrl(sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()),
    };
});
builder.Services.AddSingleton<GroupLog>(); // 단톡방: 대화 줄 · 구성원 실행
// Codex 구성원을 켤 수 있는 작업 폴더 (단톡방 ② 결정 5 — 기본 없음, 설정 화면에서 고름)
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ChatOptions>>();
    return new CodexSettings(() => options.Value.DataFolder, name => options.Value.WorkspacePath(name)); // 허용은 그때 경로에 묶임
});
builder.Services.AddSingleton<GroupChat>();
builder.Services.AddSingleton<RoomList>();
// 주체별 프로필 (3차 e): 데이터 폴더 profiles.json + profiles/ 그림
builder.Services.AddSingleton(sp => new ProfileStore(sp.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder));
builder.Services.AddSingleton<ProfileView>();
builder.Services.AddSingleton<FaceCatalog>();
builder.Services.AddSingleton<StickerPacks>(); // 스티커 팩 (4차 D2)
builder.Services.AddSingleton<RoomRuns>();
builder.Services.AddSingleton(sp => new RoomNotes(sp.GetRequiredService<IOptions<ChatOptions>>())); // 방 기록 줄 (승인 결과 · 정리 표시)

var app = builder.Build();

// 스티커 설정 폴더를 못 찾으면 스티커 · 표정 그림이 조용히 빠진다 — 상대 경로(content root 기준)로 바뀐 뒤라 시작 로그에 남긴다 (/self-review b280752387 C6)
if (builder.Configuration["Chat:StickerSettingsDir"] is { Length: > 0 } configuredStickers && (stickerBase is null || !Directory.Exists(stickerBase)))
    app.Logger.LogWarning("스티커 설정 폴더를 찾지 못했습니다: {Configured} → {Resolved} (content root {Root}) — 스티커 · 표정 그림 없이 돕니다",
        configuredStickers, stickerBase, builder.Environment.ContentRootPath);

// 데이터 폴더 쓰기가 Claude 데스크톱 앱(MSIX) 패키지 캐시로 옮겨지면 켜지 않는다 (4차 A) — 밖에서 띄운 앱과 방 목록 · 기록이 두 벌로 갈린다.
// 데이터 폴더를 설정 · 인자로 준 실행도 같이 본다: 옮겨지지 않는 폴더(F: · %TEMP% — 시험 서버의 scratchpad)는 Probe가 null이라 막히지 않는다
// (/self-review dc27a724bf C3).
{
    var chat = app.Services.GetRequiredService<IOptions<ChatOptions>>().Value;
    if (RedirectedFolder.Probe(chat.DataFolder) is { } redirected)
        throw new InvalidOperationException(RedirectedFolder.Message(chat.DataFolder, redirected));
}

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

    // Unity 프로젝트: 작업 폴더와 같은 검사 + Unity 프로젝트 모양. 방은 UnityProject 칸으로 가리킨다.
    chat.AppUnityProjects = new WorkspaceStore(Path.Combine(chat.DataFolder, "unity-projects.json"), chat.UnityProjects, guarded,
        usedBy: r => r.UnityProject,
        extraCheck: p => File.Exists(Path.Combine(p, "ProjectSettings", "ProjectVersion.txt")) ? null : "Unity 프로젝트 폴더가 아닙니다 (ProjectSettings/ProjectVersion.txt 없음).",
        noun: "Unity 프로젝트");
    // 앱에서 더한 MCP 서버 — 기본 서버 · 앱 MCP · unity 이름은 쓸 수 없다
    chat.AppMcp = new AppMcpStore(Path.Combine(chat.DataFolder, "mcp-servers.json"), [chat.McpServer, TerminalMcp.Server, McpConfigFile.UnityServer]);
    if (chat.AppMcp.LoadError is { } mcpError)
        app.Logger.LogWarning("앱에서 더한 MCP 서버(mcp-servers.json)를 읽지 못했습니다: {Error}", mcpError);
    foreach (var warning in chat.AppMcp.LoadWarnings)
        app.Logger.LogWarning("앱에서 더한 MCP 서버를 쓰지 않습니다: {Warning}", warning);
    if (chat.AppUnityProjects.LoadError is { } unityError)
        app.Logger.LogWarning("앱에서 더한 Unity 프로젝트(unity-projects.json)를 읽지 못했습니다: {Error}", unityError);
    foreach (var warning in chat.AppUnityProjects.LoadWarnings)
        app.Logger.LogWarning("앱에서 더한 Unity 프로젝트를 쓰지 않습니다: {Warning}", warning);
}

// 앱이 비정상 종료돼도 claude 프로세스 트리가 남지 않게 (ProcessJob). 실패해도 켜기는 한다 — 정상 종료 · 중지의 트리 종료는 그대로 돈다.
if (ProcessJob.AttachCurrentProcess() is { } jobError)
    app.Logger.LogWarning("Job Object에 넣지 못했습니다 — 앱이 비정상 종료되면 claude 프로세스가 남을 수 있습니다: {Error}", jobError);

// 단톡방: 처음부터 만들어 둔다 — 구성원 자식 방의 턴 끝 · 활동 알림을 구독해야 답이 단톡방에 올라간다
app.Services.GetRequiredService<GroupChat>();

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

// 휴지통 30일: 켤 때 한 번, 그 뒤 하루마다. 세션 기록은 남긴다 (Trash).
void PurgeTrash()
{
    try
    {
        var removed = Trash.Purge(app.Services.GetRequiredService<RoomStore>(),
            app.Services.GetRequiredService<IOptions<ChatOptions>>().Value.DataFolder, DateTimeOffset.Now);
        // 지운 방의 터미널 목록(끝난 것의 버퍼 포함)도 버린다 (/self-review 57b59ef134 C12)
        foreach (var id in removed)
        {
            app.Services.GetRequiredService<Terminals>().Forget(id);
            app.Services.GetRequiredService<AgentBrowser>().Close(id, "방 삭제");
        }
        if (removed.Count > 0)
            app.Logger.LogWarning("휴지통에서 30일 지난 방 {Count}개를 지웠습니다 (방 색인 · 첨부, 세션 기록은 남김): {Ids}", removed.Count, string.Join(", ", removed));
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        app.Logger.LogWarning("휴지통을 비우지 못했습니다: {Error}", ex.Message);
    }
}

// 보내지 않은 첨부 (하루 넘고 기록에 안 나온 것) — 휴지통 정리와 같은 때, 뒤에서.
// 세션 기록 폴더가 없으면(설정이 바뀜 등) 하지 않고, 예상 밖 예외도 로그로만 — 시작 · 타이머가 죽지 않게 (/self-review security 4c300badae C1 · C2, 70170a8801 C5)
void CleanAttachments()
{
    try
    {
        var o = app.Services.GetRequiredService<IOptions<ChatOptions>>().Value;
        if (!Directory.Exists(o.ProjectsRoot))
        {
            app.Logger.LogWarning("세션 기록 폴더가 없어 보내지 않은 첨부 정리를 건너뜁니다: {Root}", o.ProjectsRoot);
            return;
        }
        var cleaned = Attachments.CleanUnsent(o.DataFolder, app.Services.GetRequiredService<RoomStore>().All(),
            app.Services.GetRequiredService<ChatTranscripts>().Find, DateTimeOffset.Now, app.Services.GetRequiredService<RoomHub>().IsBusy);
        if (cleaned > 0) app.Logger.LogInformation("보내지 않은 첨부 {Count}개를 지웠습니다 (하루 넘고 대화 기록에 없는 파일)", cleaned);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("보내지 않은 첨부를 정리하지 못했습니다: {Error}", ex.Message);
    }
}
PurgeTrash();
_ = Task.Run(CleanAttachments);
using var purgeTimer = new Timer(_ =>
{
    PurgeTrash();
    CleanAttachments();
}, null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));

app.UseSameOriginGuard();
app.UseTerminalMcpToken(); // 앱 MCP는 그 방 실행 토큰이 맞을 때만
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
            // 프로필은 양식 POST라 브라우저가 응답 화면으로 옮겨 간다 — 글 대신 설정 화면으로 돌려보내 오류 한 줄 (4차 B2: 글자가 깨져 보였음).
            // 첨부는 화면 스크립트(fetch)가 받으므로 글 그대로, 문자 집합을 붙인다.
            if (string.Equals(handler, "Profile", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
                ctx.Response.Headers.Location = "/Settings?fail=image-size";
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
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
app.MapMcp(TerminalMcp.Path);
// 화면 테마 (5차 a): 데스크톱 앱이 맨 위 줄 색 · WebView2 색 설정(대시보드 탭 포함)을 맞추려고 읽는다. 읽기만.
// notifyBody: 데스크톱 알림에 답 앞부분을 보일지 (설정 화면 토글, 기본 끔)
// conductorImage: 지휘자 프로필 그림 주소 (/profile-image/…, 없으면 null) — 데스크톱 토스트 왼쪽 그림 (결정 "지휘자 프로필 그림")
app.MapGet("/api/appearance", (ProfileStore profiles, ProfileView view, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { theme = profiles.Theme(), notifyBody = profiles.NotifyBody(), conductorImage = view.ImageUrl(ProfileStore.Conductor) });
});
// 스티커 팩 그림 (4차 D2): 있는 팩 · 목록에 있는 태그만, 팩 폴더 → 없으면 기본 폴더 (StickerCatalog.FileFor가 폴더 밖을 막는다)
app.MapGet("/sticker-pack/{pack}/{tag}", IResult (string pack, string tag, StickerPacks packs, HttpResponse response) =>
{
    if (!packs.Exists(pack) || packs.For(pack).FileFor(tag) is not { } file) return Results.NotFound();
    // 같은 주소가 기본 그림 → 팩에 채운 그림으로 바뀐다 — 매번 바뀌었는지 묻는다 (Last-Modified, 안 바뀌면 304 — ef2c83416c C4)
    response.Headers.CacheControl = "private, no-cache";
    response.Headers.XContentTypeOptions = "nosniff";
    return TypedResults.PhysicalFile(file.Path, file.ContentType, lastModified: File.GetLastWriteTimeUtc(file.Path));
});
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

// 데스크톱 앱: 방 상태 요약 — 서버 확인 · 알림 · 트레이 상태용. 읽기만.
// faceUrl: 그 방 팩의 표정 그림 주소 — 데스크톱 펫, 방 목록 아바타와 같은 식
app.MapGet("/api/rooms", (RoomList list, ProfileStore profiles, FaceCatalog faces, StickerPacks packs, HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    // 미리보기는 늘 싣는다 — 펫 말풍선(4초, 알림 센터에 남지 않음)은 늘 보이고(결정 "말풍선은 늘"), 토스트 본문은 데스크톱이 notifyBody로 가린다
    return Results.Json(RoomApi.Rows(list.All(), withPreview: true, faceUrl: s => faces.Url(s.Face, packs.For(packs.Effective(s.StickerPack)))),
        RoomApi.Json);
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

// 앱 전체 알림 (SSE): 방 목록 줄이 바뀌었을 수 있는 방 ID. 화면은 받으면 목록을 다시 받는다.
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
app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<RoomHub>().Dispose();
    app.Services.GetRequiredService<Terminals>().Dispose(); // 터미널도 (Job Object가 어차피 끝내지만 끝난 표시를 남긴다)
    app.Services.GetRequiredService<AgentBrowser>().Dispose();
});
app.Run();
