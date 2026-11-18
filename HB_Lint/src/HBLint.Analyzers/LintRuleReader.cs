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
            Collect(compilation.Assembly, attributeFullName, result);
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // Unity 컴파일은 참조가 수백 개라 어트리뷰트 어셈블리를 참조하는 것만 본다.
                if (ReferencesAttributes(assembly))
                    Collect(assembly, attributeFullName, result);
            }
            return result;
        }

        /// <summary>규칙의 Requires 어셈블리를 검사 중인 컴파일이 모두 참조하는가.</summary>
        public static bool RequirementsMet(Compilation compilation, AttributeData rule)
        {
            var requires = GetNamedArgument(rule, "Requires");
            if (requires.IsNull || requires.Kind != TypedConstantKind.Array)
                return true;

            var referenced = new HashSet<string>(compilation.ReferencedAssemblyNames.Select(n => n.Name));
            foreach (var value in requires.Values)
            {
                if (value.Value is string name && !referenced.Contains(name))
                    return false;
            }
            return true;
        }

        public static DiagnosticSeverity GetSeverity(AttributeData rule)
        {
            var severity = GetNamedArgument(rule, "Severity");
            return severity.Value is int value
                ? LintSeverityValues.ToDiagnosticSeverity(value)
                : DiagnosticSeverity.Warning;
        }

        public static string? GetConstructorString(AttributeData rule, int index)
        {
            return rule.ConstructorArguments.Length > index ? rule.ConstructorArguments[index].Value as string : null;
        }

        private static TypedConstant GetNamedArgument(AttributeData rule, string name)
        {
            foreach (var pair in rule.NamedArguments)
            {
                if (pair.Key == name)
                    return pair.Value;
            }
            return default;
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

        private static void Collect(IAssemblySymbol assembly, string attributeFullName, List<LintRule> result)
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == attributeFullName)
                    result.Add(new LintRule(attribute, assembly));
            }
        }
    }

    /// <summary>규칙 어트리뷰트 하나와 그것이 붙은 어셈블리 (메타데이터 참조면 어트리뷰트에 소스 위치가 없어 진단에 어셈블리 이름을 쓴다).</summary>
    internal readonly struct LintRule
    {
        public LintRule(AttributeData data, IAssemblySymbol owner)
        {
            Data = data;
            Owner = owner;
        }

        public AttributeData Data { get; }

        public IAssemblySymbol Owner { get; }
    }
}
