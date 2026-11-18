using Microsoft.CodeAnalysis;

namespace HBLint.Analyzers
{
    /// <summary>HBLint.LintSeverity 값 (어트리뷰트 쪽 enum과 값이 같아야 함 — 분석기는 그 어셈블리를 참조하지 않는다).</summary>
    internal static class LintSeverityValues
    {
        public const int Info = 1;
        public const int Warning = 2;
        public const int Error = 3;

        /// <summary>모르는 값은 Warning.</summary>
        public static DiagnosticSeverity ToDiagnosticSeverity(int value)
        {
            switch (value)
            {
                case Info: return DiagnosticSeverity.Info;
                case Error: return DiagnosticSeverity.Error;
                default: return DiagnosticSeverity.Warning;
            }
        }
    }
}
