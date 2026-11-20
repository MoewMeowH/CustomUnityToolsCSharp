using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HBLint.Analyzers
{
    internal static class SymbolUtil
    {
        /// <summary>type이 target이거나 그 자식(target이 인터페이스면 구현 타입 포함)인가. 타입 매개변수는 제약 타입으로 본다 (T where T : UnityEngine.Object).</summary>
        public static bool IsOrDerivesFrom(ITypeSymbol type, INamedTypeSymbol target)
        {
            if (type is ITypeParameterSymbol typeParameter)
            {
                foreach (var constraint in typeParameter.ConstraintTypes)
                {
                    if (IsOrDerivesFrom(constraint, target))
                        return true;
                }
                return false;
            }

            for (ITypeSymbol? current = type; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target))
                    return true;
            }
            // 기준이 인터페이스면 그것을 구현한 타입도 계열로 본다
            return target.TypeKind == TypeKind.Interface
                && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target));
        }

        /// <summary>이름이 가리키는 타입 · 메서드 · 속성 · 필드 · 이벤트 (바인딩이 모호하면 첫 후보). 그 밖이면 null.</summary>
        public static ISymbol? Referenced(SemanticModel model, SimpleNameSyntax name, CancellationToken cancellationToken)
        {
            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            return symbol is INamedTypeSymbol || symbol is IMethodSymbol || symbol is IPropertySymbol || symbol is IFieldSymbol || symbol is IEventSymbol
                ? symbol
                : null;
        }

        /// <summary>
        /// 코드가 속한 메서드. 로컬 함수는 감싸는 메서드로 올라가고, 람다를 지나면 null (나중에 도는 콜백일 수 있어서).
        /// 접근자 · 식 본문 속성 · 타입 선언 바로 아래(필드 초기화 등)도 null. partial 메서드 구현부는 정의부로 맞춘다.
        /// </summary>
        public static IMethodSymbol? ContainingMethod(SyntaxNode syntax, SemanticModel model, CancellationToken cancellationToken)
        {
            for (var node = syntax; node != null; node = node.Parent)
            {
                switch (node)
                {
                    case AnonymousFunctionExpressionSyntax _:
                        return null;
                    case MethodDeclarationSyntax method:
                        // partial 메서드 구현부는 정의부로 맞춘다 (호출 대상 · GetMembers는 정의부)
                        var declared = model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
                        return declared?.PartialDefinitionPart ?? declared;
                    case BaseTypeDeclarationSyntax _:
                    case AccessorDeclarationSyntax _:
                    case ArrowExpressionClauseSyntax arrow when !(arrow.Parent is MethodDeclarationSyntax) && !(arrow.Parent is LocalFunctionStatementSyntax):
                        return null;
                }
            }
            return null;
        }

        /// <summary>코드를 직접 감싸는 타입 선언의 타입 (중첩 타입이면 안쪽).</summary>
        public static INamedTypeSymbol? ContainingType(SyntaxNode syntax, SemanticModel model, CancellationToken cancellationToken)
        {
            var declaration = syntax.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            return declaration == null ? null : model.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol;
        }
    }
}
