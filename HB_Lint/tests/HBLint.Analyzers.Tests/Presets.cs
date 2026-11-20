using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;

namespace HBLint.Analyzers.Tests;

/// <summary>
/// 기존 규칙 종류 셋(CLL0001 null 연산자 · CLL0002 메서드 안 멤버 · CLL0003 디파인)을 규칙 IR 그래프로 그린 것.
/// samples/의 프리셋 파일과 같은 모양이다.
/// </summary>
internal static class Presets
{
    /// <summary>시작 → null 연산자 → 대상 타입 → 보고.</summary>
    public static string NullOperator(string type, string message, string[]? requires = null, string severity = "warning", string id = "CLL0001") =>
        Rule(id, severity, requires, null, new JsonObject
        {
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "scope" },
            ["t"] = new JsonObject { ["kind"] = "filter.targetType", ["from"] = "n", ["type"] = type },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "t", ["message"] = message },
        });

    /// <summary>시작 → 타입 계열 → 메서드 이름 → (따라가기) → 멤버 사용 → 보고.</summary>
    public static string CallInMethod(string[] methods, string[] members, string owner, string message, int depth = 0, string severity = "warning", string[]? requires = null, string id = "CLL0002")
    {
        var nodes = new JsonObject
        {
            ["t"] = new JsonObject { ["kind"] = "types.derived", ["from"] = "scope", ["type"] = owner },
            ["m"] = new JsonObject { ["kind"] = "methods.named", ["from"] = "t", ["names"] = Array(methods) },
        };
        var last = "m";
        if (depth != 0)
        {
            nodes["f"] = new JsonObject { ["kind"] = "methods.follow", ["from"] = "m", ["depth"] = depth };
            last = "f";
        }
        nodes["s"] = new JsonObject { ["kind"] = "sites.member", ["from"] = last, ["members"] = Array(members) };
        nodes["r"] = new JsonObject { ["kind"] = "report", ["from"] = "s", ["message"] = message };
        return Rule(id, severity, requires, null, nodes);
    }

    /// <summary>시작 → (네임스페이스 사용 ∪ 디파인 안 선언 사용) → 디파인 가드 밖 → 보고.</summary>
    public static string RequireDefine(string define, string message, string[] namespaces, bool declaredInDefine, string[]? ignoreAssemblies = null, string id = "CLL0003")
    {
        var nodes = new JsonObject();
        var sources = new List<string>();
        if (namespaces.Length > 0)
        {
            nodes["ns"] = new JsonObject { ["kind"] = "sites.namespace", ["from"] = "scope", ["namespaces"] = Array(namespaces) };
            sources.Add("ns");
        }
        if (declaredInDefine)
        {
            nodes["dd"] = new JsonObject { ["kind"] = "sites.declaredInDefine", ["from"] = "scope", ["define"] = define };
            sources.Add("dd");
        }
        var guarded = sources[0];
        if (sources.Count > 1)
        {
            nodes["u"] = new JsonObject { ["kind"] = "sites.union", ["from"] = Array(sources.ToArray()) };
            guarded = "u";
        }
        nodes["g"] = new JsonObject { ["kind"] = "filter.define", ["from"] = guarded, ["define"] = define, ["where"] = "outside" };
        nodes["r"] = new JsonObject { ["kind"] = "report", ["from"] = "g", ["message"] = message };
        return Rule(id, "warning", null, ignoreAssemblies, nodes);
    }

    public static string Rule(string id, string severity, string[]? requires, string[]? ignoreAssemblies, JsonObject nodes)
    {
        var rule = new JsonObject { ["version"] = 1, ["id"] = id, ["severity"] = severity };
        if (requires != null)
            rule["requires"] = Array(requires);
        if (ignoreAssemblies != null)
            rule["ignoreAssemblies"] = Array(ignoreAssemblies);
        rule["nodes"] = nodes;
        return rule.ToJsonString();
    }

    /// <summary>IR 원문들을 [assembly: HBLint.Rule("…")] 줄로.</summary>
    public static string Attributes(params string[] irs) =>
        string.Join("\n", irs.Select(ir => "[assembly: HBLint.Rule(" + SymbolDisplay.FormatLiteral(ir, quote: true) + ")]"));

    private static JsonArray Array(string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
