using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HB_TestFrameworkHelper.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class HBNeedSlotNullCheckSerializeFieldAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "HB_NeedSlotNullCheckSerializeField";

        private const string Title = "[NeedSlotNullCheck] 필드의 [SerializeField] 누락";
        private const string MessageFormat = "[NeedSlotNullCheck]가 선언된 private 필드 '{0}'에는 반드시 [SerializeField]가 선언되어야 합니다";
        private const string Description = "private 필드에 [NeedSlotNullCheck]가 선언되었으나 [SerializeField]가 없으면 Unity 인스펙터에 노출 및 직렬화되지 않아 널 검사가 무의미해집니다.";
        private const string Category = "HB_CodingConvention";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSymbolAction(AnalyzeFieldSymbol, SymbolKind.Field);
        }

        private static void AnalyzeFieldSymbol(SymbolAnalysisContext context)
        {
            var fieldSymbol = (IFieldSymbol)context.Symbol;

            // 1. [NeedSlotNullCheck] 어트리뷰트 확인
            var attributes = fieldSymbol.GetAttributes();
            bool hasNeedSlotNullCheck = attributes.Any(a =>
                a.AttributeClass != null &&
                (a.AttributeClass.Name == "NeedSlotNullCheck" || a.AttributeClass.Name == "NeedSlotNullCheckAttribute"));

            if (!hasNeedSlotNullCheck)
            {
                return;
            }

            // 2. private 필드인지 확인
            if (fieldSymbol.DeclaredAccessibility != Accessibility.Private)
            {
                return;
            }

            // 3. [SerializeField] 어트리뷰트 확인
            bool hasSerializeField = attributes.Any(a =>
                a.AttributeClass != null &&
                (a.AttributeClass.Name == "SerializeField" || a.AttributeClass.Name == "SerializeFieldAttribute"));

            if (!hasSerializeField)
            {
                var location = fieldSymbol.Locations.FirstOrDefault() ?? Location.None;
                var diagnostic = Diagnostic.Create(Rule, location, fieldSymbol.Name);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
