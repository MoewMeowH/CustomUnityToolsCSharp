using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>
/// 모델 선택 카드의 에이전트 줄 하나 (결정 "에이전트 + 강도"): 모드가 쓰는 에이전트(Agent)와 그 에이전트를 쓰는 역할들.
/// Pick · Effort는 카드가 처음 보일 값 — 지휘자가 넣은 agents · reasoning, 그 위에 + 요청 창에서 고른 값. Effort null이면 설정값 그대로.
/// </summary>
public sealed record ModeCardRow(string Agent, IReadOnlyList<string> Roles, string? Fallback, string Pick, string? Effort);

/// <summary>사용자가 카드에서 고른 한 줄: 에이전트 이름과 추론 강도(null · 빈 글자면 설정값 그대로).</summary>
public sealed record ModeRowPick(string Agent, string? Effort);

/// <summary>
/// + 요청 창에서 고른 값: 줄은 원래 에이전트 이름 → 고른 값. 그 말(큐 항목)에 붙어 가고, 그 말의 실행에서 같은 모드의 첫 run_mode 카드에 미리 채운다
/// (/self-review 785ebb5e97 C1 · C6).
/// </summary>
public sealed record ModePick(string Mode, IReadOnlyDictionary<string, ModeRowPick> Rows);

/// <summary>
/// run_mode 승인 카드 (모델 선택 카드). 입력의 mode · reason · input · context를 보이고, 모드가 쓰는 에이전트마다 교체 · 추론 강도를 고르게 한다.
/// 허가하면 <see cref="Apply"/>가 원래 입력에 agents · reasoning만 다시 써서 돌려준다 — 모드 · input · context는 그대로 (모드를 바꾸면 지휘자가 준비한 입력과 어긋난다).
/// </summary>
public sealed record ModeCard(string Mode, string? Description, string? Reason, string InputHead, int InputChars, IReadOnlyList<string> Files, int Images,
    IReadOnlyList<ModeCardRow> Rows, IReadOnlyList<ModeAgent> Agents, string? Error)
{
    /// <summary>카드에 보일 input 앞부분 (글자).</summary>
    public const int InputHeadChars = 2000;

    /// <summary>자료 보충 재질문 (followUp) — 서버가 agents · reasoning을 받지 않아 고를 줄이 없다.</summary>
    public bool FollowUp { get; init; }

    /// <summary>+ 요청 창에서 고른 값으로 미리 채웠는지.</summary>
    public bool Prefilled { get; init; }

    public IReadOnlyList<string> Efforts => ModeCatalog.Efforts;

    /// <summary>input이 길어 앞부분만 보이는지 (카드 표시).</summary>
    public bool InputCut => InputChars > InputHeadChars;

    /// <summary>화면에 보낼 앞부분 상한 (카드 표시 글).</summary>
    public int InputHeadLimit => InputHeadChars;

    /// <summary>run_mode 입력 JSON과 모드 목록으로 카드를 만든다. pick이 같은 모드면 그 값을 처음 값으로. 입력이 객체가 아니면 null.</summary>
    public static ModeCard? Build(string inputJson, ModeList list, ModePick? pick)
    {
        JsonObject input;
        try
        {
            if (JsonNode.Parse(inputJson) is not JsonObject o) return null;
            input = o;
        }
        catch (JsonException)
        {
            return null;
        }
        var mode = Text(input["mode"]) ?? "";
        var text = Text(input["input"]) ?? "";
        var files = new List<string>();
        if (input["context"] is JsonArray ctx)
            foreach (var c in ctx.OfType<JsonObject>())
                if (Text(c["path"]) is { Length: > 0 } p)
                    files.Add(c["startLine"] is { } s && c["endLine"] is { } e ? $"{p}:{s}-{e}" : p);
        if (input["files"] is JsonArray fs)
            files.AddRange(fs.Select(Text).OfType<string>().Where(f => f.Length > 0 && !files.Any(x => x.StartsWith(f, StringComparison.Ordinal))));
        var images = input["images"] is JsonArray im ? im.Count : 0;
        var head = text.Length > InputHeadChars ? text[..InputHeadChars] + "…" : text;
        var info = list.Find(mode);
        var followUp = input["followUp"] is JsonObject;
        var error = list.Error ?? (info is null ? $"모드 목록에 '{mode}'가 없습니다 — 그대로 허가하거나 거절만 할 수 있습니다." : null);
        if (info is null || followUp)
            return new ModeCard(mode, info?.Description, Text(input["reason"]), head, text.Length, files, images, [], list.Agents, followUp ? null : error)
            {
                FollowUp = followUp,
            };

        // 지휘자가 넣은 교체 · 강도 ('*' 포함)를 처음 값으로 — 교체는 원래 이름, 강도는 교체 뒤 이름 기준 (서버 ParseOverrides · ParseReasoningOverrides)
        var swaps = Pairs(input["agents"]);
        var efforts = Pairs(input["reasoning"]);
        var usePick = pick is not null && string.Equals(pick.Mode, info.Name, StringComparison.OrdinalIgnoreCase);
        var rows = new List<ModeCardRow>();
        foreach (var group in info.Steps.GroupBy(s => s.Agent, StringComparer.OrdinalIgnoreCase))
        {
            var agent = group.Key;
            var chosen = Lookup(swaps, agent) ?? Lookup(swaps, "*") ?? agent;
            var effort = (Lookup(efforts, chosen) ?? Lookup(efforts, "*"))?.ToLowerInvariant();
            if (usePick && Lookup(pick!.Rows, agent) is { } picked)
                (chosen, effort) = (picked.Agent, string.IsNullOrEmpty(picked.Effort) ? null : picked.Effort);
            if (list.Agent(chosen) is { } known) chosen = known.Name;
            if (effort is not null && !ModeCatalog.Efforts.Contains(effort)) effort = null;
            rows.Add(new ModeCardRow(agent, [.. group.Select(s => s.Role).Distinct()], group.Select(s => s.Fallback).FirstOrDefault(f => f is not null), chosen, effort));
        }
        return new ModeCard(info.Name, info.Description, Text(input["reason"]), head, text.Length, files, images, rows, list.Agents, list.Error) { Prefilled = usePick };
    }

    /// <summary>그 줄의 마지막 값: 고른 값이 있으면 그것, 없으면 카드 처음 값. 빈 강도는 null(설정값).</summary>
    public ModeRowPick Final(ModeCardRow row, IReadOnlyDictionary<string, ModeRowPick>? picks) =>
        picks is not null && Lookup(picks, row.Agent) is { } p ? p with { Effort = string.IsNullOrEmpty(p.Effort) ? null : p.Effort } : new ModeRowPick(row.Pick, row.Effort);

    /// <summary>카드 처음 값 그대로인지 (지휘자가 넣은 값 · 미리 채운 값).</summary>
    private static bool Unchanged(ModeCardRow row, ModeRowPick pick) =>
        string.Equals(row.Pick, pick.Agent, StringComparison.OrdinalIgnoreCase) && string.Equals(row.Effort ?? "", pick.Effort ?? "", StringComparison.Ordinal);

    /// <summary>
    /// 고른 값의 오류 문구 (null이면 괜찮음): 카드에 있는 줄만, 바꾼 줄은 켜진 에이전트 · 정해진 강도만.
    /// 처음 값 그대로인 줄은 목록을 보지 않는다 — 꺼진 에이전트를 쓰는 모드 · 지휘자가 넣은 이름도 "그대로 허가"는 되게 (서버가 다시 검사, 785ebb5e97 C2).
    /// 두 줄이 같은 에이전트로 가는데 강도가 다르면 거절 — 서버는 이름마다 강도 하나만 받는다 (785ebb5e97 C4).
    /// </summary>
    public string? Check(IReadOnlyDictionary<string, ModeRowPick>? picks)
    {
        if (picks is { Count: > 0 } && (FollowUp || Rows.Count == 0)) return "이 요청은 에이전트를 바꿀 수 없습니다 — 그대로 허가하거나 거절하세요.";
        foreach (var (key, pick) in picks ?? new Dictionary<string, ModeRowPick>())
        {
            if (Rows.FirstOrDefault(r => string.Equals(r.Agent, key, StringComparison.OrdinalIgnoreCase)) is not { } row) return $"이 모드는 '{key}'를 쓰지 않습니다.";
            if (Unchanged(row, Final(row, picks))) continue;
            if (!Agents.Any(a => string.Equals(a.Name, pick.Agent, StringComparison.OrdinalIgnoreCase))) return $"'{pick.Agent}'는 켜진 에이전트가 아닙니다.";
            if (!string.IsNullOrEmpty(pick.Effort) && !ModeCatalog.Efforts.Contains(pick.Effort)) return $"추론 강도 '{pick.Effort}'는 쓸 수 없습니다.";
        }
        var efforts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            var final = Final(row, picks);
            if (final.Effort is not { } effort) continue;
            if (efforts.TryGetValue(final.Agent, out var other) && other != effort)
                return $"'{final.Agent}'를 두 줄이 쓰는데 추론 강도가 다릅니다 ({other} · {effort}) — 같게 맞추세요.";
            efforts[final.Agent] = effort;
        }
        return null;
    }

    /// <summary>
    /// 허가 때 돌려줄 입력. 모든 줄이 처음 값 그대로면 원래 입력 그대로 (785ebb5e97 C3).
    /// 바꾼 줄이 있으면 agents · reasoning만 다시 쓴다: 교체는 '원래=새것', 강도는 '새것=강도'(같은 이름은 한 번 — Check가 다른 강도를 막는다).
    /// 지휘자가 넣은 값 중 카드 줄이 아닌 이름(대신 에이전트 등)은 남기고, '*'는 줄마다 풀어 쓴 값으로 바뀐다.
    /// 줄이 없는 카드(목록 없음 · followUp)는 입력 그대로.
    /// </summary>
    public string Apply(string inputJson, IReadOnlyDictionary<string, ModeRowPick>? picks)
    {
        if (Rows.Count == 0 || Rows.All(r => Unchanged(r, Final(r, picks))) || JsonNode.Parse(inputJson) is not JsonObject input) return inputJson;
        var agents = new JsonArray();
        var reasoning = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            var pick = Final(row, picks);
            var name = Agents.FirstOrDefault(a => string.Equals(a.Name, pick.Agent, StringComparison.OrdinalIgnoreCase))?.Name ?? pick.Agent;
            if (!string.Equals(name, row.Agent, StringComparison.OrdinalIgnoreCase)) agents.Add($"{row.Agent}={name}");
            if (pick.Effort is not null && seen.Add(name)) reasoning.Add($"{name}={pick.Effort}");
        }
        foreach (var (from, to) in Pairs(input["agents"]))
            if (from != "*" && !Rows.Any(r => string.Equals(r.Agent, from, StringComparison.OrdinalIgnoreCase))) agents.Add($"{from}={to}");
        // 줄의 원래 · 새 이름에 걸린 값은 위에서 다시 정했다 — 남기면 교체로 빠진 이름이 남아 서버가 거절한다
        foreach (var (name, effort) in Pairs(input["reasoning"]))
            if (name != "*" && !Rows.Any(r => string.Equals(r.Agent, name, StringComparison.OrdinalIgnoreCase)) && seen.Add(name))
                reasoning.Add($"{name}={effort}");
        input.Remove("agents");
        input.Remove("reasoning");
        if (agents.Count > 0) input["agents"] = agents;
        if (reasoning.Count > 0) input["reasoning"] = reasoning;
        return input.ToJsonString();
    }

    /// <summary>+ 요청 창이 지휘자에게 보내는 말의 머리 줄 표시.</summary>
    public const string HeaderMarker = "[외부 요청]";

    /// <summary>
    /// + 요청 창 머리 줄: <c>[외부 요청] mode=이름 · agents=원래=새것, … · reasoning=새것=강도, …</c> — 바뀐 것만 (run_mode 인자 모양 그대로).
    /// </summary>
    public static string Header(ModeInfo mode, IReadOnlyDictionary<string, ModeRowPick> rows)
    {
        var agents = new List<string>();
        var reasoning = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in mode.Steps.Select(s => s.Agent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Lookup(rows, agent) is not { } pick) continue;
            if (!string.Equals(pick.Agent, agent, StringComparison.OrdinalIgnoreCase)) agents.Add($"{agent}={pick.Agent}");
            if (!string.IsNullOrEmpty(pick.Effort) && seen.Add(pick.Agent)) reasoning.Add($"{pick.Agent}={pick.Effort}");
        }
        return $"{HeaderMarker} mode={mode.Name}"
               + (agents.Count > 0 ? $" · agents={string.Join(", ", agents)}" : "")
               + (reasoning.Count > 0 ? $" · reasoning={string.Join(", ", reasoning)}" : "");
    }

    /// <summary>+ 요청 창에서 고른 값 검사 (null이면 괜찮음): 모드가 쓰는 에이전트 줄만, 켜진 에이전트 · 정해진 강도만.</summary>
    public static string? CheckPick(ModeInfo mode, ModeList list, IReadOnlyDictionary<string, ModeRowPick> rows)
    {
        foreach (var (row, pick) in rows)
        {
            if (!mode.Steps.Any(s => string.Equals(s.Agent, row, StringComparison.OrdinalIgnoreCase))) return $"이 모드는 '{row}'를 쓰지 않습니다.";
            if (list.Agent(pick.Agent) is null) return $"'{pick.Agent}'는 켜진 에이전트가 아닙니다.";
            if (!string.IsNullOrEmpty(pick.Effort) && !ModeCatalog.Efforts.Contains(pick.Effort)) return $"추론 강도 '{pick.Effort}'는 쓸 수 없습니다.";
        }
        return null;
    }

    /// <summary>화면이 보낸 고른 값 JSON (<c>{"원래 에이전트": {"agent": "…", "effort": "…"}}</c>). 읽지 못하면 null, 빈 글자면 빈 목록.</summary>
    public static IReadOnlyDictionary<string, ModeRowPick>? ParsePicks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, ModeRowPick>();
        if (json.Length > 16 * 1024) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var map = new Dictionary<string, ModeRowPick>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in o)
            {
                if (value is not JsonObject row || Text(row["agent"]) is not { Length: > 0 } agent || key.Length == 0) return null;
                var effort = Text(row["effort"]);
                if (!map.TryAdd(key, new ModeRowPick(agent, string.IsNullOrEmpty(effort) ? null : effort))) return null;
            }
            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Pairs(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonArray list) return map;
        foreach (var raw in list.Select(Text).OfType<string>())
        {
            var parts = raw.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0) map.TryAdd(parts[0], parts[1]);
        }
        return map;
    }

    private static T? Lookup<T>(IReadOnlyDictionary<string, T> map, string key) where T : class =>
        map.TryGetValue(key, out var v) ? v : map.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
