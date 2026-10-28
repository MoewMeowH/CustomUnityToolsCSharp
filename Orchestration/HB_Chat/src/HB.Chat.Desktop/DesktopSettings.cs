using System.Text.Json;

namespace HB.Chat.Desktop;

/// <summary>
/// 데스크톱 앱 설정. 기본값 ← 데이터 폴더의 desktop.json(있으면) ← 실행 인자(--chat-url= · --dashboard-url= · --data= · --debug-port=).
/// 데이터 폴더(기본 %LOCALAPPDATA%/HB_Chat — 채팅 앱 데이터 폴더와 같음)에는 WebView2 사용자 데이터 · 창 위치 · 로그를 둔다.
/// 대시보드 주소 · exe는 이 설정으로 정한다 — HB_Orchestration.config에서는 PUBLISH_DIR만 읽는다 (SOLUTION_DIR · CONFIGURATION ·
/// DASHBOARD_PORT를 바꿨다면 여기 DashboardUrl · DashboardServerExe를 같이 바꾼다, /self-review 26d478170f C9).
/// </summary>
public sealed record DesktopSettings
{
    public const string DefaultChatUrl = "http://localhost:5290", DefaultDashboardUrl = "http://localhost:5280";

    public string ChatUrl { get; init; } = DefaultChatUrl;
    public string DashboardUrl { get; init; } = DefaultDashboardUrl;

    /// <summary>비우면 저장소 배치에서 찾는다 (<see cref="RepoLayout"/>). 파일 이름이 HB.Chat.exe가 아니면 쓰지 않는다.</summary>
    public string? ChatServerExe { get; init; }

    /// <summary>파일 이름이 HB.Orchestration.Dashboard.exe가 아니면 쓰지 않는다.</summary>
    public string? DashboardServerExe { get; init; }

    /// <summary>대시보드 모드 화면이 고칠 MCP 게시 폴더. 비우면 HB_Orchestration.config의 PUBLISH_DIR (배치와 같은 값).</summary>
    public string? PublishDir { get; init; }

    public string DataFolder { get; init; } = DefaultDataFolder;

    /// <summary>미니 창 보이기 · 숨기기 전역 단축키 (예: "Ctrl+Alt+Space", "Ctrl+Shift+H"). 비우면 단축키 없음.</summary>
    public string? Hotkey { get; init; } = "Ctrl+Alt+Space";

    /// <summary>WebView2 원격 디버깅 포트 (시험용, 인자 --debug-port=로만). 없으면 열지 않는다.</summary>
    public int? DebugPort { get; init; }

    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HB_Chat");

    public string WebViewFolder => Path.Combine(DataFolder, "webview2");
    public string LogFolder => Path.Combine(DataFolder, "logs");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>설정 읽기. 파일이 깨졌거나 값이 맞지 않으면 그 값만 기본으로 켜고 이유를 돌려준다 (켜지 못하는 쪽보다 낫다).</summary>
    public static (DesktopSettings Settings, string? Warning) Load(IReadOnlyList<string> args)
    {
        var warnings = new List<string>();
        var data = Arg(args, "data") is { Length: > 0 } d ? NormalizeFolder(d) : DefaultDataFolder;
        var settings = new DesktopSettings { DataFolder = data };
        var file = Path.Combine(data, "desktop.json");
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(file), Json) is { } loaded)
                settings = loaded with { DataFolder = data, DebugPort = null };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"desktop.json을 읽지 못해 기본값으로 켭니다: {ex.Message}");
        }
        if (Arg(args, "chat-url") is { Length: > 0 } chat) settings = settings with { ChatUrl = chat };
        if (Arg(args, "dashboard-url") is { Length: > 0 } dash) settings = settings with { DashboardUrl = dash };
        if (Arg(args, "debug-port") is { Length: > 0 } port)
        {
            if (int.TryParse(port, out var p) && p is >= 1024 and <= 65535) settings = settings with { DebugPort = p };
            else warnings.Add($"--debug-port 값이 맞지 않아 무시합니다: {port}");
        }

        // 주소는 이 PC 안(http · localhost · 127.0.0.1 · [::1])만 — 서버를 --urls로 띄우므로 LAN 주소면 다른 PC에 열린다 (d459e3af9b C3)
        if (!IsLoopbackHttp(settings.ChatUrl))
        {
            warnings.Add($"채팅 주소는 이 PC 안 http 주소만 씁니다 — {settings.ChatUrl} 대신 {DefaultChatUrl}");
            settings = settings with { ChatUrl = DefaultChatUrl };
        }
        if (!IsLoopbackHttp(settings.DashboardUrl))
        {
            warnings.Add($"대시보드 주소는 이 PC 안 http 주소만 씁니다 — {settings.DashboardUrl} 대신 {DefaultDashboardUrl}");
            settings = settings with { DashboardUrl = DefaultDashboardUrl };
        }
        // 띄울 exe는 그 서버 이름일 때만 (설정 파일로 아무 프로그램이나 숨겨 띄우지 않게 — d459e3af9b C4)
        if (settings.ChatServerExe is { } ce && !HasFileName(ce, RepoLayout.ChatAssembly + ".exe"))
        {
            warnings.Add($"ChatServerExe는 {RepoLayout.ChatAssembly}.exe여야 합니다 — 무시: {ce}");
            settings = settings with { ChatServerExe = null };
        }
        if (settings.DashboardServerExe is { } de && !HasFileName(de, RepoLayout.DashboardAssembly + ".exe"))
        {
            warnings.Add($"DashboardServerExe는 {RepoLayout.DashboardAssembly}.exe여야 합니다 — 무시: {de}");
            settings = settings with { DashboardServerExe = null };
        }
        return (settings, warnings.Count == 0 ? null : string.Join(" / ", warnings));
    }

    public static bool IsLoopbackHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback
        && u.Host is "localhost" or "127.0.0.1" or "[::1]" && u.PathAndQuery == "/" && string.IsNullOrEmpty(u.UserInfo);

    private static bool HasFileName(string path, string name)
    {
        try
        {
            return string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>같은 폴더가 끝 구분자 · 상대 경로로 다르게 적혀도 같은 값 (하나만 실행의 키, 26d478170f C16).</summary>
    internal static string NormalizeFolder(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>`--이름=값` 인자.</summary>
    internal static string? Arg(IReadOnlyList<string> args, string name)
    {
        var prefix = "--" + name + "=";
        return args.LastOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } a ? a[prefix.Length..].Trim() : null;
    }
}
