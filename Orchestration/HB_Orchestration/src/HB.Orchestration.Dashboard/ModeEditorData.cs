using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>저장이 거절됐을 때 화면에 되돌려 줄 편집 내용 (편집을 이어 가게).</summary>
/// <param name="Kind">"mode" 또는 "role".</param>
public sealed record EditorDraft(string Kind, string Name, string? OriginalName, string Json);

/// <summary>
/// 모드 화면의 그래프 편집기가 읽는 자료 (페이지 안 JSON 블록). 모드 · 역할은 파일에 적힌 객체 그대로 싣는다 —
/// 화면이 그 객체를 고쳐 돌려보내므로 화면이 모르는 키도 보존된다.
/// </summary>
public static class ModeEditorData
{
    // script 블록 안에 싣는다: 한글은 그대로 두되 &lt; &gt; &amp; 따옴표는 \uXXXX로 (JavaScriptEncoder가 늘 막는 글자).
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Build(ModesSnapshot snap, IReadOnlyList<CallRecord> calls, DateTimeOffset now, bool hasSource,
        string? selected = null, EditorDraft? draft = null)
    {
        var since = now - ModesUsage.Window;
        var runs = calls.Where(c => c.Timestamp >= since && c.Mode is not null && c.RunId is not null)
            .GroupBy(c => c.Mode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => c.RunId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);

        // 최상위 키도 저장 쪽과 같이 대소문자를 무시하고 찾는다 (서버가 그렇게 읽는다)
        var modes = new JsonArray();
        if (ModesConfigService.Child(snap.Root, "Modes") is JsonObject modeNodes)
        {
            foreach (var (name, node) in modeNodes)
            {
                if (node is not JsonObject def) continue;
                modes.Add(new JsonObject
                {
                    ["name"] = name,
                    ["def"] = def.DeepClone(),
                    ["runs7d"] = runs.GetValueOrDefault(name),
                    // 연결 설정이 틀린 모드는 목록에 표시한다 (실행 · 저장이 거절되는 이유)
                    ["error"] = snap.Typed.Modes.TryGetValue(name, out var typed) ? ModeGraph.Resolve(typed).Error : null,
                });
            }
        }

        var fileRoles = ModesConfigService.Child(snap.Root, "Roles") as JsonObject;
        var roles = new JsonArray();
        var roleNames = (fileRoles?.Select(p => p.Key) ?? []).Concat(Prompts.BuiltInRoles.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in roleNames)
        {
            var key = fileRoles?.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            var builtIn = Prompts.BuiltInRoles.TryGetValue(name, out var builtInPrompt);
            roles.Add(new JsonObject
            {
                ["name"] = key ?? name,
                ["def"] = key is not null && fileRoles![key] is JsonObject roleDef ? roleDef.DeepClone() : null,
                ["builtIn"] = builtIn,
                ["builtInPrompt"] = builtIn ? builtInPrompt : null,
                ["builtInOutput"] = Prompts.BuiltInOutputs.GetValueOrDefault(name),
                ["usedBy"] = new JsonArray([.. ModesConfigService.ModesUsingRole(snap.Root, name).Select(m => (JsonNode)m)]),
            });
        }

        var agents = new JsonArray([.. snap.Agents.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.Name,
            ["model"] = a.Model,
            ["enabled"] = a.Enabled,
            ["providerType"] = snap.ProviderTypes.GetValueOrDefault(a.Provider),
        })]);

        var root = new JsonObject
        {
            ["stamp"] = snap.Stamp,
            ["hasSource"] = hasSource,
            ["selected"] = selected,
            ["modes"] = modes,
            ["roles"] = roles,
            ["agents"] = agents,
            ["outputs"] = new JsonArray([.. FindingFormat.KnownNames.Split(", ").Select(n => (JsonNode)n)]),
            ["limits"] = new JsonObject
            {
                ["maxGraphSteps"] = ModeDefinition.MaxGraphStepsHardCap,
                ["maxRounds"] = ModeDefinition.MaxRoundsHardCap,
                ["maxSearchCalls"] = WebSearchSettings.MaxCallsHardCap,
                ["searchProviderType"] = OpenAIResponsesProvider.TypeName,
            },
            ["draft"] = draft is null ? null : new JsonObject
            {
                ["kind"] = draft.Kind,
                ["name"] = draft.Name,
                ["originalName"] = draft.OriginalName,
                ["json"] = draft.Json,
            },
        };
        return root.ToJsonString(Json);
    }
}
