using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HBLint.Analyzers
{
    /// <summary>
    /// CLL0003: [assembly: HBLint.RequireDefineRule(define, message)] 규칙마다, define으로 가드되지 않은 코드에서
    /// Namespaces의 타입 · 멤버 · using을 쓰거나 같은 어셈블리에서 define 안에만 선언된 것(DeclaredInDefine)을 쓰면 진단한다.
    /// IgnoreAssemblies 패턴에 맞는 어셈블리는 예외. ([Conditional(define)] 메서드도 호출은 바인딩되므로 예외가 아니다.)
    /// 다른 어셈블리에 선언된 #if 안 멤버는 알 수 없다 (#if는 메타데이터에 남지 않음).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RequireDefineAnalyzer : DiagnosticAnalyzer
    {
        internal const string AttributeFullName = "HBLint.RequireDefineRuleAttribute";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Descriptors.RequireDefine);

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
            foreach (var source in LintRuleReader.Read(compilation, AttributeFullName))
            {
                var define = source.GetConstructorString(0);
                if (string.IsNullOrEmpty(define) || !LintRuleReader.RequirementsMet(compilation, source))
                    continue;
                if (source.GetNamedStrings("IgnoreAssemblies").Any(pattern => WildcardMatch(pattern, compilation.AssemblyName ?? "")))
                    continue;
                var message = source.GetConstructorString(1);
                if (string.IsNullOrEmpty(message))
                    message = $"#if {define} 밖에서는 쓸 수 없습니다 — {define}가 없는 빌드가 깨집니다";
                rules.Add(new Rule(
                    define!,
                    message!,
                    source.GetNamedStrings("Namespaces").Where(n => n.Length > 0).ToArray(),
                    source.GetNamed("DeclaredInDefine") is bool declared && declared,
                    LintRuleReader.GetSeverity(source)));
            }
            if (rules.Count == 0)
                return;

            var guardCache = new ConcurrentDictionary<(SyntaxTree Tree, string Define), DefineGuard.Guards>();
            DefineGuard.Guards Guards(SyntaxTree tree, string define) =>
                guardCache.GetOrAdd((tree, define), key => DefineGuard.GuardedSpans(key.Tree, key.Define, start.CancellationToken));

            // define 안에 선언된 이름들 — 파일마다 식별자를 모두 의미 분석하지 않도록 먼저 거른다
            var declaredNames = new ConcurrentDictionary<string, Lazy<HashSet<string>>>();
            HashSet<string> DeclaredNames(string define) =>
                declaredNames.GetOrAdd(define, d => new Lazy<HashSet<string>>(
                    () => CollectDeclaredNames(compilation, d, Guards, start.CancellationToken))).Value;

            // global using(C# 10)이 있으면 다른 파일에 네임스페이스 이름이 없어도 그 타입을 쓸 수 있어 텍스트로 거르지 않는다
            var hasGlobalUsing = new Lazy<bool>(() => compilation.SyntaxTrees.Any(tree =>
                tree.GetRoot(start.CancellationToken) is CompilationUnitSyntax unit
                && unit.Usings.Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))));

            start.RegisterSemanticModelAction(context => Analyze(context, rules, Guards, DeclaredNames, hasGlobalUsing));
        }

        private static void Analyze(
            SemanticModelAnalysisContext context,
            List<Rule> rules,
            Func<SyntaxTree, string, DefineGuard.Guards> guards,
            Func<string, HashSet<string>> declaredNames,
            Lazy<bool> hasGlobalUsing)
        {
            var model = context.SemanticModel;
            var tree = model.SyntaxTree;
            var root = tree.GetRoot(context.CancellationToken);
            var text = tree.GetText(context.CancellationToken).ToString();

            foreach (var rule in rules)
            {
                var namespacesInFile = rule.Namespaces.Length > 0
                    && (rule.Namespaces.Any(ns => text.Contains(LastSegment(ns))) || hasGlobalUsing.Value);
                var guardedNames = rule.DeclaredInDefine ? declaredNames(rule.Define) : null;
                if (!namespacesInFile && (guardedNames == null || guardedNames.Count == 0))
                    continue;
                var spans = guards(tree, rule.Define);

                foreach (var node in root.DescendantNodes())
                {
                    if (node is UsingDirectiveSyntax usingDirective)
                    {
                        if (namespacesInFile && !spans.Contains(usingDirective.SpanStart)
                            && model.GetSymbolInfo(usingDirective.Name, context.CancellationToken).Symbol is ISymbol imported
                            && InNamespaces(imported, rule.Namespaces))
                            Report(context, rule, usingDirective.Name.GetLocation());
                        continue;
                    }

                    if (!(node is SimpleNameSyntax name))
                        continue;
                    var isGuardedName = guardedNames != null && guardedNames.Contains(name.Identifier.ValueText);
                    if (!namespacesInFile && !isGuardedName)
                        continue;
                    if (spans.Contains(name.SpanStart) || node.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                        continue;

                    var info = model.GetSymbolInfo(name, context.CancellationToken);
                    var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (symbol == null || !IsCheckedKind(symbol))
                        continue;

                    if (namespacesInFile && InNamespaces(symbol, rule.Namespaces))
                    {
                        // UnityEditor.AssetDatabase.Refresh()처럼 왼쪽이 이미 걸린 타입이면 오른쪽 멤버는 다시 알리지 않는다
                        if (!LeftIsFlaggedType(name, model, rule, context))
                            Report(context, rule, name.GetLocation());
                    }
                    else if (isGuardedName && DeclaredInDefine(symbol, rule.Define, model.Compilation, guards))
                    {
                        Report(context, rule, name.GetLocation());
                    }
                }
            }
        }

        private static void Report(SemanticModelAnalysisContext context, Rule rule, Location location) =>
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.RequireDefine, location, rule.Severity,
                additionalLocations: null, properties: null, rule.Message));

        private static bool IsCheckedKind(ISymbol symbol) =>
            symbol is INamedTypeSymbol || symbol is IMethodSymbol || symbol is IPropertySymbol || symbol is IFieldSymbol || symbol is IEventSymbol;

        private static bool InNamespaces(ISymbol symbol, string[] namespaces)
        {
            var ns = symbol is INamespaceSymbol self ? self : symbol.ContainingNamespace;
            if (ns == null || ns.IsGlobalNamespace)
                return false;
            var name = ns.ToDisplayString();
            return namespaces.Any(target => name == target || name.StartsWith(target + ".", StringComparison.Ordinal));
        }

        private static bool LeftIsFlaggedType(SimpleNameSyntax name, SemanticModel model, Rule rule, SemanticModelAnalysisContext context)
        {
            ExpressionSyntax? left = null;
            if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
                left = memberAccess.Expression;
            else if (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name)
                left = qualified.Left;
            return left != null
                && model.GetSymbolInfo(left, context.CancellationToken).Symbol is INamedTypeSymbol type
                && InNamespaces(type, rule.Namespaces);
        }

        /// <summary>
        /// 선언이 이 컴파일 안에 있고 선언 위치가 모두 define 가드 구간 안인가.
        /// 같은 #if 묶음의 define 없이 켜지는 갈래(#else 등)에 같은 이름이 있으면 그 빌드에도 있다고 보고 아니라고 한다.
        /// </summary>
        private static bool DeclaredInDefine(ISymbol symbol, string define, Compilation compilation, Func<SyntaxTree, string, DefineGuard.Guards> guards)
        {
            var definition = symbol is IMethodSymbol method ? (method.ReducedFrom ?? method).OriginalDefinition : symbol.OriginalDefinition;
            var references = definition.DeclaringSyntaxReferences;
            return references.Length > 0 && references.All(r =>
            {
                if (!compilation.ContainsSyntaxTree(r.SyntaxTree))
                    return false;
                var spans = guards(r.SyntaxTree, define);
                return spans.Contains(r.Span.Start)
                    && !spans.AlternativeMentions(r.Span.Start, definition.Name, r.SyntaxTree.GetText());
            });
        }

        private static HashSet<string> CollectDeclaredNames(
            Compilation compilation,
            string define,
            Func<SyntaxTree, string, DefineGuard.Guards> guards,
            System.Threading.CancellationToken cancellationToken)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var spans = guards(tree, define);
                if (spans.Count == 0)
                    continue;
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
                {
                    var identifier = DeclaredIdentifier(node);
                    if (identifier.HasValue && spans.Contains(node.SpanStart))
                        names.Add(identifier.Value.ValueText);
                }
            }
            return names;
        }

        private static SyntaxToken? DeclaredIdentifier(SyntaxNode node)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method: return method.Identifier;
                case PropertyDeclarationSyntax property: return property.Identifier;
                case EventDeclarationSyntax eventDeclaration: return eventDeclaration.Identifier;
                case VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax: return variable.Identifier;
                case BaseTypeDeclarationSyntax type: return type.Identifier;
                case DelegateDeclarationSyntax @delegate: return @delegate.Identifier;
                case EnumMemberDeclarationSyntax enumMember: return enumMember.Identifier;
                default: return null;
            }
        }

        private static string LastSegment(string ns)
        {
            var dot = ns.LastIndexOf('.');
            return dot < 0 ? ns : ns.Substring(dot + 1);
        }

        /// <summary>* 와일드카드 패턴 (대소문자 가림).</summary>
        internal static bool WildcardMatch(string pattern, string value) =>
            Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

        private sealed class Rule
        {
            public Rule(string define, string message, string[] namespaces, bool declaredInDefine, DiagnosticSeverity severity)
            {
                Define = define;
                Message = message;
                Namespaces = namespaces;
                DeclaredInDefine = declaredInDefine;
                Severity = severity;
            }

            public string Define { get; }

            public string Message { get; }

            public string[] Namespaces { get; }

            public bool DeclaredInDefine { get; }

            public DiagnosticSeverity Severity { get; }
        }
    }
}
