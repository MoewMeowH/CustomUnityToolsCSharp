using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace HBLint.Analyzers
{
    /// <summary>
    /// 규칙 어트리뷰트를 모은다. [assembly:]는 그 파일이 든 어셈블리에만 붙으므로, 검사 중인 어셈블리 자신과
    /// 그것이 참조하는 어셈블리(규칙 어셈블리)를 본다. 규칙 어셈블리를 참조하지 않은 어셈블리는 규칙이 없어 검사되지 않는다.
    /// </summary>
    internal static class LintRuleReader
    {
        internal const string AttributesAssemblyName = "HBLint.Attributes";

        public static List<LintRule> Read(Compilation compilation, string attributeFullName)
        {
            var result = new List<LintRule>();
            Collect(compilation, compilation.Assembly, attributeFullName, result);
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // Unity 컴파일은 참조가 수백 개라 어트리뷰트 어셈블리를 참조하는 것만 본다.
                if (ReferencesAttributes(assembly))
                    Collect(compilation, assembly, attributeFullName, result);
            }
            return result;
        }

        /// <summary>규칙의 Requires 어셈블리를 검사 중인 컴파일이 모두 참조하는가.</summary>
        public static bool RequirementsMet(Compilation compilation, LintRule rule)
        {
            var requires = rule.GetNamed("Requires") as object?[];
            if (requires == null)
                return true;

            var referenced = new HashSet<string>(compilation.ReferencedAssemblyNames.Select(n => n.Name));
            foreach (var value in requires)
            {
                if (value is string name && !referenced.Contains(name))
                    return false;
            }
            return true;
        }

        public static DiagnosticSeverity GetSeverity(LintRule rule)
        {
            return rule.GetNamed("Severity") is int value
                ? LintSeverityValues.ToDiagnosticSeverity(value)
                : DiagnosticSeverity.Warning;
        }

        private static bool ReferencesAttributes(IAssemblySymbol assembly)
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var identity in module.ReferencedAssemblies)
                {
                    if (identity.Name == AttributesAssemblyName)
                        return true;
                }
            }
            return false;
        }

        private static void Collect(Compilation compilation, IAssemblySymbol assembly, string attributeFullName, List<LintRule> result)
        {
            var resolved = FromAttributes(assembly.GetAttributes(), assembly, attributeFullName, out var unresolved);
            if (!unresolved)
            {
                result.AddRange(resolved);
                return;
            }

            // 검사 중인 컴파일이 HBLint.Attributes를 참조하지 않으면(Unity asmdef 참조는 간접으로 이어지지 않는다)
            // 어트리뷰트 타입을 못 찾아 인자가 비어 읽힌다. 그 어셈블리의 규칙은 한 경로로만 읽는다 (섞으면 같은 규칙이 두 번 들어간다):
            // IDE의 프로젝트 참조면 원래 컴파일(어트리뷰트가 풀린 곳)에서, DLL이면 메타데이터에서 직접.
            if (compilation.GetMetadataReference(assembly) is CompilationReference source)
                result.AddRange(FromAttributes(source.Compilation.Assembly.GetAttributes(), assembly, attributeFullName, out _));
            else
                result.AddRange(MetadataRuleReader.Read(compilation, assembly, attributeFullName));
        }

        private static List<LintRule> FromAttributes(IEnumerable<AttributeData> attributes, IAssemblySymbol owner, string attributeFullName, out bool unresolved)
        {
            var rules = new List<LintRule>();
            unresolved = false;
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass?.ToDisplayString() != attributeFullName)
                    continue;
                if (attribute.AttributeConstructor == null)
                {
                    unresolved = true;
                    continue;
                }
                rules.Add(new LintRule(
                    attribute.AttributeClass.Name,
                    owner,
                    attribute.ConstructorArguments.Select(Convert).ToArray(),
                    attribute.NamedArguments.ToDictionary(p => p.Key, p => Convert(p.Value))));
            }
            return rules;
        }

        /// <summary>문자열 · 정수(enum은 기반 값) · 배열(object?[])로.</summary>
        private static object? Convert(TypedConstant constant)
        {
            if (constant.Kind != TypedConstantKind.Array)
                return constant.Value;
            return constant.IsNull ? null : constant.Values.Select(Convert).ToArray();
        }
    }

    /// <summary>
    /// 규칙 어트리뷰트 하나 — 인자를 문자열 · 정수 · 배열로 고른 것. 어트리뷰트 타입을 찾았든(AttributeData)
    /// 못 찾아 메타데이터에서 읽었든 같은 모양이다. Owner는 규칙이 붙은 어셈블리 (진단 메시지에 이름을 쓴다).
    /// </summary>
    internal sealed class LintRule
    {
        private readonly IReadOnlyList<object?> _constructorArguments;
        private readonly IReadOnlyDictionary<string, object?> _namedArguments;

        public LintRule(string attributeName, IAssemblySymbol owner, IReadOnlyList<object?> constructorArguments, IReadOnlyDictionary<string, object?> namedArguments)
        {
            AttributeName = attributeName;
            Owner = owner;
            _constructorArguments = constructorArguments;
            _namedArguments = namedArguments;
        }

        /// <summary>예 "BanNullOperatorRuleAttribute".</summary>
        public string AttributeName { get; }

        public IAssemblySymbol Owner { get; }

        public string? GetConstructorString(int index) =>
            index < _constructorArguments.Count ? _constructorArguments[index] as string : null;

        public object? GetNamed(string name) =>
            _namedArguments.TryGetValue(name, out var value) ? value : null;
    }
}
