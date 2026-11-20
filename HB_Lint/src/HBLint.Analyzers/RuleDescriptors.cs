using System.Collections.Immutable;
using System.Linq;
using HBLint.Core.Ir;
using Microsoft.CodeAnalysis;

namespace HBLint.Analyzers
{
    /// <summary>
    /// 진단 id 묶음 (CLL = Client Lint Rule). Roslyn은 SupportedDiagnostics에 미리 적은 id로만 보고를 받으므로
    /// 규칙 id는 이 묶음에서 고른다. 메시지 · 심각도는 규칙 IR의 것을 보고할 때 넣는다.
    /// </summary>
    internal static class RuleDescriptors
    {
        private const string Category = "HBLint";

        public static readonly ImmutableDictionary<string, DiagnosticDescriptor> Rules = Enumerable
            .Range(RuleIds.First, RuleIds.Last - RuleIds.First + 1)
            .Select(n => RuleIds.Format(n))
            .ToImmutableDictionary(id => id, id => new DiagnosticDescriptor(id, "HB_Lint 규칙 " + id, "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true));

        public static readonly DiagnosticDescriptor TargetNotFound = new DiagnosticDescriptor(
            "CLL9001",
            "규칙의 대상 타입 · 멤버를 찾을 수 없습니다",
            "규칙 {0}(어셈블리 {1})의 대상 '{2}'을(를) 찾을 수 없거나 여러 어셈블리에 있어 그 대상은 적용되지 않습니다. 타입 · 멤버 전체 이름과 requires를 확인하세요.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        public static readonly DiagnosticDescriptor RuleInvalid = new DiagnosticDescriptor(
            "CLL9002",
            "규칙을 읽을 수 없습니다",
            "어셈블리 {0}의 규칙 {1}을(를) 적용하지 않습니다: {2}",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTags.CompilationEnd);

        public static readonly ImmutableArray<DiagnosticDescriptor> All =
            Rules.Values.OrderBy(d => d.Id).Concat(new[] { TargetNotFound, RuleInvalid }).ToImmutableArray();
    }
}
