using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace HBLint.Analyzers
{
    /// <summary>
    /// CLL0001: [assembly: HBLint.BanNullOperatorRule(type, message)] 규칙마다, 정적 타입이 type이거나 그 자식인 식에
    /// ?. · ?? · ??= · is null · is not null을 쓰면 진단한다. 정적 타입이 인터페이스 · object면 잡지 않는다.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BanNullOperatorAnalyzer : DiagnosticAnalyzer
    {
        internal const string AttributeFullName = "HBLint.BanNullOperatorRuleAttribute";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Descriptors.BanNullOperator, Descriptors.RuleTypeNotFound);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext start)
        {
            var compilation = start.Compilation;
            var rules = new List<Rule>();
            var notFound = new List<Diagnostic>();

            foreach (var source in LintRuleReader.Read(compilation, AttributeFullName))
            {
                if (!LintRuleReader.RequirementsMet(compilation, source))
                    continue;

                var typeName = source.GetConstructorString(0);
                var type = string.IsNullOrEmpty(typeName) ? null : compilation.GetTypeByMetadataName(typeName!);
                if (type == null)
                {
                    notFound.Add(Diagnostic.Create(Descriptors.RuleTypeNotFound, Location.None,
                        source.AttributeName, source.Owner.Name, typeName ?? string.Empty));
                    continue;
                }

                var message = source.GetConstructorString(1);
                if (string.IsNullOrEmpty(message))
                    message = $"'{typeName}' 계열에는 null 연산자(?. · ?? · ??= · is null)를 쓰지 않습니다";
                rules.Add(new Rule(type, message!, LintRuleReader.GetSeverity(source)));
            }

            if (notFound.Count > 0)
            {
                start.RegisterCompilationEndAction(end =>
                {
                    foreach (var diagnostic in notFound)
                        end.ReportDiagnostic(diagnostic);
                });
            }

            if (rules.Count == 0)
                return;

            start.RegisterSyntaxNodeAction(
                node => AnalyzeNode(node, rules),
                SyntaxKind.ConditionalAccessExpression,
                SyntaxKind.CoalesceExpression,
                SyntaxKind.CoalesceAssignmentExpression,
                SyntaxKind.IsPatternExpression);
        }

        private static void AnalyzeNode(SyntaxNodeAnalysisContext context, List<Rule> rules)
        {
            ExpressionSyntax target;
            TextSpan span;
            switch (context.Node)
            {
                case ConditionalAccessExpressionSyntax conditional:
                    target = conditional.Expression;
                    // OperatorToken은 '?'뿐이고 '.' · '['는 뒤 식의 첫 토큰이다
                    span = TextSpan.FromBounds(conditional.OperatorToken.SpanStart, conditional.WhenNotNull.GetFirstToken().Span.End);
                    break;
                case BinaryExpressionSyntax binary:
                    target = binary.Left;
                    span = binary.OperatorToken.Span;
                    break;
                case AssignmentExpressionSyntax assignment:
                    target = assignment.Left;
                    span = assignment.OperatorToken.Span;
                    break;
                case IsPatternExpressionSyntax isPattern when IsNullPattern(isPattern.Pattern):
                    target = isPattern.Expression;
                    span = TextSpan.FromBounds(isPattern.IsKeyword.SpanStart, isPattern.Pattern.Span.End);
                    break;
                default:
                    return;
            }

            var type = context.SemanticModel.GetTypeInfo(target, context.CancellationToken).Type;
            if (type == null)
                return;

            foreach (var rule in rules)
            {
                if (!IsOrDerivesFrom(type, rule.Type))
                    continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.BanNullOperator,
                    Location.Create(context.Node.SyntaxTree, span),
                    rule.Severity,
                    additionalLocations: null,
                    properties: null,
                    rule.Message));
            }
        }

        private static bool IsNullPattern(PatternSyntax pattern)
        {
            switch (pattern)
            {
                case ConstantPatternSyntax constant:
                    return constant.Expression.IsKind(SyntaxKind.NullLiteralExpression);
                case UnaryPatternSyntax unary when unary.IsKind(SyntaxKind.NotPattern):
                    return IsNullPattern(unary.Pattern);
                case ParenthesizedPatternSyntax parenthesized:
                    return IsNullPattern(parenthesized.Pattern);
                case BinaryPatternSyntax binary: // is not null and var x · is null or …
                    return IsNullPattern(binary.Left) || IsNullPattern(binary.Right);
                default:
                    return false;
            }
        }

        /// <summary>type이 target이거나 그 자식인가. 타입 매개변수는 제약 타입으로 본다 (T where T : UnityEngine.Object).</summary>
        private static bool IsOrDerivesFrom(ITypeSymbol type, INamedTypeSymbol target)
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

        private sealed class Rule
        {
            public Rule(INamedTypeSymbol type, string message, DiagnosticSeverity severity)
            {
                Type = type;
                Message = message;
                Severity = severity;
            }

            public INamedTypeSymbol Type { get; }

            public string Message { get; }

            public DiagnosticSeverity Severity { get; }
        }
    }
}
