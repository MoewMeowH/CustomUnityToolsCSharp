using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HB.Orchestration.Agents.Providers;

namespace HB.Orchestration.Agents;

/// <summary>고정된 버전 명령만 관측한다. 인증·격리·도구 제공 여부를 검증하지 않는다.</summary>
internal static class ReviewEnvironmentProbe
{
    internal sealed record Observation(string Tool, string Status, string? Version, DateTimeOffset ObservedAtUtc);
    private sealed class OutputLimitException : IOException { }

    internal static string? VersionToken(string output)
    {
        // 경로·계정·에러 원문을 내보내지 않고 제한된 버전 토큰만 받는다.
        try
        {
            var match = Regex.Match(output, @"(?<![\w.])\d{1,5}\.\d{1,5}\.\d{1,5}(?:[-+.][a-zA-Z0-9.-]{1,40})?(?![\w.])",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return match.Success ? match.Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    internal static Observation Measure(string tool, string root, string? searchPath = null,
        Func<string, string, (string Status, string? Version)>? run = null)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var path = ReviewExecutablePolicy.SearchPath(root, searchPath ?? Environment.GetEnvironmentVariable("PATH"));
            var executable = tool switch
            {
                "claude" => ClaudeCliProvider.ResolveExecutable("environment-probe", null, path),
                "codex" => CodexCliProvider.ResolveExecutable("environment-probe", null, path),
                "git" => ReviewExecutablePolicy.Git(root, path) ?? throw new IOException(),
                _ => throw new ArgumentException("지원하지 않는 관측입니다.")
            };
            if (!ReviewExecutablePolicy.Allowed(executable, root)) return new(tool, "unavailable", null, now);
            var result = (run ?? RunVersion)(executable, path);
            return new(tool, result.Status, result.Version, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ProviderConfigurationException)
        { return new(tool, "unavailable", null, now); }
    }

    internal static (string Status, string? Version) RunVersion(string executable, string path)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var kept = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }
                .Select(k => (Key: k, Value: Environment.GetEnvironmentVariable(k))).ToArray();
            start.Environment.Clear();
            foreach (var pair in kept) if (pair.Value is not null) start.Environment[pair.Key] = pair.Value;
            start.Environment["PATH"] = path;
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start) ?? throw new IOException();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            void Stop()
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            using var registration = timeout.Token.Register(Stop);
            try
            {
                process.StandardInput.Close();
                async Task<string> Read(StreamReader reader)
                {
                    var buffer = new char[4097]; var used = 0;
                    while (used < buffer.Length)
                    {
                        var count = await reader.ReadAsync(buffer.AsMemory(used), timeout.Token);
                        if (count == 0) return new string(buffer, 0, used);
                        used += count;
                    }
                    Stop(); throw new OutputLimitException();
                }
                var output = Read(process.StandardOutput); var error = Read(process.StandardError);
                Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).GetAwaiter().GetResult();
                if (process.ExitCode != 0) return ("failed", null);
                var version = VersionToken(output.GetAwaiter().GetResult());
                return (version is null ? "unrecognized" : "observed", version);
            }
            finally { Stop(); }
        }
        catch (OperationCanceledException) { return ("timeout", null); }
        catch (OutputLimitException) { return ("output_limit", null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ProviderConfigurationException)
        { return ("unavailable", null); }
    }
}

public sealed partial class ReviewEvidence
{
    private readonly Dictionary<string, ReviewEnvironmentProbe.Observation> _environment = new(StringComparer.Ordinal);
    public string EnvironmentInfo(string tool) => Execute(() =>
    {
        if (tool is not ("git" or "claude" or "codex")) throw new ArgumentException("git·claude·codex 중 하나만 지정합니다.");
        if (!_original) throw new ArgumentException("버전 관측은 원본 자료 루트가 확인된 모드에서만 제공합니다.");
        var cached = _environment.TryGetValue(tool, out var observed);
        observed ??= ReviewEnvironmentProbe.Measure(tool, _root);
        _environment[tool] = observed;
        return new { scope = "version_only", cached, observation = observed,
            os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            runtime = Environment.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            behaviorVerified = false };
    });
}
