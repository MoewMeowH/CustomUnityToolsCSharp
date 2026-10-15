using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HB.Orchestration.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

public sealed record AgentInfo(string Name, string Provider, string Model, int? MaxOutputTokens, bool Enabled);

/// <summary>디스크에서 읽은 modes.json 한 시점의 상태.</summary>
public sealed class ModesSnapshot
{
    public required string ModesPath { get; init; }
    public required string? McpSettingsPath { get; init; }
    public required DateTime LastWriteLocal { get; init; }

    /// <summary>동시 수정 감지용. 읽은 뒤 파일이 바뀌었으면 저장을 거부한다.</summary>
    public required string Stamp { get; init; }

    public required JsonObject Root { get; init; }
    public required ModesFile Typed { get; init; }
    public required List<AgentInfo> Agents { get; init; }
    public required bool HasComments { get; init; }
}

/// <summary>
/// 모드 하나에 대해 화면에서 바꾼 값.
/// Settings: 키가 있으면 적용 (빈 값이면 항목 삭제 = 기본값 사용), 키가 없으면 건드리지 않는다.
/// </summary>
public sealed record ModeEdit(string Mode, IReadOnlyList<string> StepAgents, IReadOnlyDictionary<string, string> Settings);

public sealed record SaveResult(List<string> Errors, List<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

public sealed class ModesConfigException(string message) : Exception(message);

/// <summary>
/// 대시보드의 모드 화면용: MCP 서버의 modes.json을 읽고, 검증하고, 안전하게 저장한다.
/// 알지 못하는 필드(Roles 프롬프트, Sees, Description 등)를 잃지 않도록 JsonNode를 직접 고친다.
/// </summary>
public sealed class ModesConfigService(IOptions<DashboardOptions> options)
{
    private const int BackupKeep = 10;

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions TypedOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 한글 프롬프트가 \uXXXX로 바뀌지 않도록
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 경로 ────────────────────────────────────────────────────────────────

    public string? McpConfigDir =>
        string.IsNullOrWhiteSpace(options.Value.McpConfigDir) ? null : Path.GetFullPath(options.Value.McpConfigDir);

    private (string ModesPath, string? SettingsPath) ResolvePaths()
    {
        var dir = McpConfigDir;
        string? settings = dir is null ? null : Path.Combine(dir, "appsettings.json");
        if (settings is not null && !File.Exists(settings)) settings = null;

        var modes = options.Value.ModesPath;

        // MCP 서버가 실제로 쓰는 경로와 맞춘다: appsettings의 Orchestration:ModesPath가 있으면 그쪽.
        if (string.IsNullOrWhiteSpace(modes) && settings is not null)
        {
            var configured = ReadMcpOptions(settings)?.ModesPath;
            if (!string.IsNullOrWhiteSpace(configured))
                modes = Path.IsPathRooted(configured) ? configured : Path.Combine(dir!, configured);
        }

        if (string.IsNullOrWhiteSpace(modes) && dir is not null)
            modes = Path.Combine(dir, "modes.json");

        if (string.IsNullOrWhiteSpace(modes))
            throw new ModesConfigException(
                "편집할 modes.json 위치를 모릅니다. 대시보드 appsettings.json의 Dashboard:McpConfigDir에 MCP publish 폴더를 지정하세요.");

        modes = Path.GetFullPath(modes);
        if (!File.Exists(modes))
            throw new ModesConfigException($"modes.json이 없습니다: {modes}");

        return (modes, settings);
    }

    private static OrchestrationOptions? ReadMcpOptions(string settingsPath)
    {
        // Microsoft.Extensions.Configuration의 JSON 공급자는 주석을 허용하므로 MCP appsettings를 그대로 읽을 수 있다.
        var config = new ConfigurationBuilder()
            .AddJsonFile(settingsPath, optional: true, reloadOnChange: false)
            .Build();
        return config.GetSection("Orchestration").Get<OrchestrationOptions>();
    }

    // ── 읽기 ────────────────────────────────────────────────────────────────

    public ModesSnapshot Load()
    {
        var (modesPath, settingsPath) = ResolvePaths();
        var text = File.ReadAllText(modesPath);

        JsonObject root;
        ModesFile typed;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, documentOptions: DocOptions) as JsonObject
                   ?? throw new ModesConfigException("modes.json 최상위가 객체가 아닙니다.");
            typed = JsonSerializer.Deserialize<ModesFile>(text, TypedOptions) ?? new ModesFile();
        }
        catch (JsonException ex)
        {
            throw new ModesConfigException($"modes.json 파싱 실패 (줄 {(ex.LineNumber ?? 0) + 1}): {ex.Message}");
        }

        var agents = new List<AgentInfo>();
        if (settingsPath is not null)
        {
            foreach (var a in ReadMcpOptions(settingsPath)?.Agents ?? new List<AgentOptions>())
                agents.Add(new AgentInfo(a.Name, a.Provider, a.Model, a.MaxOutputTokens, a.Enabled));
        }

        var info = new FileInfo(modesPath);
        return new ModesSnapshot
        {
            ModesPath = modesPath,
            McpSettingsPath = settingsPath,
            LastWriteLocal = info.LastWriteTime,
            Stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}",
            Root = root,
            Typed = typed,
            Agents = agents,
            HasComments = ContainsComments(text),
        };
    }

    private static bool ContainsComments(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });
        try
        {
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.Comment) return true;
        }
        catch (JsonException)
        {
            // 파싱 오류는 Load에서 따로 보고한다.
        }
        return false;
    }

    // ── 저장 ────────────────────────────────────────────────────────────────

    public SaveResult Save(string expectedStamp, IReadOnlyList<ModeEdit> edits)
    {
        var snap = Load();
        if (snap.Stamp != expectedStamp)
            return new SaveResult(["이 화면을 연 뒤에 modes.json이 다른 곳에서 바뀌었습니다. '다시 불러오기' 후 다시 수정하세요."], []);

        var modesObj = Child(snap.Root, "Modes") as JsonObject
                       ?? throw new ModesConfigException("modes.json에 Modes가 없습니다.");

        var inputErrors = new List<string>();
        foreach (var edit in edits)
        {
            if (modesObj[edit.Mode] is not JsonObject modeObj) continue;

            if (Child(modeObj, "Steps") is JsonArray steps)
            {
                for (var i = 0; i < steps.Count && i < edit.StepAgents.Count; i++)
                {
                    if (steps[i] is JsonObject step && !string.IsNullOrWhiteSpace(edit.StepAgents[i]))
                        step[KeyOf(step, "Agent")] = JsonValue.Create(edit.StepAgents[i]);
                }
            }

            foreach (var (key, raw) in edit.Settings)
            {
                if (!EditableSettings.Contains(key)) continue;
                var k = KeyOf(modeObj, key);
                if (string.IsNullOrWhiteSpace(raw)) modeObj.Remove(k);
                else if (key == "Return") modeObj[k] = JsonValue.Create(raw.Trim());
                else if (int.TryParse(raw, out var n)) modeObj[k] = JsonValue.Create(n);
                else inputErrors.Add($"[{edit.Mode}] {key} 값 '{raw}'은(는) 숫자가 아닙니다.");
            }
        }

        if (inputErrors.Count > 0) return new SaveResult(inputErrors, []);

        var json = snap.Root.ToJsonString(WriteOptions) + Environment.NewLine;
        var result = Validate(json, snap.Agents);
        if (!result.Ok) return result;

        WriteSafely(snap.ModesPath, json);
        return result;
    }

    /// <summary>MCP 서버가 실행 시 거부할 설정을 저장 전에 걸러낸다.</summary>
    public static SaveResult Validate(string json, IReadOnlyList<AgentInfo> agents)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        ModesFile file;
        try
        {
            file = JsonSerializer.Deserialize<ModesFile>(json, TypedOptions) ?? new ModesFile();
        }
        catch (JsonException ex)
        {
            return new SaveResult([$"JSON 형식 오류: {ex.Message}"], []);
        }

        var known = agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var roles = new HashSet<string>(file.Roles.Keys, StringComparer.OrdinalIgnoreCase);
        roles.UnionWith(Prompts.BuiltInRoles.Keys);

        foreach (var (name, mode) in file.Modes)
        {
            if (mode.Steps.Count == 0)
                errors.Add($"[{name}] Steps가 비어 있습니다.");

            if (mode.Flow == ModeFlow.Debate && mode.Steps.Count is not (2 or 3))
                errors.Add($"[{name}] Debate는 Steps가 2개 또는 3개여야 합니다.");

            if (mode.MaxRounds is { } r && (r < 1 || r > ModeDefinition.MaxRoundsHardCap))
                errors.Add($"[{name}] MaxRounds는 1~{ModeDefinition.MaxRoundsHardCap} 사이여야 합니다.");

            if (mode.MaxRunsPerSession is < 0)
                errors.Add($"[{name}] MaxRunsPerSession은 0 이상이어야 합니다 (0 = 무제한).");

            foreach (var step in mode.Steps)
            {
                if (!roles.Contains(step.Role))
                    errors.Add($"[{name}] 역할 '{step.Role}'이(가) 정의되지 않았습니다.");

                if (agents.Count == 0) continue; // MCP appsettings를 못 읽었으면 에이전트 검사는 건너뜀

                if (!known.TryGetValue(step.Agent, out var agent))
                    errors.Add($"[{name}] 에이전트 '{step.Agent}'이(가) appsettings.json에 없습니다.");
                else if (!agent.Enabled)
                    warnings.Add($"[{name}] 에이전트 '{step.Agent}'은(는) 꺼져 있어 이 모드는 실행 시 거부됩니다.");
            }
        }

        if (agents.Count == 0)
            warnings.Add("MCP appsettings.json을 읽지 못해 에이전트 이름 검사를 건너뛰었습니다.");

        return new SaveResult(errors, warnings);
    }

    /// <summary>백업을 남기고, 임시 파일에 쓴 뒤 한 번에 교체한다 (MCP 서버가 읽는 도중이어도 반쯤 쓴 파일을 보지 않게).</summary>
    private static void WriteSafely(string path, string json)
    {
        var dir = Path.GetDirectoryName(path)!;
        var backupDir = Path.Combine(dir, "modes-backup");
        Directory.CreateDirectory(backupDir);
        File.Copy(path, Path.Combine(backupDir, $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);

        foreach (var old in new DirectoryInfo(backupDir).GetFiles("*.json")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupKeep))
        {
            try { old.Delete(); } catch (IOException) { /* 다음 저장 때 다시 시도 */ }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // MCP 서버가 마침 파일을 읽고 있으면 교체가 잠깐 실패할 수 있어 짧게 재시도한다.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    // ── JsonNode 도우미 ──────────────────────────────────────────────────────

    private static string KeyOf(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    private static JsonNode? Child(JsonObject obj, string name) => obj[KeyOf(obj, name)];

    /// <summary>화면에서 바꿀 수 있는 모드 단위 설정.</summary>
    public static readonly IReadOnlySet<string> EditableSettings =
        new HashSet<string> { "MaxRunsPerSession", "MaxRounds", "Return" };

    /// <summary>모드 항목의 원본 값 (없으면 null). 화면에 "파일에 실제로 적힌 값"을 보여줄 때 쓴다.</summary>
    public static string? GetRaw(JsonObject root, string mode, string key)
    {
        if (Child(root, "Modes") is not JsonObject modes || modes[mode] is not JsonObject modeObj) return null;
        return Child(modeObj, key)?.ToString();
    }
}
