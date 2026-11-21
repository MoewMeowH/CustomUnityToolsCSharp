#nullable enable
using System.Linq;
using HBLint.Core.Ir;
using HBLint.Core.Json;

namespace HBLint.Core.Generation
{
    /// <summary>노드 카탈로그 → 규칙 파일의 JSON 스키마 (편집기 · AI 자동 완성용). 그래프 검사(소켓 · 고리)는 스키마로 못 하므로 validate가 한다.</summary>
    internal static class IrSchema
    {
        public const string FileName = "hblint.schema.json";

        public static string Generate()
        {
            var nodeKinds = new JsonArray();
            foreach (var spec in NodeCatalog.All)
                nodeKinds.Add(Node(spec));

            var root = new JsonObject()
                .Add("$schema", S("https://json-schema.org/draft/2020-12/schema"))
                .Add("title", S("HB_Lint 규칙 (규칙 하나 = 그래프 하나)"))
                .Add("type", S("object"))
                .Add("required", Strings("version", "id", "nodes"))
                .Add("additionalProperties", new JsonBool(false))
                .Add("properties", new JsonObject()
                    .Add("$schema", new JsonObject().Add("type", S("string")))
                    .Add("version", new JsonObject().Add("const", new JsonNumber(RuleIr.CurrentVersion.ToString())))
                    .Add("id", new JsonObject()
                        .Add("type", S("string"))
                        .Add("pattern", S("^" + RuleIds.Prefix + "[0-9]{4}$"))
                        .Add("description", S($"진단 id — {RuleIds.Format(RuleIds.First)}~{RuleIds.Format(RuleIds.Last)}. 지운 규칙의 id는 다시 쓰지 않는다.")))
                    .Add("title", new JsonObject().Add("type", S("string")).Add("description", S("규칙 설명 (생성물 주석)")))
                    .Add("severity", new JsonObject().Add("enum", Strings(RuleIr.Severities)).Add("description", S("기본 warning. Unity 콘솔에는 info가 보이지 않는다.")))
                    .Add("requires", StringArray("검사 중인 어셈블리가 이것들을 모두 참조할 때만 규칙을 쓴다"))
                    .Add("ignoreAssemblies", StringArray("* 와일드카드 어셈블리 이름 — 맞으면 검사하지 않는다"))
                    .Add("nodes", new JsonObject()
                        .Add("type", S("object"))
                        .Add("description", S($"노드 이름 → 노드. 입력에 \"{NodeCatalog.ScopeName}\"을 쓰면 규칙 시작(전체 범위)."))
                        .Add("additionalProperties", new JsonObject().Add("oneOf", nodeKinds)))
                    .Add("layout", new JsonObject().Add("description", S("편집기용 노드 위치 · 메모 (분석기에는 넘기지 않음)"))));
            return JsonWriter.Write(root);
        }

        private static JsonObject Node(NodeSpec spec)
        {
            var properties = new JsonObject().Add("kind", new JsonObject().Add("const", S(spec.Kind)));
            var required = new JsonArray().Add(S("kind"));
            foreach (var input in spec.Inputs)
            {
                var accepts = "받는 소켓: " + string.Join(" · ", input.Accepts);
                properties.Add(input.Name, input.Many
                    ? new JsonObject().Add("type", S("array")).Add("minItems", new JsonNumber("2")).Add("items", new JsonObject().Add("type", S("string"))).Add("description", S(accepts))
                    : new JsonObject().Add("type", S("string")).Add("description", S(accepts)));
                required.Add(S(input.Name));
            }
            foreach (var field in spec.Fields)
            {
                properties.Add(field.Name, Field(field));
                if (field.Required)
                    required.Add(S(field.Name));
            }
            var node = new JsonObject()
                .Add("title", S($"{spec.Kind} — {spec.Title}" + (spec.Output == null ? "" : $" (→ {spec.Output})")))
                .Add("type", S("object"))
                .Add("required", required)
                .Add("additionalProperties", new JsonBool(false))
                .Add("properties", properties);
            if (spec.RequireOneOf.Length > 0)
            {
                var anyOf = new JsonArray();
                foreach (var name in spec.RequireOneOf)
                    anyOf.Add(new JsonObject().Add("required", Strings(name)));
                node.Add("anyOf", anyOf);
            }
            return node;
        }

        private static JsonObject Field(FieldSpec field)
        {
            var schema = new JsonObject();
            switch (field.Type)
            {
                case FieldType.String:
                    schema.Add("type", S("string")).Add("minLength", new JsonNumber("1"));
                    break;
                case FieldType.StringArray:
                    schema.Add("type", S("array")).Add("minItems", new JsonNumber("1"))
                        .Add("items", new JsonObject().Add("type", S("string")).Add("minLength", new JsonNumber("1")));
                    break;
                case FieldType.Int:
                    schema.Add("type", S("integer"));
                    if (field.Min != int.MinValue)
                        schema.Add("minimum", new JsonNumber(field.Min.ToString()));
                    if (field.Max != int.MaxValue)
                        schema.Add("maximum", new JsonNumber(field.Max.ToString()));
                    break;
                case FieldType.Bool:
                    schema.Add("type", S("boolean"));
                    break;
                case FieldType.Choice:
                    schema.Add("enum", Strings(field.Choices));
                    break;
                case FieldType.ChoiceArray:
                    schema.Add("type", S("array")).Add("minItems", new JsonNumber("1")).Add("items", new JsonObject().Add("enum", Strings(field.Choices)));
                    break;
            }
            return schema.Add("description", S(field.Description));
        }

        private static JsonObject StringArray(string description) =>
            new JsonObject().Add("type", S("array")).Add("items", new JsonObject().Add("type", S("string"))).Add("description", S(description));

        private static JsonArray Strings(params string[] values)
        {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add(S(value));
            return array;
        }

        private static JsonString S(string value) => new JsonString(value);
    }
}
