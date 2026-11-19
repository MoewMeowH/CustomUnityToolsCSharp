using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace HBLint.Analyzers
{
    /// <summary>
    /// 파일에서 어떤 전처리 기호(define)가 없으면 꺼지는 구간을 찾는다.
    /// #if · #elif · #else · #endif와 중첩을 따라가며, 구간 조건이 "define = false이면 다른 기호가 무엇이든 거짓"일 때 가드된 구간으로 본다.
    /// 예 `#if UNITY_EDITOR`는 가드, `#if UNITY_EDITOR || DEVELOPMENT_BUILD`는 가드가 아니다 (개발 빌드에서 켜지므로).
    /// </summary>
    internal static class DefineGuard
    {
        /// <summary>다른 기호가 이보다 많으면 모든 조합 대신 지금 파싱 옵션의 값으로만 계산한다.</summary>
        private const int MaxEnumeratedSymbols = 8;

        public static Guards GuardedSpans(SyntaxTree tree, string define, CancellationToken cancellationToken)
        {
            var guards = new Guards();
            var defined = tree.Options is CSharpParseOptions options
                ? new HashSet<string>(options.PreprocessorSymbolNames)
                : new HashSet<string>();
            var stack = new Stack<Frame>();

            var directives = tree.GetRoot(cancellationToken)
                .DescendantTrivia(descendIntoTrivia: false)
                .Where(t => t.IsDirective)
                .Select(t => t.GetStructure())
                .OfType<DirectiveTriviaSyntax>();
            foreach (var directive in directives)
            {
                switch (directive)
                {
                    case IfDirectiveTriviaSyntax ifDirective:
                        var frame = new Frame(stack.Count > 0 && stack.Peek().CurrentGuarded);
                        frame.Begin(ifDirective.Condition, ifDirective.FullSpan.End, define, defined);
                        stack.Push(frame);
                        break;
                    case ElifDirectiveTriviaSyntax elifDirective when stack.Count > 0:
                        stack.Peek().End(elifDirective.FullSpan.Start);
                        stack.Peek().Begin(elifDirective.Condition, elifDirective.FullSpan.End, define, defined);
                        break;
                    case ElseDirectiveTriviaSyntax elseDirective when stack.Count > 0:
                        stack.Peek().End(elseDirective.FullSpan.Start);
                        stack.Peek().Begin(null, elseDirective.FullSpan.End, define, defined);
                        break;
                    case EndIfDirectiveTriviaSyntax endIfDirective when stack.Count > 0:
                        var ended = stack.Pop();
                        ended.End(endIfDirective.FullSpan.Start);
                        guards.Add(ended.Branches);
                        break;
                }
            }
            return guards;
        }

        /// <summary>한 파일에서 define이 없으면 꺼지는 구간들과, 그 구간이 속한 #if 묶음의 갈래들.</summary>
        internal sealed class Guards
        {
            private readonly List<TextSpan> _spans = new List<TextSpan>();
            private readonly List<Branch[]> _frames = new List<Branch[]>();

            public int Count => _spans.Count;

            public bool Contains(int position)
            {
                foreach (var span in _spans)
                {
                    if (span.Contains(position))
                        return true;
                }
                return false;
            }

            /// <summary>
            /// position을 감싸는 #if 묶음마다, define이 없어도 켜질 수 있는 다른 갈래에 name이 낱말로 있나
            /// (예 `#if UNITY_EDITOR X … #else X … #endif` — 플레이어 빌드에는 #else 쪽 X가 있다).
            /// </summary>
            public bool AlternativeMentions(int position, string name, SourceText text)
            {
                var word = new Regex(@"(?<![\w@])@?" + Regex.Escape(name) + @"(?!\w)");
                foreach (var branches in _frames)
                {
                    if (!branches.Any(b => b.Guarded && b.Span.Contains(position)))
                        continue;
                    if (branches.Any(b => !b.Guarded && word.IsMatch(text.ToString(b.Span))))
                        return true;
                }
                return false;
            }

            public void Add(List<Branch> branches)
            {
                foreach (var branch in branches)
                {
                    if (branch.Guarded)
                        _spans.Add(branch.Span);
                }
                if (branches.Any(b => b.Guarded) && branches.Any(b => !b.Guarded))
                    _frames.Add(branches.ToArray());
            }
        }

        internal struct Branch
        {
            public Branch(TextSpan span, bool guarded)
            {
                Span = span;
                Guarded = guarded;
            }

            public TextSpan Span { get; }

            public bool Guarded { get; }
        }

        /// <summary>#if 하나와 그 #elif · #else 갈래들.</summary>
        private sealed class Frame
        {
            private readonly bool _parentGuarded;
            private readonly List<ExpressionSyntax> _previous = new List<ExpressionSyntax>();
            private ExpressionSyntax? _current;
            private int _start;

            public Frame(bool parentGuarded) => _parentGuarded = parentGuarded;

            public bool CurrentGuarded { get; private set; }

            public List<Branch> Branches { get; } = new List<Branch>();

            /// <summary>새 갈래: 조건이 null이면 #else (앞 갈래가 모두 거짓).</summary>
            public void Begin(ExpressionSyntax? condition, int start, string define, HashSet<string> defined)
            {
                if (_current != null)
                    _previous.Add(_current);
                _current = condition;
                _start = start;
                CurrentGuarded = _parentGuarded || IsGuarded(_previous, condition, define, defined);
            }

            public void End(int end)
            {
                if (end > _start)
                    Branches.Add(new Branch(TextSpan.FromBounds(_start, end), CurrentGuarded));
            }
        }

        /// <summary>define = false일 때 다른 기호가 무엇이든 이 갈래가 거짓인가.</summary>
        private static bool IsGuarded(List<ExpressionSyntax> previous, ExpressionSyntax? current, string define, HashSet<string> defined)
        {
            var others = previous.Concat(current == null ? Enumerable.Empty<ExpressionSyntax>() : new[] { current })
                .SelectMany(e => e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                .Select(i => i.Identifier.ValueText)
                .Where(name => name != define)
                .Distinct()
                .ToList();

            if (others.Count > MaxEnumeratedSymbols)
                return !BranchTrue(previous, current, name => name != define && defined.Contains(name));

            for (var mask = 0; mask < 1 << others.Count; mask++)
            {
                var assignment = new HashSet<string>();
                for (var i = 0; i < others.Count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                        assignment.Add(others[i]);
                }
                if (BranchTrue(previous, current, name => name != define && assignment.Contains(name)))
                    return false;
            }
            return true;
        }

        private static bool BranchTrue(List<ExpressionSyntax> previous, ExpressionSyntax? current, System.Func<string, bool> defined) =>
            previous.All(p => !Evaluate(p, defined)) && (current == null || Evaluate(current, defined));

        private static bool Evaluate(ExpressionSyntax expression, System.Func<string, bool> defined)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    return defined(identifier.Identifier.ValueText);
                case LiteralExpressionSyntax literal:
                    return literal.IsKind(SyntaxKind.TrueLiteralExpression);
                case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression):
                    return !Evaluate(unary.Operand, defined);
                case ParenthesizedExpressionSyntax parenthesized:
                    return Evaluate(parenthesized.Expression, defined);
                case BinaryExpressionSyntax binary:
                    var left = Evaluate(binary.Left, defined);
                    var right = Evaluate(binary.Right, defined);
                    switch (binary.Kind())
                    {
                        case SyntaxKind.LogicalAndExpression: return left && right;
                        case SyntaxKind.LogicalOrExpression: return left || right;
                        case SyntaxKind.EqualsExpression: return left == right;
                        case SyntaxKind.NotEqualsExpression: return left != right;
                    }
                    return false;
                default:
                    return false;
            }
        }
    }
}
