using System.Collections.Concurrent;
using System.Text.Json;

namespace HB.Chat;

/// <summary>외부 에이전트 하나 (MCP appsettings.json Orchestration:Agents, 켜진 것만). Reasoning은 설정의 추론 강도 — 없으면 null(공급자 기본).</summary>
public sealed record ModeAgent(string Name, string Provider, string Model, string? Reasoning);

/// <summary>모드의 단계 하나 (역할 @ 에이전트, 실패 때 대신 부를 에이전트).</summary>
public sealed record ModeStep(string Role, string Agent, string? Fallback);

/// <summary>modes.json의 모드 하나. Flow는 Sequential · Concurrent · Debate 글자 그대로.</summary>
public sealed record ModeInfo(string Name, string Description, string Flow, IReadOnlyList<ModeStep> Steps, bool OwnReview);

/// <summary>모드 · 에이전트 목록 한 시점. 읽지 못했으면 목록이 비고 Error에 이유.</summary>
public sealed record ModeList(IReadOnlyList<ModeInfo> Modes, IReadOnlyList<ModeAgent> Agents, string? Error)
{
    public static readonly ModeList Empty = new([], [], null);

    public ModeInfo? Find(string? name) => name is null ? null : Modes.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public ModeAgent? Agent(string? name) => name is null ? null : Agents.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 모델 선택창 (계획서 2026-10-29_모델-선택창 1번): 방 작업 폴더 .mcp.json의 MCP 서버 항목 → 서버 폴더(dll 옆)의 appsettings.json · modes.json을 읽는다.
/// modes.json 찾는 순서는 서버와 같다 (HB.Orchestration.Agents ModesPathResolver): 설정 ModesPath(서버 폴더 기준) → 항목 env의 HB_MODES_PATH
/// → 작업 폴더 hb-modes.json → 서버 폴더 modes.json. 읽기만 하고, 세 파일의 시각 · 크기가 같으면 캐시를 쓴다.
/// 이 앱은 HB.Orchestration.Agents를 참조하지 않는다 — 공급자 SDK까지 끌려오므로 필요한 칸만 직접 읽는다.
/// </summary>
public static class ModeCatalog
{
    public const string ProjectFileName = "hb-modes.json";
    public const string EnvVar = "HB_MODES_PATH";

    /// <summary>추론 강도 이름 (서버 AgentSettings.EffortNames와 같은 값 · 차례).</summary>
    public static readonly IReadOnlyList<string> Efforts = ["none", "low", "medium", "high", "xhigh"];

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly ConcurrentDictionary<string, (string Stamp, ModeList List)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ModeList Read(string workspaceDir, string server)
    {
        try
        {
            var (serverDir, env, error) = ServerDir(workspaceDir, server);
            if (serverDir is null) return ModeList.Empty with { Error = error };
            var settingsPath = Path.Combine(serverDir, "appsettings.json");
            var (configured, settingsError) = ConfiguredModesPath(settingsPath);
            if (settingsError is not null) return ModeList.Empty with { Error = settingsError };
            var (modesPath, modesError) = ResolveModes(configured, env, workspaceDir, serverDir);
            if (modesPath is null) return ModeList.Empty with { Error = modesError };

            var stamp = $"{Stamp(Path.Combine(workspaceDir, ".mcp.json"))}|{Stamp(settingsPath)}|{modesPath}|{Stamp(modesPath)}";
            var key = $"{Path.GetFullPath(workspaceDir)}|{server}";
            if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.List;
            var list = new ModeList(ReadModes(modesPath), ReadAgents(settingsPath), null);
            Cache[key] = (stamp, list);
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or KeyNotFoundException)
        {
            return ModeList.Empty with { Error = $"모드 목록을 읽지 못했습니다: {ex.Message}" };
        }
    }

    /// <summary>.mcp.json 항목의 서버 폴더: args 중 .dll 경로, 없으면 command가 .exe 경로일 때 그 폴더. 상대 경로는 작업 폴더 기준.</summary>
    internal static (string? Dir, string? Env, string? Error) ServerDir(string workspaceDir, string server)
    {
        var source = Path.Combine(workspaceDir, ".mcp.json");
        if (!File.Exists(source)) return (null, null, $"{source}가 없습니다.");
        using var doc = JsonDocument.Parse(File.ReadAllText(source), JsonOptions);
        if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object
            || !servers.TryGetProperty(server, out var entry) || entry.ValueKind != JsonValueKind.Object)
            return (null, null, $"{source}에 mcpServers.{server} 항목이 없습니다.");
        // 서버는 CLI가 물려준 이 앱의 환경 + 항목 env로 돈다 — 항목 값이 없거나 ${…} 틀이면 이 앱의 환경 값 (/self-review 785ebb5e97 C5).
        // 상대 경로는 서버 현재 폴더(= CLI 작업 폴더) 기준이라 작업 폴더로 푼다.
        var env = entry.TryGetProperty("env", out var envs) && envs.ValueKind == JsonValueKind.Object && Str(envs, EnvVar) is { Length: > 0 } e && !e.Contains("${")
            ? e
            : Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env) && !Path.IsPathRooted(env)) env = Path.Combine(workspaceDir, env);
        var candidates = new List<string>();
        if (entry.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            candidates.AddRange(args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!)
                .Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        if (Str(entry, "command") is { } command && command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && command.IndexOfAny(['/', '\\']) >= 0)
            candidates.Add(command);
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(c) ? c : Path.Combine(workspaceDir, c));
            if (File.Exists(full)) return (Path.GetDirectoryName(full), env, null);
        }
        return (null, env, $"mcpServers.{server}에서 서버 dll 경로를 찾지 못했습니다 (dotnet <dll> 모양만 읽습니다).");
    }

    private static (string? Path, string? Error) ConfiguredModesPath(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return (null, $"{settingsPath}가 없습니다.");
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath), JsonOptions);
        return (doc.RootElement.TryGetProperty("Orchestration", out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, "ModesPath") : null, null);
    }

    /// <summary>서버 ModesPathResolver와 같은 차례. 직접 지정한 경로(설정 · 환경 값)에 파일이 없으면 다음으로 넘어가지 않고 오류.</summary>
    internal static (string? Path, string? Error) ResolveModes(string? configured, string? env, string workspaceDir, string serverDir)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Specified(Path.IsPathRooted(configured) ? configured : Path.Combine(serverDir, configured), "MCP appsettings ModesPath");
        if (!string.IsNullOrWhiteSpace(env)) return Specified(env, EnvVar);
        var project = Path.Combine(workspaceDir, ProjectFileName);
        if (File.Exists(project)) return (Path.GetFullPath(project), null);
        var standard = Path.Combine(serverDir, "modes.json");
        return File.Exists(standard) ? (Path.GetFullPath(standard), null) : (null, "modes.json을 찾을 수 없습니다.");

        static (string?, string?) Specified(string path, string label)
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full) ? (full, null) : (null, $"{label}로 지정한 modes.json이 없습니다: {full}");
        }
    }

    private static List<ModeInfo> ReadModes(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
        var list = new List<ModeInfo>();
        if (!doc.RootElement.TryGetProperty("Modes", out var modes) || modes.ValueKind != JsonValueKind.Object) return list;
        foreach (var m in modes.EnumerateObject())
        {
            if (m.Value.ValueKind != JsonValueKind.Object) continue;
            var steps = new List<ModeStep>();
            if (m.Value.TryGetProperty("Steps", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var s in ss.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
                    if (Str(s, "Agent") is { Length: > 0 } agent)
                        steps.Add(new ModeStep(Str(s, "Role") ?? "", agent, Str(s, "FallbackAgent") is { Length: > 0 } f ? f : null));
            var own = m.Value.TryGetProperty("RequireOwnReview", out var r) && r.ValueKind == JsonValueKind.True;
            list.Add(new ModeInfo(m.Name, Str(m.Value, "Description") ?? "", Str(m.Value, "Flow") ?? "Sequential", steps, own));
        }
        return list;
    }

    private static List<ModeAgent> ReadAgents(string settingsPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath), JsonOptions);
        var list = new List<ModeAgent>();
        if (!doc.RootElement.TryGetProperty("Orchestration", out var o) || !o.TryGetProperty("Agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var a in agents.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
        {
            if (a.TryGetProperty("Enabled", out var on) && on.ValueKind == JsonValueKind.False) continue;
            if (Str(a, "Name") is not { Length: > 0 } name) continue;
            var effort = Str(a, "ReasoningEffort")?.Trim().ToLowerInvariant();
            list.Add(new ModeAgent(name, Str(a, "Provider") ?? "", Str(a, "Model") ?? "", effort is { Length: > 0 } && Efforts.Contains(effort) ? effort : null));
        }
        return list;
    }

    private static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
