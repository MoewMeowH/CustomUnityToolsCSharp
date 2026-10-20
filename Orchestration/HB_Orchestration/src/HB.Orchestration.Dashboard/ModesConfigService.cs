using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HB.Orchestration.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

public sealed record AgentInfo(string Name, string Provider, string Model, int? MaxOutputTokens, bool Enabled, int? TimeoutSeconds = null, bool Vision = true);

/// <summary>디스크에서 읽은 MCP 설정(modes.json + appsettings.json) 한 시점의 상태.</summary>
public sealed class ModesSnapshot
{
    public required string ModesPath { get; init; }
    public required DateTime LastWriteLocal { get; init; }

    /// <summary>modes.json 동시 수정 감지용. 읽은 뒤 파일이 바뀌었으면 저장을 거부한다.</summary>
    public required string Stamp { get; init; }

    public required JsonObject Root { get; init; }
    public required ModesFile Typed { get; init; }
    public required bool HasComments { get; init; }

    // ── MCP appsettings.json ──
    public required string? McpSettingsPath { get; init; }
    public required string SettingsStamp { get; init; }
    public required JsonObject? SettingsRoot { get; init; }
    public required bool SettingsHasComments { get; init; }
    public required List<AgentInfo> Agents { get; init; }
    public required List<string> ProviderNames { get; init; }

    /// <summary>제공사별 TimeoutSeconds. 에이전트 칸이 비었을 때 실제로 적용되는 값을 안내하는 데 쓴다.</summary>
    public Dictionary<string, int?> ProviderTimeouts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>제공사별 Type (openai-compatible · openai-responses …). 웹검색 단계가 검색 가능한 에이전트인지 표시할 때 쓴다.</summary>
    public Dictionary<string, string> ProviderTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>제공사별 ImageDetail (run_mode images의 detail). 비어 있으면 제공사 기본값. 표시 전용.</summary>
    public Dictionary<string, string?> ProviderImageDetails { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Orchestration:Images (run_mode images 상한). 표시 전용.</summary>
    public ImageOptions Images { get; init; } = new();

    /// <summary>Orchestration:Pricing (모델 단가 USD + 원화 환율).</summary>
    public required PricingOptions Pricing { get; init; }

    /// <summary>modes.json이 참조하지만 appsettings에 없는 에이전트 이름.</summary>
    public IEnumerable<string> MissingAgents =>
        Typed.Modes.Values.SelectMany(m => m.Steps).Select(s => s.Agent)
            .Where(a => !Agents.Any(x => string.Equals(x.Name, a, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 모드 하나에 대해 화면에서 바꾼 값.
/// Settings: 키가 있으면 적용 (빈 값이면 항목 삭제 = 기본값 사용), 키가 없으면 건드리지 않는다.
/// </summary>
public sealed record ModeEdit(string Mode, IReadOnlyList<string> StepAgents, IReadOnlyDictionary<string, string> Settings);

/// <summary>에이전트 표의 한 줄. OriginalName이 비어 있으면 새 에이전트.</summary>
public sealed record AgentEdit(string? OriginalName, string Name, string Provider, string Model, string MaxOutputTokens, bool Enabled, bool Delete, string TimeoutSeconds = "");

/// <summary>모델 단가 표의 한 줄 (USD / 1M 토큰). 셋 다 비우면 그 모델의 단가를 지운다.</summary>
public sealed record PriceEdit(string Model, string InputPrice, string CachedInputPrice, string OutputPrice);

public sealed record SaveResult(List<string> Errors, List<string> Warnings)
{
    public bool Ok => Errors.Count == 0;

    /// <summary>저장 결과 안내 (예: 이름 변경을 모드에 반영한 개수).</summary>
    public List<string> Notes { get; init; } = [];

    public static SaveResult Fail(string error) => new([error], []);
}

public sealed class ModesConfigException(string message) : Exception(message);

/// <summary>
/// 대시보드 설정 화면용: MCP 서버의 modes.json · appsettings.json을 읽고, 검증하고, 안전하게 저장한다.
/// 알지 못하는 필드(Roles 프롬프트, Sees, Providers 등)를 잃지 않도록 JsonNode를 직접 고친다.
/// MCP 서버는 두 파일이 바뀌면 다음 호출 때 스스로 다시 읽으므로 재시작이 필요 없다.
/// </summary>
public sealed partial class ModesConfigService(IOptions<DashboardOptions> options)
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

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex AgentNameRegex();

    // ── 경로 ────────────────────────────────────────────────────────────────

    public string? McpConfigDir =>
        string.IsNullOrWhiteSpace(options.Value.McpConfigDir) ? null : Path.GetFullPath(options.Value.McpConfigDir);

    /// <summary>publish 폴더의 서버 dll 수정 시각. 세션의 서버가 최신 빌드인지 비교할 때 쓴다.</summary>
    public DateTimeOffset? CurrentServerBuild
    {
        get
        {
            var dir = McpConfigDir;
            if (dir is null) return null;
            var dll = Path.Combine(dir, "HB.Orchestration.Mcp.dll");
            return File.Exists(dll) ? new DateTimeOffset(File.GetLastWriteTime(dll)) : null;
        }
    }

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

    /// <summary>
    /// MCP appsettings의 Orchestration 섹션. 첨부 보기의 전송 금지 패턴·이미지 상한에 쓴다 (modes.json이 없어도 동작).
    /// 설정을 찾지 못하거나 읽지 못하면 기본값 (내장 금지 패턴은 항상 적용됨).
    /// </summary>
    public OrchestrationOptions McpOptionsOrDefault()
    {
        try
        {
            var dir = McpConfigDir;
            var settings = dir is null ? null : Path.Combine(dir, "appsettings.json");
            return settings is not null && File.Exists(settings) ? ReadMcpOptions(settings) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or InvalidOperationException)
        {
            return new();
        }
    }

    private static OrchestrationOptions? ReadMcpOptions(string settingsPath)
    {
        // Microsoft.Extensions.Configuration의 JSON 공급자는 주석을 허용하므로 MCP appsettings를 그대로 읽을 수 있다.
        var config = new ConfigurationBuilder()
            .AddJsonFile(settingsPath, optional: true, reloadOnChange: false)
            .Build();
        return config.GetSection("Orchestration").Get<OrchestrationOptions>();
    }

    private static string StampOf(string path)
    {
        var info = new FileInfo(path);
        return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
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
        var providers = new List<string>();
        var providerTimeouts = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var imageDetails = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var providerTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var images = new ImageOptions();
        var pricing = new PricingOptions();
        JsonObject? settingsRoot = null;
        var settingsStamp = "";
        var settingsHasComments = false;

        if (settingsPath is not null)
        {
            var settingsText = File.ReadAllText(settingsPath);
            try
            {
                settingsRoot = JsonNode.Parse(settingsText, nodeOptions: null, documentOptions: DocOptions) as JsonObject;
            }
            catch (JsonException ex)
            {
                throw new ModesConfigException($"MCP appsettings.json 파싱 실패 (줄 {(ex.LineNumber ?? 0) + 1}): {ex.Message}");
            }

            settingsStamp = StampOf(settingsPath);
            settingsHasComments = ContainsComments(settingsText);

            if (ReadMcpOptions(settingsPath) is { } mcp)
            {
                foreach (var a in mcp.Agents)
                    agents.Add(new AgentInfo(a.Name, a.Provider, a.Model, a.MaxOutputTokens, a.Enabled, a.TimeoutSeconds, a.Vision));
                providers.AddRange(mcp.Providers.Keys.Order(StringComparer.OrdinalIgnoreCase));
                foreach (var (name, p) in mcp.Providers)
                {
                    providerTimeouts[name] = p.TimeoutSeconds;
                    imageDetails[name] = p.ImageDetail;
                    providerTypes[name] = p.Type;
                }
                images = mcp.Images;
                pricing = mcp.Pricing;
            }
        }

        return new ModesSnapshot
        {
            ModesPath = modesPath,
            LastWriteLocal = new FileInfo(modesPath).LastWriteTime,
            Stamp = StampOf(modesPath),
            Root = root,
            Typed = typed,
            HasComments = ContainsComments(text),
            McpSettingsPath = settingsPath,
            SettingsStamp = settingsStamp,
            SettingsRoot = settingsRoot,
            SettingsHasComments = settingsHasComments,
            Agents = agents,
            ProviderNames = providers,
            ProviderTimeouts = providerTimeouts,
            ProviderImageDetails = imageDetails,
            ProviderTypes = providerTypes,
            Images = images,
            Pricing = pricing,
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

    // ── 모드 저장 ────────────────────────────────────────────────────────────

    public SaveResult Save(string expectedStamp, IReadOnlyList<ModeEdit> edits)
    {
        var snap = Load();
        if (snap.Stamp != expectedStamp) return SaveResult.Fail(ChangedElsewhere);

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

        var json = Serialize(snap.Root);
        var result = Validate(json, snap.Agents);
        if (!result.Ok) return result;

        WriteSafely(snap.ModesPath, json, "modes-backup");
        return result;
    }

    /// <summary>modes.json에서 from 에이전트를 쓰는 모든 단계를 to로 바꾼다.</summary>
    public SaveResult ReplaceAgent(string expectedStamp, string from, string to)
    {
        var snap = Load();
        if (snap.Stamp != expectedStamp) return SaveResult.Fail(ChangedElsewhere);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return SaveResult.Fail("바꿀 이름을 고르세요.");
        if (!snap.Agents.Any(a => string.Equals(a.Name, to, StringComparison.OrdinalIgnoreCase)))
            return SaveResult.Fail($"'{to}'은(는) appsettings.json에 없는 에이전트입니다.");

        var count = RenameInModes(snap.Root, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = to });
        if (count == 0) return SaveResult.Fail($"'{from}'을(를) 쓰는 단계가 없습니다.");

        var json = Serialize(snap.Root);
        var result = Validate(json, snap.Agents);
        if (!result.Ok) return result;

        WriteSafely(snap.ModesPath, json, "modes-backup");
        return result with { Notes = [$"'{from}' → '{to}' : {count}개 단계를 바꿨습니다."] };
    }

    // ── 에이전트 저장 (appsettings.json) ────────────────────────────────────────

    /// <summary>
    /// appsettings.json의 Agents를 표 내용으로 바꾼다. 이름을 바꾼 에이전트는 modes.json의 단계도 같이 바꾼다.
    /// 모드가 쓰고 있는 에이전트는 삭제할 수 없다.
    /// </summary>
    public SaveResult SaveAgents(string expectedSettingsStamp, string expectedModesStamp, IReadOnlyList<AgentEdit> rows)
    {
        var snap = Load();
        if (snap.McpSettingsPath is null || snap.SettingsRoot is null)
            return SaveResult.Fail("MCP appsettings.json을 찾지 못했습니다. Dashboard:McpConfigDir를 확인하세요.");
        if (snap.SettingsStamp != expectedSettingsStamp || snap.Stamp != expectedModesStamp)
            return SaveResult.Fail(ChangedElsewhere);

        var orch = Child(snap.SettingsRoot, "Orchestration") as JsonObject;
        if (orch is null) return SaveResult.Fail("appsettings.json에 Orchestration 섹션이 없습니다.");

        // 원래 항목을 이름으로 찾아 두면, 표에 없는 필드도 그대로 보존할 수 있다.
        var originals = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (Child(orch, "Agents") is JsonArray oldArray)
        {
            foreach (var node in oldArray)
                if (node is JsonObject o && Child(o, "Name")?.ToString() is { Length: > 0 } n)
                    originals.TryAdd(n, o);
        }

        var errors = new List<string>();
        var newArray = new JsonArray();
        var finalAgents = new List<AgentInfo>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var original = row.OriginalName?.Trim() ?? "";
            var name = row.Name.Trim();
            var label = original.Length > 0 ? original : (name.Length > 0 ? name : "새 에이전트");

            if (original.Length == 0 && name.Length == 0) continue; // 비워 둔 새 줄
            if (row.Delete)
            {
                if (original.Length > 0) deleted.Add(original);
                continue;
            }

            if (!AgentNameRegex().IsMatch(name))
                errors.Add($"[{label}] 이름은 영문·숫자·_·- 만, 64자 이내로 쓸 수 있습니다.");
            else if (!names.Add(name))
                errors.Add($"[{label}] 이름 '{name}'이(가) 중복됩니다.");

            if (!snap.ProviderNames.Contains(row.Provider, StringComparer.OrdinalIgnoreCase))
                errors.Add($"[{label}] 제공사 '{row.Provider}'이(가) Providers에 없습니다.");

            var model = row.Model.Trim();
            if (model.Length == 0)
                errors.Add($"[{label}] 모델 ID를 입력하세요.");

            int? max = null;
            if (!string.IsNullOrWhiteSpace(row.MaxOutputTokens))
            {
                if (int.TryParse(row.MaxOutputTokens.Trim(), out var m) && m > 0) max = m;
                else errors.Add($"[{label}] 출력 상한은 1 이상의 숫자이거나 비워야 합니다.");
            }

            int? timeout = null;
            if (!string.IsNullOrWhiteSpace(row.TimeoutSeconds))
            {
                if (int.TryParse(row.TimeoutSeconds.Trim(), out var t) && t > 0) timeout = t;
                else errors.Add($"[{label}] 타임아웃은 1 이상의 초이거나 비워야 합니다.");
            }

            var obj = original.Length > 0 && originals.TryGetValue(original, out var source)
                ? (JsonObject)source.DeepClone()
                : new JsonObject();

            obj[KeyOf(obj, "Name")] = JsonValue.Create(name);
            obj[KeyOf(obj, "Provider")] = JsonValue.Create(row.Provider);
            obj[KeyOf(obj, "Model")] = JsonValue.Create(model);
            if (max is { } maxValue) obj[KeyOf(obj, "MaxOutputTokens")] = JsonValue.Create(maxValue);
            else obj.Remove(KeyOf(obj, "MaxOutputTokens"));
            if (timeout is { } timeoutValue) obj[KeyOf(obj, "TimeoutSeconds")] = JsonValue.Create(timeoutValue);
            else obj.Remove(KeyOf(obj, "TimeoutSeconds"));    // 비우면 제공사 값 → SDK 기본값
            if (row.Enabled) obj.Remove(KeyOf(obj, "Enabled"));      // 기본값 true는 적지 않는다
            else obj[KeyOf(obj, "Enabled")] = JsonValue.Create(false);

            newArray.Add(obj);
            finalAgents.Add(new AgentInfo(name, row.Provider, model, max, row.Enabled, timeout));

            if (original.Length > 0 && !string.Equals(original, name, StringComparison.Ordinal))
                renames[original] = name;
        }

        // 삭제하려는 에이전트를 모드가 쓰고 있으면 막는다 (먼저 다른 에이전트로 바꾸게).
        foreach (var (modeName, mode) in snap.Typed.Modes)
            foreach (var step in mode.Steps.Where(s => deleted.Contains(s.Agent)))
                errors.Add($"[{modeName}] '{step.Agent}'을(를) 쓰고 있어 삭제할 수 없습니다. 모드의 에이전트를 먼저 바꾸세요.");

        if (errors.Count > 0) return new SaveResult(errors.Distinct().ToList(), []);

        // 이름 변경을 modes.json에 반영
        var renamedSteps = renames.Count > 0 ? RenameInModes(snap.Root, renames) : 0;
        var modesJson = Serialize(snap.Root);
        var result = Validate(modesJson, finalAgents);
        if (!result.Ok) return result;

        orch[KeyOf(orch, "Agents")] = newArray;
        var settingsJson = Serialize(snap.SettingsRoot);

        // appsettings를 먼저 쓴다. 이름 변경이 있으면 modes.json도 바로 이어서 쓴다.
        WriteSafely(snap.McpSettingsPath, settingsJson, "settings-backup");
        if (renamedSteps > 0) WriteSafely(snap.ModesPath, modesJson, "modes-backup");

        var notes = new List<string>();
        foreach (var (from, to) in renames)
            notes.Add($"이름 변경 '{from}' → '{to}'");
        if (renamedSteps > 0) notes.Add($"modes.json의 {renamedSteps}개 단계에도 새 이름을 반영했습니다.");
        return result with { Notes = notes };
    }

    // ── 단가 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 모델 단가 표와 환율을 appsettings.json의 Orchestration:Pricing에 저장한다.
    /// 표에 있는 모델만 바꾸고, 표에 없는 모델의 단가는 그대로 둔다. 에이전트와 상관없이 모델 단위로 관리한다.
    /// </summary>
    public SaveResult SavePrices(string expectedSettingsStamp, IReadOnlyList<PriceEdit> rows, string krwPerUsd)
    {
        var snap = Load();
        if (snap.McpSettingsPath is null || snap.SettingsRoot is null)
            return SaveResult.Fail("MCP appsettings.json을 찾지 못했습니다. Dashboard:McpConfigDir를 확인하세요.");
        if (snap.SettingsStamp != expectedSettingsStamp) return SaveResult.Fail(ChangedElsewhere);
        if (Child(snap.SettingsRoot, "Orchestration") is not JsonObject orch)
            return SaveResult.Fail("appsettings.json에 Orchestration 섹션이 없습니다.");

        var errors = new List<string>();
        var prices = new Dictionary<string, ModelPrice?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var model = row.Model.Trim();
            if (model.Length == 0) continue;
            var (price, error) = ParsePrice(row);
            if (error is not null) errors.Add($"[{model}] {error}");
            else prices[model] = price;
        }

        decimal? rate = null;
        if (!string.IsNullOrWhiteSpace(krwPerUsd))
        {
            if (TryParseMoney(krwPerUsd, out var r) && r > 0) rate = r;
            else errors.Add($"환율 '{krwPerUsd}'은(는) 0보다 큰 숫자이거나 비워야 합니다.");
        }

        if (errors.Count > 0) return new SaveResult(errors, []);

        ApplyPricing(orch, prices, rate);
        WriteSafely(snap.McpSettingsPath, Serialize(snap.SettingsRoot), "settings-backup");
        return new SaveResult([], []);
    }

    private static void ApplyPricing(JsonObject orch, Dictionary<string, ModelPrice?> prices, decimal? rate)
    {
        // 이미 있는 노드는 그 자리에서 고치고, 새로 만든 노드만 부모에 붙인다
        if (Child(orch, "Pricing") is not JsonObject pricing)
        {
            pricing = new JsonObject();
            orch[KeyOf(orch, "Pricing")] = pricing;
        }

        if (rate is { } r) pricing[KeyOf(pricing, "KrwPerUsd")] = JsonValue.Create(r);
        else pricing.Remove(KeyOf(pricing, "KrwPerUsd"));

        if (Child(pricing, "Models") is not JsonObject models)
        {
            models = new JsonObject();
            pricing[KeyOf(pricing, "Models")] = models;
        }

        foreach (var (model, price) in prices)
        {
            var key = KeyOf(models, model);
            if (price is null) { models.Remove(key); continue; }

            if (models[key] is not JsonObject entry)
            {
                entry = new JsonObject();
                models[key] = entry;
            }
            entry[KeyOf(entry, "InputPerMillion")] = JsonValue.Create(price.InputPerMillion);
            if (price.CachedInputPerMillion is { } cached) entry[KeyOf(entry, "CachedInputPerMillion")] = JsonValue.Create(cached);
            else entry.Remove(KeyOf(entry, "CachedInputPerMillion"));
            entry[KeyOf(entry, "OutputPerMillion")] = JsonValue.Create(price.OutputPerMillion);
        }
    }

    private static (ModelPrice? Price, string? Error) ParsePrice(PriceEdit row)
    {
        var input = row.InputPrice?.Trim() ?? "";
        var cached = row.CachedInputPrice?.Trim() ?? "";
        var output = row.OutputPrice?.Trim() ?? "";
        if (input.Length == 0 && cached.Length == 0 && output.Length == 0) return (null, null);

        if (input.Length == 0 || output.Length == 0)
            return (null, "단가는 입력과 출력을 둘 다 적어야 합니다 (캐시는 비우면 입력 단가와 같음).");
        if (!TryParseMoney(input, out var i) || i < 0) return (null, $"입력 단가 '{input}'이(가) 0 이상의 숫자가 아닙니다.");
        if (!TryParseMoney(output, out var o) || o < 0) return (null, $"출력 단가 '{output}'이(가) 0 이상의 숫자가 아닙니다.");

        decimal? c = null;
        if (cached.Length > 0)
        {
            if (!TryParseMoney(cached, out var cv) || cv < 0) return (null, $"캐시 입력 단가 '{cached}'이(가) 0 이상의 숫자가 아닙니다.");
            c = cv;
        }
        return (new ModelPrice { InputPerMillion = i, CachedInputPerMillion = c, OutputPerMillion = o }, null);
    }

    // 소수점은 '.'만 받는다. ','는 올바른 천 단위 구분("1,380", "12,345.5")일 때만 허용 ("1,5"처럼 애매한 값은 거부).
    // 저장은 JSON 숫자로 한다.
    private static bool TryParseMoney(string text, out decimal value)
    {
        value = 0;
        var t = text.Trim();
        if (!MoneyRegex().IsMatch(t)) return false;
        return decimal.TryParse(t.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"^(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$")]
    private static partial Regex MoneyRegex();

    /// <summary>모든 모드의 단계에서 에이전트 이름을 바꾼다. 바꾼 단계 수를 돌려준다.</summary>
    private static int RenameInModes(JsonObject root, IReadOnlyDictionary<string, string> renames)
    {
        if (Child(root, "Modes") is not JsonObject modes) return 0;

        var count = 0;
        foreach (var (_, modeNode) in modes)
        {
            if (modeNode is not JsonObject mode || Child(mode, "Steps") is not JsonArray steps) continue;
            foreach (var stepNode in steps)
            {
                if (stepNode is not JsonObject step) continue;
                var key = KeyOf(step, "Agent");
                var current = step[key]?.ToString();
                if (current is not null && renames.TryGetValue(current, out var to))
                {
                    step[key] = JsonValue.Create(to);
                    count++;
                }
            }
        }
        return count;
    }

    // ── 검증 ────────────────────────────────────────────────────────────────

    private const string ChangedElsewhere =
        "이 화면을 연 뒤에 설정 파일이 다른 곳에서 바뀌었습니다. '다시 불러오기' 후 다시 수정하세요.";

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
            return SaveResult.Fail($"JSON 형식 오류: {ex.Message}");
        }

        var known = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents) known.TryAdd(a.Name, a);

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

    // ── 파일 쓰기 ────────────────────────────────────────────────────────────

    private static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions) + Environment.NewLine;

    /// <summary>백업을 남기고, 임시 파일에 쓴 뒤 한 번에 교체한다 (MCP 서버가 읽는 도중이어도 반쯤 쓴 파일을 보지 않게).</summary>
    private static void WriteSafely(string path, string json, string backupFolderName)
    {
        var dir = Path.GetDirectoryName(path)!;
        var backupDir = Path.Combine(dir, backupFolderName);
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
        new HashSet<string> { "MaxRounds", "Return" };

    /// <summary>모드 항목의 원본 값 (없으면 null). 화면에 "파일에 실제로 적힌 값"을 보여줄 때 쓴다.</summary>
    public static string? GetRaw(JsonObject root, string mode, string key)
    {
        if (Child(root, "Modes") is not JsonObject modes || modes[mode] is not JsonObject modeObj) return null;
        return Child(modeObj, key)?.ToString();
    }
}
