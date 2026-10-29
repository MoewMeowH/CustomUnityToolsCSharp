using System.Text.Json;
using System.Text.Json.Serialization;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// 골든 세트 사례 하나 (사례 폴더 = &lt;골든 폴더&gt;/&lt;id&gt;/).
/// case.json · input.md · expected.json은 필수, traps.json은 선택이다.
/// </summary>
public sealed record GoldenCase(GoldenCaseInfo Info, string Folder, string Input, IReadOnlyList<GoldenItem> Expected, IReadOnlyList<GoldenItem> Traps)
{
    /// <summary>expected.json + traps.json 원본 바이트의 해시. 정답지를 고치면 바뀐다 (점수 비교 때 입력 해시와 함께 본다).</summary>
    public string KeySha256 { get; init; } = "";

    /// <summary>replay 사례만: context.json (경로는 files/ 기준). 서버가 이 범위로 작업 폴더를 만든다.</summary>
    public IReadOnlyList<ContextRange> Context { get; init; } = [];

    /// <summary>replay 사례만: 복원한 파일 폴더 (사례 폴더/files).</summary>
    public string FilesFolder => Path.Combine(Folder, GoldenCaseLoader.FilesFolder);
}

/// <summary>
/// case.json. Kind는 "record"(판정 기록에서 옴) · "seeded"(고친 결함을 되돌린 코드) · "crash"(원인이 알려진 크래시, 다단계 모드 전체를 돌림)
/// "replay"(과거 검토형 서브에이전트 리뷰를 복원, API 리뷰 sub_* 모드로 돌림).
/// crash · replay 사례의 input.md는 run_mode input (서버가 "## 요청"으로 감쌈), 리뷰 사례는 보낼 프롬프트 원문이다.
/// </summary>
public sealed record GoldenCaseInfo
{
    public const string Crash = "crash";
    public const string Replay = "replay";

    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    /// <summary>모드 이름. 예전 이름(review · crash 등)으로 적힌 사례도 새 이름으로 읽는다 (ModeNames, 파일은 그대로).</summary>
    public string Mode { get => _mode; init => _mode = ModeNames.Normalize(value) ?? ""; }
    private readonly string _mode = "review_main";

    [JsonIgnore] public bool IsCrash => Kind == Crash;
    [JsonIgnore] public bool IsReplay => Kind == Replay;

    /// <summary>출처: runId, 커밋 등 (기록용).</summary>
    public string? Source { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// 기대 지적(expected.json) 또는 함정(traps.json) 하나.
/// Files는 파일 이름(경로 앞부분은 비교하지 않음) 후보, Lines는 [시작, 끝] 또는 [줄], Keywords는 하나라도 맞으면 된다.
/// </summary>
public sealed record GoldenItem
{
    public string Id { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<int> Lines { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// 필수 키워드 — 그 결함에만 맞는 말 (동의어 묶음, 하나라도 맞으면 됨). 있으면 찾음(강도 2 이상)은 이 말이 맞을 때만이고
    /// <see cref="Keywords"/>는 보조(줄만 맞은 것과 같은 강도 1)가 된다. 비우면 예전처럼 Keywords가 필수 역할 (채점기 ⑤, 2026-10-22).
    /// </summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>
    /// 위치 목록 — 원인 지점과 증상 지점처럼 여러 곳 중 하나라도 맞으면 위치 일치. 비우면 <see cref="Files"/> × <see cref="Lines"/> (예전 형식).
    /// </summary>
    public IReadOnlyList<GoldenLocation> Locations { get; init; } = [];

    /// <summary>정답 출처 (seeded · later-commit · self-review · conductor · common · ds · sol · user-test). 모델 비교 집합을 고를 때 쓴다.</summary>
    public string? Origin { get; init; }

    public string? Severity { get; init; }

    /// <summary>함정만: 왜 틀린 지적으로 판정됐는지 (결과에 함께 보여 "다른 관점에서 타당한지" 검토하게 한다).</summary>
    public string? Why { get; init; }

    /// <summary>crash 사례만: 어느 단계 본문에서 찾는지 — "triage"(분류) 또는 "search"(검색). 함정은 비우면 두 단계 모두.</summary>
    public string? Step { get; init; }

    /// <summary>crash search 항목: 정답 이슈 URL의 일부 (예: "issuetracker.unity.com/issues/11844"). 검색 단계가 실제로 본 URL에 있어야 찾음.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    public const string StepTriage = "triage";
    public const string StepSearch = "search";

    [JsonIgnore] public int? From => Lines.Count > 0 ? Lines[0] : null;
    [JsonIgnore] public int? To => Lines.Count > 1 ? Lines[1] : From;

    /// <summary>채점에 쓰는 위치: <see cref="Locations"/>, 없으면 Files마다 Lines 범위 하나 (예전 형식).</summary>
    [JsonIgnore]
    public IReadOnlyList<GoldenLocation> AllLocations =>
        Locations.Count > 0 ? Locations : [.. Files.Select(f => new GoldenLocation { File = f, From = From, To = To })];
}

/// <summary>정답 위치 하나 (파일 이름 + 줄 범위, 줄은 비울 수 있음).</summary>
public sealed record GoldenLocation
{
    public string? File { get; init; }
    public int? From { get; init; }
    public int? To { get; init; }
}

public static class GoldenCaseLoader
{
    public const string CaseFile = "case.json";
    public const string InputFile = "input.md";
    public const string ExpectedFile = "expected.json";
    public const string TrapsFile = "traps.json";
    public const string ContextFile = "context.json";
    public const string FilesFolder = "files";

    /// <summary>결과 폴더 이름. 사례 목록에서 뺀다.</summary>
    public const string ResultsFolder = "results";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 사례를 읽는다. which가 "all"이면 폴더 안 모든 사례(이름순), 아니면 그 이름 하나.
    /// 하나라도 형식이 틀리면 아무것도 돌리지 않도록 오류 목록을 함께 돌려준다 (실행 전 거절).
    /// </summary>
    public static (List<GoldenCase> Cases, List<string> Errors) LoadMany(string root, string which)
    {
        var cases = new List<GoldenCase>();
        var errors = new List<string>();
        if (!Directory.Exists(root)) return (cases, [$"골든 폴더가 없습니다: {root}"]);
        // 사례 이름으로 골든 폴더 밖을 가리키지 않게 한다.
        if (which != "all" && !IsPlainName(which)) return (cases, [$"사례 이름은 폴더 이름 하나여야 합니다 ('..'·경로 구분자·드라이브 불가): '{which}'"]);

        IEnumerable<string> dirs = which == "all"
            ? Directory.GetDirectories(root)
                .Where(d => !string.Equals(Path.GetFileName(d), ResultsFolder, StringComparison.OrdinalIgnoreCase))
                .Where(d => !Path.GetFileName(d).StartsWith('_')) // _보관 · _company(따로 돌리는 사례 묶음) 같은 폴더는 사례가 아님
                .Where(d => !Path.GetFileName(d).StartsWith('.')) // .git 등 (골든 폴더를 별도 저장소로 둠 — 2026-10-27)
                .Order(StringComparer.OrdinalIgnoreCase)
            : [Path.Combine(root, which)];

        foreach (var dir in dirs)
        {
            var (c, error) = Load(dir);
            if (error is not null) errors.Add($"{Path.GetFileName(dir)}: {error}");
            else cases.Add(c!);
        }
        if (cases.Count == 0 && errors.Count == 0) errors.Add($"사례가 없습니다: {root}");
        return (cases, errors);
    }

    private static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static (GoldenCase? Case, string? Error) Load(string dir)
    {
        if (!Directory.Exists(dir)) return (null, "사례 폴더가 없습니다.");
        try
        {
            var info = Read<GoldenCaseInfo>(dir, CaseFile, required: true);
            var inputPath = Path.Combine(dir, InputFile);
            if (!File.Exists(inputPath)) return (null, $"{InputFile}이(가) 없습니다.");
            var input = GuidelinesLoader.DecodeLikeReadAllText(File.ReadAllBytes(inputPath));
            var expected = Read<List<GoldenItem?>>(dir, ExpectedFile, required: true) ?? [];
            var traps = Read<List<GoldenItem?>>(dir, TrapsFile, required: false) ?? [];

            var name = Path.GetFileName(dir.TrimEnd('/', '\\'));
            if (info is null || string.IsNullOrWhiteSpace(info.Id)) return (null, $"{CaseFile}에 id가 없습니다.");
            if (!string.Equals(info.Id, name, StringComparison.OrdinalIgnoreCase)) return (null, $"{CaseFile}의 id '{info.Id}'가 폴더 이름 '{name}'과 다릅니다.");
            if (info.Kind is not ("record" or "seeded" or GoldenCaseInfo.Crash or GoldenCaseInfo.Replay))
                return (null, $"{CaseFile}의 kind는 record · seeded · crash · replay 중 하나여야 합니다 (지금 '{info.Kind}').");
            if (string.IsNullOrWhiteSpace(info.Mode)) return (null, $"{CaseFile}의 mode가 비어 있습니다 (생략하면 review_main).");
            if (string.IsNullOrWhiteSpace(input)) return (null, $"{InputFile}이(가) 비어 있습니다.");
            if (expected.Count == 0) return (null, $"{ExpectedFile}에 기대 지적이 없습니다.");
            var problem = info.IsCrash
                ? CrashItemProblem(expected, ExpectedFile, isTrap: false) ?? CrashItemProblem(traps, TrapsFile, isTrap: true)
                : ItemProblem(expected, ExpectedFile) ?? ItemProblem(traps, TrapsFile);
            if (problem is not null) return (null, problem);

            IReadOnlyList<ContextRange> context = [];
            if (info.IsReplay)
            {
                var (ranges, contextProblem) = ReplayContext(dir);
                if (contextProblem is not null) return (null, contextProblem);
                context = ranges!;
            }

            return (new GoldenCase(info, dir, input, [.. expected!], [.. traps!]) { KeySha256 = KeyHash(dir), Context = context }, null);
        }
        catch (JsonException ex) { return (null, $"JSON 형식 오류: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, $"읽기 실패: {ex.Message}"); }
    }

    /// <summary>replay: context.json(필수, 비면 안 됨)의 경로가 모두 files/ 안의 파일인지. 실행 전 거절용.</summary>
    private static (IReadOnlyList<ContextRange>? Ranges, string? Problem) ReplayContext(string dir)
    {
        var ranges = Read<List<ContextRange?>>(dir, ContextFile, required: false);
        if (ranges is null || ranges.Count == 0) return (null, $"replay 사례는 {ContextFile}(files/ 기준 경로 · 줄 범위)이 필요합니다.");
        var files = Path.GetFullPath(Path.Combine(dir, FilesFolder));
        foreach (var r in ranges)
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Path) || r.StartLine < 1 || r.EndLine < r.StartLine)
                return (null, $"{ContextFile}: 빈 항목이나 잘못된 줄 범위가 있습니다.");
            var full = Path.GetFullPath(Path.Combine(files, r.Path));
            if (!full.StartsWith(files + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return (null, $"{ContextFile}: '{r.Path}'이(가) {FilesFolder}/ 안에 없습니다.");
        }
        return ([.. ranges!], null);
    }

    private static T? Read<T>(string dir, string file, bool required)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path))
            return required ? throw new IOException($"{file}이(가) 없습니다.") : default;
        return JsonSerializer.Deserialize<T>(GuidelinesLoader.DecodeLikeReadAllText(File.ReadAllBytes(path)), Json);
    }

    /// <summary>
    /// 정답지 묶음 해시 (동결 표시용, SHA-256 앞 16자리): 사례 폴더 이름 순(Ordinal)으로 사례마다 "&lt;사례&gt;/expected.json\n" + 원본 바이트,
    /// traps.json이 있으면 "&lt;사례&gt;/traps.json\n" + 원본 바이트를 이어 붙인다. 함정도 점수(함정 수)를 바꾸므로 넣는다
    /// (2026-10-22 동결부터 — 그 전 동결 aab21c2e23ed4e5a · 61c79cbf9388aba5는 expected.json만 이어 붙인 값).
    /// </summary>
    public static string SetKeyHash(IEnumerable<GoldenCase> cases)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var c in cases.OrderBy(c => Path.GetFileName(c.Folder), StringComparer.Ordinal))
        {
            var name = Path.GetFileName(c.Folder);
            foreach (var file in new[] { ExpectedFile, TrapsFile })
            {
                var path = Path.Combine(c.Folder, file);
                if (!File.Exists(path)) continue;
                sha.AppendData(System.Text.Encoding.UTF8.GetBytes($"{name}/{file}\n"));
                sha.AppendData(File.ReadAllBytes(path));
            }
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
    }

    private static string KeyHash(string dir)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in new[] { ExpectedFile, TrapsFile })
        {
            var path = Path.Combine(dir, file);
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(file + "\n"));
            if (File.Exists(path)) sha.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// 짝을 지을 단서(줄 또는 키워드)가 없는 항목, 중복 id, 잘못된 줄 범위, 명시적 null.
    /// System.Text.Json은 "files": null 같은 값으로 기본값 []를 덮으므로 여기서 형식 오류로 돌려준다 (NullReferenceException으로 죽지 않게).
    /// </summary>
    private static string? ItemProblem(IReadOnlyList<GoldenItem?> items, string file)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not { } item) return $"{file}: {i + 1}번째 항목이 null입니다.";
            if (item.Files is null || item.Lines is null || item.Keywords is null || item.Required is null || item.Locations is null
                || item.Files.Any(f => f is null) || item.Keywords.Any(k => k is null) || item.Required.Any(k => k is null) || item.Locations.Any(l => l is null))
                return $"{file}: '{item.Id}'의 files·lines·keywords·required·locations는 null일 수 없습니다 (비우려면 []).";
            if (string.IsNullOrWhiteSpace(item.Id)) return $"{file}: id가 빈 항목이 있습니다.";
            if (!seen.Add(item.Id)) return $"{file}: id '{item.Id}'가 두 번 나옵니다.";
            if (item.Lines.Count > 2 || item.Lines.Any(l => l < 1) || (item.Lines.Count == 2 && item.Lines[1] < item.Lines[0]))
                return $"{file}: '{item.Id}'의 lines는 [줄] 또는 [시작, 끝]이어야 합니다.";
            if (item.Lines.Count > 0 && item.Files.Count == 0) return $"{file}: '{item.Id}'는 lines가 있는데 files가 없습니다.";
            // 채점은 파일 이름이 있는 위치만 쓴다 — 파일 없는 위치 · 빈 범위는 오류 대신 조용한 "놓침"이 되므로 여기서 거절한다.
            foreach (var l in item.Locations)
                if (string.IsNullOrWhiteSpace(l.File) || l.From is not { } from || from < 1 || (l.To is { } to && to < from))
                    return $"{file}: '{item.Id}'의 locations는 {{ file, from ≥ 1, to ≥ from(선택) }}이어야 합니다.";
            if (item.Keywords.Concat(item.Required).FirstOrDefault(k => k.Length == 0 || k != k.Trim()) is { } bad)
                return $"{file}: '{item.Id}'의 키워드 '{bad}' — 빈 말이나 앞뒤 공백은 안 됩니다 (공백이 경계 검사를 없앤다).";
            // 필수 키워드만 있는 항목도 짝을 지을 수 있다 (보조 키워드는 비워도 됨)
            if (item.AllLocations.All(l => l.From is null) && item.Keywords.Concat(item.Required).All(string.IsNullOrWhiteSpace))
                return $"{file}: '{item.Id}'는 줄 위치도 keywords·required도 없어 짝을 지을 수 없습니다.";
        }
        return null;
    }

    /// <summary>crash 항목: step은 triage·search (함정은 비워도 됨), triage는 keywords 필수, search는 urls나 keywords 중 하나. files·lines는 쓰지 않는다.</summary>
    private static string? CrashItemProblem(IReadOnlyList<GoldenItem?> items, string file, bool isTrap)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not { } item) return $"{file}: {i + 1}번째 항목이 null입니다.";
            if (item.Keywords is null || item.Urls is null || item.Files is null || item.Lines is null || item.Keywords.Any(k => k is null) || item.Urls.Any(u => u is null))
                return $"{file}: '{item.Id}'의 keywords·urls는 null일 수 없습니다 (비우려면 []).";
            if (string.IsNullOrWhiteSpace(item.Id)) return $"{file}: id가 빈 항목이 있습니다.";
            if (!seen.Add(item.Id)) return $"{file}: id '{item.Id}'가 두 번 나옵니다.";
            if (item.Files.Count > 0 || item.Lines.Count > 0) return $"{file}: '{item.Id}' — crash 사례는 files·lines를 쓰지 않습니다.";
            // crash 채점은 keywords(예전 검사)만 본다 — 적어도 조용히 무시되므로 거절
            if (item.Required is not { Count: 0 } || item.Locations is not { Count: 0 })
                return $"{file}: '{item.Id}' — crash 사례는 required·locations를 쓰지 않습니다 (keywords만 봄).";
            if (item.Step is not (GoldenItem.StepTriage or GoldenItem.StepSearch) && !(isTrap && item.Step is null))
                return $"{file}: '{item.Id}'의 step은 triage 또는 search여야 합니다{(isTrap ? " (함정은 비우면 두 단계 모두)" : "")}.";
            if (item.Urls.Any(string.IsNullOrWhiteSpace)) return $"{file}: '{item.Id}'의 urls에 빈 조각이 있습니다 (모든 URL에 맞게 됨).";
            var hasKeyword = item.Keywords.Any(k => !string.IsNullOrWhiteSpace(k));
            var hasUrl = item.Urls.Count > 0;
            if (item.Step == GoldenItem.StepSearch && !isTrap ? !hasKeyword && !hasUrl : !hasKeyword)
                return $"{file}: '{item.Id}'에 짝을 지을 단서가 없습니다 ({(item.Step == GoldenItem.StepSearch && !isTrap ? "urls나 keywords" : "keywords")} 필요).";
        }
        return null;
    }
}
