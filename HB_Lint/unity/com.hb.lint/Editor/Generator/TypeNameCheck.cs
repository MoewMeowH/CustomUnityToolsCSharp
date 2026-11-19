#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HBLint.Generator.Json;

namespace HBLint.Generator
{
    /// <summary>
    /// [HBLint.TypeName] · [HBLint.MemberName] 칸의 값이 실제 타입 · 멤버인지 본다 (분석기 CLL9001은 위치가 없어 Unity 콘솔에 안 보이므로 생성 단계에서 막는다).
    /// 칸이 배열이면 항목마다 본다. 규칙의 requires에 적은 어셈블리가 하나라도 없으면(예: 서버 전용 규칙) 그 규칙은 확인하지 않는다.
    /// 타입 · 멤버 · 어셈블리가 있는지는 호출하는 쪽이 알려 준다 (Unity는 불려 있는 어셈블리).
    /// 멤버는 분석기처럼 그 멤버가 선언된 타입 이름으로 적어야 한다 — 부모에 선언된 것이면 선언 타입을 알려 주고 막는다.
    /// </summary>
    public static class TypeNameCheck
    {
        public const string RequiresKey = "requires";

        private const string NameHint = "네임스페이스 포함 메타데이터 이름 — 중첩은 Outer+Inner, 제네릭은 List`1";

        /// <param name="memberOwner">(타입, 멤버 이름) → 그 이름의 메서드 · 속성 · 필드를 선언한 타입의 메타데이터 이름 (그 타입 자신에 있으면 그 타입, 부모에만 있으면 가까운 부모), 없으면 null.</param>
        public static IReadOnlyList<RulesError> Check(
            string json,
            RuleCatalog catalog,
            Func<string, bool> typeExists,
            Func<string, bool> assemblyAvailable,
            Func<string, string, string?> memberOwner)
        {
            var errors = new List<RulesError>();
            var document = RulesDocument.Load(json, catalog, out _);
            if (document == null)
                return errors; // 구조 오류는 RulesJson.Generate가 이미 알린다

            foreach (var rule in document.Rules)
            {
                var kind = catalog.Find(rule.Kind);
                if (kind == null || !RequirementsAvailable(rule, assemblyAvailable))
                    continue;
                foreach (var field in kind.Fields.Where(f => f.IsTypeName || f.IsMemberName))
                {
                    if (!rule.Values.TryGetValue(field.JsonName, out var value))
                        continue;
                    foreach (var name in Strings(value))
                    {
                        var error = field.IsMemberName
                            ? CheckMember(field, name, typeExists, memberOwner)
                            : CheckType(field, name, typeExists);
                        if (error != null)
                            errors.Add(new RulesError(error, name.Line, name.Column, rule.Id));
                    }
                }
            }
            return errors;
        }

        private static string? CheckType(RuleField field, JsonString name, Func<string, bool> typeExists)
        {
            if (string.IsNullOrWhiteSpace(name.Value))
                return $"'{field.JsonName}'이(가) 비어 있습니다 — 타입 이름을 적으세요";
            return typeExists(name.Value) ? null : $"'{field.JsonName}'의 타입 '{name.Value}'을(를) 찾을 수 없습니다 ({NameHint})";
        }

        private static string? CheckMember(RuleField field, JsonString name, Func<string, bool> typeExists, Func<string, string, string?> memberOwner)
        {
            var separator = name.Value.LastIndexOf('.');
            if (string.IsNullOrWhiteSpace(name.Value) || separator <= 0 || separator == name.Value.Length - 1)
                return $"'{field.JsonName}' 항목 '{name.Value}'은(는) '타입.멤버' 꼴이어야 합니다 (예 UnityEngine.Component.GetComponent)";
            var typeName = name.Value.Substring(0, separator);
            var memberName = name.Value.Substring(separator + 1);
            if (!typeExists(typeName))
                return $"'{field.JsonName}' 항목 '{name.Value}'의 타입 '{typeName}'을(를) 찾을 수 없습니다 ({NameHint})";
            var owner = memberOwner(typeName, memberName);
            if (owner == null)
                return $"'{field.JsonName}' 항목 '{name.Value}': 타입 '{typeName}'에 멤버 '{memberName}'이(가) 없습니다 (메서드 · 속성 · 필드)";
            if (owner != typeName)
                return $"'{field.JsonName}' 항목 '{name.Value}': '{memberName}'은(는) '{owner}'에 선언돼 있습니다 — '{owner}.{memberName}'(으)로 적으세요";
            return null;
        }

        /// <summary>문자열 칸이면 그 값, 문자열 배열 칸이면 문자열 항목들.</summary>
        private static IEnumerable<JsonString> Strings(JsonValue value)
        {
            if (value is JsonString single)
                return new[] { single };
            if (value is JsonArray array)
                return array.Items.OfType<JsonString>();
            return Enumerable.Empty<JsonString>();
        }

        private static bool RequirementsAvailable(RuleEntry rule, Func<string, bool> assemblyAvailable)
        {
            if (!rule.Values.TryGetValue(RequiresKey, out var requires) || !(requires is JsonArray array))
                return true;
            return array.Items.OfType<JsonString>().All(name => assemblyAvailable(name.Value));
        }
    }
}
