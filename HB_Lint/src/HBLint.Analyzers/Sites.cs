using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace HBLint.Analyzers
{
    /// <summary>
    /// 사이트 — 규칙이 걸 수 있는 코드 위치 하나. Node는 속한 메서드 · 타입을 찾을 때,
    /// Anchor는 디파인 가드 판정에, TargetType은 "대상 타입" 필터에 쓴다 (사이트가 다루는 식의 정적 타입, 없으면 null).
    /// </summary>
    internal sealed class Site
    {
        public Site(SyntaxNode node, SemanticModel model, Location location, int anchor, ITypeSymbol? targetType)
        {
            Node = node;
            Model = model;
            Location = location;
            Anchor = anchor;
            TargetType = targetType;
        }

        public SyntaxNode Node { get; }

        public SemanticModel Model { get; }

        public Location Location { get; }

        public int Anchor { get; }

        public ITypeSymbol? TargetType { get; }
    }

    /// <summary>사이트를 만드는 노드 (When). 연산 또는 구문 노드 동작으로 불리고, 노드 · 연산 하나에서 사이트는 많아야 하나다.</summary>
    internal abstract class SiteProducer
    {
        protected SiteProducer(OperationKind[] operationKinds, SyntaxKind[] syntaxKinds)
        {
            OperationKinds = operationKinds;
            SyntaxKinds = syntaxKinds;
        }

        public OperationKind[] OperationKinds { get; }

        public SyntaxKind[] SyntaxKinds { get; }

        public virtual Site? OnOperation(IOperation operation) => null;

        public virtual Site? OnSyntax(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) => null;
    }

    /// <summary>sites.member — 멤버(메서드 오버로드 · 오버라이드 · 속성 · 필드)를 호출 · 읽기 · 쓰기로 쓴 곳.</summary>
    internal sealed class MemberProducer : SiteProducer
    {
        private readonly HashSet<ISymbol> _members;
        private readonly bool _call;
        private readonly bool _read;
        private readonly bool _write;

        public MemberProducer(HashSet<ISymbol> members, string[] uses)
            : base(Kinds(uses), new SyntaxKind[0])
        {
            _members = members;
            _call = uses.Contains("call");
            _read = uses.Contains("read");
            _write = uses.Contains("write");
        }

        private static OperationKind[] Kinds(string[] uses) =>
            (uses.Contains("call") ? new[] { OperationKind.Invocation } : new OperationKind[0])
            .Concat(uses.Contains("read") || uses.Contains("write") ? new[] { OperationKind.PropertyReference, OperationKind.FieldReference } : new OperationKind[0])
            .ToArray();

        public override Site? OnOperation(IOperation operation)
        {
            ISymbol target;
            IOperation? instance;
            switch (operation)
            {
                case IInvocationOperation invocation when _call:
                    target = invocation.TargetMethod;
                    instance = invocation.Instance;
                    break;
                case IMemberReferenceOperation reference when (reference is IPropertyReferenceOperation || reference is IFieldReferenceOperation):
                    var (read, written) = Uses(operation);
                    if (!(read && _read) && !(written && _write))
                        return null;
                    target = reference.Member;
                    instance = reference.Instance;
                    break;
                default:
                    return null;
            }
            if (!Matches(target, _members) || operation.SemanticModel == null)
                return null;
            var location = NameLocation(operation.Syntax);
            return new Site(operation.Syntax, operation.SemanticModel, location, location.SourceSpan.Start, instance?.Type ?? target.ContainingType);
        }

        /// <summary>
        /// 속성 · 필드 참조를 읽는가 · 쓰는가. 단순 대입 대상 · out 인자 · 분해 대입 대상은 쓰기만,
        /// 복합 대입(+= · ??=) · 증감 · ref 인자는 둘 다, 그 밖(in 인자 포함)은 읽기만.
        /// </summary>
        private static (bool Read, bool Write) Uses(IOperation reference)
        {
            switch (reference.Parent)
            {
                case ISimpleAssignmentOperation simple when simple.Target == reference:
                    return (false, true);
                case IAssignmentOperation compound when compound.Target == reference:
                    return (true, true);
                case IIncrementOrDecrementOperation _:
                    return (true, true);
                case IArgumentOperation argument when argument.Parameter != null:
                    return argument.Parameter.RefKind == RefKind.Out ? (false, true)
                        : argument.Parameter.RefKind == RefKind.Ref ? (true, true)
                        : (true, false);
                case ITupleOperation _:
                    // (x.f, y) = … — 바깥 튜플이 분해 대입의 대상이면 쓰기
                    var child = reference.Parent;
                    while (child.Parent is ITupleOperation outer)
                        child = outer;
                    return child.Parent is IDeconstructionAssignmentOperation deconstruction && deconstruction.Target == child ? (false, true) : (true, false);
                default:
                    return (true, false);
            }
        }

        /// <summary>"타입 메타데이터 이름.멤버 이름" → 그 이름의 메서드(오버로드 · 제네릭 전부) · 속성 · 필드.</summary>
        public static List<ISymbol> Resolve(Compilation compilation, string member)
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

        /// <summary>대상 멤버이거나, 그것을 오버라이드한 멤버인가 (확장 메서드는 원래 정의, 제네릭은 열린 정의로).</summary>
        private static bool Matches(ISymbol target, HashSet<ISymbol> members)
        {
            switch (target)
            {
                case IMethodSymbol method:
                    for (var current = method.ReducedFrom ?? method; current != null; current = current.OverriddenMethod)
                    {
                        if (members.Contains(current.OriginalDefinition))
                            return true;
                    }
                    return false;
                case IPropertySymbol property:
                    for (var current = property; current != null; current = current.OverriddenProperty)
                    {
                        if (members.Contains(current.OriginalDefinition))
                            return true;
                    }
                    return false;
                case IFieldSymbol field:
                    return members.Contains(field.OriginalDefinition);
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
    }

    /// <summary>sites.nullOperator — ?. · ?? · ??= · is null(is not null · 복합 패턴 포함). TargetType은 연산자 왼쪽 식의 정적 타입.</summary>
    internal sealed class NullOperatorProducer : SiteProducer
    {
        private readonly HashSet<string> _operators;

        public NullOperatorProducer(string[] operators)
            : base(new OperationKind[0], new[]
            {
                SyntaxKind.ConditionalAccessExpression,
                SyntaxKind.CoalesceExpression,
                SyntaxKind.CoalesceAssignmentExpression,
                SyntaxKind.IsPatternExpression,
            })
        {
            _operators = new HashSet<string>(operators);
        }

        public override Site? OnSyntax(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax target;
            TextSpan span;
            switch (node)
            {
                case ConditionalAccessExpressionSyntax conditional when _operators.Contains("?."):
                    target = conditional.Expression;
                    // OperatorToken은 '?'뿐이고 '.' · '['는 뒤 식의 첫 토큰이다
                    span = TextSpan.FromBounds(conditional.OperatorToken.SpanStart, conditional.WhenNotNull.GetFirstToken().Span.End);
                    break;
                case BinaryExpressionSyntax binary when _operators.Contains("??"):
                    target = binary.Left;
                    span = binary.OperatorToken.Span;
                    break;
                case AssignmentExpressionSyntax assignment when _operators.Contains("??="):
                    target = assignment.Left;
                    span = assignment.OperatorToken.Span;
                    break;
                case IsPatternExpressionSyntax isPattern when _operators.Contains("is null") && IsNullPattern(isPattern.Pattern):
                    target = isPattern.Expression;
                    span = TextSpan.FromBounds(isPattern.IsKeyword.SpanStart, isPattern.Pattern.Span.End);
                    break;
                default:
                    return null;
            }

            var type = model.GetTypeInfo(target, cancellationToken).Type;
            return type == null ? null : new Site(node, model, Location.Create(node.SyntaxTree, span), span.Start, type);
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
    }

    /// <summary>
    /// sites.namespace — 네임스페이스(하위 포함)의 타입 · 멤버 · using을 쓴 곳.
    /// UnityEditor.AssetDatabase.Refresh()처럼 왼쪽이 이미 걸린 타입이면 오른쪽 멤버는 다시 만들지 않는다.
    /// </summary>
    internal sealed class NamespaceProducer : SiteProducer
    {
        private readonly Compilation _compilation;
        private readonly string[] _namespaces;
        private readonly ConcurrentDictionary<SyntaxTree, bool> _mentioned = new ConcurrentDictionary<SyntaxTree, bool>();

        /// <summary>0 아직 모름 · 1 없음 · 2 있음.</summary>
        private int _globalUsing;

        public NamespaceProducer(Compilation compilation, string[] namespaces, bool usings)
            : base(new OperationKind[0], usings
                ? new[] { SyntaxKind.IdentifierName, SyntaxKind.GenericName, SyntaxKind.UsingDirective }
                : new[] { SyntaxKind.IdentifierName, SyntaxKind.GenericName })
        {
            _compilation = compilation;
            _namespaces = namespaces;
        }

        public override Site? OnSyntax(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            // 파일마다 식별자를 모두 의미 분석하지 않도록 네임스페이스 이름이 글에 없는 파일은 건너뛴다
            var tree = node.SyntaxTree;
            if (!_mentioned.GetOrAdd(tree, t => HasGlobalUsing(cancellationToken)
                || _namespaces.Any(ns => t.GetText(cancellationToken).ToString().Contains(LastSegment(ns)))))
                return null;

            if (node is UsingDirectiveSyntax usingDirective)
            {
                return model.GetSymbolInfo(usingDirective.Name, cancellationToken).Symbol is ISymbol imported && InNamespaces(imported, _namespaces)
                    ? new Site(node, model, usingDirective.Name.GetLocation(), usingDirective.SpanStart, null)
                    : null;
            }

            var name = (SimpleNameSyntax)node;
            if (node.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                return null;
            var symbol = SymbolUtil.Referenced(model, name, cancellationToken);
            if (symbol == null || !InNamespaces(symbol, _namespaces) || LeftIsFlaggedType(name, model, cancellationToken))
                return null;
            return new Site(node, model, name.GetLocation(), name.SpanStart, null);
        }

        /// <summary>
        /// global using(C# 10)이 있으면 다른 파일에 네임스페이스 이름이 없어도 그 타입을 쓸 수 있어 텍스트로 거르지 않는다.
        /// 지금 콜백의 토큰으로 한 번 계산한다 (취소되면 저장하지 않고 다음 콜백이 다시 계산).
        /// </summary>
        private bool HasGlobalUsing(CancellationToken cancellationToken)
        {
            var known = Volatile.Read(ref _globalUsing);
            if (known != 0)
                return known == 2;
            var found = _compilation.SyntaxTrees.Any(tree =>
                tree.GetRoot(cancellationToken) is CompilationUnitSyntax unit
                && unit.Usings.Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)));
            Volatile.Write(ref _globalUsing, found ? 2 : 1);
            return found;
        }

        private static bool InNamespaces(ISymbol symbol, string[] namespaces)
        {
            var ns = symbol is INamespaceSymbol self ? self : symbol.ContainingNamespace;
            if (ns == null || ns.IsGlobalNamespace)
                return false;
            var name = ns.ToDisplayString();
            return namespaces.Any(target => name == target || name.StartsWith(target + ".", StringComparison.Ordinal));
        }

        private bool LeftIsFlaggedType(SimpleNameSyntax name, SemanticModel model, CancellationToken cancellationToken)
        {
            ExpressionSyntax? left = null;
            if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
                left = memberAccess.Expression;
            else if (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name)
                left = qualified.Left;
            return left != null
                && model.GetSymbolInfo(left, cancellationToken).Symbol is INamedTypeSymbol type
                && InNamespaces(type, _namespaces);
        }

        private static string LastSegment(string ns)
        {
            var dot = ns.LastIndexOf('.');
            return dot < 0 ? ns : ns.Substring(dot + 1);
        }
    }

    /// <summary>
    /// sites.declaredInDefine — 같은 컴파일에서 선언이 모두 define 가드 안에만 있는 것을 쓴 곳.
    /// 같은 #if 묶음의 define 없이 켜지는 갈래(#else 등)에 같은 이름이 있으면 그 빌드에도 있다고 보고 만들지 않는다.
    /// 다른 어셈블리에 선언된 #if 안 멤버는 알 수 없다 (#if는 메타데이터에 남지 않음).
    /// </summary>
    internal sealed class DeclaredInDefineProducer : SiteProducer
    {
        private readonly string _define;
        private readonly Compilation _compilation;
        private readonly GuardCache _guards;
        private HashSet<string>? _names;

        public DeclaredInDefineProducer(Compilation compilation, string define, GuardCache guards)
            : base(new OperationKind[0], new[] { SyntaxKind.IdentifierName, SyntaxKind.GenericName })
        {
            _define = define;
            _compilation = compilation;
            _guards = guards;
        }

        public override Site? OnSyntax(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            var name = (SimpleNameSyntax)node;
            if (!Names(cancellationToken).Contains(name.Identifier.ValueText) || node.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                return null;
            var symbol = SymbolUtil.Referenced(model, name, cancellationToken);
            return symbol != null && DeclaredInDefine(symbol, cancellationToken) ? new Site(node, model, name.GetLocation(), name.SpanStart, null) : null;
        }

        /// <summary>
        /// define 안에 선언된 이름들 — 파일마다 식별자를 모두 의미 분석하지 않도록 먼저 거른다.
        /// 지금 콜백의 토큰으로 한 번 계산한다 (취소되면 저장하지 않고 다음 콜백이 다시 계산).
        /// </summary>
        private HashSet<string> Names(CancellationToken cancellationToken)
        {
            var names = Volatile.Read(ref _names);
            if (names != null)
                return names;
            names = CollectDeclaredNames(_compilation, _define, _guards, cancellationToken);
            return Interlocked.CompareExchange(ref _names, names, null) ?? names;
        }

        /// <summary>선언이 이 컴파일 안에 있고 선언 위치가 모두 define 가드 구간 안인가.</summary>
        private bool DeclaredInDefine(ISymbol symbol, CancellationToken cancellationToken)
        {
            var definition = symbol is IMethodSymbol method ? (method.ReducedFrom ?? method).OriginalDefinition : symbol.OriginalDefinition;
            var references = definition.DeclaringSyntaxReferences;
            return references.Length > 0 && references.All(r =>
            {
                if (!_compilation.ContainsSyntaxTree(r.SyntaxTree))
                    return false;
                var spans = _guards.Get(r.SyntaxTree, _define, cancellationToken);
                return spans.Contains(r.Span.Start)
                    && !spans.AlternativeMentions(r.Span.Start, definition.Name, r.SyntaxTree.GetText());
            });
        }

        private static HashSet<string> CollectDeclaredNames(Compilation compilation, string define, GuardCache guards, CancellationToken cancellationToken)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var spans = guards.Get(tree, define, cancellationToken);
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
    }

    /// <summary>사이트 필터 (filter.*). 흐름 중간에서 사이트를 거른다.</summary>
    internal abstract class SiteFilter
    {
        public abstract bool Passes(Site site, CancellationToken cancellationToken);
    }

    /// <summary>filter.targetType — 사이트가 다루는 식의 정적 타입이 이 타입 계열인 것만 (대상 타입은 null 연산자 · 멤버 사이트에만 있다).</summary>
    internal sealed class TargetTypeFilter : SiteFilter
    {
        private readonly INamedTypeSymbol _type;

        public TargetTypeFilter(INamedTypeSymbol type) => _type = type;

        public override bool Passes(Site site, CancellationToken cancellationToken) => site.TargetType != null && SymbolUtil.IsOrDerivesFrom(site.TargetType, _type);
    }

    /// <summary>filter.define — define이 없으면 꺼지는 구간 안 · 밖.</summary>
    internal sealed class DefineFilter : SiteFilter
    {
        private readonly string _define;
        private readonly bool _inside;
        private readonly GuardCache _guards;

        public DefineFilter(string define, bool inside, GuardCache guards)
        {
            _define = define;
            _inside = inside;
            _guards = guards;
        }

        public override bool Passes(Site site, CancellationToken cancellationToken) =>
            _guards.Get(site.Node.SyntaxTree, _define, cancellationToken).Contains(site.Anchor) == _inside;
    }

    /// <summary>
    /// 파일 · define마다 가드 구간을 한 번만 계산한다 (컴파일 하나 동안).
    /// 토큰은 부르는 콜백의 것을 쓴다 — 컴파일 시작 때의 토큰을 붙잡으면 IDE가 시작 결과를 다른 요청에서 다시 쓸 때 취소 상태일 수 있다.
    /// </summary>
    internal sealed class GuardCache
    {
        private readonly ConcurrentDictionary<(SyntaxTree Tree, string Define), DefineGuard.Guards> _cache =
            new ConcurrentDictionary<(SyntaxTree Tree, string Define), DefineGuard.Guards>();

        public DefineGuard.Guards Get(SyntaxTree tree, string define, CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue((tree, define), out var guards))
                return guards;
            return _cache.GetOrAdd((tree, define), DefineGuard.GuardedSpans(tree, define, cancellationToken));
        }
    }
}
