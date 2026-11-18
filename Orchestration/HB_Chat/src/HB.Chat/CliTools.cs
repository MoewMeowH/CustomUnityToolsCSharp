using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// 설정 화면 "CLI 도구": codex · claude · Unity CLI의 설치 버전 · 최신 버전 확인과 올리기. 명령은 여기 정해 둔 것뿐이다 — 화면에서 받는 것은 도구 이름 하나.
/// 셸을 거치지 않는다: npm은 npm.cmd 대신 node.exe + npm-cli.js로 띄운다. 한 번에 하나만 돌고, 결과는 메모리에만 둔다.
/// </summary>
public sealed partial class CliTools(Func<CliTools.Paths> paths, CliTools.Runner? run = null)
{
    public const string Codex = "codex", Claude = "claude", Unity = "unity";

    public static readonly IReadOnlyList<(string Name, string Label)> Tools = [(Codex, "Codex CLI"), (Claude, "claude CLI"), (Unity, "Unity CLI")];

    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20), UpdateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>화면에 남기는 출력 줄 수 (끝에서부터).</summary>
    public const int OutputLines = 40;

    public const string BusyError = "다른 CLI 확인 · 올리기가 도는 중입니다.";

    /// <summary>실행 파일 자리. 못 찾은 것은 null. NpmRoot: npm 전역 설치 폴더(…\npm\node_modules) — codex가 그 아래일 때만 npm으로 올린다.</summary>
    public sealed record Paths(string? Codex, string? Claude, string? Unity, string? Node, string? NpmCli, string NpmRoot)
    {
        public static Paths From(ChatOptions o)
        {
            var claude = o.ResolveClaude();
            var node = FindOnPath("node.exe");
            var npmCli = node is null ? null : Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
            return new Paths(o.ResolveCodex(), File.Exists(claude) ? claude : null, o.ResolveUnityCli(), node, File.Exists(npmCli) ? npmCli : null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules"));
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

        public string? Exe(string tool) => tool switch { CliTools.Codex => Codex, CliTools.Claude => Claude, CliTools.Unity => Unity, _ => null };
    }

    public sealed record Command(string FileName, IReadOnlyList<string> Args);

    /// <summary>명령 하나를 돌린다 — ExitCode null은 띄우지 못했거나 시간 제한(Output에 까닭). Output은 표준 출력 · 오류를 합친 글.</summary>
    public delegate Task<(int? ExitCode, string Output)> Runner(Command command, TimeSpan timeout, CancellationToken ct);

    public sealed record Versions(string? Installed, string? Latest);

    public sealed record CheckResult(DateTimeOffset At, IReadOnlyDictionary<string, Versions> Tools);

    public sealed record UpdateResult(string Tool, DateTimeOffset At, string? Before, string? After, bool Ok, string Output);

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
        _ => null,
    };

    /// <summary>올리기 명령, 또는 올릴 수 없는 까닭.</summary>
    public static (Command? Command, string? WhyNot) UpdateCommand(string? tool, Paths p) => tool switch
    {
        Codex => p.Codex is null ? (null, "codex 실행 파일을 찾지 못했습니다.")
            : !IsUnder(p.Codex, Path.Combine(p.NpmRoot, "@openai", "codex")) ? (null, "npm 전역 설치가 아닙니다 (설정 Chat:CodexPath) — 설치한 방법으로 올리세요.")
            : Npm(p, "install", "-g", "@openai/codex@latest") is { } npm ? (npm, null)
            : (null, "node(npm)를 찾지 못했습니다."),
        Claude => p.Claude is { } claude ? (new Command(claude, ["update"]), null) : (null, "claude 실행 파일을 찾지 못했습니다."),
        Unity => p.Unity is { } unity ? (new Command(unity, ["self-update", "--yes", "--no-banner", "--non-interactive"]), null)
            : (null, "Unity CLI(unity.exe)를 찾지 못했습니다."),
        _ => (null, "알 수 없는 도구입니다."),
    };

    private static Command? Npm(Paths p, params string[] args) => p.Node is { } node && p.NpmCli is { } cli ? new Command(node, [cli, .. args]) : null;

    private static bool IsUnder(string path, string folder)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>최신 확인 출력에서 버전 — npm view는 버전 한 줄, unity는 JSON의 data.latestVersion. 못 읽으면 null.</summary>
    public static string? ParseLatest(string tool, string output)
    {
        if (tool != Unity) return CliVersions.Clean(output);
        try
        {
            var start = output.IndexOf('{');
            if (start < 0) return null;
            using var doc = JsonDocument.Parse(output[start..]);
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("latestVersion", out var latest) && latest.ValueKind == JsonValueKind.String
                ? CliVersions.Clean(latest.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- 확인 · 올리기 ----

    /// <summary>세 도구의 설치 버전 · 최신 버전을 묻는다 (최신은 네트워크). 이미 도는 중이면 <see cref="BusyError"/>.</summary>
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
    public async Task<string?> UpdateAsync(string? tool)
    {
        var p = paths();
        var (command, whyNot) = UpdateCommand(tool, p);
        if (command is null) return whyNot;
        if (!await _one.WaitAsync(0, CancellationToken.None)) return BusyError;
        try
        {
            var before = await InstalledAsync(tool!, p, CancellationToken.None);
            var (exit, output) = await _run(command, UpdateTimeout, CancellationToken.None);
            var after = await InstalledAsync(tool!, p, CancellationToken.None);
            LastUpdate = new UpdateResult(tool!, DateTimeOffset.Now, before, after, exit == 0, Tail(output));
            if (LastCheck is { } check && check.Tools.TryGetValue(tool!, out var old))
                LastCheck = check with { Tools = new Dictionary<string, Versions>(check.Tools) { [tool!] = old with { Installed = after } } };
            return null;
        }
        finally { _one.Release(); }
    }

    private async Task<string?> InstalledAsync(string tool, Paths p, CancellationToken ct) =>
        VersionCommand(tool, p) is { } command && await _run(command, CheckTimeout, ct) is { ExitCode: 0 } r ? CliVersions.Clean(r.Output) : null;

    private async Task<string?> LatestAsync(string tool, Paths p, CancellationToken ct) =>
        LatestCommand(tool, p) is { } command && await _run(command, CheckTimeout, ct) is { ExitCode: 0 } r ? ParseLatest(tool, r.Output) : null;

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
