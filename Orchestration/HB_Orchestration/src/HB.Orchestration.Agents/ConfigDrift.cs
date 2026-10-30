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
    /// <summary>기준 = 사용 중 ≠ 원본: 원본이 바뀌었는데 사용 중은 옛 값이다.</summary>
    SourceValueChanged,
    /// <summary>원본 · 사용 중 · 기준이 모두 다르다. 기준에 없는 경로인데 원본과 사용 중이 다른 경우도 여기.</summary>
    BothChanged,
    /// <summary>기준이 없어 원본과 사용 중이 다르다는 것만 안다.</summary>
    ValueDiffers,
}

public sealed record ConfigDriftItem(ConfigDriftKind Kind, string Path, string? Detail = null);

/// <summary>
/// 원본 설정(src)과 사용 중 설정(publish 폴더)의 차이를 찾는다. 파일은 바꾸지 않는다.
/// publish가 사용 중 설정을 지키느라 원본의 변경이 조용히 빠지는 것을 알리기 위한 것이다.
/// <see cref="Compare"/>는 원본에만 있는 경로를 찾는다. 사용 중에만 있는 키와 같은 자료형의 값 차이는 보지 않는다.
/// <see cref="CompareValues"/>는 여기에 값 비교를 더한다. 두 벌로는 일부러 바꾼 값과 옛 값을 가를 수 없어,
/// 사용자가 확인한 때의 원본(기준)을 함께 본다. 기준과 원본이 같은 값은 일부러 바꾼 값으로 보고 알리지 않는다.
/// </summary>
/// <remarks>
/// 비교 규칙
/// - 객체 키는 대소문자 무시로 짝짓는다 (서버가 modes.json · appsettings.json을 그렇게 읽는다).
/// - 배열은 원소가 모두 Name을 가진 객체면(appsettings의 Agents) Name으로 짝짓는다 (대소문자 무시).
///   그 밖의 배열(Steps, FreeProviders 등)은 값으로 보고, 값 비교에서 통째로 견준다.
/// </remarks>
public static class ConfigDrift
{
    private const int MaxShownText = 40;

    /// <summary>값 비교의 기준 쪽. Known이면 기준 파일이 있고, Present면 그 안에 지금 경로가 있다.</summary>
    private readonly record struct Baseline(bool Known, bool Present, JsonNode? Node);

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
        Walk(source, live, null, "", items);
        return items;
    }

    /// <param name="hasBaseline">기준 파일이 있는가. 없으면 값이 다른 경로를 모두 <see cref="ConfigDriftKind.ValueDiffers"/>로 낸다.</param>
    public static IReadOnlyList<ConfigDriftItem> CompareValues(JsonNode? source, JsonNode? live, bool hasBaseline, JsonNode? baseline)
    {
        var items = new List<ConfigDriftItem>();
        Walk(source, live, new Baseline(hasBaseline, hasBaseline, baseline), "", items);
        return items;
    }

    private static void Walk(JsonNode? source, JsonNode? live, Baseline? baseline, string path, List<ConfigDriftItem> items)
    {
        var sourceKind = KindOf(source);
        var liveKind = KindOf(live);
        if (sourceKind != liveKind)
        {
            items.Add(new(ConfigDriftKind.KindMismatch, Display(path), $"원본 {sourceKind}, 사용 중 {liveKind}"));
            return;
        }
        if (source is JsonObject sourceObject) WalkObject(sourceObject, (JsonObject)live!, baseline, path, items);
        else if (source is JsonArray sourceArray) WalkArray(sourceArray, (JsonArray)live!, baseline, path, items);
        else CheckValue(source, live, baseline, path, items);
    }

    private static void CheckValue(JsonNode? source, JsonNode? live, Baseline? baseline, string path, List<ConfigDriftItem> items)
    {
        if (baseline is not { } b || SameValue(source, live)) return;

        ConfigDriftKind kind;
        if (!b.Known) kind = ConfigDriftKind.ValueDiffers;
        else if (b.Present && SameValue(b.Node, source)) return;
        else if (b.Present && SameValue(b.Node, live)) kind = ConfigDriftKind.SourceValueChanged;
        else kind = ConfigDriftKind.BothChanged;

        var detail = source is JsonArray ? "내용 다름" : $"사용 중 {Show(live)} → 원본 {Show(source)}";
        items.Add(new(kind, Display(path), detail));
    }

    private static string Show(JsonNode? node)
    {
        if (node is not JsonValue value) return node?.ToJsonString() ?? "null";
        if (value.GetValueKind() != JsonValueKind.String) return value.ToJsonString();
        var text = value.GetValue<string>();
        if (text.Length == 0) return "(빈 글자)";
        return text.Length <= MaxShownText && !text.Any(char.IsControl) ? text : $"{text.Length}자";
    }

    /// <summary>키 대소문자를 무시하는 값 비교. 배열은 순서까지 본다.</summary>
    private static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (a is JsonObject x && b is JsonObject y)
        {
            if (x.Count != y.Count) return false;
            var byKey = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in y) byKey[key] = value;
            return x.All(p => byKey.TryGetValue(p.Key, out var other) && SameValue(p.Value, other));
        }
        if (a is JsonArray m && b is JsonArray n)
            return m.Count == n.Count && m.Zip(n).All(p => SameValue(p.First, p.Second));
        return JsonNode.DeepEquals(a, b);
    }

    private static Baseline? ChildOf(Baseline? parent, string key)
    {
        if (parent is not { } b) return null;
        if (b.Node is JsonObject o)
        {
            foreach (var (k, value) in o)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return b with { Node = value };
        }
        return b with { Present = false, Node = null };
    }

    private static Baseline? ElementOf(Baseline? parent, string name)
    {
        if (parent is not { } b) return null;
        if (b.Node is JsonArray a && Classify(a).Names?.TryGetValue(name, out var element) == true)
            return b with { Node = element };
        return b with { Present = false, Node = null };
    }

    private static void WalkObject(JsonObject source, JsonObject live, Baseline? baseline, string path, List<ConfigDriftItem> items)
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
            if (liveByKey.TryGetValue(key, out var liveValue)) Walk(sourceValue, liveValue, ChildOf(baseline, key), child, items);
            else items.Add(new(ConfigDriftKind.OnlyInSource, child));
        }
    }

    private static void WalkArray(JsonArray source, JsonArray live, Baseline? baseline, string path, List<ConfigDriftItem> items)
    {
        var (sourceShape, sourceNames) = Classify(source);
        // 일반 배열이거나 원본이 비어 있으면 안쪽을 짝짓지 않고 값으로 본다.
        if (sourceShape is ArrayShape.Plain or ArrayShape.Empty)
        {
            CheckValue(source, live, baseline, path, items);
            return;
        }
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
            if (liveNames!.TryGetValue(name, out var liveElement)) Walk(sourceElement, liveElement, ElementOf(baseline, name), child, items);
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
/// 인자에 기준 폴더를 주면 값 비교까지 한다. 기준 폴더를 고치는 것은 bat이다 (사용자가 확인했을 때만).
/// 종료 코드: 0 두 파일 모두 비교했고 확인할 값 없음 / 1 비교하지 못한 파일 있음 (기준을 쓰면 건너뛴 경로 포함) / 2 사용법 오류 / 3 확인할 값 있음.
/// </summary>
public static class ConfigDriftReport
{
    public const string Flag = "--config-drift";

    /// <summary>비교하는 파일. publish가 덮지 않고 지키는 설정 파일과 같다.</summary>
    public static readonly string[] Files = ["modes.json", "appsettings.json"];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is not (3 or 4) || args[0] != Flag || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            error.WriteLine($"사용법: HB.Orchestration.Mcp.dll {Flag} <원본 폴더> <publish 폴더> [기준 폴더]");
            return 2;
        }

        var sourceDir = args[1];
        var liveDir = args[2];
        var baselineDir = args.Length == 4 ? args[3] : null;
        output.WriteLine(baselineDir is null
            ? "[설정 비교] 원본에만 있는 경로를 알립니다. 파일은 바꾸지 않습니다."
            : "[설정 비교] 원본에만 있는 경로와, 지난 확인 뒤 원본에서 바뀐 값을 알립니다. 파일은 바꾸지 않습니다.");
        output.WriteLine("            publish 폴더 기준입니다. ModesPath · HB_MODES_PATH · 프로젝트의 hb-modes.json을 쓰면 실제 사용 파일은 다를 수 있습니다.");

        var failed = false;
        var valuesToReview = false;
        foreach (var file in Files)
        {
            IReadOnlyList<ConfigDriftItem> items;
            try
            {
                var source = Load(Path.Combine(sourceDir, file));
                var live = Load(Path.Combine(liveDir, file));
                items = baselineDir is null
                    ? ConfigDrift.Compare(source, live)
                    : CompareWithBaseline(source, live, Path.Combine(baselineDir, file), file, output);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                output.WriteLine($"[경고] {file}: 비교하지 못했습니다 — {ex.Message}");
                failed = true;
                continue;
            }

            if (items.Count == 0)
            {
                output.WriteLine(baselineDir is null ? $"{file}: 원본에만 있는 경로 없음" : $"{file}: 알릴 것 없음");
                continue;
            }
            foreach (var item in items)
            {
                output.WriteLine($"{(item.Kind == ConfigDriftKind.ValueDiffers ? "[참고]" : "[알림]")} {file}: {Describe(item)}");
                valuesToReview |= item.Kind is ConfigDriftKind.SourceValueChanged or ConfigDriftKind.BothChanged or ConfigDriftKind.ValueDiffers;
                // 건너뛴 안쪽의 변경을 기준에 넣으면 그 알림을 영영 잃으므로, 기준을 쓸 때는 끝내지 못한 비교로 친다.
                failed |= baselineDir is not null && item.Kind == ConfigDriftKind.NotComparable;
            }
        }
        if (failed) return 1;
        return valuesToReview ? 3 : 0;
    }

    public static string Describe(ConfigDriftItem item) => item.Kind switch
    {
        ConfigDriftKind.OnlyInSource => $"원본에만 있음 {item.Path}",
        ConfigDriftKind.KindMismatch => $"자료형 다름 {item.Path} ({item.Detail})",
        ConfigDriftKind.SourceValueChanged => $"원본 값 바뀜 {item.Path} ({item.Detail})",
        ConfigDriftKind.BothChanged => $"원본과 사용 중이 둘 다 바뀜 {item.Path} ({item.Detail})",
        ConfigDriftKind.ValueDiffers => $"값 다름 {item.Path} ({item.Detail})",
        _ => $"비교 못 함 {item.Path} ({item.Detail})",
    };

    private static IReadOnlyList<ConfigDriftItem> CompareWithBaseline(
        JsonNode? source, JsonNode? live, string baselinePath, string file, TextWriter output)
    {
        if (!File.Exists(baselinePath)) return ConfigDrift.CompareValues(source, live, hasBaseline: false, baseline: null);
        try
        {
            return ConfigDrift.CompareValues(source, live, hasBaseline: true, Load(baselinePath));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // 기준은 다시 만들 수 있으니 비교를 멈추지 않는다.
            output.WriteLine($"[경고] {file}: 기준 파일을 읽지 못해 처음 비교처럼 봅니다 — {ex.Message}");
            return ConfigDrift.CompareValues(source, live, hasBaseline: false, baseline: null);
        }
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;

    // 서버(ModeStore)와 같은 방식으로 글자를 푼다 (BOM · 인코딩).
    private static JsonNode? Load(string path) =>
        ConfigDrift.Parse(GuidelinesLoader.DecodeLikeReadAllText(File.ReadAllBytes(path)));
}
