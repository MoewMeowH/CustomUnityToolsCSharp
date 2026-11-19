#nullable enable
using System.Linq;
using HBLint.Generator.Json;

namespace HBLint.Generator
{
    /// <summary>
    /// 카탈로그에서 rules.json의 JSON Schema(draft 2020-12)를 만든다. 규칙마다 kind에 따라 그 종류의 칸 · 필수 · 값 종류를 건다.
    /// 출력은 늘 같다 (종류는 이름 순, 칸은 생성자 매개변수 순 → 속성 이름 순).
    /// </summary>
    public static class RulesSchema
    {
        public static string Generate(RuleCatalog catalog)
        {
            var kindDefs = new JsonObject();
            var conditions = new JsonArray();
            foreach (var kind in catalog.Kinds)
            {
                conditions.Add(new JsonObject()
                    .Add("if", new JsonObject()
                        .Add("properties", new JsonObject().Add(RulesJson.KindKey, new JsonObject().Add("const", new JsonString(kind.Name))))
                        .Add("required", Strings(RulesJson.KindKey)))
                    .Add("then", new JsonObject().Add("$ref", new JsonString("#/$defs/" + KindDefName(kind)))));

                var properties = new JsonObject();
                foreach (var key in RulesJson.ReservedRuleKeys)
                    properties.Add(key, new JsonBool(true));
                foreach (var field in kind.Fields)
                    properties.Add(field.JsonName, FieldSchema(field));
                kindDefs.Add(KindDefName(kind), new JsonObject()
                    .Add("required", Strings(kind.Parameters.Select(p => p.JsonName).ToArray()))
                    .Add("properties", properties)
                    .Add("additionalProperties", new JsonBool(false)));
            }

            var rule = new JsonObject()
                .Add("type", new JsonString("object"))
                .Add("required", Strings(RulesJson.IdKey, RulesJson.KindKey))
                .Add("properties", new JsonObject()
                    .Add(RulesJson.IdKey, new JsonObject()
                        .Add("type", new JsonString("string"))
                        .Add("pattern", new JsonString("^[A-Za-z0-9._-]+$"))
                        .Add("description", new JsonString("규칙 이름. 생성물은 id 순으로 정렬된다.")))
                    .Add(RulesJson.KindKey, new JsonObject()
                        .Add("enum", Strings(catalog.Kinds.Select(k => k.Name).ToArray()))
                        .Add("description", new JsonString("규칙 종류 = HBLint.{kind}RuleAttribute")))
                    .Add(RulesJson.DescriptionKey, new JsonObject()
                        .Add("type", new JsonString("string"))
                        .Add("description", new JsonString("설명. 생성물에 주석으로 들어간다."))))
                .Add("allOf", conditions);
            var defs = new JsonObject().Add("rule", rule);
            foreach (var member in kindDefs.Members)
                defs.Add(member.Key, member.Value);

            var root = new JsonObject()
                .Add("$schema", new JsonString("https://json-schema.org/draft/2020-12/schema"))
                .Add("title", new JsonString("HBLint rules.json"))
                .Add("description", new JsonString("이 파일은 hblint-gen schema가 HBLint.Attributes에서 만들었습니다. 직접 고치지 마세요."))
                .Add("type", new JsonString("object"))
                .Add("required", Strings(RulesJson.RulesKey))
                .Add("properties", new JsonObject()
                    .Add(RulesJson.SchemaKey, new JsonObject().Add("type", new JsonString("string")))
                    .Add(RulesJson.RulesKey, new JsonObject()
                        .Add("type", new JsonString("array"))
                        .Add("items", new JsonObject().Add("$ref", new JsonString("#/$defs/rule")))))
                .Add("additionalProperties", new JsonBool(false))
                .Add("$defs", defs);
            return JsonWriter.Write(root);
        }

        /// <summary>종류 정의 이름. "kind-" 접두사로 공통 정의 "rule"과 겹치지 않게 한다.</summary>
        private static string KindDefName(RuleKind kind) => "kind-" + kind.Name;

        private static JsonObject FieldSchema(RuleField field)
        {
            switch (field.Kind)
            {
                case RuleValueKind.String:
                    return new JsonObject().Add("type", new JsonString("string"));
                case RuleValueKind.StringArray:
                    return new JsonObject()
                        .Add("type", new JsonString("array"))
                        .Add("items", new JsonObject().Add("type", new JsonString("string")));
                case RuleValueKind.Enum:
                    return new JsonObject().Add("enum", Strings(field.EnumNames.ToArray()));
                case RuleValueKind.Bool:
                    return new JsonObject().Add("type", new JsonString("boolean"));
                default:
                    return new JsonObject()
                        .Add("type", new JsonString("integer"))
                        .Add("minimum", new JsonNumber(int.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                        .Add("maximum", new JsonNumber(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        private static JsonArray Strings(params string[] values)
        {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add(new JsonString(value));
            return array;
        }
    }
}
