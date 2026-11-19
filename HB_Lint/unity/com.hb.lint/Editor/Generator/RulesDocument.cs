#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HBLint.Generator.Json;

namespace HBLint.Generator
{
    /// <summary>규칙 하나. Values는 rules.json 칸 이름(예약 칸 제외) → JSON 값.</summary>
    public sealed class RuleEntry
    {
        public RuleEntry(string id, string kind)
        {
            Id = id;
            Kind = kind;
        }

        public string Id { get; set; }

        public string Kind { get; set; }

        public string? Description { get; set; }

        public Dictionary<string, JsonValue> Values { get; } = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 편집 창이 고치는 rules.json. 읽을 때 구조(맨 바깥 · rules 배열 · 규칙 객체 · id · kind · 칸 이름)를 보고,
    /// 하나라도 틀리면 열지 않는다 — 고칠 수 없는 내용을 저장하면서 잃지 않게. 값 종류는 <see cref="Validate"/>가 본다.
    /// 쓸 때는 규칙을 id 순으로, 칸은 id · kind · description 다음 카탈로그 순(생성자 매개변수 → 속성 이름 순)으로 쓴다.
    /// </summary>
    public sealed class RulesDocument
    {
        public string? Schema { get; set; }

        public List<RuleEntry> Rules { get; } = new List<RuleEntry>();

        /// <summary>구조 오류가 하나라도 있으면 null. 오류는 errors에 모두.</summary>
        public static RulesDocument? Load(string json, RuleCatalog catalog, out IReadOnlyList<RulesError> errors)
        {
            var found = new List<RulesError>();
            errors = found;

            JsonValue root;
            try
            {
                root = JsonReader.Parse(json);
            }
            catch (JsonParseException e)
            {
                found.Add(new RulesError("JSON 형식 오류: " + e.Message, e.Line, e.Column));
                return null;
            }

            if (!(root is JsonObject rootObject))
            {
                found.Add(new RulesError($"맨 바깥은 객체여야 합니다 (지금 {root.KindName})", root.Line, root.Column));
                return null;
            }

            var document = new RulesDocument();
            foreach (var member in rootObject.Members)
            {
                if (member.Key == RulesJson.SchemaKey)
                {
                    if (member.Value is JsonString schema)
                        document.Schema = schema.Value;
                    else
                        found.Add(new RulesError($"\"{RulesJson.SchemaKey}\"는 문자열이어야 합니다", member.Value.Line, member.Value.Column));
                }
                else if (member.Key != RulesJson.RulesKey)
                    found.Add(new RulesError($"편집 창이 모르는 칸 '{member.Key}'", member.Value.Line, member.Value.Column));
            }

            if (!(rootObject.Get(RulesJson.RulesKey) is JsonArray rules))
            {
                found.Add(new RulesError($"\"{RulesJson.RulesKey}\" 배열이 없습니다", rootObject.Line, rootObject.Column));
                return null;
            }

            foreach (var item in rules.Items)
            {
                var entry = ReadEntry(item, catalog, found);
                if (entry != null)
                    document.Rules.Add(entry);
            }
            return found.Count == 0 ? document : null;
        }

        /// <summary>새 규칙 — 그 종류의 필수 칸을 빈 값으로 채운다.</summary>
        public static RuleEntry NewRule(string id, RuleKind kind)
        {
            var entry = new RuleEntry(id, kind.Name);
            foreach (var parameter in kind.Parameters)
                entry.Values[parameter.JsonName] = EmptyValue(parameter);
            return entry;
        }

        /// <summary>그 칸 종류의 빈 값 (편집 창에서 칸을 더할 때).</summary>
        public static JsonValue EmptyValue(RuleField field)
        {
            switch (field.Kind)
            {
                case RuleValueKind.StringArray: return new JsonArray();
                case RuleValueKind.Enum: return new JsonString(field.EnumNames.Count > 0 ? field.EnumNames[0] : "");
                case RuleValueKind.Bool: return new JsonBool(false);
                case RuleValueKind.Int: return new JsonNumber("0");
                default: return new JsonString("");
            }
        }

        public string ToJson(RuleCatalog catalog)
        {
            var root = new JsonObject();
            if (Schema != null)
                root.Add(RulesJson.SchemaKey, new JsonString(Schema));

            var rules = new JsonArray();
            foreach (var entry in Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var obj = new JsonObject()
                    .Add(RulesJson.IdKey, new JsonString(entry.Id))
                    .Add(RulesJson.KindKey, new JsonString(entry.Kind));
                if (entry.Description != null)
                    obj.Add(RulesJson.DescriptionKey, new JsonString(entry.Description));

                var kind = catalog.Find(entry.Kind);
                var ordered = kind == null
                    ? entry.Values.Keys.OrderBy(k => k, StringComparer.Ordinal)
                    : kind.Fields.Select(f => f.JsonName).Where(entry.Values.ContainsKey)
                        .Concat(entry.Values.Keys.Where(k => kind.Fields.All(f => f.JsonName != k)).OrderBy(k => k, StringComparer.Ordinal));
                foreach (var key in ordered)
                    obj.Add(key, entry.Values[key]);
                rules.Add(obj);
            }
            root.Add(RulesJson.RulesKey, rules);
            return JsonWriter.Write(root);
        }

        /// <summary>쓸 내용을 생성기 검증에 그대로 돌린다 (값 종류 · id 중복 · AllowMultiple 등).</summary>
        public GenerateResult Validate(RuleCatalog catalog) => RulesJson.Generate(ToJson(catalog), catalog);

        private static RuleEntry? ReadEntry(JsonValue item, RuleCatalog catalog, List<RulesError> errors)
        {
            if (!(item is JsonObject obj))
            {
                errors.Add(new RulesError($"규칙은 객체여야 합니다 (지금 {item.KindName})", item.Line, item.Column));
                return null;
            }

            var id = obj.Get(RulesJson.IdKey) as JsonString;
            var kindName = obj.Get(RulesJson.KindKey) as JsonString;
            if (id == null || kindName == null)
            {
                errors.Add(new RulesError("규칙에 문자열 'id' · 'kind'가 있어야 편집할 수 있습니다", obj.Line, obj.Column));
                return null;
            }
            var kind = catalog.Find(kindName.Value);
            if (kind == null)
            {
                errors.Add(new RulesError($"모르는 kind '{kindName.Value}'", kindName.Line, kindName.Column, id.Value));
                return null;
            }

            var entry = new RuleEntry(id.Value, kind.Name);
            var ok = true;
            foreach (var member in obj.Members)
            {
                if (member.Key == RulesJson.IdKey || member.Key == RulesJson.KindKey)
                    continue;
                if (member.Key == RulesJson.DescriptionKey)
                {
                    if (member.Value is JsonString description)
                    {
                        entry.Description = description.Value;
                        continue;
                    }
                    errors.Add(new RulesError("'description'은(는) 문자열이어야 합니다", member.Value.Line, member.Value.Column, id.Value));
                    ok = false;
                    continue;
                }
                if (kind.Fields.All(f => f.JsonName != member.Key))
                {
                    errors.Add(new RulesError($"{kind.Name}에 없는 칸 '{member.Key}'", member.Value.Line, member.Value.Column, id.Value));
                    ok = false;
                    continue;
                }
                entry.Values[member.Key] = member.Value;
            }
            return ok ? entry : null;
        }
    }
}
