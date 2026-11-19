using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace HBLint.Analyzers
{
    /// <summary>
    /// CLL0002: [assembly: HBLint.BanCallInMethodRule(methods, members, ownerType, message)] 규칙마다,
    /// ownerType 계열 클래스의 methods(예 Update) 안에서 members(호출 · 속성 · 필드, 오버라이드 포함)를 쓰면 진단한다.
    /// 메서드 안 로컬 함수는 그 메서드로 보고, 람다 안은 보지 않는다. FollowDepth만큼 같은 클래스 헬퍼를 따라간다.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BanCallInMethodAnalyzer : DiagnosticAnalyzer
    {
        internal const string AttributeFullName = "HBLint.BanCallInMethodRuleAttribute";
        internal const int MaxFollowDepth = 2;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Descriptors.BanCallInMethod, Descriptors.RuleTypeNotFound);

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

                var ownerName = source.GetConstructorString(2);
                var owner = string.IsNullOrEmpty(ownerName) ? null : compilation.GetTypeByMetadataName(ownerName!);
                if (owner == null)
                {
                    notFound.Add(NotFound(source, ownerName ?? string.Empty));
                    continue;
                }

                var banned = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                foreach (var member in source.GetConstructorStrings(1))
                {
                    var found = ResolveMembers(compilation, member);
                    if (found.Count == 0)
                        notFound.Add(NotFound(source, member));
                    banned.UnionWith(found);
                }

                var methods = new HashSet<string>(source.GetConstructorStrings(0), StringComparer.Ordinal);
                if (banned.Count == 0 || methods.Count == 0)
                    continue;

                var message = source.GetConstructorString(3);
                if (string.IsNullOrEmpty(message))
                    message = $"{string.Join(" · ", methods)} 안에서는 이 멤버를 쓰지 않습니다";
                var depth = source.GetNamed("FollowDepth") is int value ? Math.Max(0, Math.Min(MaxFollowDepth, value)) : 0;
                rules.Add(new Rule(methods, banned, owner, message!, LintRuleReader.GetSeverity(source), depth));
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
            start.RegisterSymbolStartAction(symbolStart => OnTypeStart(symbolStart, rules), SymbolKind.NamedType);
        }

        private static Diagnostic NotFound(LintRule source, string name) =>
            Diagnostic.Create(Descriptors.RuleTypeNotFound, Location.None, source.AttributeName, source.Owner.Name, name);

        /// <summary>"타입 메타데이터 이름.멤버 이름" → 그 이름의 메서드(오버로드 · 제네릭 전부) · 속성 · 필드.</summary>
        private static List<ISymbol> ResolveMembers(Compilation compilation, string member)
        {
            var separator = member.LastIndexOf('.');
            if (separator <= 0 || separator == member.Length - 1)
                return new List<ISymbol>();
            var type = compilation.GetTypeByMetadataName(member.Substring(0, separator));
            if (type == null)
                return new List<ISymbol>();
            return type.GetMembers(member.Substring(separator + 1))
                .Where(m => m is IMethodSymbol || m is IPropertySymbol || m is IFieldSymbol)
                .ToList();
        }

        private static void OnTypeStart(SymbolStartAnalysisContext context, List<Rule> rules)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            var applicable = rules.Where(r => SymbolUtil.IsOrDerivesFrom(type, r.Owner)).ToArray();
            if (applicable.Length == 0)
                return;

            var hits = new ConcurrentQueue<Hit>();
            var calls = new ConcurrentQueue<KeyValuePair<IMethodSymbol, IMethodSymbol>>();
            context.RegisterOperationAction(
                operation => OnOperation(operation, type, applicable, hits, calls),
                OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.FieldReference);
            context.RegisterSymbolEndAction(end => Report(end, type, applicable, hits, calls));
        }

        private static void OnOperation(
            OperationAnalysisContext context,
            INamedTypeSymbol type,
            Rule[] rules,
            ConcurrentQueue<Hit> hits,
            ConcurrentQueue<KeyValuePair<IMethodSymbol, IMethodSymbol>> calls)
        {
            var container = ContainingMethod(context.Operation, context.CancellationToken);
            if (container == null || !SymbolEqualityComparer.Default.Equals(container.ContainingType?.OriginalDefinition, type))
                return;

            ISymbol target;
            switch (context.Operation)
            {
                case IInvocationOperation invocation:
                    target = invocation.TargetMethod;
                    var callee = invocation.TargetMethod.OriginalDefinition;
                    if (callee.MethodKind == MethodKind.Ordinary && SymbolEqualityComparer.Default.Equals(callee.ContainingType?.OriginalDefinition, type))
                        calls.Enqueue(new KeyValuePair<IMethodSymbol, IMethodSymbol>(container, callee));
                    break;
                case IPropertyReferenceOperation property:
                    target = property.Property;
                    break;
                case IFieldReferenceOperation field:
                    target = field.Field;
                    break;
                default:
                    return;
            }

            foreach (var rule in rules)
            {
                if (Matches(target, rule.Banned))
                    hits.Enqueue(new Hit(container, rule, NameLocation(context.Operation.Syntax)));
            }
        }

        /// <summary>연산을 감싸는 메서드. 로컬 함수는 감싸는 메서드로 올라가고, 람다를 지나면 null (나중에 도는 콜백일 수 있어서).</summary>
        private static IMethodSymbol? ContainingMethod(IOperation operation, System.Threading.CancellationToken cancellationToken)
        {
            for (var node = operation.Syntax; node != null; node = node.Parent)
            {
                switch (node)
                {
                    case AnonymousFunctionExpressionSyntax _:
                        return null;
                    case MethodDeclarationSyntax method:
                        // partial 메서드 구현부는 정의부로 맞춘다 (호출 대상 · GetMembers는 정의부)
                        var declared = operation.SemanticModel?.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
                        return declared?.PartialDefinitionPart ?? declared;
                    case BaseTypeDeclarationSyntax _:
                    case AccessorDeclarationSyntax _:
                    case ArrowExpressionClauseSyntax arrow when !(arrow.Parent is MethodDeclarationSyntax) && !(arrow.Parent is LocalFunctionStatementSyntax):
                        return null;
                }
            }
            return null;
        }

        /// <summary>금지 멤버이거나, 그것을 오버라이드한 멤버인가 (확장 메서드는 원래 정의, 제네릭은 열린 정의로).</summary>
        private static bool Matches(ISymbol target, HashSet<ISymbol> banned)
        {
            switch (target)
            {
                case IMethodSymbol method:
                    for (var current = method.ReducedFrom ?? method; current != null; current = current.OverriddenMethod)
                    {
                        if (banned.Contains(current.OriginalDefinition))
                            return true;
                    }
                    return false;
                case IPropertySymbol property:
                    for (var current = property; current != null; current = current.OverriddenProperty)
                    {
                        if (banned.Contains(current.OriginalDefinition))
                            return true;
                    }
                    return false;
                case IFieldSymbol field:
                    return banned.Contains(field.OriginalDefinition);
                default:
                    return false;
            }
        }

        /// <summary>멤버 이름 자리 (예 gameObject.GetComponent&lt;T&gt;()의 GetComponent&lt;T&gt;).</summary>
        private static Location NameLocation(SyntaxNode syntax)
        {
            if (syntax is InvocationExpressionSyntax invocation)
                syntax = invocation.Expression;
            if (syntax is MemberAccessExpressionSyntax memberAccess)
                return memberAccess.Name.GetLocation();
            return syntax.GetLocation();
        }

        private static void Report(
            SymbolAnalysisContext context,
            INamedTypeSymbol type,
            Rule[] rules,
            ConcurrentQueue<Hit> hits,
            ConcurrentQueue<KeyValuePair<IMethodSymbol, IMethodSymbol>> calls)
        {
            var callMap = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
            foreach (var call in calls)
            {
                if (!callMap.TryGetValue(call.Key, out var callees))
                    callMap[call.Key] = callees = new List<IMethodSymbol>();
                callees.Add(call.Value);
            }

            foreach (var rule in rules)
            {
                // 규칙의 메서드에서 출발해 같은 클래스 메서드를 FollowDepth만큼 따라간 경로 (먼저 닿은 경로 하나)
                var paths = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
                var frontier = type.GetMembers().OfType<IMethodSymbol>().Where(m => rule.Methods.Contains(m.Name)).ToList();
                foreach (var entry in frontier)
                    paths[entry] = entry.Name;
                for (var level = 0; level < rule.Depth; level++)
                {
                    var next = new List<IMethodSymbol>();
                    foreach (var caller in frontier)
                    {
                        if (!callMap.TryGetValue(caller, out var callees))
                            continue;
                        foreach (var callee in callees)
                        {
                            if (paths.ContainsKey(callee))
                                continue;
                            paths[callee] = paths[caller] + " → " + callee.Name;
                            next.Add(callee);
                        }
                    }
                    frontier = next;
                }

                foreach (var hit in hits)
                {
                    if (hit.Rule != rule || !paths.TryGetValue(hit.Container, out var path))
                        continue;
                    var message = rule.Methods.Contains(hit.Container.Name) && path == hit.Container.Name
                        ? rule.Message
                        : rule.Message + " (" + path + ")";
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.BanCallInMethod, hit.Location, rule.Severity,
                        additionalLocations: null, properties: null, message));
                }
            }
        }

        private sealed class Rule
        {
            public Rule(HashSet<string> methods, HashSet<ISymbol> banned, INamedTypeSymbol owner, string message, DiagnosticSeverity severity, int depth)
            {
                Methods = methods;
                Banned = banned;
                Owner = owner;
                Message = message;
                Severity = severity;
                Depth = depth;
            }

            public HashSet<string> Methods { get; }

            public HashSet<ISymbol> Banned { get; }

            public INamedTypeSymbol Owner { get; }

            public string Message { get; }

            public DiagnosticSeverity Severity { get; }

            public int Depth { get; }
        }

        private sealed class Hit
        {
            public Hit(IMethodSymbol container, Rule rule, Location location)
            {
                Container = container;
                Rule = rule;
                Location = location;
            }

            public IMethodSymbol Container { get; }

            public Rule Rule { get; }

            public Location Location { get; }
        }
    }
}
