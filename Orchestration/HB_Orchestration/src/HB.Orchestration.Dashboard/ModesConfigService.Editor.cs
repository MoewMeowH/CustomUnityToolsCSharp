using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// 그래프 편집기의 저장: 모드 하나 · 역할 하나 단위. 사용 중 사본(화면이 본 파일)에 쓴 뒤,
/// 설정 Dashboard:ModesSourcePath가 있으면 같은 동작을 git 원본에도 적용한다 (파일을 통째로 덮지 않는다 — 두 파일이 다른 곳은 남는다).
/// </summary>
public sealed partial class ModesConfigService
{
    /// <summary>원본 반영 실패 안내의 머리 (화면이 경고로 보인다).</summary>
    public const string SourceFailedPrefix = "원본에는 반영하지 못했습니다";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z")]
    private static partial Regex EditorNameRegex();

    /// <summary>
    /// 편집 동작 하나. 문제가 있으면 이유를 돌려준다.
    /// strict: 사용 중 사본에 적용할 때 — 화면이 본 대상이 없으면 오류. 원본에는 느슨하게 (없으면 더하거나 건너뛴다).
    /// </summary>
    private delegate string? ModesEdit(JsonObject root, bool strict);

    /// <summary>git 원본 경로가 설정돼 있는가.</summary>
    public bool HasModesSource => !string.IsNullOrWhiteSpace(options.Value.ModesSourcePath);

    /// <summary>
    /// 이 파일을 저장할 때 git 원본에도 적용하는가 (화면 안내용). 원본은 서버 폴더의 기본 modes.json의 원본이라,
    /// 편집 대상이 다른 파일(Dashboard:ModesPath · MCP appsettings ModesPath · HB_MODES_PATH)이면 적용하지 않는다.
    /// </summary>
    public bool WritesSource(ModesSnapshot snap) => HasModesSource && IsServerFolderFile(snap);

    private static bool IsServerFolderFile(ModesSnapshot snap) =>
        snap.ModesPathSource == ModesPathResolver.Label(ModesPathResolver.Source.ServerFolder);

    /// <summary>모드를 더하거나 바꾼다. originalName이 비면 새 모드, name과 다르면 이름 바꾸기 (자리는 그대로).</summary>
    /// <param name="modeJson">모드 객체 전체 (화면이 받은 객체를 고친 것 — 화면이 모르는 키도 그대로 온다).</param>
    public SaveResult SaveMode(string expectedStamp, string? originalName, string name, string modeJson)
    {
        name = (name ?? "").Trim();
        var original = string.IsNullOrWhiteSpace(originalName) ? null : originalName.Trim();
        if (NameProblem("모드", name) is { } nameError) return SaveResult.Fail(nameError);
        if (ModeNames.Renamed.ContainsKey(name))
            return SaveResult.Fail($"'{name}'은(는) 예전 모드 이름이라 쓸 수 없습니다 (지금 이름: {ModeNames.Normalize(name)}).");
        var (parsed, parseError) = ParseObject(modeJson, "모드");
        if (parsed is not { } template) return SaveResult.Fail(parseError!);

        return ApplyEdit(expectedStamp, (root, strict) =>
        {
            var modes = EnsureObject(root, "Modes");
            var originalKey = original is null ? null : ExistingKey(modes, original);
            if (strict && original is not null && originalKey is null)
                return $"모드 '{original}'이(가) 파일에 없습니다. '다시 불러오기' 후 다시 하세요.";

            var nameKey = ExistingKey(modes, name);
            var node = template.DeepClone(); // 사본과 원본에 한 번씩 적용되므로 매번 새 사본
            if (originalKey is null)
            {
                if (nameKey is null) modes[name] = node;
                else if (strict && original is null) return $"모드 '{name}'이(가) 이미 있습니다.";
                else modes[nameKey] = node;
                return null;
            }

            if (nameKey is not null && nameKey != originalKey) return $"모드 '{name}'이(가) 이미 있습니다.";
            ReplaceEntry(modes, originalKey, name, node);
            return null;
        }, ownPrefix: $"[{name}] ");
    }

    public SaveResult DeleteMode(string expectedStamp, string name)
    {
        name = (name ?? "").Trim();
        return ApplyEdit(expectedStamp, (root, strict) =>
        {
            var modes = Child(root, "Modes") as JsonObject;
            var key = modes is null ? null : ExistingKey(modes, name);
            if (key is null) return strict ? $"모드 '{name}'이(가) 파일에 없습니다. '다시 불러오기' 후 다시 하세요." : null;
            modes!.Remove(key);
            return null;
        });
    }

    /// <summary>역할을 더하거나 바꾼다 (이름 바꾸기는 없다). 내장 역할과 같은 이름이면 그 역할을 덮어쓴다.</summary>
    /// <param name="isNew">화면의 "새 역할"로 만든 것 — 이미 있는 이름(파일 · 내장)이면 거절한다 (있는 역할을 모르고 덮지 않게).</param>
    public SaveResult SaveRole(string expectedStamp, string name, string roleJson, bool isNew = false)
    {
        name = (name ?? "").Trim();
        if (NameProblem("역할", name) is { } nameError) return SaveResult.Fail(nameError);
        var (parsed, parseError) = ParseObject(roleJson, "역할");
        if (parsed is not { } template) return SaveResult.Fail(parseError!);

        var builtIn = Prompts.BuiltInRoles.ContainsKey(name);
        if (isNew && builtIn)
            return SaveResult.Fail($"'{name}'은(는) 내장 역할 이름입니다. 역할 목록에서 그 역할을 열어 덮어쓰세요.");
        // 내장이 아닌 역할은 프롬프트가 없으면 실행 때 "정의되지 않았습니다"로 거절된다 (내장은 프롬프트를 비우면 내장 것을 쓴다).
        if (!builtIn && string.IsNullOrWhiteSpace(Text(template, "Prompt")) && string.IsNullOrWhiteSpace(Text(template, "PromptFile")))
            return SaveResult.Fail($"역할 '{name}'에 프롬프트가 없습니다. 프롬프트를 적으세요.");

        return ApplyEdit(expectedStamp, (root, strict) =>
        {
            var roles = EnsureObject(root, "Roles");
            var key = ExistingKey(roles, name);
            if (strict && isNew && key is not null) return $"역할 '{name}'이(가) 이미 있습니다. 역할 목록에서 그 역할을 열어 고치세요.";
            roles[key ?? name] = template.DeepClone();
            return null;
        });
    }

    /// <summary>문자열 값. 없거나 문자열이 아니면 null.</summary>
    private static string? Text(JsonObject obj, string name) =>
        Child(obj, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>역할을 지운다. 쓰는 단계가 있으면 지우지 않는다 — 내장 역할을 덮어쓴 것은 지워도 내장 프롬프트로 돌아가므로 지운다.</summary>
    public SaveResult DeleteRole(string expectedStamp, string name)
    {
        name = (name ?? "").Trim();
        return ApplyEdit(expectedStamp, (root, strict) =>
        {
            var roles = Child(root, "Roles") as JsonObject;
            var key = roles is null ? null : ExistingKey(roles, name);
            if (key is null) return strict ? $"역할 '{name}'이(가) 파일에 없습니다. '다시 불러오기' 후 다시 하세요." : null;
            if (!Prompts.BuiltInRoles.ContainsKey(name) && ModesUsingRole(root, name) is { Count: > 0 } users)
                return $"역할 '{name}'을(를) 쓰는 모드가 있어 지울 수 없습니다: {string.Join(", ", users)}. 그 단계의 역할을 먼저 바꾸세요.";
            roles!.Remove(key);
            return null;
        });
    }

    /// <summary>이 역할을 쓰는 단계가 있는 모드 이름들.</summary>
    public static List<string> ModesUsingRole(JsonObject root, string role)
    {
        var users = new List<string>();
        if (Child(root, "Modes") is not JsonObject modes) return users;
        foreach (var (modeName, modeNode) in modes)
        {
            if (modeNode is not JsonObject mode || Child(mode, "Steps") is not JsonArray steps) continue;
            if (steps.OfType<JsonObject>().Any(s => string.Equals(Child(s, "Role")?.ToString(), role, StringComparison.OrdinalIgnoreCase)))
                users.Add(modeName);
        }
        return users;
    }

    /// <param name="ownPrefix">고치는 모드의 오류 머리 ("[이름] "). 그 모드의 오류는 전부터 있던 것이어도 막는다.</param>
    private SaveResult ApplyEdit(string expectedStamp, ModesEdit edit, string? ownPrefix = null) => WithSaveLock(() =>
    {
        var snap = Load();
        if (snap.Stamp != expectedStamp) return SaveResult.Fail(ChangedElsewhere);
        var before = Validate(Serialize(snap.Root), snap.Agents, snap.ProviderTypes);
        if (edit(snap.Root, strict: true) is { } error) return SaveResult.Fail(error);

        var json = Serialize(snap.Root);
        var result = Validate(json, snap.Agents, snap.ProviderTypes);
        if (NewErrors(before, result, ownPrefix) is { Count: > 0 } errors) return new SaveResult(errors, result.Warnings);

        WriteSafely(snap.ModesPath, json, "modes-backup");
        return new SaveResult([], result.Warnings) { Notes = [ApplyToSource(edit, snap, ownPrefix)] };
    });

    /// <summary>
    /// 이번 수정으로 새로 생긴 오류와 고친 모드 자체의 오류. 다른 모드에 전부터 있던 오류로는 막지 않는다
    /// — 막으면 오류 난 모드가 둘일 때 하나씩 고칠 수 없다.
    /// </summary>
    private static List<string> NewErrors(SaveResult before, SaveResult after, string? ownPrefix) =>
        [.. after.Errors.Where(e => !before.Errors.Contains(e) || (ownPrefix is not null && e.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>
    /// 같은 동작을 git 원본에도 적용하고 결과 안내를 돌려준다. 실패해도 사용 중 사본의 저장은 그대로 둔다
    /// (원본 문제 때문에 서버 설정을 못 고치게 되지 않도록) — 안내가 <see cref="SourceFailedPrefix"/>로 시작한다.
    /// 에이전트 이름은 검사하지 않는다 (원본 쪽 appsettings.json의 에이전트는 사본과 다를 수 있다).
    /// </summary>
    private string ApplyToSource(ModesEdit edit, ModesSnapshot snap, string? ownPrefix)
    {
        static string Failed(string why) => $"{SourceFailedPrefix}: {why}";

        var configured = options.Value.ModesSourcePath;
        if (string.IsNullOrWhiteSpace(configured))
            return "원본 경로 설정(Dashboard:ModesSourcePath)이 없어 사용 중 사본만 저장했습니다.";
        if (!IsServerFolderFile(snap))
            return $"편집한 파일이 서버 폴더의 기본 modes.json이 아니라({snap.ModesPathSource}) git 원본에는 적용하지 않았습니다.";
        try
        {
            var path = Path.GetFullPath(configured);
            if (string.Equals(path, Path.GetFullPath(snap.ModesPath), StringComparison.OrdinalIgnoreCase))
                return "원본 경로가 사용 중 사본과 같은 파일입니다.";
            // 설정 실수로 엉뚱한 파일을 고치지 않게: 이름이 modes.json이고 Modes가 있는 JSON일 때만 쓴다.
            if (!string.Equals(Path.GetFileName(path), ModesPathResolver.DefaultFileName, StringComparison.OrdinalIgnoreCase))
                return Failed($"파일 이름이 {ModesPathResolver.DefaultFileName}이 아닙니다 ({path}).");
            if (!File.Exists(path)) return Failed($"파일이 없습니다 ({path}).");

            string text;
            using (var reader = new StreamReader(new MemoryStream(File.ReadAllBytes(path)), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            if (JsonNode.Parse(text, nodeOptions: null, documentOptions: DocOptions) is not JsonObject root || Child(root, "Modes") is not JsonObject)
                return Failed($"Modes가 있는 modes.json이 아닙니다 ({path}).");

            // 원본 쪽 에이전트 · 제공사는 모르므로 빈 목록 (웹검색은 설정 값 · 구조화 출력만 본다)
            var noTypes = new Dictionary<string, string>();
            var unchanged = Serialize(root);
            var before = Validate(unchanged, [], noTypes);
            if (edit(root, strict: false) is { } error) return Failed(error);
            var json = Serialize(root);
            if (json == unchanged) return "git 원본은 바꿀 것이 없었습니다 (이미 같은 내용이거나 그 항목이 없음).";
            if (NewErrors(before, Validate(json, [], noTypes), ownPrefix) is { Count: > 0 } errors) return Failed(string.Join(" / ", errors));

            WriteSafely(path, json, backupFolderName: null);
            return $"git 원본에도 반영했습니다 ({path}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? NameProblem(string kind, string name) =>
        EditorNameRegex().IsMatch(name) ? null : $"{kind} 이름 '{name}'은(는) 영문 · 숫자 · _ · - 1~64자여야 합니다.";

    private static (JsonObject? Node, string? Error) ParseObject(string? json, string kind)
    {
        try
        {
            return JsonNode.Parse(json ?? "", nodeOptions: null, documentOptions: DocOptions) is JsonObject obj
                ? (obj, null)
                : (null, $"{kind} 내용이 JSON 객체가 아닙니다.");
        }
        catch (JsonException ex)
        {
            return (null, $"{kind} 내용의 JSON 형식 오류: {ex.Message}");
        }
    }

    /// <summary>대소문자를 무시하고 찾은 실제 키. 없으면 null.</summary>
    private static string? ExistingKey(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static JsonObject EnsureObject(JsonObject root, string name)
    {
        if (Child(root, name) is JsonObject existing) return existing;
        var created = new JsonObject();
        root[KeyOf(root, name)] = created;
        return created;
    }

    /// <summary>항목을 같은 자리에서 바꾼다 (키가 달라지면 이름 바꾸기 — 차례를 지키려고 다시 채운다).</summary>
    private static void ReplaceEntry(JsonObject obj, string oldKey, string newKey, JsonNode value)
    {
        if (oldKey == newKey)
        {
            obj[oldKey] = value;
            return;
        }

        var entries = obj.ToList();
        obj.Clear();
        foreach (var (key, node) in entries)
        {
            if (key == oldKey) obj[newKey] = value;
            else obj[key] = node;
        }
    }
}
