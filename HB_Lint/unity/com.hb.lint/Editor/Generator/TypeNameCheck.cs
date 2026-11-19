#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HBLint.Generator.Json;

namespace HBLint.Generator
{
    /// <summary>
    /// [HBLint.TypeName] 칸의 값이 실제 타입인지 본다 (분석기 CLL9001은 위치가 없어 Unity 콘솔에 안 보이므로 생성 단계에서 막는다).
    /// 규칙의 requires에 적은 어셈블리가 하나라도 없으면(예: 서버 전용 규칙) 그 규칙은 확인하지 않는다.
    /// 타입 · 어셈블리가 있는지는 호출하는 쪽이 알려 준다 (Unity는 불려 있는 어셈블리).
    /// </summary>
    public static class TypeNameCheck
    {
        public const string RequiresKey = "requires";

        public static IReadOnlyList<RulesError> Check(string json, RuleCatalog catalog, Func<string, bool> typeExists, Func<string, bool> assemblyAvailable)
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
                foreach (var field in kind.Fields.Where(f => f.IsTypeName))
                {
                    if (!rule.Values.TryGetValue(field.JsonName, out var value) || !(value is JsonString typeName))
                        continue;
                    if (string.IsNullOrWhiteSpace(typeName.Value))
                        errors.Add(new RulesError($"'{field.JsonName}'이(가) 비어 있습니다 — 타입 이름을 적으세요", typeName.Line, typeName.Column, rule.Id));
                    else if (!typeExists(typeName.Value))
                        errors.Add(new RulesError(
                            $"'{field.JsonName}'의 타입 '{typeName.Value}'을(를) 찾을 수 없습니다 (네임스페이스 포함 메타데이터 이름 — 중첩은 Outer+Inner, 제네릭은 List`1)",
                            typeName.Line, typeName.Column, rule.Id));
                }
            }
            return errors;
        }

        private static bool RequirementsAvailable(RuleEntry rule, Func<string, bool> assemblyAvailable)
        {
            if (!rule.Values.TryGetValue(RequiresKey, out var requires) || !(requires is JsonArray array))
                return true;
            return array.Items.OfType<JsonString>().All(name => assemblyAvailable(name.Value));
        }
    }
}
