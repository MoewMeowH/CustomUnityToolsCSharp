#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HBLint.Generator.Json;

namespace HBLint.Generator
{
    /// <summary>rules.json 오류 하나. RuleId는 규칙 안 오류일 때만.</summary>
    public sealed class RulesError
    {
        public RulesError(string message, int line, int column, string? ruleId = null)
        {
            Message = message;
            Line = line;
            Column = column;
            RuleId = ruleId;
        }

        public string Message { get; }

        public int Line { get; }

        public int Column { get; }

        public string? RuleId { get; }

        public override string ToString() =>
            $"({Line},{Column}){(RuleId == null ? "" : " [" + RuleId + "]")} {Message}";
    }

    public sealed class GenerateResult
    {
        internal GenerateResult(string? text, IReadOnlyList<RulesError> errors)
        {
            Text = text;
            Errors = errors;
        }

        /// <summary>오류가 하나라도 있으면 null.</summary>
        public string? Text { get; }

        public IReadOnlyList<RulesError> Errors { get; }

        public bool Success => Text != null;
    }

    /// <summary>
    /// rules.json → LintRules.g.cs. 오류를 모두 모은 뒤 하나라도 있으면 만들지 않는다 (생성물 컴파일 에러는 참조 어셈블리 전체를 막으므로).
    /// 출력은 늘 같다: id 순, 생성자 인자는 매개변수 순, 속성은 이름 순, LF.
    /// </summary>
    public static class RulesJson
    {
        public const string SchemaKey = "$schema";
        public const string RulesKey = "rules";
        public const string IdKey = "id";
        public const string KindKey = "kind";
        public const string DescriptionKey = "description";

        internal static readonly string[] ReservedRuleKeys = { IdKey, KindKey, DescriptionKey };

        private static readonly char[] CSharpNewLines = { '\r', '\n', '\u0085', '\u2028', '\u2029' };

        public static GenerateResult Generate(string json, RuleCatalog catalog)
        {
            JsonValue root;
            try
            {
                root = JsonReader.Parse(json);
            }
            catch (JsonParseException e)
            {
                return Fail(new RulesError("JSON 형식 오류: " + e.Message, e.Line, e.Column));
            }

            var errors = new List<RulesError>();
            var rules = ReadRules(root, catalog, errors);
            if (errors.Count > 0)
                return new GenerateResult(null, errors);
            return new GenerateResult(Emit(rules), errors);
        }

        /// <summary>id 형식: 영문 · 숫자 · '.' · '_' · '-' 한 자 이상.</summary>
        public static bool IsValidId(string id)
        {
            if (id.Length == 0)
                return false;
            foreach (var c in id)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!ok)
                    return false;
            }
            return true;
        }

        private static GenerateResult Fail(RulesError error) => new GenerateResult(null, new[] { error });

        private static List<ParsedRule> ReadRules(JsonValue root, RuleCatalog catalog, List<RulesError> errors)
        {
            var result = new List<ParsedRule>();
            if (!(root is JsonObject rootObject))
            {
                errors.Add(new RulesError($"맨 바깥은 객체여야 합니다 (지금 {root.KindName})", root.Line, root.Column));
                return result;
            }
            foreach (var member in rootObject.Members)
            {
                if (member.Key != SchemaKey && member.Key != RulesKey)
                    errors.Add(new RulesError($"모르는 칸 '{member.Key}' (맨 바깥에는 \"{SchemaKey}\" · \"{RulesKey}\"만)", member.Value.Line, member.Value.Column));
            }
            var schema = rootObject.Get(SchemaKey);
            if (schema != null && !(schema is JsonString))
                errors.Add(new RulesError($"\"{SchemaKey}\"는 문자열이어야 합니다", schema.Line, schema.Column));

            var rulesValue = rootObject.Get(RulesKey);
            if (rulesValue == null)
            {
                errors.Add(new RulesError($"\"{RulesKey}\" 배열이 없습니다", rootObject.Line, rootObject.Column));
                return result;
            }
            if (!(rulesValue is JsonArray rulesArray))
            {
                errors.Add(new RulesError($"\"{RulesKey}\"는 배열이어야 합니다 (지금 {rulesValue.KindName})", rulesValue.Line, rulesValue.Column));
                return result;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var singleKinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in rulesArray.Items)
            {
                // id 중복은 규칙에 다른 오류가 있어도 본다 (고친 다음 실행에서야 중복이 드러나지 않게)
                if (item is JsonObject itemObject && itemObject.Get(IdKey) is JsonString idValue && !ids.Add(idValue.Value))
                    errors.Add(new RulesError($"id '{idValue.Value}'이(가) 두 번 있습니다", item.Line, item.Column, idValue.Value));

                var rule = ReadRule(item, catalog, errors);
                if (rule == null)
                    continue;
                // AllowMultiple = false인 어트리뷰트를 두 번 내보내면 생성물이 컴파일되지 않는다 (CS0579)
                if (!rule.Kind.AllowMultiple && !singleKinds.Add(rule.Kind.Name))
                    errors.Add(new RulesError($"{rule.Kind.Name}은(는) 한 번만 쓸 수 있습니다 (어트리뷰트가 AllowMultiple = false)", item.Line, item.Column, rule.Id));
                result.Add(rule);
            }
            return result;
        }

        private static ParsedRule? ReadRule(JsonValue item, RuleCatalog catalog, List<RulesError> errors)
        {
            if (!(item is JsonObject obj))
            {
                errors.Add(new RulesError($"규칙은 객체여야 합니다 (지금 {item.KindName})", item.Line, item.Column));
                return null;
            }

            var before = errors.Count;
            var id = ReadReservedString(obj, IdKey, required: true, null, errors);
            if (id != null && !IsValidId(id))
                errors.Add(Error(obj.Get(IdKey)!, $"id '{id}' 형식이 틀렸습니다 (영문 · 숫자 · . _ - 만)", id));
            var kindName = ReadReservedString(obj, KindKey, required: true, id, errors);
            var description = ReadReservedString(obj, DescriptionKey, required: false, id, errors);

            RuleKind? kind = null;
            if (kindName != null)
            {
                kind = catalog.Find(kindName);
                if (kind == null)
                    errors.Add(Error(obj.Get(KindKey)!, $"모르는 kind '{kindName}' (있는 것: {string.Join(" · ", catalog.Kinds.Select(k => k.Name))})", id));
            }
            if (kind == null)
                return null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in obj.Members)
            {
                if (Array.IndexOf(ReservedRuleKeys, member.Key) >= 0)
                    continue;
                var field = kind.Fields.FirstOrDefault(f => f.JsonName == member.Key);
                if (field == null)
                {
                    errors.Add(Error(member.Value, $"{kind.Name}에 없는 칸 '{member.Key}' (있는 것: {string.Join(" · ", kind.Fields.Select(f => f.JsonName))})", id));
                    continue;
                }
                var literal = ToLiteral(field, member.Value, id, errors);
                if (literal != null)
                    values[field.ClrName] = literal;
            }
            foreach (var parameter in kind.Parameters)
            {
                if (!obj.Contains(parameter.JsonName))
                    errors.Add(Error(obj, $"{kind.Name}의 필수 칸 '{parameter.JsonName}'이(가) 없습니다", id));
            }

            if (errors.Count > before)
                return null;
            return new ParsedRule(id!, kind, description, values);
        }

        private static string? ReadReservedString(JsonObject obj, string key, bool required, string? id, List<RulesError> errors)
        {
            var value = obj.Get(key);
            if (value == null)
            {
                if (required)
                    errors.Add(Error(obj, $"필수 칸 '{key}'이(가) 없습니다", id));
                return null;
            }
            if (value is JsonString str)
                return str.Value;
            errors.Add(Error(value, $"'{key}'은(는) 문자열이어야 합니다 (지금 {value.KindName})", id));
            return null;
        }

        /// <summary>JSON 값을 C# 리터럴로. 값 종류가 틀리면 오류를 더하고 null.</summary>
        private static string? ToLiteral(RuleField field, JsonValue value, string? id, List<RulesError> errors)
        {
            switch (field.Kind)
            {
                case RuleValueKind.String:
                    if (value is JsonString str)
                        return StringLiteral(str.Value);
                    break;
                case RuleValueKind.StringArray:
                    if (value is JsonArray array)
                    {
                        var items = new List<string>();
                        foreach (var element in array.Items)
                        {
                            if (!(element is JsonString elementString))
                            {
                                errors.Add(Error(element, $"'{field.JsonName}'의 항목은 문자열이어야 합니다 (지금 {element.KindName})", id));
                                return null;
                            }
                            items.Add(StringLiteral(elementString.Value));
                        }
                        // 필수 배열이 비면 규칙이 아무것도 검사하지 않는다 — 분석기는 알리지 않으므로 여기서 막는다
                        if (items.Count == 0 && field.Required)
                        {
                            errors.Add(Error(value, $"필수 칸 '{field.JsonName}'이(가) 비어 있습니다 — 항목을 하나 이상 적으세요", id));
                            return null;
                        }
                        return items.Count == 0 ? "new string[0]" : "new string[] { " + string.Join(", ", items) + " }";
                    }
                    break;
                case RuleValueKind.Enum:
                    if (value is JsonString enumName)
                    {
                        if (field.EnumNames.Contains(enumName.Value))
                            return "global::" + string.Join(".", (field.EnumTypeName + "." + enumName.Value).Split('.').Select(Identifier));
                        errors.Add(Error(value, $"'{field.JsonName}' 값 '{enumName.Value}'이(가) 없습니다 (있는 것: {string.Join(" · ", field.EnumNames)})", id));
                        return null;
                    }
                    break;
                case RuleValueKind.Bool:
                    if (value is JsonBool boolean)
                        return boolean.Value ? "true" : "false";
                    break;
                case RuleValueKind.Int:
                    if (value is JsonNumber number)
                    {
                        if (int.TryParse(number.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                            return parsed.ToString(CultureInfo.InvariantCulture);
                        errors.Add(Error(value, $"'{field.JsonName}'은(는) int 범위의 정수여야 합니다 (지금 {number.Raw})", id));
                        return null;
                    }
                    break;
            }
            errors.Add(Error(value, $"'{field.JsonName}'은(는) {Describe(field)}이어야 합니다 (지금 {value.KindName})", id));
            return null;
        }

        private static string Describe(RuleField field)
        {
            switch (field.Kind)
            {
                case RuleValueKind.String: return "문자열";
                case RuleValueKind.StringArray: return "문자열 배열";
                case RuleValueKind.Enum: return "문자열(" + string.Join(" · ", field.EnumNames) + ")";
                case RuleValueKind.Bool: return "true · false";
                default: return "정수";
            }
        }

        private static RulesError Error(JsonValue at, string message, string? id) => new RulesError(message, at.Line, at.Column, id);

        private static string Emit(List<ParsedRule> rules)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated>\n");
            sb.Append("//     HBLint 생성기(hblint-gen)가 rules.json에서 만들었습니다.\n");
            sb.Append("//     직접 고치지 말고 rules.json을 고친 뒤 다시 생성하세요.\n");
            sb.Append("// </auto-generated>\n");

            foreach (var rule in rules.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                sb.Append('\n');
                sb.Append("// ").Append(rule.Id).Append('\n');
                if (rule.Description != null)
                {
                    foreach (var line in rule.Description.Split(CSharpNewLines))
                        sb.Append("//   ").Append(line).Append('\n');
                }

                var attributeName = rule.Kind.AttributeFullName.Substring(0, rule.Kind.AttributeFullName.Length - "Attribute".Length);
                var arguments = rule.Kind.Parameters.Select(p => rule.Values[p.ClrName])
                    .Concat(rule.Kind.Properties.Where(p => rule.Values.ContainsKey(p.ClrName)).Select(p => Identifier(p.ClrName) + " = " + rule.Values[p.ClrName]));
                sb.Append("[assembly: global::").Append(attributeName).Append('(').Append(string.Join(", ", arguments)).Append(")]\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// C# 일반 문자열 리터럴. C#이 줄바꿈으로 보는 문자(\r \n U+0085 U+2028 U+2029) · 제어 문자 · 짝 없는 대리 문자는 \uXXXX로.
        /// </summary>
        /// <summary>C# 예약 키워드면 @를 붙인다 (리플렉션은 @default · @event를 "default" · "event"로 돌려준다).</summary>
        internal static string Identifier(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
            "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
            "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
            "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
            "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };

        internal static string StringLiteral(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); continue;
                    case '"': sb.Append("\\\""); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\n': sb.Append("\\n"); continue;
                    case '\t': sb.Append("\\t"); continue;
                }
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c).Append(value[i + 1]);
                    i++;
                    continue;
                }
                if (char.IsControl(c) || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029')
                {
                    sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    continue;
                }
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private sealed class ParsedRule
        {
            public ParsedRule(string id, RuleKind kind, string? description, Dictionary<string, string> values)
            {
                Id = id;
                Kind = kind;
                Description = description;
                Values = values;
            }

            public string Id { get; }

            public RuleKind Kind { get; }

            public string? Description { get; }

            /// <summary>ClrName → C# 리터럴.</summary>
            public Dictionary<string, string> Values { get; }
        }
    }
}
