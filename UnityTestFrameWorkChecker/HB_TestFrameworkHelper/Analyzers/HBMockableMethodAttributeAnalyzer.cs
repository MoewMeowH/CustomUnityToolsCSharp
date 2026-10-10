using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HB_TestFrameworkHelper.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class HBMockableMethodAttributeAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "HB_MockableMethodAttribute";

        private const string Title = "IAutoTestMockable 구현 메서드 필수 어트리뷰트 누락";
        private const string MessageFormat = "IAutoTestMockable의 구현 메서드 '{0}'에는 반드시 필수 어트리뷰트가 선언되어야 합니다";
        private const string Description = "IAutoTestMockable 인터페이스의 SetupForAutoTest() 구현 메서드는 에디터 전용이어야 하므로 필수 어트리뷰트가 선언되어야 합니다.";
        private const string Category = "HB_CodingConvention";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSymbolAction(AnalyzeMethodSymbol, SymbolKind.Method);
        }

        private static void AnalyzeMethodSymbol(SymbolAnalysisContext context)
        {
            var methodSymbol = (IMethodSymbol)context.Symbol;

            // 1. 대상 메서드명 확인
            if (methodSymbol.Name != "SetupForAutoTest")
            {
                return;
            }

            var containingType = methodSymbol.ContainingType;
            if (containingType == null || (containingType.TypeKind != TypeKind.Class && containingType.TypeKind != TypeKind.Struct))
            {
                return;
            }

            // 2. IAutoTestMockable 인터페이스 구현 여부 검사
            bool implementsMockable = false;
            foreach (var iface in containingType.AllInterfaces)
            {
                if (iface.Name != "IAutoTestMockable")
                {
                    continue;
                }

                // 2-1. 명시적 인터페이스 구현 검사
                if (methodSymbol.ExplicitInterfaceImplementations.Any(m =>
                        m.ContainingType != null &&
                        m.ContainingType.Name == "IAutoTestMockable" &&
                        m.Name == "SetupForAutoTest"))
                {
                    implementsMockable = true;
                    break;
                }

                // 2-2. 일반(암시적) 구현 검사
                var interfaceMember = iface.GetMembers("SetupForAutoTest").OfType<IMethodSymbol>().FirstOrDefault();
                if (interfaceMember != null)
                {
                    var impl = containingType.FindImplementationForInterfaceMember(interfaceMember);
                    if (SymbolEqualityComparer.Default.Equals(impl, methodSymbol))
                    {
                        implementsMockable = true;
                        break;
                    }
                }
            }

            if (!implementsMockable)
            {
                return;
            }

            // 3. 필수 어트리뷰트 존재 여부 검사
            bool hasRequiredAttribute = false;
            foreach (var attr in methodSymbol.GetAttributes())
            {
                var attrClass = attr.AttributeClass;
                if (attrClass == null) continue;

                // [CustomEditorOnly]
                if (attrClass.Name == "CustomEditorOnly" || attrClass.Name == "CustomEditorOnlyAttribute")
                {
                    hasRequiredAttribute = true;
                    break;
                }

                // [System.Diagnostics.Conditional("UNITY_EDITOR")]
                if ((attrClass.Name == "Conditional" || attrClass.Name == "ConditionalAttribute") &&
                    attrClass.ContainingNamespace?.ToDisplayString() == "System.Diagnostics")
                {
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string conditionString &&
                        conditionString == "UNITY_EDITOR")
                    {
                        hasRequiredAttribute = true;
                        break;
                    }
                }
            }

            if (!hasRequiredAttribute)
            {
                var location = methodSymbol.Locations.FirstOrDefault() ?? Location.None;
                var diagnostic = Diagnostic.Create(Rule, location, methodSymbol.Name);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }
}
