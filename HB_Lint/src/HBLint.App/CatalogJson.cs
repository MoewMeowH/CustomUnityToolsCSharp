using System.Text.Json.Nodes;
using HBLint.Core.Help;
using HBLint.Core.Ir;

namespace HBLint.App;

/// <summary>노드 카탈로그 → UI 팔레트 · 칸 편집기용 JSON. 머리 칸은 카탈로그에 표가 없어 스키마(IrSchema)처럼 여기 적는다.</summary>
internal static class CatalogJson
{
    public static JsonObject Build() => new()
    {
        ["scopeName"] = NodeCatalog.ScopeName,
        ["reportKind"] = NodeCatalog.ReportKind,
        ["sockets"] = Array(Enum.GetNames<Socket>()),
        ["severities"] = Array(RuleIr.Severities),
        ["idPrefix"] = RuleIds.Prefix,
        ["idFirst"] = RuleIds.First,
        ["idLast"] = RuleIds.Last,
        ["maxNodes"] = RuleIr.MaxNodes,
        ["head"] = new JsonArray(
            Head("id", "string", true, $"진단 id — {RuleIds.Format(RuleIds.First)}~{RuleIds.Format(RuleIds.Last)}. 지운 규칙의 id는 다시 쓰지 않는다."),
            Head("title", "string", false, "규칙 설명 (생성물 주석)"),
            Head("severity", "choice", false, "기본 warning. error는 Unity 컴파일을 막는다.", RuleIr.Severities),
            Head("requires", "stringArray", false, "검사 중인 어셈블리가 이것들을 모두 참조할 때만 규칙을 쓴다", hint: "assembly"),
            Head("ignoreAssemblies", "stringArray", false, "* 와일드카드 어셈블리 이름 — 맞으면 검사하지 않는다")),
        ["nodes"] = new JsonArray(NodeCatalog.All.Select(Node).ToArray<JsonNode?>()),
        ["scopeHelp"] = Help(NodeHelp.Scope),
        ["socketHelp"] = new JsonObject(NodeHelp.Sockets.Select(s => KeyValuePair.Create(s.Key.ToString(), (JsonNode?)JsonValue.Create(s.Value)))),
        ["glossary"] = new JsonArray(NodeHelp.Glossary.Select(t => (JsonNode?)new JsonObject { ["term"] = t.Key, ["meaning"] = t.Value }).ToArray()),
    };

    private static JsonObject? Help(HelpText? help) =>
        help == null ? null : new JsonObject { ["help"] = help.Help, ["example"] = help.Example };

    private static JsonObject Node(NodeSpec spec)
    {
        var node = new JsonObject
        {
            ["kind"] = spec.Kind,
            ["title"] = spec.Title,
            ["output"] = spec.Output?.ToString(),
            ["inputs"] = new JsonArray(spec.Inputs.Select(i => (JsonNode?)new JsonObject
            {
                ["name"] = i.Name,
                ["many"] = i.Many,
                ["accepts"] = Array(i.Accepts.Select(a => a.ToString())),
            }).ToArray()),
            ["fields"] = new JsonArray(spec.Fields.Select(Field).ToArray<JsonNode?>()),
        };
        if (spec.RequireOneOf.Length > 0)
            node["requireOneOf"] = Array(spec.RequireOneOf);
        var help = NodeHelp.Find(spec.Kind);
        if (help != null)
        {
            node["help"] = help.Help;
            node["example"] = help.Example;
        }
        return node;
    }

    private static JsonObject Field(FieldSpec field)
    {
        var json = new JsonObject
        {
            ["name"] = field.Name,
            ["type"] = TypeName(field.Type),
            ["required"] = field.Required,
            ["description"] = field.Description,
        };
        if (field.Choices.Length > 0)
            json["choices"] = Array(field.Choices);
        if (field.Min != int.MinValue)
            json["min"] = field.Min;
        if (field.Max != int.MaxValue)
            json["max"] = field.Max;
        if (field.Hint != FieldHint.None)
            json["hint"] = HintName(field.Hint);
        return json;
    }

    public static string HintName(FieldHint hint) => hint switch
    {
        FieldHint.Type => "type",
        FieldHint.Member => "member",
        FieldHint.Namespace => "namespace",
        FieldHint.Define => "define",
        _ => "",
    };

    private static string TypeName(FieldType type) => type switch
    {
        FieldType.String => "string",
        FieldType.StringArray => "stringArray",
        FieldType.Int => "int",
        FieldType.Bool => "bool",
        FieldType.Choice => "choice",
        FieldType.ChoiceArray => "choiceArray",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static JsonObject Head(string name, string type, bool required, string description, string[]? choices = null, string? hint = null)
    {
        var json = new JsonObject { ["name"] = name, ["type"] = type, ["required"] = required, ["description"] = description };
        if (choices != null)
            json["choices"] = Array(choices);
        if (hint != null)
            json["hint"] = hint;
        return json;
    }

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
