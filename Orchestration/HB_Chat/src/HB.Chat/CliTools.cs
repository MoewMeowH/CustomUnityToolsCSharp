using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// 설정 화면 "CLI 도구": codex · claude · Unity CLI · agy의 설치 버전 · 최신 버전 확인과 올리기 · 없으면 설치. 명령은 여기 정해 둔 것뿐이다 — 화면에서 받는 것은 도구 이름 하나.
/// 셸을 거치지 않는다: npm은 npm.cmd 대신 node.exe + npm-cli.js로 띄운다. 설치 스크립트만 powershell.exe(절대 경로)로 공식 주소를 받아 돌린다
/// (사용자 결정 "공식 스크립트" — 깔린 자리 · 올리기 명령과 맞음). 한 번에 하나만 돌고, 결과는 메모리에만 둔다.
/// </summary>
/// <param name="fetch">agy 최신 판 manifest 읽기 (시험용, 비우면 HTTP GET).</param>
public sealed partial class CliTools(Func<CliTools.Paths> paths, CliTools.Runner? run = null, Func<string, CancellationToken, Task<string?>>? fetch = null)
{
    public const string Codex = "codex", Claude = "claude", Unity = "unity", Agy = "agy";

    public static readonly IReadOnlyList<(string Name, string Label)> Tools =
        [(Codex, "Codex CLI"), (Claude, "claude CLI"), (Unity, "Unity CLI"), (Agy, "Antigravity CLI (agy)")];

    /// <summary>
    /// agy 최신 판 — 공식 설치 스크립트(antigravity.google/cli/install.ps1)가 읽는 manifest의 version. agy는 npm 패키지가 아니다.
    /// </summary>
    public static string AgyManifestUrl(Architecture arch) =>
        $"https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/{(arch == Architecture.Arm64 ? "windows_arm64" : "windows_amd64")}.json";

    /// <summary>공식 설치 스크립트 주소 (codex는 npm).</summary>
    public const string ClaudeInstallUrl = "https://claude.ai/install.ps1", UnityInstallUrl = "https://unity.com/install.ps1",
        AgyInstallUrl = "https://antigravity.google/cli/install.ps1";

    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20), UpdateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>화면에 남기는 출력 줄 수 (끝에서부터).</summary>
    public const int OutputLines = 40;

    public const string BusyError = "다른 CLI 확인 · 올리기가 도는 중입니다.";

    public const string ChangedError = "화면을 연 뒤 도구가 설치되거나 사라졌습니다 — 화면을 새로 읽고 다시 누르세요.";

    /// <summary>실행 파일 자리. 못 찾은 것은 null. NpmRoot: npm 전역 설치 폴더(…\npm\node_modules) — codex가 그 아래일 때만 npm으로 올린다.</summary>
    /// <param name="PowerShell">Windows PowerShell 절대 경로 — 설치 스크립트용 (PATH의 다른 powershell을 쓰지 않게).</param>
    /// <param name="BadSetting">설정 경로(Chat:…Path)를 적었는데 그 파일이 없는 도구 → 설정 키. 설치하지 않는다 — 기본 자리에 깔아도 앱은 설정 경로만 본다.</param>
    public sealed record Paths(string? Codex, string? Claude, string? Unity, string? Node, string? NpmCli, string NpmRoot, string? Agy = null, string? PowerShell = null,
        IReadOnlyDictionary<string, string>? BadSetting = null)
    {
        public static Paths From(ChatOptions o)
        {
            var claude = o.ResolveClaude();
            var codex = o.ResolveCodex();
            var node = FindOnPath("node.exe");
            var npmCli = node is null ? null : Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var bad = new Dictionary<string, string>();
            foreach (var (tool, set, key) in new[]
                     {
                         (CliTools.Codex, o.CodexPath, "Chat:CodexPath"), (CliTools.Claude, o.ClaudePath, "Chat:ClaudePath"),
                         (CliTools.Unity, o.UnityCliPath, "Chat:UnityCliPath"), (CliTools.Agy, o.AgyPath, "Chat:AgyPath"),
                     })
                if (!string.IsNullOrWhiteSpace(set) && !File.Exists(set.Trim())) bad[tool] = key;
            return new Paths(bad.ContainsKey(CliTools.Codex) ? null : codex, File.Exists(claude) ? claude : null, o.ResolveUnityCli(), node,
                File.Exists(npmCli) ? npmCli : null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"),
                o.ResolveAgy(), File.Exists(powerShell) ? powerShell : null, bad);
        }

        private static string? FindOnPath(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public string? Exe(string tool) => tool switch
        {
            CliTools.Codex => Codex, CliTools.Claude => Claude, CliTools.Unity => Unity, CliTools.Agy => Agy, _ => null,
        };
    }

    public sealed record Command(string FileName, IReadOnlyList<string> Args)
    {
        /// <summary>설치 전 확인 창에 보일 한 줄.</summary>
        public string Display => string.Join(' ', Args.Prepend(FileName).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    /// <summary>명령 하나를 돌린다 — ExitCode null은 띄우지 못했거나 시간 제한(Output에 까닭). Output은 표준 출력 · 오류를 합친 글.</summary>
    public delegate Task<(int? ExitCode, string Output)> Runner(Command command, TimeSpan timeout, CancellationToken ct);

    public sealed record Versions(string? Installed, string? Latest);

    public sealed record CheckResult(DateTimeOffset At, IReadOnlyDictionary<string, Versions> Tools);

    /// <param name="Install">없어서 설치했다 (올리기가 아니라).</param>
    public sealed record UpdateResult(string Tool, DateTimeOffset At, string? Before, string? After, bool Ok, string Output, bool Install = false);

    private readonly Runner _run = run ?? RunProcessAsync;
    private readonly SemaphoreSlim _one = new(1, 1);

    public bool Busy => _one.CurrentCount == 0;
    public CheckResult? LastCheck { get; private set; }
    public UpdateResult? LastUpdate { get; private set; }

    public Paths CurrentPaths => paths();

    // ---- 명령 (정해 둔 것만) ----

    public static Command? VersionCommand(string tool, Paths p) => p.Exe(tool) is { } exe ? new Command(exe, ["--version"]) : null;

    public static Command? LatestCommand(string tool, Paths p) => tool switch
    {
        Codex => Npm(p, "view", "@openai/codex", "version"),
        Claude => Npm(p, "view", "@anthropic-ai/claude-code", "version"),
        Unity => p.Unity is { } unity ? new Command(unity, ["self-update", "--check", "--json", "--no-banner", "--non-interactive"]) : null,
        _ => null, // agy는 명령이 아니라 manifest (LatestAsync)
    };

    /// <summary>실행 파일이 없으면 설치, 있으면 올리기 — 화면은 같은 단추 자리 (사용자 결정 "모든 도구에 설치"). 설정 경로의 파일이 없으면 설치하지 않는다.</summary>
    public static bool Installs(string? tool, Paths p) =>
        tool is not null && Tools.Any(t => t.Name == tool) && p.Exe(tool) is null && p.BadSetting?.ContainsKey(tool) != true;

    /// <summary>올리기(없으면 설치) 명령, 또는 할 수 없는 까닭.</summary>
    public static (Command? Command, string? WhyNot) UpdateCommand(string? tool, Paths p) =>
        tool is not null && p.BadSetting?.GetValueOrDefault(tool) is { } key ? (null, $"설정 {key}의 파일이 없습니다 — 경로를 고치거나 비우세요.")
        : Installs(tool, p) ? InstallCommand(tool!, p)
        : tool switch
        {
            Codex => !IsUnder(p.Codex!, Path.Combine(p.NpmRoot, "@openai", "codex")) ? (null, "npm 전역 설치가 아닙니다 (설정 Chat:CodexPath) — 설치한 방법으로 올리세요.")
                : Npm(p, "install", "-g", "@openai/codex@latest") is { } npm ? (npm, null)
                : (null, "node(npm)를 찾지 못했습니다."),
            Claude => (new Command(p.Claude!, ["update"]), null),
            Unity => (new Command(p.Unity!, ["self-update", "--yes", "--no-banner", "--non-interactive"]), null),
            Agy => (new Command(p.Agy!, ["update"]), null), // update에는 옵션이 없다 (실측) — 입력은 닫고 돌린다
            _ => (null, "알 수 없는 도구입니다."),
        };

    /// <summary>
    /// 설치 명령 — codex는 npm 전역(앱이 찾는 %APPDATA%\npm에 — 다른 prefix면 설치 뒤 못 찾는다), 나머지는 공식 설치 스크립트(기본 자리 = 앱이 찾는 자리).
    /// 믿음의 근거는 HTTPS와 공식 도메인뿐이다 — 스크립트는 검증 없이 돌고, 스크립트가 하는 해시 대조는 같은 서버에서 온 값이라 전송 깨짐만 잡는다 (서명 검사 없음).
    /// </summary>
    public static (Command? Command, string? WhyNot) InstallCommand(string tool, Paths p) => tool switch
    {
        Codex => Npm(p, "install", "-g", "--prefix", Path.GetDirectoryName(p.NpmRoot)!, "@openai/codex@latest") is { } npm ? (npm, null)
            : (null, "Node.js(npm)를 찾지 못했습니다 — Node.js를 먼저 설치하세요."),
        Claude => Script(p, ClaudeInstallUrl),
        Unity => Script(p, UnityInstallUrl),
        Agy => Script(p, AgyInstallUrl),
        _ => (null, "알 수 없는 도구입니다."),
    };

    /// <summary>설치 확인 창에 덧붙일 말 (명령만으로 안 보이는 일).</summary>
    public static string? InstallNote(string tool) => tool == Unity
        ? "Unity 설치 스크립트는 AI 에이전트용 스킬 · 플러그인도 함께 깝니다 (공식 설치와 같음)."
        : null;

    // 진행률 표시를 끈다 — Windows PowerShell 5.1은 진행률 때문에 내려받기가 크게 느려져 5분 제한에 걸릴 수 있다
    private static (Command?, string?) Script(Paths p, string url) => p.PowerShell is { } ps
        ? (new Command(ps, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"$ProgressPreference='SilentlyContinue'; irm {url} | iex"]), null)
        : (null, "Windows PowerShell(powershell.exe)을 찾지 못했습니다.");

    private static Command? Npm(Paths p, params string[] args) => p.Node is { } node && p.NpmCli is { } cli ? new Command(node, [cli, .. args]) : null;

    private static bool IsUnder(string path, string folder)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>최신 확인 출력에서 버전 — npm view는 버전 한 줄, unity는 JSON의 data.latestVersion, agy는 manifest JSON의 version. 못 읽으면 null.</summary>
    public static string? ParseLatest(string tool, string output)
    {
        if (tool is not (Unity or Agy)) return CliVersions.Clean(output);
        try
        {
            var start = output.IndexOf('{');
            if (start < 0) return null;
            using var doc = JsonDocument.Parse(output[start..]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (tool == Agy)
                return root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? CliVersions.Clean(version.GetString()) : null;
            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("latestVersion", out var latest) && latest.ValueKind == JsonValueKind.String
                ? CliVersions.Clean(latest.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- 확인 · 올리기 ----

    /// <summary>네 도구의 설치 버전 · 최신 버전을 묻는다 (최신은 네트워크). 이미 도는 중이면 <see cref="BusyError"/>.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, CancellationToken.None)) return BusyError;
        try
        {
            var p = paths();
            var asked = Tools.Select(async t => (t.Name, Versions: new Versions(await InstalledAsync(t.Name, p, ct), await LatestAsync(t.Name, p, ct)))).ToList();
            var done = await Task.WhenAll(asked);
            if (ct.IsCancellationRequested) return null; // 화면을 떠나 끊긴 확인으로 앞 결과를 덮지 않는다
            LastCheck = new CheckResult(DateTimeOffset.Now, done.ToDictionary(d => d.Name, d => d.Versions));
            return null;
        }
        finally { _one.Release(); }
    }

    /// <summary>
    /// 한 도구를 올린다. 시작하지 못하면 까닭(알 수 없는 도구 · 올릴 수 없음 · 도는 중)을 돌려주고, 돌렸으면 null — 결과는 <see cref="LastUpdate"/>.
    /// 화면을 떠나도 끊지 않는다 (설치 도중에 끝내면 반쯤 깔린 채 남는다) — 시간 제한만.
    /// </summary>
    /// <param name="install">화면이 보인 작업이 설치였는가 — 지금 판정과 다르면 돌리지 않는다 (확인 창 없이 설치 스크립트가 돌지 않게).</param>
    public async Task<string?> UpdateAsync(string? tool, bool install = false)
    {
        var p = paths();
        if (Installs(tool, p) != install) return ChangedError;
        var (command, whyNot) = UpdateCommand(tool, p);
        if (command is null) return whyNot;
        if (!await _one.WaitAsync(0, CancellationToken.None)) return BusyError;
        try
        {
            var before = await InstalledAsync(tool!, p, CancellationToken.None);
            var (exit, output) = await _run(command, UpdateTimeout, CancellationToken.None);
            var after = await InstalledAsync(tool!, paths(), CancellationToken.None); // 설치했으면 이제 자리가 생겼다 — 다시 찾는다
            // 설치는 실행 파일이 생겼는지로 본다 — 스크립트 마지막 단계만 실패해도 종료 코드가 0이 아닐 수 있다
            LastUpdate = new UpdateResult(tool!, DateTimeOffset.Now, before, after, install ? after is not null : exit == 0, Tail(output), install);
            if (LastCheck is { } check && check.Tools.TryGetValue(tool!, out var old))
                LastCheck = check with { Tools = new Dictionary<string, Versions>(check.Tools) { [tool!] = old with { Installed = after } } };
            return null;
        }
        finally { _one.Release(); }
    }

    private async Task<string?> InstalledAsync(string tool, Paths p, CancellationToken ct) =>
        VersionCommand(tool, p) is { } command && await _run(command, CheckTimeout, ct) is { ExitCode: 0 } r ? CliVersions.Clean(r.Output) : null;

    private async Task<string?> LatestAsync(string tool, Paths p, CancellationToken ct)
    {
        if (tool == Agy)
            return await (fetch ?? FetchAsync)(AgyManifestUrl(RuntimeInformation.OSArchitecture), ct) is { } manifest ? ParseLatest(Agy, manifest) : null;
        return LatestCommand(tool, p) is { } command && await _run(command, CheckTimeout, ct) is { ExitCode: 0 } r ? ParseLatest(tool, r.Output) : null;
    }

    private static readonly HttpClient Http = new() { Timeout = CheckTimeout };

    /// <summary>manifest 읽기 — 못 읽으면 null (최신 칸이 "—"일 뿐).</summary>
    private static async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        try { return await Http.GetStringAsync(url, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>끝 <see cref="OutputLines"/>줄, 색 · 커서 제어 문자는 뺀다.</summary>
    public static string Tail(string output)
    {
        var lines = Ansi().Replace(output, "").Split('\n').Select(l => l.TrimEnd('\r', ' ')).Where(l => l.Length > 0).ToList();
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - OutputLines)));
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    /// <summary>프로세스가 끝난 뒤 남은 출력을 기다리는 시간.</summary>
    internal static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(3);

    internal static async Task<(int? ExitCode, string Output)> RunProcessAsync(Command command, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in command.Args) info.ArgumentList.Add(arg);
        info.Environment["NO_COLOR"] = "1";
        Process? p = null;
        try
        {
            p = Process.Start(info);
            if (p is null) return (null, "프로세스를 띄우지 못했습니다.");
            p.StandardInput.Close(); // 입력을 기다리는 물음에 멈추지 않게
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            var both = Task.WhenAll(stdout, stderr);
            _ = both.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // 프로세스를 닫아 끊긴 읽기
            await p.WaitForExitAsync(cts.Token);
            // 끝난 뒤 출력은 잠깐만 기다린다 — 갱신 명령이 남긴 자식이 파이프를 물고 있으면 읽기가 끝나지 않는다
            await Task.WhenAny(both, Task.Delay(OutputGrace, CancellationToken.None));
            return (p.ExitCode, (stdout.IsCompletedSuccessfully ? stdout.Result : "") + (stderr.IsCompletedSuccessfully ? stderr.Result : ""));
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            return (null, ct.IsCancellationRequested ? "취소했습니다." : $"시간 제한({timeout.TotalSeconds:0}초) 안에 끝나지 않아 끝냈습니다.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Kill(p);
            return (null, $"실행하지 못했습니다: {ex.Message}");
        }
        finally { p?.Dispose(); }
    }

    private static void Kill(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
