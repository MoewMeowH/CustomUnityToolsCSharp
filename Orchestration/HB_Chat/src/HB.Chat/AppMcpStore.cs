using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>~/.claude.json에서 가져올 수 있는 서버 하나: 범위(user · local 폴더) · 이름 · 항목.</summary>
public sealed record ImportableMcp(string Scope, string Name, JsonObject Entry);

/// <summary>
/// 앱에서 더한 MCP 서버: 데이터 폴더 mcp-servers.json에 이름 → .mcp.json 모양 항목.
/// 모든 방 연결 탭에 "앱 서버"로 보이고 방마다 켠다(Room.McpExtra). 이름은 작업 폴더 .mcp.json에 같은 것이 있으면 그쪽을 쓴다.
/// 원자적으로 쓰고(RoomStore와 같은 규칙), 깨졌으면 덮지 않고 쓰기를 거절한다. 받는 칸만 받는다 — 그 밖의 칸 · 잘못된 모양은 거절.
/// </summary>
public sealed partial class AppMcpStore
{
    /// <summary>항목 하나 상한 (JSON 글자).</summary>
    public const int MaxEntryChars = 8 * 1024;

    /// <summary>받는 칸 (.mcp.json · claude mcp add-json 모양).</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal) { "type", "command", "args", "env", "url", "headers" };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly IReadOnlyCollection<string> _reserved;
    private readonly SortedDictionary<string, JsonObject> _servers = new(StringComparer.OrdinalIgnoreCase);

    public string? LoadError { get; }

    /// <summary>파일에 있지만 쓰지 않는 항목과 이유 (설정 화면 경고).</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <param name="reserved">쓸 수 없는 이름 — 기본 서버(hb-orchestration) · 앱 MCP(hb-chat) · unity.</param>
    public AppMcpStore(string path, IEnumerable<string> reserved)
    {
        _path = path;
        _reserved = [.. reserved];
        var warnings = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                // JSON이어도 모양이 다르면(객체가 아님 · servers가 객체가 아님) 깨진 것으로 — 빈 목록으로 읽으면 다음 저장이 원래 파일을 덮었다
                // (/self-review 779716239c C1)
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["servers"] is { } s && s is not JsonObject)
                    LoadError = "mcp-servers.json 모양이 아닙니다 ({\"servers\": {…}} 객체여야 함)";
                else if (root["servers"] is JsonObject saved)
                    foreach (var (name, node) in saved)
                    {
                        if (node is not JsonObject entry) { warnings.Add($"{name}: 항목 모양이 아닙니다"); continue; }
                        if (Validate(name, entry, _reserved) is { } why) { warnings.Add($"{name}: {why}"); continue; }
                        _servers[name] = (JsonObject)entry.DeepClone();
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
        LoadWarnings = warnings;
    }

    public IReadOnlyList<(string Name, JsonObject Entry)> All()
    {
        lock (_lock) return [.. _servers.Select(p => (p.Key, (JsonObject)p.Value.DeepClone()))];
    }

    /// <summary>그 이름의 항목 사본. 없으면 null.</summary>
    public JsonObject? Get(string name)
    {
        lock (_lock) return _servers.TryGetValue(name, out var e) ? (JsonObject)e.DeepClone() : null;
    }

    public bool Contains(string name)
    {
        lock (_lock) return _servers.ContainsKey(name);
    }

    /// <summary>저장된 정식 이름 (대소문자 무시로 찾음). 없으면 null — 방에 넣을 이름을 맞출 때 (/self-review 779716239c C4).</summary>
    public string? NameOf(string name)
    {
        lock (_lock) return _servers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>더하기 (같은 이름은 거절). 오류 문구 — null이면 더함.</summary>
    public string? Add(string? name, JsonObject entry)
    {
        var n = name?.Trim() ?? "";
        if (Validate(n, entry, _reserved) is { } why) return why;
        var saved = (JsonObject)entry.DeepClone();
        // type 없는 url 항목은 http로 채운다 — CLI 스키마가 http · sse에 type을 요구할 수 있다 (/self-review 779716239c C2, 실측 안 함)
        if (saved["url"] is not null && saved["type"] is null) saved["type"] = "http";
        lock (_lock)
        {
            if (LoadError is not null) return $"mcp-servers.json을 읽지 못해 쓰지 않습니다: {LoadError}";
            if (_servers.ContainsKey(n)) return "이미 있는 이름입니다.";
            _servers[n] = saved;
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _servers.Remove(n);
                return $"저장하지 못했습니다: {ex.Message}";
            }
        }
        return null;
    }

    /// <summary>빼기. 쓰는 방이 있어도 뺀다 — 그 방 실행은 "없는 연결은 빼고 돕니다" 알림. 오류 문구 — null이면 뺌.</summary>
    public string? Remove(string? name)
    {
        var n = name?.Trim() ?? "";
        lock (_lock)
        {
            if (LoadError is not null) return $"mcp-servers.json을 읽지 못해 쓰지 않습니다: {LoadError}";
            if (!_servers.Remove(n, out var before)) return "없는 서버입니다.";
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _servers[n] = before;
                return $"저장하지 못했습니다: {ex.Message}";
            }
        }
        return null;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var servers = new JsonObject();
        foreach (var (name, entry) in _servers) servers[name] = entry.DeepClone();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["version"] = 1, ["servers"] = servers }.ToJsonString(Json), new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// 이름 · 항목 검사. 오류 문구 — null이면 됨. 이름은 서버 이름 규칙(영숫자 _ -) · 예약 이름 아님. 항목은 받는 칸만, stdio(command · args · env) 또는
    /// http · sse(url · headers) 중 하나, 값은 글자(args는 글자 배열), url은 http · https 절대 주소.
    /// </summary>
    public static string? Validate(string name, JsonObject entry, IReadOnlyCollection<string> reserved)
    {
        if (!ClaudeArgs.IsServerName(name)) return "이름은 영문 · 숫자로 시작하고 영문 · 숫자 · _ - 만 64자까지입니다.";
        if (reserved.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"'{name}'은 앱이 쓰는 이름이라 쓸 수 없습니다.";
        if (entry.ToJsonString().Length > MaxEntryChars) return $"항목이 너무 큽니다 ({MaxEntryChars / 1024}KB까지).";
        if (entry.Select(p => p.Key).FirstOrDefault(k => !Keys.Contains(k)) is { } unknown) return $"모르는 칸입니다: {unknown} (받는 칸: {string.Join(", ", Keys)})";
        var type = Text(entry["type"]);
        if (entry["type"] is not null && type is not ("stdio" or "http" or "sse")) return "type은 stdio · http · sse 중 하나입니다.";
        if (entry["url"] is not null)
        {
            if (entry["command"] is not null || entry["args"] is not null || entry["env"] is not null) return "url 항목에는 command · args · env를 같이 쓰지 않습니다.";
            if (type == "stdio") return "url 항목의 type은 http 또는 sse입니다.";
            if (Text(entry["url"]) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return "url은 http · https 절대 주소여야 합니다.";
            return StringMap(entry["headers"], "headers");
        }
        if (entry["headers"] is not null) return "headers는 url 항목에만 씁니다.";
        if (type is "http" or "sse") return "http · sse 항목에는 url이 있어야 합니다.";
        if (Text(entry["command"]) is not { Length: > 0 }) return "command(실행할 프로그램) 또는 url이 있어야 합니다.";
        if (entry["args"] is { } args && (args is not JsonArray list || list.Any(a => Text(a) is null))) return "args는 글자 배열이어야 합니다.";
        return StringMap(entry["env"], "env");
    }

    private static string? StringMap(JsonNode? node, string key) =>
        node is null ? null
        : node is not JsonObject map || map.Any(p => Text(p.Value) is null) ? $"{key}는 이름 → 글자 값 모양이어야 합니다."
        // 줄바꿈 등 제어 문자는 헤더 · 환경 변수에서 뜻이 달라진다 (지휘자 검토 K6)
        : map.Any(p => p.Key.Any(char.IsControl) || Text(p.Value)!.Any(char.IsControl)) ? $"{key}의 이름 · 값에 줄바꿈 같은 제어 문자가 있습니다."
        : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// 평문 비밀 값으로 보이는 칸 (env · headers 값 — 결정 2 "경고만"). ${VAR} 자리표시가 든 값은 괜찮다. 토큰 모양:
    /// 알려진 머리(sk- · ghp_ · github_pat_ · xox · ntn_ · secret_ · Bearer 뒤 긴 값) 또는 공백 없는 20자 이상 영숫자 섞인 값.
    /// </summary>
    /// <remarks>
    /// args 낱말(--key=값이면 값) · url 쿼리 값도 본다 (/self-review security ae543471a8 C3 · 779716239c C3). 그리고 비밀이 아니어도 알릴 것:
    /// 원격 url이 헤더 · url에 ${VAR}를 쓰면 앱 환경 변수 값이 그 호스트로 나간다(ae543471a8 C1), 원격 http://는 암호화 없이 나간다(C2).
    /// </remarks>
    public static IReadOnlyList<string> SecretWarnings(JsonObject entry)
    {
        var found = new List<string>();
        foreach (var key in new[] { "env", "headers" })
            if (entry[key] is JsonObject map)
                foreach (var (name, node) in map)
                    if (Text(node) is { } value && LooksSecret(value)) found.Add($"{key}.{name}");
        if (entry["args"] is JsonArray args)
            for (var i = 0; i < args.Count; i++)
                if (Text(args[i]) is { } arg && LooksSecret(ArgValue(arg))) found.Add($"args[{i}]");
        if (Text(entry["url"]) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            foreach (var (name, value) in QueryPairs(uri))
                if (LooksSecret(value)) found.Add($"url 쿼리 {name}");
            if (!uri.IsLoopback)
            {
                if (uri.Scheme == "http") found.Add("url이 원격 http:// — 헤더 · 토큰이 암호화 없이 나감");
                var vars = Placeholders(entry).ToList();
                if (vars.Count > 0) found.Add($"환경 변수 {string.Join(", ", vars.Select(v => "${" + v + "}"))} 값을 원격 호스트 {uri.Host}로 보냄");
            }
        }
        return found;
    }

    /// <summary>--key=값 → 값, 그 밖은 그대로.</summary>
    private static string ArgValue(string arg) => arg.StartsWith('-') && arg.IndexOf('=') is > 0 and var eq ? arg[(eq + 1)..] : arg;

    private static IEnumerable<(string Name, string Value)> QueryPairs(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => (Uri.UnescapeDataString(p[0]), p.Length > 1 ? Uri.UnescapeDataString(p[1]) : ""));

    /// <summary>url · headers 값 속 ${VAR} 이름들 (env는 로컬 프로세스에만 가므로 뺀다).</summary>
    public static IEnumerable<string> Placeholders(JsonObject entry)
    {
        var texts = new List<string>();
        if (Text(entry["url"]) is { } url) texts.Add(url);
        if (entry["headers"] is JsonObject headers) texts.AddRange(headers.Select(p => Text(p.Value) ?? ""));
        return texts.SelectMany(t => PlaceholderName().Matches(t).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PlaceholderName();

    public static bool LooksSecret(string value)
    {
        var v = value.Trim();
        if (v.Contains("${", StringComparison.Ordinal)) return false;
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) v = v[7..].Trim();
        if (KnownPrefix().IsMatch(v)) return true;
        return v.Length >= 20 && !v.Contains(' ') && v.Any(char.IsDigit) && v.Any(char.IsLetter);
    }

    [GeneratedRegex("^(sk-|ghp_|gho_|github_pat_|xox[abpr]-|ntn_|secret_|glpat-|AKIA)", RegexOptions.IgnoreCase)]
    private static partial Regex KnownPrefix();

    /// <summary>
    /// 붙여 넣은 JSON → (이름, 항목) 목록. 세 모양: <c>{"mcpServers": {...}}</c> · <c>{"이름": {...}}</c> · 항목 하나(claude mcp add-json 모양 —
    /// command · url · type 칸이 있으면, 이름은 따로 받은 것). 오류면 (빈 목록, 문구).
    /// </summary>
    public static (IReadOnlyList<(string Name, JsonObject Entry)> Items, string? Error) ParsePaste(string? text, string? bareName)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text ?? "") as JsonObject;
        }
        catch (JsonException ex)
        {
            return ([], $"JSON을 읽지 못했습니다: {ex.Message}");
        }
        if (root is null) return ([], "JSON 객체({ … })를 붙여 넣으세요.");
        if (root["command"] is not null || root["url"] is not null || root["type"] is not null)
            return string.IsNullOrWhiteSpace(bareName) ? ([], "항목 하나만 붙여 넣었으면 이름 칸을 채우세요.") : ([(bareName.Trim(), root)], null);
        var map = root["mcpServers"] as JsonObject ?? root;
        var items = new List<(string, JsonObject)>();
        foreach (var (name, node) in map)
        {
            if (node is not JsonObject entry) return ([], $"{name}: 항목 모양이 아닙니다.");
            items.Add((name, entry));
        }
        return items.Count == 0 ? ([], "서버 항목이 없습니다.") : (items, null);
    }

    /// <summary>칸 채우기 → 항목. stdio는 명령 줄을 따옴표를 지켜 나눈다(첫 낱말 = command), http · sse는 url.</summary>
    public static JsonObject FromFields(string? type, string? commandLine, string? url)
    {
        if (type is "http" or "sse") return new JsonObject { ["type"] = type, ["url"] = url?.Trim() ?? "" };
        var parts = SplitCommandLine(commandLine ?? "");
        var entry = new JsonObject { ["command"] = parts.Count > 0 ? parts[0] : "" };
        if (parts.Count > 1) entry["args"] = new JsonArray([.. parts.Skip(1).Select(p => (JsonNode?)JsonValue.Create(p))]);
        return entry;
    }

    /// <summary>명령 줄 나누기: 공백으로, 큰따옴표 안은 한 낱말 (따옴표는 뺀다).</summary>
    public static IReadOnlyList<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in line.Trim())
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) parts.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>
    /// ~/.claude.json의 서버들 — user 범위(맨 위 mcpServers)와 local 범위(projects[폴더].mcpServers). 읽지 못하면 빈 목록.
    /// 설정 화면은 이름 · 명령/URL만 보이고 env · headers 값은 보이지 않는다.
    /// </summary>
    /// <remarks>파일이 수 MB일 수 있어(프로젝트 기록) 같은 시각 · 크기면 앞 결과를 쓴다 (/self-review 779716239c C7 · 지휘자 K3). 항목은 사본으로 준다.</remarks>
    public static IReadOnlyList<ImportableMcp> ReadClaudeJson(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return [];
            var key = (path, info.LastWriteTimeUtc, info.Length);
            lock (ImportCacheLock)
                if (_importCache is { } cached && cached.Key == key) return Copy(cached.Items);
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return [];
            var list = new List<ImportableMcp>();
            void Take(string scope, JsonNode? servers)
            {
                if (servers is not JsonObject map) return;
                foreach (var (name, node) in map)
                    if (node is JsonObject entry) list.Add(new ImportableMcp(scope, name, (JsonObject)entry.DeepClone()));
            }
            Take("user", root["mcpServers"]);
            if (root["projects"] is JsonObject projects)
                foreach (var (folder, project) in projects) Take("local: " + folder, project is JsonObject p ? p["mcpServers"] : null); // 객체가 아니면 건너뜀 (779716239c C1)
            lock (ImportCacheLock) _importCache = (key, list);
            return Copy(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return []; }
    }

    private static IReadOnlyList<ImportableMcp> Copy(IReadOnlyList<ImportableMcp> items) =>
        [.. items.Select(i => i with { Entry = (JsonObject)i.Entry.DeepClone() })];

    private static readonly Lock ImportCacheLock = new();
    private static ((string Path, DateTime Time, long Length) Key, IReadOnlyList<ImportableMcp> Items)? _importCache;

    /// <summary>
    /// 화면에 보일 짧은 명령 (command + args 또는 url, 200자). 토큰처럼 보이는 args 값 · url 쿼리 값은 ••• 로 가리고
    /// (/self-review security ae543471a8 C3 · 779716239c C3), env · headers는 값 없이 이름과 그 안 ${VAR} 이름만 붙인다(ae543471a8 C1).
    /// </summary>
    public static string Describe(JsonObject entry)
    {
        string text;
        if (Text(entry["url"]) is { } url)
        {
            text = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Query.Length > 1
                ? url[..url.IndexOf('?')] + "?" + string.Join("&", QueryPairs(uri).Select(p => p.Name + "=" + (LooksSecret(p.Value) ? "•••" : p.Value)))
                : url;
        }
        else
        {
            var args = entry["args"] is JsonArray a ? a.Select(x => Mask(Text(x) ?? "")) : [];
            text = string.Join(" ", new[] { Text(entry["command"]) ?? "(명령 없음)" }.Concat(args));
        }
        foreach (var key in new[] { "env", "headers" })
            if (entry[key] is JsonObject map && map.Count > 0)
                text += $" · {key}: " + string.Join(", ", map.Select(p =>
                    p.Key + (PlaceholderName().Match(Text(p.Value) ?? "") is { Success: true } m ? "=${" + m.Groups[1].Value + "}" : "")));
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>args 낱말 가리기: 토큰처럼 보이면 •••, --key=토큰이면 --key=•••.</summary>
    private static string Mask(string arg) =>
        !LooksSecret(ArgValue(arg)) ? arg
        : ArgValue(arg).Length < arg.Length ? arg[..(arg.Length - ArgValue(arg).Length)] + "•••"
        : "•••";

    public static string TransportOf(JsonObject entry) => entry["url"] is not null ? (Text(entry["type"]) == "sse" ? "sse" : "http") : "stdio";

    /// <summary>~/.claude.json 경로 (CLAUDE_CONFIG_DIR가 있으면 그 안의 .claude.json — CLI와 같은 규칙, 추정).</summary>
    public static string ClaudeJsonPath() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
}
