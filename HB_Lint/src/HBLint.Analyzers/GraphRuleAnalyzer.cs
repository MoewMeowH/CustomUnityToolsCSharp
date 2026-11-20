using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using HBLint.Core.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace HBLint.Analyzers
{
    /// <summary>
    /// [assembly: HBLint.Rule(ir)] 규칙(그래프 하나 = 규칙 하나)마다 그래프를 실행 계획으로 바꿔 필요한 동작만 등록한다.
    /// 규칙 하나가 잘못돼도(읽기 실패 · 대상 못 찾음) 그 규칙만 빼고 CLL9001 · CLL9002로 알린다.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class GraphRuleAnalyzer : DiagnosticAnalyzer
    {
        internal const string AttributeFullName = "HBLint.RuleAttribute";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => RuleDescriptors.All;

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext start)
        {
            var compilation = start.Compilation;
            var config = new List<Diagnostic>();
            var guards = new GuardCache();
            var referenced = new HashSet<string>(compilation.ReferencedAssemblyNames.Select(n => n.Name));
            var activeIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var source in LintRuleReader.Read(compilation, AttributeFullName))
            {
                var parsed = RuleIr.Parse(source.GetConstructorString(0) ?? "");
                if (parsed.Rule == null)
                {
                    var errors = parsed.Errors.Where(e => !e.IsWarning).ToList();
                    var text = errors.Count == 0 ? "" : errors[0].Message + (errors.Count > 1 ? $" (외 {errors.Count - 1}개)" : "");
                    config.Add(Diagnostic.Create(RuleDescriptors.RuleInvalid, Location.None, source.Owner.Name, parsed.Id ?? "(id 없음)", text));
                    continue;
                }

                var rule = parsed.Rule;
                if (!rule.Requires.All(referenced.Contains))
                    continue;
                if (rule.IgnoreAssemblies.Any(pattern => WildcardMatch(pattern, compilation.AssemblyName ?? "")))
                    continue;
                // 같은 id를 두 규칙이 쓰면 id 하나로 둘을 따로 끌 수 없다 — 먼저 읽은 것만 쓴다
                if (!activeIds.Add(rule.Id))
                {
                    config.Add(Diagnostic.Create(RuleDescriptors.RuleInvalid, Location.None, source.Owner.Name, rule.Id, "다른 규칙이 이미 같은 id를 씁니다"));
                    continue;
                }
                RulePlan.Build(rule, source.Owner, compilation, guards, config)?.Register(start);
            }

            if (config.Count > 0)
            {
                start.RegisterCompilationEndAction(end =>
                {
                    foreach (var diagnostic in config)
                        end.ReportDiagnostic(diagnostic);
                });
            }
        }

        /// <summary>* 와일드카드 패턴 (대소문자 가림).</summary>
        internal static bool WildcardMatch(string pattern, string value) =>
            Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");
    }

    /// <summary>사이트 노드가 받는 범위: Scope(컴파일 전체) · Types(그 타입 계열 안) · Methods(그 메서드 안, 따라가기 포함).</summary>
    internal sealed class SiteContext
    {
        public List<INamedTypeSymbol> Types { get; } = new List<INamedTypeSymbol>();

        /// <summary>null이면 메서드로 좁히지 않음.</summary>
        public HashSet<string>? MethodNames { get; set; }

        public int Depth { get; set; }

        public bool IsScope => Types.Count == 0 && MethodNames == null;
    }

    /// <summary>사이트 노드 하나와 그 범위, 그래프에서의 자리 (report까지 가는 길 하나라도 필터를 통과하면 보고).</summary>
    internal sealed class ProducerPlan
    {
        private readonly SiteGraph _graph;
        private readonly int _index;

        public ProducerPlan(SiteProducer producer, SiteContext context, SiteGraph graph, int index)
        {
            Producer = producer;
            Context = context;
            _graph = graph;
            _index = index;
        }

        public SiteProducer Producer { get; }

        public SiteContext Context { get; }

        public bool Passes(Site site, System.Threading.CancellationToken cancellationToken) => _graph.Passes(_index, site, cancellationToken);
    }

    /// <summary>
    /// 사이트가 흐르는 노드들(사이트 노드 · 필터 · 합치기 · report)을 번호로 편 것.
    /// 경로를 펼쳐 두지 않고 사이트마다 따라가 판정한다 — 합치기가 겹겹이면 경로 수가 지수로 늘어서.
    /// 한 노드를 여러 노드가 받는 그래프에서만 노드마다 결과를 기억한다.
    /// </summary>
    internal sealed class SiteGraph
    {
        private readonly SiteFilter?[] _filters;
        private readonly bool[] _isReport;
        private readonly int[][] _consumers;
        private readonly bool _fanOut;

        public SiteGraph(SiteFilter?[] filters, bool[] isReport, int[][] consumers)
        {
            _filters = filters;
            _isReport = isReport;
            _consumers = consumers;
            _fanOut = consumers.Any(c => c.Length > 1);
        }

        public bool Passes(int from, Site site, System.Threading.CancellationToken cancellationToken) =>
            AnyConsumer(from, site, _fanOut ? new sbyte[_filters.Length] : null, cancellationToken);

        private bool AnyConsumer(int node, Site site, sbyte[]? memo, System.Threading.CancellationToken cancellationToken)
        {
            foreach (var next in _consumers[node])
            {
                if (Reaches(next, site, memo, cancellationToken))
                    return true;
            }
            return false;
        }

        /// <summary>memo: 0 모름 · 1 닿음 · 2 안 닿음.</summary>
        private bool Reaches(int node, Site site, sbyte[]? memo, System.Threading.CancellationToken cancellationToken)
        {
            if (_isReport[node])
                return true;
            if (memo != null && memo[node] != 0)
                return memo[node] == 1;
            var reaches = (_filters[node]?.Passes(site, cancellationToken) ?? true) && AnyConsumer(node, site, memo, cancellationToken);
            if (memo != null)
                memo[node] = (sbyte)(reaches ? 1 : 2);
            return reaches;
        }
    }

    /// <summary>규칙 하나의 실행 계획.</summary>
    internal sealed class RulePlan
    {
        private readonly DiagnosticDescriptor _descriptor;
        private readonly DiagnosticSeverity _severity;
        private readonly string _message;
        private readonly bool _appendPath;
        private readonly List<ProducerPlan> _producers;

        private RulePlan(RuleIr rule, List<ProducerPlan> producers)
        {
            _descriptor = RuleDescriptors.Rules[rule.Id];
            _severity = rule.Severity == "error" ? DiagnosticSeverity.Error : rule.Severity == "info" ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning;
            _message = rule.Report.GetString("message")!;
            _appendPath = rule.Report.GetBool("appendPath") ?? true;
            _producers = producers;
        }

        /// <summary>대상 타입을 못 찾으면 규칙째 빼고 null (멤버는 못 찾은 것만 뺀다).</summary>
        public static RulePlan? Build(RuleIr rule, IAssemblySymbol owner, Compilation compilation, GuardCache guards, List<Diagnostic> config)
        {
            var resolved = new Dictionary<string, INamedTypeSymbol?>();
            INamedTypeSymbol? Type(string name)
            {
                if (!resolved.TryGetValue(name, out var type))
                {
                    resolved[name] = type = compilation.GetTypeByMetadataName(name);
                    if (type == null)
                        NotFound(name);
                }
                return type;
            }
            void NotFound(string target) =>
                config.Add(Diagnostic.Create(RuleDescriptors.TargetNotFound, Location.None, rule.Id, owner.Name, target));

            // report에 닿는 노드만 쓴다 (닿지 않는 노드는 검증 경고일 뿐 — 그 노드의 타입을 못 찾아도 규칙을 빼지 않는다)
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<IrNode>(new[] { rule.Report });
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!reached.Add(node.Name))
                    continue;
                foreach (var source in node.AllInputs)
                {
                    var upstream = rule.Find(source);
                    if (upstream != null)
                        pending.Push(upstream);
                }
            }

            // 사이트가 흐르는 노드(Sites를 내보내는 노드 · report)를 번호로 편다
            var flow = rule.Nodes.Where(n => reached.Contains(n.Name) && (n.Spec.Output == Socket.Sites || n.Kind == NodeCatalog.ReportKind)).ToList();
            var index = flow.Select((n, i) => (n.Name, i)).ToDictionary(x => x.Name, x => x.i);
            var filters = new SiteFilter?[flow.Count];
            for (var i = 0; i < flow.Count; i++)
            {
                switch (flow[i].Kind)
                {
                    case "filter.targetType":
                        var type = Type(flow[i].GetString("type")!);
                        if (type == null)
                            return null;
                        filters[i] = new TargetTypeFilter(type);
                        break;
                    case "filter.define":
                        filters[i] = new DefineFilter(flow[i].GetString("define")!, flow[i].GetString("where") == "inside", guards);
                        break;
                }
            }
            var consumers = flow
                .Select(n => flow.Where(c => c.AllInputs.Contains(n.Name)).Select(c => index[c.Name]).ToArray())
                .ToArray();
            var graph = new SiteGraph(filters, flow.Select(n => n.Kind == NodeCatalog.ReportKind).ToArray(), consumers);

            var producers = new List<ProducerPlan>();
            foreach (var node in flow)
            {
                // 사이트를 만드는 노드만 (필터 · 합치기는 그래프에서 지나간다)
                if (node.Kind == NodeCatalog.ReportKind || node.Kind == "sites.union" || node.Kind.StartsWith("filter.", StringComparison.Ordinal))
                    continue;

                var context = new SiteContext();
                for (var current = node.Inputs["from"][0]; current != NodeCatalog.ScopeName;)
                {
                    var upstream = rule.Find(current)!;
                    switch (upstream.Kind)
                    {
                        case "methods.follow":
                            context.Depth += upstream.GetInt("depth") ?? 0;
                            break;
                        case "methods.named":
                            context.MethodNames = new HashSet<string>(upstream.GetStrings("names"), StringComparer.Ordinal);
                            break;
                        case "types.derived":
                            var type = Type(upstream.GetString("type")!);
                            if (type == null)
                                return null;
                            context.Types.Add(type);
                            break;
                    }
                    current = upstream.Inputs["from"][0];
                }

                var producer = CreateProducer(node, compilation, guards, NotFound);
                if (producer != null)
                    producers.Add(new ProducerPlan(producer, context, graph, index[node.Name]));
            }
            return producers.Count == 0 ? null : new RulePlan(rule, producers);
        }

        private static SiteProducer? CreateProducer(IrNode node, Compilation compilation, GuardCache guards, Action<string> notFound)
        {
            switch (node.Kind)
            {
                case "sites.member":
                    var members = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    foreach (var name in node.GetStrings("members"))
                    {
                        var found = MemberProducer.Resolve(compilation, name);
                        if (found.Count == 0)
                            notFound(name);
                        members.UnionWith(found);
                    }
                    var uses = node.GetStrings("uses");
                    return members.Count == 0 ? null : new MemberProducer(members, uses.Length == 0 ? NodeCatalog.MemberUses : uses);
                case "sites.namespace":
                    return new NamespaceProducer(compilation, node.GetStrings("namespaces"), node.GetBool("usings") ?? true);
                case "sites.declaredInDefine":
                    return new DeclaredInDefineProducer(compilation, node.GetString("define")!, guards);
                case "sites.nullOperator":
                    var operators = node.GetStrings("operators");
                    return new NullOperatorProducer(operators.Length == 0 ? NodeCatalog.NullOperators : operators);
                default:
                    return null;
            }
        }

        public void Register(CompilationStartAnalysisContext start)
        {
            // 컴파일 전체 범위 사이트 노드는 한 콜백에 모아 같은 위치를 한 번만 알린다 (합치기로 겹칠 수 있어서)
            var scoped = _producers.Where(p => p.Context.IsScope).ToList();
            var operationKinds = scoped.SelectMany(p => p.Producer.OperationKinds).Distinct().ToArray();
            if (operationKinds.Length > 0)
            {
                start.RegisterOperationAction(context =>
                {
                    HashSet<TextSpan>? seen = null;
                    foreach (var plan in scoped)
                    {
                        if (Array.IndexOf(plan.Producer.OperationKinds, context.Operation.Kind) < 0)
                            continue;
                        var site = plan.Producer.OnOperation(context.Operation);
                        if (site != null)
                            Report(plan, site, null, ref seen, context.ReportDiagnostic, context.CancellationToken);
                    }
                }, operationKinds);
            }
            var syntaxKinds = scoped.SelectMany(p => p.Producer.SyntaxKinds).Distinct().ToArray();
            if (syntaxKinds.Length > 0)
            {
                start.RegisterSyntaxNodeAction(context =>
                {
                    HashSet<TextSpan>? seen = null;
                    var kind = context.Node.Kind();
                    foreach (var plan in scoped)
                    {
                        if (Array.IndexOf(plan.Producer.SyntaxKinds, kind) < 0)
                            continue;
                        var site = plan.Producer.OnSyntax(context.Node, context.SemanticModel, context.CancellationToken);
                        if (site != null)
                            Report(plan, site, null, ref seen, context.ReportDiagnostic, context.CancellationToken);
                    }
                }, syntaxKinds);
            }

            foreach (var plan in _producers.Where(p => !p.Context.IsScope))
                start.RegisterSymbolStartAction(symbolStart => OnTypeStart(symbolStart, plan), SymbolKind.NamedType);
        }

        /// <summary>타입 계열 안 사이트. 메서드로 좁히면 타입 끝에서 따라가기 경로를 계산해 알린다.</summary>
        private void OnTypeStart(SymbolStartAnalysisContext context, ProducerPlan plan)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (!plan.Context.Types.All(target => SymbolUtil.IsOrDerivesFrom(type, target)))
                return;
            var producer = plan.Producer;

            if (plan.Context.MethodNames == null)
            {
                void InType(Site? site, Action<Diagnostic> report, System.Threading.CancellationToken cancellationToken)
                {
                    HashSet<TextSpan>? seen = null;
                    if (site != null && SymbolEqualityComparer.Default.Equals(SymbolUtil.ContainingType(site.Node, site.Model, cancellationToken), type))
                        Report(plan, site, null, ref seen, report, cancellationToken);
                }
                if (producer.OperationKinds.Length > 0)
                    context.RegisterOperationAction(c => InType(producer.OnOperation(c.Operation), c.ReportDiagnostic, c.CancellationToken), producer.OperationKinds);
                if (producer.SyntaxKinds.Length > 0)
                    context.RegisterSyntaxNodeAction(c => InType(producer.OnSyntax(c.Node, c.SemanticModel, c.CancellationToken), c.ReportDiagnostic, c.CancellationToken), producer.SyntaxKinds);
                return;
            }

            var hits = new ConcurrentQueue<(IMethodSymbol Container, Site Site)>();
            var calls = new ConcurrentQueue<(IMethodSymbol Caller, IMethodSymbol Callee)>();
            void Collect(Site? site, System.Threading.CancellationToken cancellationToken)
            {
                if (site == null)
                    return;
                var container = SymbolUtil.ContainingMethod(site.Node, site.Model, cancellationToken);
                if (container != null && SymbolEqualityComparer.Default.Equals(container.ContainingType?.OriginalDefinition, type))
                    hits.Enqueue((container, site));
            }
            if (producer.OperationKinds.Length > 0)
                context.RegisterOperationAction(c => Collect(producer.OnOperation(c.Operation), c.CancellationToken), producer.OperationKinds);
            if (producer.SyntaxKinds.Length > 0)
                context.RegisterSyntaxNodeAction(c => Collect(producer.OnSyntax(c.Node, c.SemanticModel, c.CancellationToken), c.CancellationToken), producer.SyntaxKinds);
            if (plan.Context.Depth > 0)
            {
                context.RegisterOperationAction(c =>
                {
                    var invocation = (IInvocationOperation)c.Operation;
                    var callee = invocation.TargetMethod.OriginalDefinition;
                    if (callee.MethodKind != MethodKind.Ordinary || !SymbolEqualityComparer.Default.Equals(callee.ContainingType?.OriginalDefinition, type) || c.Operation.SemanticModel == null)
                        return;
                    var caller = SymbolUtil.ContainingMethod(c.Operation.Syntax, c.Operation.SemanticModel, c.CancellationToken);
                    if (caller != null && SymbolEqualityComparer.Default.Equals(caller.ContainingType?.OriginalDefinition, type))
                        calls.Enqueue((caller, callee));
                }, OperationKind.Invocation);
            }
            context.RegisterSymbolEndAction(end =>
            {
                var paths = FollowPaths(type, plan.Context, calls);
                HashSet<TextSpan>? seen = null;
                foreach (var hit in hits.OrderBy(h => h.Site.Location.SourceSpan.Start))
                {
                    if (paths.TryGetValue(hit.Container, out var path))
                        Report(plan, hit.Site, path, ref seen, end.ReportDiagnostic, end.CancellationToken);
                }
            });
        }

        /// <summary>규칙의 메서드에서 출발해 같은 클래스 메서드를 Depth만큼 따라간 경로 (먼저 닿은 경로 하나).</summary>
        private static Dictionary<IMethodSymbol, string> FollowPaths(INamedTypeSymbol type, SiteContext context, IEnumerable<(IMethodSymbol Caller, IMethodSymbol Callee)> calls)
        {
            var callMap = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
            foreach (var call in calls)
            {
                if (!callMap.TryGetValue(call.Caller, out var callees))
                    callMap[call.Caller] = callees = new List<IMethodSymbol>();
                callees.Add(call.Callee);
            }

            var paths = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
            var frontier = type.GetMembers().OfType<IMethodSymbol>().Where(m => context.MethodNames!.Contains(m.Name)).ToList();
            foreach (var entry in frontier)
                paths[entry] = entry.Name;
            for (var level = 0; level < context.Depth; level++)
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
            return paths;
        }

        /// <summary>
        /// 필터 경로를 하나라도 통과하고 같은 위치를 아직 알리지 않았으면 알린다. path는 따라가기 경로(메서드로 좁힌 경우).
        /// seen은 한 콜백 안에서만 쓴다 (IDE는 같은 컴파일에서 파일을 다시 분석할 수 있어 콜백 밖에 두면 진단을 잃는다).
        /// </summary>
        private void Report(ProducerPlan plan, Site site, string? path, ref HashSet<TextSpan>? seen, Action<Diagnostic> report, System.Threading.CancellationToken cancellationToken)
        {
            if (!plan.Passes(site, cancellationToken))
                return;
            seen ??= new HashSet<TextSpan>();
            if (!seen.Add(site.Location.SourceSpan))
                return;
            var message = _appendPath && path != null && path.Contains(" → ") ? _message + " (" + path + ")" : _message;
            report(Diagnostic.Create(_descriptor, site.Location, _severity, additionalLocations: null, properties: null, message));
        }
    }
}
