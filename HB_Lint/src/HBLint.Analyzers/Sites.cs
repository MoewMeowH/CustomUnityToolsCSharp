using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HBLint.Core.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace HBLint.Analyzers
{
    /// <summary>
    /// 사이트 — 규칙이 걸 수 있는 코드 위치 하나. Node는 속한 메서드 · 타입 · 맥락을 찾을 때,
    /// Anchor는 디파인 가드 판정에, TargetType은 "대상 타입" 필터에 쓴다 (사이트가 다루는 식의 정적 타입, 없으면 null).
    /// Instance는 멤버 사용의 인스턴스 식 — "같은 대상"으로 묶을 때만 키로 바꾼다. 람다 사이트는 람다 연산(대상 아님 — 키는 null).
    /// Declared는 선언 사이트의 선언 기호 (이름 필터 · 코드 자리 필터가 쓴다, 다른 사이트는 null).
    /// </summary>
    internal sealed class Site
    {
        public Site(SyntaxNode node, SemanticModel model, Location location, int anchor, ITypeSymbol? targetType, IOperation? instance = null, ISymbol? declared = null)
        {
            Node = node;
            Model = model;
            Location = location;
            Anchor = anchor;
            TargetType = targetType;
            Instance = instance;
            Declared = declared;
        }

        public ISymbol? Declared { get; }

        public IOperation? Instance { get; }

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

        /// <summary>비싼 판정 — 범위(메서드 · 따라가기 경로)와 필터를 통과한 사이트에만, 보고 직전에 부른다.</summary>
        public virtual bool Confirm(Site site, CancellationToken cancellationToken) => true;
    }

    /// <summary>sites.member — 멤버(메서드 오버로드 · 오버라이드 · 속성 · 필드)를 호출 · 읽기 · 쓰기 · 비교로 쓴 곳.</summary>
    internal sealed class MemberProducer : SiteProducer
    {
        private readonly HashSet<ISymbol> _members;
        private readonly bool _call;
        private readonly bool _read;
        private readonly bool _write;
        private readonly bool _compare;

        public MemberProducer(HashSet<ISymbol> members, string[] uses)
            : base(Kinds(uses), new SyntaxKind[0])
        {
            _members = members;
            _call = uses.Contains("call");
            _read = uses.Contains("read");
            _write = uses.Contains("write");
            _compare = uses.Contains("compare");
        }

        private static OperationKind[] Kinds(string[] uses) =>
            (uses.Contains("call") ? new[] { OperationKind.Invocation } : new OperationKind[0])
            .Concat(uses.Contains("read") || uses.Contains("write") || uses.Contains("compare") ? new[] { OperationKind.PropertyReference, OperationKind.FieldReference } : new OperationKind[0])
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
                    // 비교 = == · != 의 한쪽 피연산자. 암시적 변환(count == maybe의 nullable 등)은 지나가고, (object)x == y처럼 명시적 변환이 끼면 아님
                    var parent = operation.Parent;
                    while (parent is IConversionOperation implicitConversion && implicitConversion.IsImplicit)
                        parent = parent.Parent;
                    var compared = read && parent is IBinaryOperation binary
                        && (binary.OperatorKind == BinaryOperatorKind.Equals || binary.OperatorKind == BinaryOperatorKind.NotEquals);
                    if (!(read && _read) && !(written && _write) && !(compared && _compare))
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
            return new Site(operation.Syntax, operation.SemanticModel, location, location.SourceSpan.Start, instance?.Type ?? target.ContainingType, instance);
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

        /// <summary>"타입 메타데이터 이름.멤버 이름" → 그 이름의 메서드(오버로드 · 제네릭 전부) · 속성 · 필드. "타입.*"이면 그 타입에 선언된 것 전부.</summary>
        public static List<ISymbol> Resolve(Compilation compilation, string member)
        {
            var separator = member.LastIndexOf('.');
            if (separator <= 0 || separator == member.Length - 1)
                return new List<ISymbol>();
            var type = compilation.GetTypeByMetadataName(member.Substring(0, separator));
            if (type == null)
                return new List<ISymbol>();
            var name = member.Substring(separator + 1);
            return (name == NodeCatalog.AllMembers ? type.GetMembers() : type.GetMembers(name))
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
    /// sites.new — new X() · new() · new T() · new X[n] · 배열 초기화 { … } · 무명 new { } · new Action(M). TargetType은 만드는 형.
    /// params 인자 배열 · 메서드 그룹 · 람다의 암시적 델리게이트 변환은 소스에 new가 없어서 뺀다.
    /// </summary>
    internal sealed class NewProducer : SiteProducer
    {
        public NewProducer()
            : base(new[] { OperationKind.ObjectCreation, OperationKind.TypeParameterObjectCreation, OperationKind.ArrayCreation, OperationKind.AnonymousObjectCreation, OperationKind.DelegateCreation }, new SyntaxKind[0])
        {
        }

        public override Site? OnOperation(IOperation operation)
        {
            if (operation.SemanticModel == null || operation.Type == null)
                return null;
            if (operation.IsImplicit && operation.Parent is IArgumentOperation argument && argument.ArgumentKind == ArgumentKind.ParamArray)
                return null;
            if (operation is IDelegateCreationOperation && !(operation.Syntax is ObjectCreationExpressionSyntax))
                return null;
            var syntax = operation.Syntax;
            var span = syntax switch
            {
                ObjectCreationExpressionSyntax o => TextSpan.FromBounds(o.NewKeyword.SpanStart, o.Type.Span.End),
                ArrayCreationExpressionSyntax a => TextSpan.FromBounds(a.NewKeyword.SpanStart, a.Type.Span.End),
                ImplicitArrayCreationExpressionSyntax ia => TextSpan.FromBounds(ia.NewKeyword.SpanStart, ia.CloseBracketToken.Span.End),
                ImplicitObjectCreationExpressionSyntax implicitNew => implicitNew.NewKeyword.Span,
                AnonymousObjectCreationExpressionSyntax an => an.NewKeyword.Span,
                InitializerExpressionSyntax init => init.OpenBraceToken.Span,
                _ => syntax.Span,
            };
            return new Site(syntax, operation.SemanticModel, Location.Create(syntax.SyntaxTree, span), span.Start, operation.Type);
        }
    }

    /// <summary>
    /// sites.boxing — 박싱 변환 (암시적 · 명시적 캐스트, C# 변환의 IsBoxing). TargetType은 박싱되는 값의 형.
    /// 변환으로 보이지 않는 박싱(보간 · 오버라이드 없는 struct의 ToString 등 호출 · foreach 변수)은 잡지 않는다.
    /// string + · += 의 피연산자는 연산 트리에 박싱 변환이 있어도 컴파일러가 ToString 호출로 바꿔 박싱하지 않으므로 뺀다.
    /// </summary>
    internal sealed class BoxingProducer : SiteProducer
    {
        public BoxingProducer()
            : base(new[] { OperationKind.Conversion }, new SyntaxKind[0])
        {
        }

        public override Site? OnOperation(IOperation operation)
        {
            var conversion = (IConversionOperation)operation;
            if (operation.SemanticModel == null || !conversion.GetConversion().IsBoxing || StringConcatProducer.IsConcat(operation.Parent))
                return null;
            var syntax = conversion.Operand.Syntax;
            return new Site(syntax, operation.SemanticModel, syntax.GetLocation(), syntax.SpanStart, conversion.Operand.Type);
        }
    }

    /// <summary>
    /// sites.stringConcat — string + · += · 보간. 바깥 식 하나만 (a + b + c · 연결 안 보간은 한 번), 상수는 뺀다.
    /// string.Concat · Format 직접 호출은 잡지 않는다.
    /// </summary>
    internal sealed class StringConcatProducer : SiteProducer
    {
        public StringConcatProducer()
            : base(new[] { OperationKind.Binary, OperationKind.CompoundAssignment, OperationKind.InterpolatedString }, new SyntaxKind[0])
        {
        }

        public override Site? OnOperation(IOperation operation)
        {
            if (operation.SemanticModel == null || operation.ConstantValue.HasValue)
                return null;
            var concat = operation is IInterpolatedStringOperation
                ? !IsConcat(operation.Parent)
                : IsConcat(operation) && !(operation is IBinaryOperation && IsConcat(operation.Parent));
            if (!concat)
                return null;
            var syntax = operation.Syntax;
            return new Site(syntax, operation.SemanticModel, syntax.GetLocation(), syntax.SpanStart, null);
        }

        /// <summary>string + 이항 · += 복합 대입인가.</summary>
        public static bool IsConcat(IOperation? operation) =>
            operation is IBinaryOperation binary ? binary.OperatorKind == BinaryOperatorKind.Add && binary.Type?.SpecialType == SpecialType.System_String
            : operation is ICompoundAssignmentOperation compound && compound.OperatorKind == BinaryOperatorKind.Add && compound.Type?.SpecialType == SpecialType.System_String;
    }

    /// <summary>
    /// sites.lambda — 람다 · 무명 메서드를 만드는 자리. capturingOnly면 지역 · 매개변수 · this를 캡처하는 것만
    /// (데이터 흐름의 CapturedInside에서 람다 안에 선언한 것 — 안쪽 람다 몫 — 을 뺀다). 쿼리 식 안 암시적 람다는 뺀다.
    /// Node는 람다의 부모다 (속한 메서드 · 맥락을 람다 바깥에서 찾게). 데이터 흐름은 비싸서 Confirm(범위 통과 뒤)에서 한다.
    /// </summary>
    internal sealed class LambdaProducer : SiteProducer
    {
        private readonly bool _capturingOnly;

        public LambdaProducer(bool capturingOnly)
            : base(new[] { OperationKind.AnonymousFunction }, new SyntaxKind[0])
        {
            _capturingOnly = capturingOnly;
        }

        public override Site? OnOperation(IOperation operation)
        {
            if (operation.IsImplicit || operation.SemanticModel == null || !(operation.Syntax is AnonymousFunctionExpressionSyntax lambda) || lambda.Parent == null)
                return null;
            var span = lambda is LambdaExpressionSyntax arrow
                ? TextSpan.FromBounds(lambda.SpanStart, arrow.ArrowToken.Span.End)
                : ((AnonymousMethodExpressionSyntax)lambda).DelegateKeyword.Span;
            return new Site(lambda.Parent, operation.SemanticModel, Location.Create(lambda.SyntaxTree, span), span.Start, null, operation);
        }

        public override bool Confirm(Site site, CancellationToken cancellationToken)
        {
            if (!_capturingOnly)
                return true;
            var flow = site.Model.AnalyzeDataFlow(site.Instance!.Syntax);
            return flow != null && flow.Succeeded && flow.CapturedInside.Except(flow.VariablesDeclared, SymbolEqualityComparer.Default).Any();
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

    /// <summary>
    /// sites.declaration — 타입 · 메서드 · 속성 · 필드 · 이벤트 선언. 위치는 선언 이름, 필드 int a, b;는 변수마다.
    /// method는 일반 메서드만 (생성자 · 연산자 · 접근자 · 로컬 함수 · 명시적 인터페이스 구현은 이름을 정할 수 없어 뺀다). 속성 · 이벤트도 명시적 구현 · 인덱서는 뺀다.
    /// partial 타입 · 메서드는 조각마다 사이트 하나.
    /// </summary>
    internal sealed class DeclarationProducer : SiteProducer
    {
        private readonly HashSet<string> _kinds;
        private readonly bool? _static;
        private readonly bool? _extension;
        private readonly HashSet<string>? _accessibility;

        public DeclarationProducer(string[] kinds, bool? isStatic, bool? extension, string[] accessibility)
            : base(new OperationKind[0], SyntaxKindsFor(kinds))
        {
            _kinds = new HashSet<string>(kinds, StringComparer.Ordinal);
            _static = isStatic;
            _extension = extension;
            _accessibility = accessibility.Length == 0 ? null : new HashSet<string>(accessibility, StringComparer.Ordinal);
        }

        private static SyntaxKind[] SyntaxKindsFor(string[] kinds)
        {
            var result = new List<SyntaxKind>();
            if (kinds.Contains("class") || kinds.Contains("struct"))
                result.AddRange(new[] { SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration });
            if (kinds.Contains("class"))
                result.Add(SyntaxKind.ClassDeclaration);
            if (kinds.Contains("struct"))
                result.Add(SyntaxKind.StructDeclaration);
            if (kinds.Contains("interface"))
                result.Add(SyntaxKind.InterfaceDeclaration);
            if (kinds.Contains("enum"))
                result.Add(SyntaxKind.EnumDeclaration);
            if (kinds.Contains("delegate"))
                result.Add(SyntaxKind.DelegateDeclaration);
            if (kinds.Contains("method"))
                result.Add(SyntaxKind.MethodDeclaration);
            if (kinds.Contains("property"))
                result.Add(SyntaxKind.PropertyDeclaration);
            if (kinds.Contains("event"))
                result.Add(SyntaxKind.EventDeclaration);
            // 필드 · 필드 꼴 이벤트는 변수(int a, b;의 a · b)마다
            if (kinds.Contains("field") || kinds.Contains("event"))
                result.Add(SyntaxKind.VariableDeclarator);
            return result.ToArray();
        }

        public override Site? OnSyntax(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            SyntaxToken identifier;
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    identifier = type.Identifier;
                    break;
                case DelegateDeclarationSyntax @delegate:
                    identifier = @delegate.Identifier;
                    break;
                case MethodDeclarationSyntax method:
                    identifier = method.Identifier;
                    break;
                case PropertyDeclarationSyntax property:
                    identifier = property.Identifier;
                    break;
                case EventDeclarationSyntax eventDeclaration:
                    identifier = eventDeclaration.Identifier;
                    break;
                case VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax:
                    identifier = variable.Identifier;
                    break;
                default:
                    return null;
            }

            var symbol = model.GetDeclaredSymbol(node, cancellationToken);
            var kind = symbol == null ? null : KindOf(symbol);
            if (symbol == null || kind == null || !_kinds.Contains(kind) || !Passes(symbol))
                return null;
            return new Site(node, model, identifier.GetLocation(), identifier.SpanStart, null, declared: symbol);
        }

        /// <summary>선언 종류 이름 (고를 수 없는 선언이면 null).</summary>
        private static string? KindOf(ISymbol symbol)
        {
            switch (symbol)
            {
                case INamedTypeSymbol type:
                    switch (type.TypeKind)
                    {
                        case TypeKind.Class: return "class";
                        case TypeKind.Struct: return "struct";
                        case TypeKind.Interface: return "interface";
                        case TypeKind.Enum: return "enum";
                        case TypeKind.Delegate: return "delegate";
                        default: return null;
                    }
                case IMethodSymbol method:
                    return method.MethodKind == MethodKind.Ordinary ? "method" : null;
                case IPropertySymbol property:
                    return property.IsIndexer || property.ExplicitInterfaceImplementations.Length > 0 ? null : "property";
                case IEventSymbol eventSymbol:
                    return eventSymbol.ExplicitInterfaceImplementations.Length > 0 ? null : "event";
                case IFieldSymbol _:
                    return "field";
                default:
                    return null;
            }
        }

        private bool Passes(ISymbol symbol)
        {
            if (_static.HasValue && symbol.IsStatic != _static.Value)
                return false;
            if (_extension.HasValue && (symbol is IMethodSymbol method && method.IsExtensionMethod) != _extension.Value)
                return false;
            return _accessibility == null || Accessibilities(symbol.DeclaredAccessibility).Any(_accessibility.Contains);
        }

        /// <summary>protected internal = protected · internal, private protected = private · protected.</summary>
        private static string[] Accessibilities(Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public: return new[] { "public" };
                case Accessibility.Internal: return new[] { "internal" };
                case Accessibility.Protected: return new[] { "protected" };
                case Accessibility.Private: return new[] { "private" };
                case Accessibility.ProtectedOrInternal: return new[] { "protected", "internal" };
                case Accessibility.ProtectedAndInternal: return new[] { "private", "protected" };
                default: return new string[0];
            }
        }
    }

    /// <summary>사이트 필터 (filter.*). 흐름 중간에서 사이트를 거른다.</summary>
    internal abstract class SiteFilter
    {
        public abstract bool Passes(Site site, CancellationToken cancellationToken);
    }

    /// <summary>
    /// filter.targetType — 사이트가 다루는 식의 정적 타입이 이 타입 계열 · 참조/값 형식인 것만 (둘 다 주면 둘 다).
    /// 대상 타입은 null 연산자 · 멤버 · 객체 생성 · 박싱 사이트에만 있다. 타입 매개변수의 참조/값은 제약으로 (제약이 없으면 어느 쪽도 아님).
    /// </summary>
    internal sealed class TargetTypeFilter : SiteFilter
    {
        private readonly INamedTypeSymbol? _type;
        private readonly string? _category;

        public TargetTypeFilter(INamedTypeSymbol? type, string? category)
        {
            _type = type;
            _category = category;
        }

        public override bool Passes(Site site, CancellationToken cancellationToken)
        {
            var target = site.TargetType;
            if (target == null || (_type != null && !SymbolUtil.IsOrDerivesFrom(target, _type)))
                return false;
            return _category == null || (_category == "reference" ? target.IsReferenceType : target.IsValueType);
        }
    }

    /// <summary>
    /// filter.context — 루프 안 · 람다 안 (또는 밖).
    /// 루프 안 = for · foreach · while · do의 본문 · 조건 · for 증감식 (for 초기화 · foreach 컬렉션 식은 한 번만 돌아서 밖). 람다 · 로컬 함수 · 멤버 경계에서 멈춘다.
    /// 람다 안 = 람다 · 무명 메서드 몸통 안 (멤버 경계까지).
    /// </summary>
    internal sealed class ContextFilter : SiteFilter
    {
        private readonly bool _loop;
        private readonly bool _inside;

        public ContextFilter(bool loop, bool inside)
        {
            _loop = loop;
            _inside = inside;
        }

        public override bool Passes(Site site, CancellationToken cancellationToken) => (_loop ? InLoop(site.Node) : InLambda(site.Node)) == _inside;

        private static bool InLoop(SyntaxNode syntax)
        {
            var child = syntax;
            for (var node = syntax.Parent; node != null; child = node, node = node.Parent)
            {
                switch (node)
                {
                    case AnonymousFunctionExpressionSyntax _:
                    case LocalFunctionStatementSyntax _:
                    case AccessorDeclarationSyntax _:
                    case MemberDeclarationSyntax _:
                        return false;
                    case ForStatementSyntax forLoop when child == forLoop.Statement || child == forLoop.Condition || forLoop.Incrementors.Any(i => i == child):
                    case CommonForEachStatementSyntax forEach when child == forEach.Statement:
                    case WhileStatementSyntax whileLoop when child == whileLoop.Statement || child == whileLoop.Condition:
                    case DoStatementSyntax doLoop when child == doLoop.Statement || child == doLoop.Condition:
                        return true;
                }
            }
            return false;
        }

        private static bool InLambda(SyntaxNode syntax)
        {
            for (var node = syntax; node != null; node = node.Parent)
            {
                if (node is AnonymousFunctionExpressionSyntax)
                    return true;
                if (node is AccessorDeclarationSyntax || node is MemberDeclarationSyntax)
                    return false;
            }
            return false;
        }
    }

    /// <summary>filter.name — 선언 이름이 조건에 맞는 것(matching) · 안 맞는 것(notMatching)만. 이름이 없는 사이트 · 정규식 시간 초과는 늘 뺀다.</summary>
    internal sealed class NameFilter : SiteFilter
    {
        private readonly NamePattern _pattern;
        private readonly bool _keepMatching;

        public NameFilter(NamePattern pattern, bool keepMatching)
        {
            _pattern = pattern;
            _keepMatching = keepMatching;
        }

        public override bool Passes(Site site, CancellationToken cancellationToken)
        {
            if (site.Declared == null)
                return false;
            var matches = _pattern.Matches(site.Declared.Name);
            return matches.HasValue && matches.Value == _keepMatching;
        }
    }

    /// <summary>
    /// filter.location — 사이트가 있는 파일(경로 끝부분 패턴) · 감싸는 타입(바깥 어느 단계든) 안 · 밖. 둘 다 주면 둘 다 맞아야 안.
    /// 감싸는 타입: 선언 사이트는 선언을 감싸는 바깥 타입(자기 자신은 아님), 그 밖 사이트는 코드를 감싸는 타입.
    /// 타입 이름에 . · +가 없으면 이름만, 있으면 메타데이터 이름(Game.Outer+Inner)과 맞춘다.
    /// </summary>
    internal sealed class LocationFilter : SiteFilter
    {
        private readonly PathPattern[] _paths;
        private readonly string[] _types;
        private readonly bool _inside;

        public LocationFilter(PathPattern[] paths, string[] types, bool inside)
        {
            _paths = paths;
            _types = types;
            _inside = inside;
        }

        public override bool Passes(Site site, CancellationToken cancellationToken) => Inside(site, cancellationToken) == _inside;

        private bool Inside(Site site, CancellationToken cancellationToken)
        {
            if (_paths.Length > 0)
            {
                var path = site.Location.SourceTree?.FilePath;
                if (path == null || path.Length == 0 || !_paths.Any(p => p.Matches(path)))
                    return false;
            }
            if (_types.Length == 0)
                return true;
            var type = site.Declared != null ? site.Declared.ContainingType : SymbolUtil.ContainingType(site.Node, site.Model, cancellationToken);
            for (; type != null; type = type.ContainingType)
            {
                if (_types.Any(name => TypeMatches(type, name)))
                    return true;
            }
            return false;
        }

        private static bool TypeMatches(INamedTypeSymbol type, string name) =>
            name.IndexOf('.') < 0 && name.IndexOf('+') < 0
                ? type.Name == name
                : SymbolUtil.MetadataName(type) == name;
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
