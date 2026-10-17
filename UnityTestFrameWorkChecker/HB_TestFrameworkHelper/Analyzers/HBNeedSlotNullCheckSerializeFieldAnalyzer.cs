using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HB_TestFrameworkHelper.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class HBNeedSlotNullCheckSerializeFieldAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticIdSerializeField = "HB_NeedSlotNullCheckSerializeField";
        public const string DiagnosticIdInvalidType = "HB_NeedSlotNullCheckInvalidType";
        public const string DiagnosticIdNotSerializable = "HB_NeedSlotNullCheckNotSerializable";

        private const string Category = "HB_CodingConvention";

        // 규칙 1: [SerializeField] 누락 검사
        private static readonly DiagnosticDescriptor RuleMissingSerializeField = new DiagnosticDescriptor(
            DiagnosticIdSerializeField,
            "[NeedSlotNullCheck] 필드의 [SerializeField] 누락",
            "[NeedSlotNullCheck]가 선언된 public이 아닌 필드 '{0}'에는 반드시 [SerializeField]가 선언되어야 합니다",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "public이 아닌(private · protected · internal) 슬롯 필드에 [NeedSlotNullCheck]가 선언되었으나 [SerializeField]가 없으면 Unity 인스펙터에 노출 및 직렬화되지 않아 널 검사가 무의미해집니다.");

        // 규칙 2: UnityEngine.Object 상속 타입 검사 (컴포넌트, GameObject, ScriptableObject 등)
        private static readonly DiagnosticDescriptor RuleInvalidType = new DiagnosticDescriptor(
            DiagnosticIdInvalidType,
            "[NeedSlotNullCheck] 유효하지 않은 슬롯 필드 타입",
            "[NeedSlotNullCheck]가 선언된 필드 '{0}'의 타입({1})은 UnityEngine.Object(Component, GameObject, ScriptableObject 등) 계열이어야 합니다",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "[NeedSlotNullCheck]는 인스펙터에서 참조를 할당받는 UnityEngine.Object 계열 슬롯 필드에만 유효합니다. 일반 C# 클래스나 값 타입에는 사용할 수 없습니다.");

        // 규칙 3: [SerializeField]를 붙여도 Unity가 직렬화하지 않는 필드 (static · readonly · const · [NonSerialized])
        private static readonly DiagnosticDescriptor RuleNotSerializable = new DiagnosticDescriptor(
            DiagnosticIdNotSerializable,
            "[NeedSlotNullCheck] 직렬화되지 않는 필드",
            "[NeedSlotNullCheck]가 선언된 필드 '{0}'은(는) {1}라서 Unity가 직렬화하지 않습니다",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Unity는 static · readonly · const 필드와 [NonSerialized] 필드를 [SerializeField]가 있어도 직렬화하지 않습니다. 인스펙터에서 할당할 수 없으므로 널 검사가 무의미합니다.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(RuleMissingSerializeField, RuleInvalidType, RuleNotSerializable);

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

            // 2. 타입 검사 (UnityEngine.Object 계열 여부)
            if (!IsUnityEngineObjectType(fieldSymbol.Type))
            {
                var location = fieldSymbol.Locations.FirstOrDefault() ?? Location.None;
                var diagnostic = Diagnostic.Create(
                    RuleInvalidType,
                    location,
                    fieldSymbol.Name,
                    fieldSymbol.Type.ToDisplayString());
                context.ReportDiagnostic(diagnostic);
                return; // 유효하지 않은 타입인 경우 [SerializeField] 검사는 생략
            }

            // 3. Unity가 직렬화하지 않는 필드 ([SerializeField]로도 해결되지 않음)
            if (GetNotSerializableReason(fieldSymbol, attributes) is { } reason)
            {
                var location = fieldSymbol.Locations.FirstOrDefault() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(RuleNotSerializable, location, fieldSymbol.Name, reason));
                return;
            }

            // 4. public이 아닌 필드(private · protected · internal)는 [SerializeField]가 있어야 직렬화된다
            if (fieldSymbol.DeclaredAccessibility != Accessibility.Public)
            {
                bool hasSerializeField = attributes.Any(a =>
                    a.AttributeClass != null &&
                    (a.AttributeClass.Name == "SerializeField" || a.AttributeClass.Name == "SerializeFieldAttribute"));

                if (!hasSerializeField)
                {
                    var location = fieldSymbol.Locations.FirstOrDefault() ?? Location.None;
                    var diagnostic = Diagnostic.Create(RuleMissingSerializeField, location, fieldSymbol.Name);
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }

        private static string? GetNotSerializableReason(IFieldSymbol fieldSymbol, ImmutableArray<AttributeData> attributes)
        {
            if (fieldSymbol.IsConst) return "const";
            if (fieldSymbol.IsStatic) return "static";
            if (fieldSymbol.IsReadOnly) return "readonly";

            bool hasNonSerialized = attributes.Any(a =>
                a.AttributeClass != null &&
                (a.AttributeClass.Name == "NonSerialized" || a.AttributeClass.Name == "NonSerializedAttribute"));
            return hasNonSerialized ? "[NonSerialized]" : null;
        }

        private static bool IsUnityEngineObjectType(ITypeSymbol? typeSymbol)
        {
            if (typeSymbol == null)
            {
                return false;
            }

            // 배열 지원 (e.g. GameObject[], Transform[])
            if (typeSymbol is IArrayTypeSymbol arrayType)
            {
                return IsUnityEngineObjectType(arrayType.ElementType);
            }

            // 제네릭 컬렉션 지원 (e.g. List<Transform>, List<GameObject>)
            if (typeSymbol is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                if (namedType.Name == "List" || namedType.AllInterfaces.Any(i => i.Name == "IEnumerable"))
                {
                    var typeArg = namedType.TypeArguments.FirstOrDefault();
                    if (typeArg != null && IsUnityEngineObjectType(typeArg))
                    {
                        return true;
                    }
                }
            }

            // 상속 계층 순회: UnityEngine.Object 상속 여부 확인
            var current = typeSymbol;
            while (current != null)
            {
                if (current.Name == "Object" && current.ContainingNamespace?.ToDisplayString() == "UnityEngine")
                {
                    return true;
                }
                current = current.BaseType;
            }

            return false;
        }
    }
}
