using System.Text.Json.Nodes;
using HB.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HB.Chat.Tests;

/// <summary>
/// 화면 · 통합 시험용 실제 앱: WebApplicationFactory + Kestrel 임의 포트 · 임시 데이터 폴더 · 가짜 claude.
/// 0단계 실측의 함정 — appsettings.json의 Urls(5290)가 UseKestrel(0)보다 이기므로 Urls를 덮는다(안 덮으면 사용자 앱 포트에 뜬다).
/// Workspaces는 사전이라 시험 이름을 더해도 실제 저장소 이름(ToolWithCS)이 남는다 — 그것까지 덮고, 켠 뒤 모든 경로가 시험 폴더 안인지 본다(아니면 실패).
/// 쓰는 법: <see cref="Room"/>으로 방 + 세션 기록을 만들고 <see cref="User"/> · <see cref="Conductor"/>로 기록 줄을 만든다.
/// 화면에서 보낸 말은 TestData/fake-claude.ps1이 받는다 — 화면 시험용 모드 'say N'(지휘자 말 N줄 덧붙임) · 'shrink'(기록이 줄어듦).
/// </summary>
public sealed class ChatApp : IDisposable
{
    /// <summary>시험이 만드는 기록 줄의 시각 기준 — 지금보다 앞이라 지금 쓰는 방 기록 줄 · 가짜 claude 줄이 뒤에 온다.</summary>
    private static readonly DateTimeOffset Base = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "hbchat-app-" + Guid.NewGuid().ToString("N")[..12]);
    public WebApplicationFactory<Program> Factory { get; }
    /// <summary>Kestrel 주소 (http://127.0.0.1:포트/).</summary>
    public Uri BaseAddress { get; } = null!;
    public IServiceProvider Services => Factory.Services;
    public RoomStore Store => Services.GetRequiredService<RoomStore>();

    public ChatApp()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "ws"));
        // 읽기 방은 작업 폴더 .mcp.json의 기본 서버 항목이 있어야 돈다 (RoomHubTests와 같다)
        File.WriteAllText(Path.Combine(Dir, "ws", ".mcp.json"), """{"mcpServers":{"hb-orchestration":{"command":"dotnet","args":["x.dll"]}}}""");
        var script = Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.ps1");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production"); // 실제 실행(dotnet run, launchSettings 없음)과 같게
            // 사용자가 둔 appsettings.local.json(Program.cs가 끼움)은 읽지 않는다 — Kestrel:Endpoints 같은 키가 Urls 덮기를 이길 수 있다 (c8752a0b82 C2)
            b.ConfigureAppConfiguration((_, c) =>
            {
                for (var i = c.Sources.Count - 1; i >= 0; i--)
                    if (c.Sources[i] is FileConfigurationSource { Path: LocalSettings }) c.Sources.RemoveAt(i);
            });
            b.UseSetting("Urls", "http://127.0.0.1:0");
            b.UseSetting("Chat:DataDir", Path.Combine(Dir, "data"));
            b.UseSetting("Chat:ClaudeProjectsDir", Path.Combine(Dir, "projects"));
            b.UseSetting("Chat:Workspaces:ws", Path.Combine(Dir, "ws"));
            b.UseSetting("Chat:Workspaces:ToolWithCS", Path.Combine(Dir, "ws"));
            b.UseSetting("Chat:StickerSettingsDir", "");
            b.UseSetting("Chat:ClaudePath", Path.Combine(Dir, "no-claude.exe")); // 가짜 claude를 빼먹으면 진짜 대신 실패로 보이게
            b.UseSetting("Chat:CallLogPath", Path.Combine(Dir, "calls.jsonl")); // 기본값은 실제 호출 기록 파일 (외부 말풍선이 읽는다)
            // RoomHub를 Program.cs와 같은 모양으로 다시 등록하되 ProcessFactory만 더한다 (마지막 등록이 쓰인다)
            b.ConfigureTestServices(s => s.AddSingleton(sp =>
            {
                var options = sp.GetRequiredService<IOptions<ChatOptions>>();
                return new RoomHub(sp.GetRequiredService<RoomStore>(), sp.GetRequiredService<ChatTranscripts>(), options)
                {
                    Log = new ApprovalLog(() => options.Value.DataFolder),
                    Terminals = sp.GetRequiredService<Terminals>(),
                    Browser = sp.GetRequiredService<AgentBrowser>(),
                    SelfUrl = () => TerminalMcp.LoopbackUrl(sp.GetRequiredService<IServer>()),
                    ProcessFactory = (room, spec, workspace) => new ProcessSpec("powershell.exe",
                        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, spec.SessionId,
                            Path.Combine(Dir, "tasks"), TranscriptPath(spec.SessionId)], workspace),
                };
            }));
        });
        // 서버가 뜨다 실패해도 Factory · 임시 폴더를 정리한다 (xUnit은 생성자에서 던진 픽스처를 Dispose하지 않는다, c8752a0b82 C1)
        try
        {
            Factory.UseKestrel(0);
            Factory.StartServer();
            BaseAddress = Factory.ClientOptions.BaseAddress;
            CheckIsolated();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private const string LocalSettings = "appsettings.local.json";

    /// <summary>
    /// 켠 앱이 사용자 데이터 · 포트를 쓰지 않는지 (바뀐 appsettings.json이 새어 들면 시험을 멈춘다). 켠 뒤에 보지만, 켤 때 쓰는 데이터 폴더 ·
    /// 기록 폴더는 늘 덮으므로 사용자 데이터에 닿기 전이다.
    /// </summary>
    private void CheckIsolated()
    {
        var o = Services.GetRequiredService<IOptions<ChatOptions>>().Value;
        string[] paths = [o.DataFolder, o.ProjectsRoot, o.ResolveClaude(), o.CallLogFile, .. o.Workspaces.Values];
        var outside = paths.Where(p => !Path.GetFullPath(p).StartsWith(Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
        var badAddresses = addresses.Where(a => !Uri.TryCreate(a, UriKind.Absolute, out var u) || u.Host != "127.0.0.1" || u.Port is 5290 or 5291).ToList();
        var localRead = Services.GetRequiredService<IConfiguration>() is IConfigurationRoot root
            && root.Providers.OfType<FileConfigurationProvider>().Any(p => p.Source.Path == LocalSettings);
        if (outside.Count > 0 || !string.IsNullOrEmpty(o.StickerSettingsDir) || addresses.Count == 0 || badAddresses.Count > 0 || localRead)
            throw new InvalidOperationException($"시험 앱이 시험 폴더 · 포트 밖을 씁니다: {string.Join(", ", outside)} · 스티커 '{o.StickerSettingsDir}' · " +
                $"주소 {string.Join(", ", addresses)} · {LocalSettings} 읽음 {localRead}");
    }

    /// <summary>
    /// 세션 기록 파일 자리 (가짜 claude도 같은 곳에 쓴다). "F--ws"는 임의로 정한 하위 폴더 이름이다 — 진짜 CLI는 작업 폴더 경로를 바꾼 이름을 쓰지만,
    /// 앱(ChatTranscripts.Find)은 기록 폴더의 하위 폴더를 모두 훑어 세션 ID로 찾으므로 이름은 상관없다.
    /// </summary>
    public string TranscriptPath(string sessionId) => Path.Combine(Dir, "projects", "F--ws", sessionId + ".jsonl");

    /// <summary>읽기 방 하나 + 세션 기록 (줄마다 <see cref="User"/> · <see cref="Conductor"/>).</summary>
    public Room Room(string title, params string[] lines)
    {
        var room = Store.Create("ws", PermissionLevel.Read, "opus", title);
        Directory.CreateDirectory(Path.GetDirectoryName(TranscriptPath(room.SessionId))!);
        File.WriteAllText(TranscriptPath(room.SessionId), string.Concat(lines.Select(l => l + "\n")));
        return room;
    }

    public Uri RoomUrl(Room room) => new(BaseAddress, "/Room/" + room.Id);

    /// <summary>사용자 말 한 줄 (시각 = 기준 + 초).</summary>
    public static string User(string text, int second) => new JsonObject
    {
        ["type"] = "user",
        ["timestamp"] = Base.AddSeconds(second).ToString("O"),
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    }.ToJsonString();

    /// <summary>지휘자 도구 호출 한 줄 (Read · Bash 등 — 곁칸 문서 탭의 방 기준 목록).</summary>
    public static string Tool(string name, JsonObject input, int second) => new JsonObject
    {
        ["type"] = "assistant",
        ["timestamp"] = Base.AddSeconds(second).ToString("O"),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "t" + second, ["name"] = name, ["input"] = input }),
        },
    }.ToJsonString();

    /// <summary>지휘자 말 한 줄.</summary>
    public static string Conductor(string text, int second) => new JsonObject
    {
        ["type"] = "assistant",
        ["timestamp"] = Base.AddSeconds(second).ToString("O"),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        },
    }.ToJsonString();

    public void Dispose()
    {
        Factory.Dispose();
        DeleteFolder(Dir);
    }

    /// <summary>
    /// 임시 폴더 지우기 — 막 끝낸 프로세스(가짜 claude · Chrome 자식)가 파일을 잠깐 더 잡고 있을 수 있어 몇 번 다시 시도한다 (c8752a0b82 C8).
    /// 끝내 못 지우면 둔다 (시험 결과에는 영향 없음).
    /// </summary>
    public static void DeleteFolder(string path)
    {
        for (var i = 0; i < 10 && Directory.Exists(path); i++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
