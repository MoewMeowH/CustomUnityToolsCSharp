using Microsoft.CodeAnalysis;

namespace HBLint.Analyzers
{
    /// <summary>진단 ID는 규칙 종류마다 하나 (CLL = Client Lint Rule). 메시지는 규칙 어트리뷰트의 것을 그대로 쓴다.</summary>
    internal static class Descriptors
    {
        private const string Category = "HBLint";

        public static readonly DiagnosticDescriptor BanNullOperator = new DiagnosticDescriptor(
            "CLL0001",
            "이 타입에는 null 연산자를 쓰지 않습니다",
            "{0}",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleTypeNotFound = new DiagnosticDescriptor(
            "CLL9001",
            "규칙의 대상 타입을 찾을 수 없습니다",
            "규칙 {0}(어셈블리 {1})의 대상 타입 '{2}'을(를) 찾을 수 없거나 여러 어셈블리에 있어 이 규칙이 적용되지 않습니다. 타입 전체 이름과 Requires를 확인하세요.",
            Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTags.CompilationEnd);
    }
}
