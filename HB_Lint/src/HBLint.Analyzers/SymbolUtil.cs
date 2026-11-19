using Microsoft.CodeAnalysis;

namespace HBLint.Analyzers
{
    internal static class SymbolUtil
    {
        /// <summary>type이 target이거나 그 자식인가. 타입 매개변수는 제약 타입으로 본다 (T where T : UnityEngine.Object).</summary>
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
            return false;
        }
    }
}
