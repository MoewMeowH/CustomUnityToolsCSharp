using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// agy(Antigravity CLI) 설정. 중계 세션 규칙은 앱이 쓰는 중계 설정 파일(<c>agy --settings</c> — 중계 셸의 agy 함수가 붙인다)에 두고,
/// 전역 settings.json은 읽기만 하며 statusLine(AGY 한도)만 본다 — 따로 쓰는 agy가 중계 규칙에 걸리지 않게 (사용자 요청 2026-11-26).
/// 2026-11-22 실측(agy 1.5.14): Default 모드(agentMode "") · 터미널 샌드박스 · 드라이브 문자가 붙은 read_file deny가 같이 있어야
/// 도구 읽기 · 셸 읽기(사람이 승인해도)가 막혔다 — auto 모드는 deny 밖 경로를 묻지 않고 읽었고, Default만이면 승인한 셸이 샌드박스 밖에서 돌았고,
/// 드라이브 없는 "/AI/…" 항목은 도구 읽기를 막지 못했다.
/// 2026-11-26 실측(agy 1.5.17): --settings 파일의 모드 · 샌드박스 · deny가 전역의 auto · 샌드박스 끔 · 같은 경로 allow를 이겼다.
/// </summary>
public static partial class AgyCheck
{
    /// <param name="Problems">하나라도 있으면 중계를 막는다.</param>
    /// <param name="Warnings">막지 않는 것 (옛 중계 규칙이 전역에 남음 · AGY 한도 statusLine).</param>
    /// <param name="SuggestedJson">전역 settings.json에 넣을 전문 (사용자가 복사해 넣는다 — 앱은 전역 파일을 고치지 않는다).</param>
    public sealed record Result(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, string SuggestedJson)
    {
        public bool Ok => Problems.Count == 0;

        /// <summary>넣을 JSON을 펼쳐 보일지 — 옛 중계 규칙을 뺐거나 AGY 한도용 statusLine을 더했을 때.</summary>
        public bool OpenSuggestion { get; init; }

        /// <summary>앱이 쓴 중계 설정 파일 전문 (설정 화면 보기용, 쓰지 못했으면 null).</summary>
        public string? RelayJson { get; init; }

        /// <summary>전역에 남은 옛 중계 규칙 수 — 남아 있으면 카나리가 --settings가 아니라 전역 규칙을 시험하게 된다.</summary>
        public int OldRelayRules { get; init; }
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>규칙 안 경로 모양: 드라이브 대문자 · "/" 구분 · 끝 "/" 없음 (드라이브 루트는 "C:/").</summary>
    public static string RulePath(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':') full = char.ToUpperInvariant(full[0]) + full[1..];
        return full.Length > 3 ? full.TrimEnd('/') : full;
    }

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

    /// <summary>규칙 <c>action(경로)</c>의 경로 부분. 그 action이 아니면 null.</summary>
    private static string? Inner(string rule, string action) =>
        rule.StartsWith(action + "(", StringComparison.Ordinal) && rule.EndsWith(')') ? rule[(action.Length + 1)..^1] : null;

    /// <summary>
    /// 중계 설정 파일 전문 (<c>agy --settings</c>): Default 모드 · 터미널 샌드박스 · deny(필수 + 권장 read_file, 필수 경로 · 데이터 폴더 write_file —
    /// 이 파일 · statusLine 스크립트(agy가 샌드박스 밖에서 돌림) · PowerShell 프로필 · 보관함을 중계 중인 agy가 고치지 못하게) · allow(current write_file — 없으면 쓸 때마다 승인창).
    /// </summary>
    /// <param name="required">deny가 덮어야 하는 경로 (<see cref="RequiredPaths"/>).</param>
    /// <param name="recommended">중계 폴더가 있는 드라이브의 다른 폴더 (없으면 읽을 때 승인창만 지킴).</param>
    public static string RelaySettings(IReadOnlyList<string> required, IReadOnlyList<string> recommended, string relayCurrent, string dataFolder)
    {
        var deny = required.Concat(recommended).Select(RulePath).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => $"read_file({p})")
            .Concat(required.Append(dataFolder).Select(RulePath).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => $"write_file({p})"));
        var root = new JsonObject
        {
            ["agentMode"] = "",
            ["enableTerminalSandbox"] = true,
            ["permissions"] = new JsonObject
            {
                ["deny"] = new JsonArray([.. deny.Select(s => (JsonNode?)JsonValue.Create(s))]),
                ["allow"] = new JsonArray(JsonValue.Create($"write_file({RulePath(relayCurrent)})")),
            },
        };
        return root.ToJsonString(Pretty);
    }

    /// <summary>
    /// 전역 settings.json 글을 본다 (json이 null이면 파일 없음). 모드 · 샌드박스 · deny는 중계 설정 파일이 정하므로 막는 문제는 없고 경고만 —
    /// 옛 중계 규칙이 남음(따로 쓰는 agy도 막힘) · 중계에도 합쳐질 수 있는 항목(넓은 allow · current를 덮는 deny — agy가 --settings와 전역 permissions를
    /// 합치는지 대체하는지는 확인 못 함) · AGY 한도 statusLine.
    /// </summary>
    /// <param name="relayJson">중계 설정 파일 전문 — 전역 deny · allow에 같은 항목이 있으면 옛 중계 규칙으로 보고 넣을 JSON에서 뺀다.</param>
    /// <param name="relayCurrent">중계 폴더 current — 전역 deny가 덮으면 경고.</param>
    /// <param name="statusScript">AGY 한도용 statusLine 스크립트 (<see cref="AgyLimits"/>). 없으면 statusLine은 보지 않는다. 그 폴더 write_file deny는 옛 권고라 옛 중계 규칙으로 본다.</param>
    public static Result Inspect(string? json, string relayJson, string relayCurrent, string? statusScript = null)
    {
        var warnings = new List<string>();
        JsonObject root = [];
        var readable = true;
        if (json is not null)
            try
            {
                // 손으로 고친 파일의 주석 · 끝 쉼표도 받는다
                root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                       ?? throw new JsonException("맨 위가 객체가 아닙니다");
            }
            catch (JsonException ex)
            {
                warnings.Add($"전역 agy 설정 파일을 읽지 못했습니다: {ex.Message} — 넣을 JSON에는 필요한 키만 담았습니다. 기존 내용과 합쳐 넣으세요.");
                readable = false;
            }

        // 넣을 JSON: 지금 파일에서 옛 중계 규칙만 빼고 필요한 키만 더한다 (다른 키 · 항목은 그대로)
        var suggested = readable ? (JsonObject)root.DeepClone() : [];
        var open = false;
        var removed = 0;
        if (suggested["permissions"] is JsonObject permissions)
        {
            var relay = JsonNode.Parse(relayJson)!["permissions"]!;
            foreach (var key in new[] { "deny", "allow" })
            {
                if (permissions[key] is not JsonArray list) continue;
                var ours = Strings(relay[key]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // 옛 권고(11-24): statusLine 스크립트 폴더 write_file deny — 이제 중계 설정 파일의 write_file deny가 맡는다
                if (key == "deny" && statusScript is not null) ours.Add($"write_file({RulePath(Path.GetDirectoryName(Path.GetFullPath(statusScript))!)})");
                var keep = list.Where(x => !(x is JsonValue v && v.GetValueKind() == JsonValueKind.String && ours.Contains(v.GetValue<string>()))).Select(x => x?.DeepClone()).ToList();
                removed += list.Count - keep.Count;
                permissions[key] = new JsonArray([.. keep]);
            }
            if (removed > 0)
            {
                warnings.Add($"전역 설정에 옛 중계 규칙 {removed}개가 남아 있어 따로 쓰는 agy도 막힙니다 — 중계는 이제 중계 설정 파일로 하므로 넣을 JSON처럼 빼도 됩니다 " +
                             "(agentMode · enableTerminalSandbox도 따로 쓸 때 원하는 대로). 작업 폴더가 바뀌었으면 앱이 넣었던 다른 옛 항목도 남았을 수 있습니다. " +
                             "남아 있는 동안은 카나리가 --settings를 시험하지 못해 판정하지 않습니다.");
                open = true;
            }

            // 중계에도 합쳐질 수 있는 항목 (사용자 항목만 — 옛 중계 규칙은 위에서 뺐다)
            var deny = Strings(permissions["deny"]);
            var allow = Strings(permissions["allow"]);
            foreach (var wide in allow.Where(r => r is "*" || Inner(r, "read_file") == "*" || Inner(r, "write_file") == "*" || Inner(r, "command") == "*"))
                warnings.Add($"전역 allow에 넓은 항목이 있습니다: {wide} — agy가 --settings와 전역 permissions를 합치면 중계 세션에서도 deny 밖 경로 · 명령이 묻지 않고 열립니다.");
            var current = RulePath(relayCurrent);
            foreach (var blocking in deny.Where(r => (Inner(r, "read_file") ?? Inner(r, "write_file")) is { } p && (p == "*" || Covers(p, current))))
                warnings.Add($"전역 deny {blocking}가 중계 폴더({current})를 덮습니다 — 합쳐지면 중계 agy가 요청을 읽거나 결과를 쓰지 못합니다.");
        }
        if (statusScript is not null)
        {
            var status = StatusLine(root, statusScript, readable);
            if (status.Warning is { } statusWarning) warnings.Add(statusWarning);
            if (status.Add)
            {
                suggested["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = AgyLimits.Command(statusScript), ["stack_with_default"] = true };
                open = true;
            }
        }
        return new Result([], warnings, suggested.ToJsonString(Pretty)) { OpenSuggestion = open, OldRelayRules = removed };
    }

    /// <summary>
    /// AGY 한도 statusLine 검사: 경고(없으면 null) · 넣을 JSON에 statusLine을 넣을지(없을 때만 — 다른 명령은 사용자 것이라 덮지 않고,
    /// 경로에 공백이 있으면 agy가 따옴표를 글자 그대로 넘겨 쓸 수 없다).
    /// </summary>
    private static (string? Warning, bool Add) StatusLine(JsonObject root, string script, bool readable)
    {
        var command = AgyLimits.Command(script);
        var node = readable ? root["statusLine"] : null;
        var current = node is JsonObject o && o["command"] is JsonValue c && c.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : null;
        if (string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            return node is JsonObject s && s["enabled"] is JsonValue e && e.GetValueKind() == JsonValueKind.False
                ? ("AGY 한도: statusLine이 꺼져 있습니다 (\"enabled\": false) — 켜야 사이드바에 AGY 한도가 갱신됩니다.", false)
                : (null, false);
        if (Path.GetFullPath(script).Contains(' ', StringComparison.Ordinal))
            return ($"AGY 한도: 데이터 폴더 경로에 공백이 있어 statusLine 명령을 쓸 수 없습니다 (agy가 따옴표를 글자 그대로 넘김) — 사이드바에 AGY 한도가 보이지 않습니다: {script}", false);
        if (node is not null)
            return ($"AGY 한도: 다른 statusLine이 있어 덮지 않았습니다 — AGY 한도를 보려면 statusLine.command를 다음으로 바꾸세요: {command}", false);
        return ("AGY 한도: statusLine이 없습니다 — 넣을 JSON의 statusLine을 넣으면 agy를 띄워 둔 동안 사이드바에 AGY 한도가 보입니다.", true);
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
