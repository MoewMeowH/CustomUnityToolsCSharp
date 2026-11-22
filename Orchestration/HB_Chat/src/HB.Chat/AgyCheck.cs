using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// agy(Antigravity CLI) 설정 검사 — 읽기만 하고 고치지 않는다 (사용자 결정 "검사 + 추천 JSON만").
/// 2026-11-22 실측(agy 1.5.14): Default 모드(agentMode "") · 터미널 샌드박스 · 드라이브 문자가 붙은 read_file deny가 같이 있어야
/// 도구 읽기 · 셸 읽기(사람이 승인해도)가 막혔다 — auto 모드는 deny 밖 경로를 묻지 않고 읽었고, Default만이면 승인한 셸이 샌드박스 밖에서 돌았고,
/// 드라이브 없는 "/AI/…" 항목은 도구 읽기를 막지 못했다.
/// </summary>
public static partial class AgyCheck
{
    /// <param name="Problems">하나라도 있으면 중계를 막는다.</param>
    /// <param name="Warnings">막지 않는 것 (권장 deny · 넓은 allow · 승인창이 늘어나는 것).</param>
    /// <param name="MissingDeny">deny가 덮지 않는 필수 경로 (규칙 경로 모양).</param>
    /// <param name="MissingRecommended">deny가 덮지 않는 권장 경로.</param>
    /// <param name="SuggestedJson">지금 파일에 필요한 키를 합친 전문 (사용자가 복사해 넣는다).</param>
    public sealed record Result(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, IReadOnlyList<string> MissingDeny,
        IReadOnlyList<string> MissingRecommended, string SuggestedJson)
    {
        public bool Ok => Problems.Count == 0;
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>규칙 안 경로 모양: 드라이브 대문자 · "/" 구분 · 끝 "/" 없음 (드라이브 루트는 "C:/").</summary>
    public static string RulePath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':') full = char.ToUpperInvariant(full[0]) + full[1..];
        return full.Length > 3 ? full.TrimEnd('/') : full;
    }

    /// <summary>규칙 <c>action(경로)</c>의 경로 부분. 그 action이 아니면 null.</summary>
    public static string? Inner(string rule, string action) =>
        rule.StartsWith(action + "(", StringComparison.Ordinal) && rule.EndsWith(')') ? rule[(action.Length + 1)..^1] : null;

    /// <summary>
    /// 규칙 경로가 그 경로를 덮는지 — 같거나 조상. 드라이브 문자가 없는 규칙 경로는 덮지 않는 것으로 본다 (실측: 도구 읽기를 막지 못함).
    /// target은 <see cref="RulePath"/> 모양.
    /// </summary>
    public static bool Covers(string rulePath, string target)
    {
        if (!DriveRule().IsMatch(rulePath)) return false;
        var rule = RulePath(rulePath);
        return string.Equals(rule, target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(rule.EndsWith('/') ? rule : rule + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// settings.json 글을 본다. json이 null이면 파일 없음.
    /// </summary>
    /// <param name="required">deny가 덮어야 하는 경로 (없으면 막힘).</param>
    /// <param name="recommended">덮으면 좋은 경로 (경고만).</param>
    /// <param name="relayCurrent">agy가 결과를 쓰는 폴더 — allow write_file이 없으면 쓸 때마다 승인창 (경고).</param>
    public static Result Inspect(string? json, IReadOnlyList<string> required, IReadOnlyList<string> recommended, string relayCurrent)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        JsonObject root;
        var readable = true;
        if (json is null)
        {
            problems.Add("agy 설정 파일이 없습니다 — 곁칸 셸에서 agy를 한 번 띄워 로그인하고 /config를 저장하세요.");
            root = [];
        }
        else
        {
            try
            {
                // 손으로 고친 파일의 주석 · 끝 쉼표도 받는다
                root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new JsonException("맨 위가 객체가 아닙니다");
            }
            catch (JsonException ex)
            {
                problems.Add($"agy 설정 파일을 읽지 못했습니다: {ex.Message}");
                warnings.Add("파일을 읽지 못해 넣을 JSON에는 필요한 키만 담았습니다 — 기존 내용과 합쳐 넣으세요.");
                root = [];
                readable = false;
            }
        }

        if (readable && json is not null)
        {
            if (root["agentMode"] is not JsonValue mode || mode.GetValueKind() != JsonValueKind.String || mode.GetValue<string>() != "")
                problems.Add("Agent Mode가 Default가 아닙니다 — agy /config → Agent Mode → Default (\"agentMode\": \"\"). auto는 deny 밖 경로를 묻지 않고 읽습니다.");
            if (root["enableTerminalSandbox"] is not JsonValue sandbox || sandbox.GetValueKind() != JsonValueKind.True)
                problems.Add("터미널 샌드박스가 꺼져 있습니다 — agy /config에서 켜기 (\"enableTerminalSandbox\": true). 꺼져 있으면 승인한 셸 명령이 deny 구역을 읽습니다.");
        }

        // 객체가 아닌 permissions에 이름으로 접근하면 JsonNode가 예외를 던진다 — 문제로 돌린다
        var permissionsNode = root["permissions"];
        var permissionsObject = permissionsNode as JsonObject;
        if (permissionsNode is not null && permissionsObject is null) problems.Add("agy 설정의 permissions가 객체가 아닙니다 — 아래 넣을 JSON으로 바꾸세요.");
        var deny = Strings(permissionsObject?["deny"]);
        var allow = Strings(permissionsObject?["allow"]);
        var denyPaths = deny.Select(r => Inner(r, "read_file")).OfType<string>().ToList();
        foreach (var bad in denyPaths.Where(p => p != "*" && !DriveRule().IsMatch(p)))
            warnings.Add($"드라이브 문자가 없는 deny 항목은 도구 읽기를 막지 못합니다: read_file({bad})");
        var covered = (string target) => denyPaths.Contains("*") || denyPaths.Any(d => Covers(d, target));
        var missing = required.Select(RulePath).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !covered(p)).ToList();
        var missingRecommended = recommended.Select(RulePath).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !covered(p)).ToList();
        if (missing.Count > 0) problems.Add($"deny가 덮지 않는 필수 경로가 {missing.Count}개 있습니다 — 아래 넣을 JSON을 쓰세요.");
        if (missingRecommended.Count > 0) warnings.Add($"권장 deny {missingRecommended.Count}개가 없습니다 (중계 폴더가 있는 드라이브의 다른 폴더 — 없으면 읽을 때 승인창이 지킵니다).");

        var current = RulePath(relayCurrent);
        if (covered(current)) problems.Add($"deny가 중계 폴더({current})까지 덮습니다 — agy가 요청을 읽거나 결과를 쓰지 못합니다 (deny가 allow를 이김).");
        var allowNeeded = $"write_file({current})";
        var allowWrites = allow.Select(r => Inner(r, "write_file")).OfType<string>().ToList();
        var allowOk = allowWrites.Any(a => Covers(a, current));
        if (!allowOk) warnings.Add($"allow에 {allowNeeded}가 없습니다 — agy가 결과를 쓸 때마다 승인창이 뜹니다.");
        foreach (var wide in allow.Where(r => r is "*" || Inner(r, "read_file") == "*" || Inner(r, "write_file") == "*"))
            warnings.Add($"allow에 넓은 항목이 있습니다: {wide} (deny가 이기므로 필수 경로는 막히지만, 그 밖은 묻지 않고 열립니다).");

        // 넣을 JSON: 지금 파일에 필요한 키만 더하거나 고친다 (다른 키 · 기존 항목은 그대로)
        var suggested = readable ? (JsonObject)root.DeepClone() : [];
        suggested["agentMode"] = "";
        suggested["enableTerminalSandbox"] = true;
        if (suggested["permissions"] is not JsonObject permissions) suggested["permissions"] = permissions = [];
        permissions["deny"] = new JsonArray([.. deny.Concat(missing.Concat(missingRecommended).Select(p => $"read_file({p})")).Select(s => (JsonNode?)JsonValue.Create(s))]);
        permissions["allow"] = new JsonArray([.. (allowOk ? allow : [.. allow, allowNeeded]).Select(s => (JsonNode?)JsonValue.Create(s))]);
        return new Result(problems, warnings, missing, missingRecommended, suggested.ToJsonString(Pretty));
    }

    /// <summary>
    /// deny가 덮어야 하는 경로: 사용자 프로필 · 데이터 폴더 · 작업 폴더 · Unity 프로젝트 · 보호 폴더(앱 · 저장소 · Claude · Codex 설정 등) ·
    /// 중계 폴더가 없는 드라이브 루트(고정 · 네트워크 · 이동식). 다른 항목 안인 항목은 뺀다 (넣을 JSON이 짧게).
    /// </summary>
    public static IReadOnlyList<string> RequiredPaths(string profile, string dataFolder, IEnumerable<string> projects, IEnumerable<string> driveRoots, string relay)
    {
        var relayRoot = RulePath(Path.GetPathRoot(Path.GetFullPath(relay)) ?? relay);
        var all = new[] { profile, dataFolder }.Concat(projects).Where(p => !string.IsNullOrWhiteSpace(p)).Select(RulePath)
            .Concat(driveRoots.Select(RulePath).Where(r => !string.Equals(r, relayRoot, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return [.. all.Where(p => !all.Any(o => !string.Equals(o, p, StringComparison.OrdinalIgnoreCase) && Covers(o, p)))];
    }

    /// <summary>
    /// 중계 폴더가 못 쓰이는 까닭 (null이면 됨): 드라이브 문자 절대 경로 · 드라이브 루트 아님 · 필수 deny 경로 안이 아님(deny가 allow를 이겨 agy가 못 씀) ·
    /// 필수 경로를 품지 않음.
    /// </summary>
    public static string? WhyBadRelay(string relay, IReadOnlyList<string> required)
    {
        if (!DriveRule().IsMatch(relay)) return "중계 폴더는 드라이브 문자부터 쓴 절대 경로여야 합니다 (설정 Chat:AgyRelayDir).";
        var path = RulePath(relay);
        if (path.Length <= 3) return "드라이브 루트는 중계 폴더로 쓸 수 없습니다.";
        foreach (var r in required)
        {
            if (Covers(r, path))
                return $"중계 폴더가 agy deny로 막을 경로 안입니다 ({r}) — 사용자 프로필 · 작업 폴더 · 데이터 폴더 밖으로 옮기세요 (설정 Chat:AgyRelayDir).";
            if (Covers(path, r)) return $"중계 폴더가 agy deny로 막을 경로를 품습니다 ({r}).";
        }
        return null;
    }

    /// <summary>그 경로가 들어 있는 폴더 (같거나 조상, 규칙 경로 모양 목록). 없으면 null.</summary>
    public static string? InsideAny(string path, IEnumerable<string> folders)
    {
        var target = RulePath(path);
        return folders.FirstOrDefault(f => Covers(f, target));
    }

    /// <summary>output.md 형식 (self-reviewer 결과와 같은 기준: ## 지적 · ## 읽은 파일 절). 맞으면 null.</summary>
    public static string? OutputFormatProblem(string text)
    {
        var headings = text.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        if (!headings.Contains("## 지적")) return "'## 지적' 절이 없습니다";
        if (!headings.Contains("## 읽은 파일")) return "'## 읽은 파일' 절이 없습니다";
        return null;
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? [.. a.Where(x => x is JsonValue v && v.GetValueKind() == JsonValueKind.String).Select(x => x!.GetValue<string>())] : [];

    /// <summary>드라이브 문자 + 콜론 + 구분자로 시작 (agy도 "\"를 "/"로 바꿔 비교한다).</summary>
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DriveRule();
}
