using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Orchestration.Agents;

public enum ConfigDriftKind
{
    /// <summary>원본에만 있는 경로. 그 아래는 펼치지 않는다.</summary>
    OnlyInSource,
    /// <summary>같은 경로에서 자료형(object · array · string · number · bool · null)이 다르다.</summary>
    KindMismatch,
    /// <summary>대소문자만 다른 키, Name이 깨진 배열처럼 짝을 지을 수 없어 안쪽을 건너뛴 곳.</summary>
    NotComparable,
}

public sealed record ConfigDriftItem(ConfigDriftKind Kind, string Path, string? Detail = null);

/// <summary>
/// 원본 설정(src)과 사용 중 설정(publish 폴더)의 차이 중 "원본에만 있는 것"을 찾는다. 파일은 바꾸지 않는다.
/// publish가 사용 중 설정을 지키느라 원본에 새로 생긴 모드·역할·필드가 빠지는 것을 알리기 위한 것이라,
/// 사용 중에만 있는 키와 같은 자료형의 값 차이는 알리지 않는다.
/// </summary>
/// <remarks>
/// 비교 규칙
/// - 객체 키는 대소문자 무시로 짝짓는다 (서버가 modes.json · appsettings.json을 그렇게 읽는다).
/// - 배열은 원소가 모두 Name을 가진 객체면(appsettings의 Agents) Name으로 짝짓는다 (대소문자 무시).
///   그 밖의 배열(Steps, FreeProviders 등)은 값으로 보고 안쪽을 비교하지 않는다.
/// </remarks>
public static class ConfigDrift
{
    /// <summary>서버와 같은 허용 문법: 주석 건너뜀, 후행 쉼표 허용 (ModeStore · Microsoft.Extensions.Configuration).</summary>
    public static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <exception cref="JsonException">JSON이 깨졌을 때.</exception>
    public static JsonNode? Parse(string text) => JsonNode.Parse(text, nodeOptions: null, documentOptions: ParseOptions);

    public static IReadOnlyList<ConfigDriftItem> Compare(JsonNode? source, JsonNode? live)
    {
        var items = new List<ConfigDriftItem>();
        Walk(source, live, "", items);
        return items;
    }

    private static void Walk(JsonNode? source, JsonNode? live, string path, List<ConfigDriftItem> items)
    {
        var sourceKind = KindOf(source);
        var liveKind = KindOf(live);
        if (sourceKind != liveKind)
        {
            items.Add(new(ConfigDriftKind.KindMismatch, Display(path), $"원본 {sourceKind}, 사용 중 {liveKind}"));
            return;
        }
        if (source is JsonObject sourceObject) WalkObject(sourceObject, (JsonObject)live!, path, items);
        else if (source is JsonArray sourceArray) WalkArray(sourceArray, (JsonArray)live!, path, items);
    }

    private static void WalkObject(JsonObject source, JsonObject live, string path, List<ConfigDriftItem> items)
    {
        if (CaseCollision(source) is { } sourceKeys)
        {
            items.Add(new(ConfigDriftKind.NotComparable, Display(path), $"원본에 대소문자만 다른 키: {sourceKeys}"));
            return;
        }
        if (CaseCollision(live) is { } liveKeys)
        {
            items.Add(new(ConfigDriftKind.NotComparable, Display(path), $"사용 중에 대소문자만 다른 키: {liveKeys}"));
            return;
        }

        var liveByKey = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in live) liveByKey[key] = value;

        foreach (var (key, sourceValue) in source)
        {
            var child = ChildPath(path, key);
            if (liveByKey.TryGetValue(key, out var liveValue)) Walk(sourceValue, liveValue, child, items);
            else items.Add(new(ConfigDriftKind.OnlyInSource, child));
        }
    }

    private static void WalkArray(JsonArray source, JsonArray live, string path, List<ConfigDriftItem> items)
    {
        var (sourceShape, sourceNames) = Classify(source);
        // 일반 배열이거나 원본이 비어 있으면 안쪽은 비교하지 않는다.
        if (sourceShape is ArrayShape.Plain or ArrayShape.Empty) return;
        if (sourceShape == ArrayShape.BrokenNames)
        {
            items.Add(new(ConfigDriftKind.NotComparable, Display(path), "원본 배열의 Name이 비었거나 겹침"));
            return;
        }

        var (liveShape, liveNames) = Classify(live);
        if (liveShape is ArrayShape.Plain or ArrayShape.BrokenNames)
        {
            items.Add(new(ConfigDriftKind.NotComparable, Display(path), "원본은 Name 배열인데 사용 중은 Name으로 짝지을 수 없음"));
            return;
        }

        foreach (var (name, sourceElement) in sourceNames!)
        {
            var child = $"{path}[Name={QuoteIfNeeded(name)}]";
            if (liveNames!.TryGetValue(name, out var liveElement)) Walk(sourceElement, liveElement, child, items);
            else items.Add(new(ConfigDriftKind.OnlyInSource, child));
        }
    }

    private enum ArrayShape { Empty, Plain, Named, BrokenNames }

    /// <summary>
    /// Named: 원소가 모두 객체이고 비어 있지 않은 문자열 Name을 가지며 Name이 겹치지 않음.
    /// BrokenNames: 원소가 모두 객체이고 일부가 Name을 갖지만 위 조건을 못 지킴. Plain: 그 밖.
    /// </summary>
    private static (ArrayShape Shape, Dictionary<string, JsonNode>? Names) Classify(JsonArray array)
    {
        if (array.Count == 0) return (ArrayShape.Empty, new(StringComparer.OrdinalIgnoreCase));
        if (array.Any(e => e is not JsonObject)) return (ArrayShape.Plain, null);
        var objects = array.Cast<JsonObject>().ToList();
        // 키가 있는지로 본다 ("Name": null도 Name을 가진 것으로 친다).
        if (!objects.Any(o => NameKeys(o).Count > 0)) return (ArrayShape.Plain, null);

        var names = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects)
        {
            var keys = NameKeys(o);
            // Name · name처럼 대소문자만 다른 키가 둘이면 어느 쪽으로 짝지을지 모른다.
            if (keys.Count != 1) return (ArrayShape.BrokenNames, null);
            var name = o[keys[0]] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            if (string.IsNullOrWhiteSpace(name) || !names.TryAdd(name, o)) return (ArrayShape.BrokenNames, null);
        }
        return (ArrayShape.Named, names);
    }

    private static List<string> NameKeys(JsonObject o) =>
        o.Select(p => p.Key).Where(k => string.Equals(k, "Name", StringComparison.OrdinalIgnoreCase)).ToList();

    private static string? CaseCollision(JsonObject o)
    {
        var group = o.Select(p => p.Key)
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        return group is null ? null : string.Join(", ", group);
    }

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Null => "null",
            var other => other.ToString(),
        },
    };

    private static string ChildPath(string path, string key)
    {
        if (NeedsQuote(key)) return $"{path}[{Quote(key)}]";
        return path.Length == 0 ? key : $"{path}.{key}";
    }

    private static string QuoteIfNeeded(string name) => NeedsQuote(name) ? Quote(name) : name;

    private static bool NeedsQuote(string s) =>
        s.Length == 0 || s.Any(c => c is '.' or '[' or ']' or '"' or '=' || char.IsWhiteSpace(c));

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Display(string path) => path.Length == 0 ? "(최상위)" : path;
}

/// <summary>
/// publish 뒤 HB_Orchestration.bat이 부르는 설정 비교 (MCP dll의 --config-drift). 서버를 띄우지 않고 출력 후 끝낸다.
/// 종료 코드: 0 두 파일 모두 비교함 (차이 여부와 무관) / 1 비교하지 못한 파일 있음 / 2 사용법 오류.
/// </summary>
public static class ConfigDriftReport
{
    public const string Flag = "--config-drift";

    /// <summary>비교하는 파일. publish가 덮지 않고 지키는 설정 파일과 같다.</summary>
    public static readonly string[] Files = ["modes.json", "appsettings.json"];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 3 || args[0] != Flag || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            error.WriteLine($"사용법: HB.Orchestration.Mcp.dll {Flag} <원본 폴더> <publish 폴더>");
            return 2;
        }

        var sourceDir = args[1];
        var liveDir = args[2];
        output.WriteLine("[설정 비교] 원본에만 있는 경로를 알립니다. 파일은 바꾸지 않습니다.");
        output.WriteLine("            publish 폴더 기준입니다. ModesPath · HB_MODES_PATH · 프로젝트의 hb-modes.json을 쓰면 실제 사용 파일은 다를 수 있습니다.");

        var failed = false;
        foreach (var file in Files)
        {
            IReadOnlyList<ConfigDriftItem> items;
            try
            {
                items = ConfigDrift.Compare(Load(Path.Combine(sourceDir, file)), Load(Path.Combine(liveDir, file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                output.WriteLine($"[경고] {file}: 비교하지 못했습니다 — {ex.Message}");
                failed = true;
                continue;
            }

            if (items.Count == 0)
            {
                output.WriteLine($"{file}: 원본에만 있는 경로 없음");
                continue;
            }
            foreach (var item in items) output.WriteLine($"[알림] {file}: {Describe(item)}");
        }
        return failed ? 1 : 0;
    }

    public static string Describe(ConfigDriftItem item) => item.Kind switch
    {
        ConfigDriftKind.OnlyInSource => $"원본에만 있음 {item.Path}",
        ConfigDriftKind.KindMismatch => $"자료형 다름 {item.Path} ({item.Detail})",
        _ => $"비교 못 함 {item.Path} ({item.Detail})",
    };

    // 서버(ModeStore)와 같은 방식으로 글자를 푼다 (BOM · 인코딩).
    private static JsonNode? Load(string path) =>
        ConfigDrift.Parse(GuidelinesLoader.DecodeLikeReadAllText(File.ReadAllBytes(path)));
}
