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
                // 같은 id를 두 규칙이 쓰면 id 하나로 둘을 따로 끌 수 없다 — 먼저 읽은 것만 쓴다.
                // 적용 여부(requires · 제외)보다 먼저 id를 잡는다 — 먼저 읽은 규칙이 이 어셈블리를 건너뛰어도 뒤의 같은 id 규칙(편집 앱 미리 보기의 옛 생성물 등)이 대신 돌지 않게
                if (!activeIds.Add(rule.Id))
                {
                    config.Add(Diagnostic.Create(RuleDescriptors.RuleInvalid, Location.None, source.Owner.Name, rule.Id, "다른 규칙이 이미 같은 id를 씁니다"));
                    continue;
                }
                if (!rule.Requires.All(referenced.Contains))
                    continue;
                // 제외(공통 설정이 생성 때 합쳐짐 + 규칙)에 맞고 다시 넣기에는 안 맞으면 이 어셈블리는 검사하지 않는다
                if (rule.SkipsAssembly(compilation.AssemblyName ?? ""))
                    continue;
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

    }

    /// <summary>
    /// 규칙 머리의 경로 제외 — 사이트 파일이 ignorePaths에 맞고 includePaths에 안 맞으면 보고하지 않는다.
    /// 파일(SyntaxTree)마다 한 번만 판정한다 (사이트마다 경로 패턴을 돌리지 않게).
    /// </summary>
    internal sealed class PathScope
    {
        private readonly PathPattern[] _ignore;
        private readonly PathPattern[] _include;
        private readonly ConcurrentDictionary<SyntaxTree, bool> _skipped = new ConcurrentDictionary<SyntaxTree, bool>();

        private PathScope(PathPattern[] ignore, PathPattern[] include)
        {
            _ignore = ignore;
            _include = include;
        }

        /// <summary>제외 경로가 없으면 null (판정 안 함).</summary>
        public static PathScope? Create(RuleIr rule)
        {
            if (rule.IgnorePaths.Length == 0)
                return null;
            // 검증(RuleIr)을 통과했으면 모두 읽힌다
            PathPattern[] Read(string[] patterns) => patterns.Select(p => PathPattern.Parse(p, out _)).OfType<PathPattern>().ToArray();
            return new PathScope(Read(rule.IgnorePaths), Read(rule.IncludePaths));
        }

        public bool Skips(Location location)
        {
            var tree = location.SourceTree;
            if (tree == null || string.IsNullOrEmpty(tree.FilePath))
                return false;
            return _skipped.GetOrAdd(tree, t => _ignore.Any(p => p.Matches(t.FilePath)) && !_include.Any(p => p.Matches(t.FilePath)));
        }
    }

    /// <summary>사이트 노드가 받는 범위: Scope(컴파일 전체) · Types(그 타입 계열 안) · Methods(그 메서드 안, 따라가기 포함).</summary>
    internal sealed class SiteContext
    {
        public List<INamedTypeSymbol> Types { get; } = new List<INamedTypeSymbol>();

        /// <summary>출발 메서드 판정 (methods.named · methods.coroutine). null이면 메서드로 좁히지 않음.</summary>
        public Func<IMethodSymbol, bool>? Entry { get; set; }

        public int Depth { get; set; }

        public bool IsScope => Types.Count == 0 && Entry == null;
    }

    /// <summary>사이트 노드 하나와 그 범위, 그래프에서의 자리 (끝 자리까지 가는 길 하나라도 필터를 통과하면 닿음).</summary>
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

        /// <summary>slot: 끝 자리 번호 (report이면 0, 짝 묶기면 a 0 · b 1).</summary>
        public bool Passes(Site site, int slot, System.Threading.CancellationToken cancellationToken) => _graph.Passes(_index, slot, site, cancellationToken);
    }

    /// <summary>
    /// 사이트가 흐르는 노드들(사이트 노드 · 필터 · 합치기)과 끝 자리(report 또는 묶기 노드의 입력)를 번호로 편 것.
    /// 경로를 펼쳐 두지 않고 사이트마다 따라가 판정한다 — 합치기가 겹겹이면 경로 수가 지수로 늘어서.
    /// 한 노드를 여러 노드가 받는 그래프에서만 노드마다 결과를 기억한다.
    /// </summary>
    internal sealed class SiteGraph
    {
        private readonly SiteFilter?[] _filters;
        private readonly int[][] _consumers;
        private readonly int _firstSlot;
        private readonly bool _fanOut;

        /// <summary>번호 firstSlot부터가 끝 자리 (필터 없음 · 소비자 없음).</summary>
        public SiteGraph(SiteFilter?[] filters, int[][] consumers, int firstSlot)
        {
            _filters = filters;
            _consumers = consumers;
            _firstSlot = firstSlot;
            _fanOut = consumers.Any(c => c.Length > 1);
        }

        public bool Passes(int from, int slot, Site site, System.Threading.CancellationToken cancellationToken) =>
            AnyConsumer(from, _firstSlot + slot, site, _fanOut ? new sbyte[_filters.Length] : null, cancellationToken);

        private bool AnyConsumer(int node, int target, Site site, sbyte[]? memo, System.Threading.CancellationToken cancellationToken)
        {
            foreach (var next in _consumers[node])
            {
                if (Reaches(next, target, site, memo, cancellationToken))
                    return true;
            }
            return false;
        }

        /// <summary>memo: 0 모름 · 1 닿음 · 2 안 닿음.</summary>
        private bool Reaches(int node, int target, Site site, sbyte[]? memo, System.Threading.CancellationToken cancellationToken)
        {
            if (node >= _firstSlot)
                return node == target;
            if (memo != null && memo[node] != 0)
                return memo[node] == 1;
            var reaches = (_filters[node]?.Passes(site, cancellationToken) ?? true) && AnyConsumer(node, target, site, memo, cancellationToken);
            if (memo != null)
                memo[node] = (sbyte)(reaches ? 1 : 2);
            return reaches;
        }
    }

    /// <summary>Groups 끝 — 묶기(by) · 짝 묶기(pair) 하나와 그 뒤 개수 사슬.</summary>
    internal sealed class GroupSpec
    {
        public GroupSpec(bool pair, bool byTarget, int min)
        {
            Pair = pair;
            ByTarget = byTarget;
            Min = min;
        }

        public bool Pair { get; }

        public bool ByTarget { get; }

        /// <summary>묶음의 사이트가 이 수 이상이어야 (개수 노드가 없으면 1).</summary>
        public int Min { get; }
    }

    /// <summary>규칙 하나의 실행 계획.</summary>
    internal sealed class RulePlan
    {
        private readonly DiagnosticDescriptor _descriptor;
        private readonly DiagnosticSeverity _severity;
        private readonly string _message;
        private readonly bool _appendPath;
        private readonly List<ProducerPlan> _producers;
        private readonly GroupSpec? _group;
        private readonly string _id;
        private readonly string _owner;
        private readonly List<NamePattern> _namePatterns;
        private readonly PathScope? _paths;

        private RulePlan(RuleIr rule, string owner, List<ProducerPlan> producers, GroupSpec? group, List<NamePattern> namePatterns)
        {
            _id = rule.Id;
            _owner = owner;
            _namePatterns = namePatterns;
            _paths = PathScope.Create(rule);
            _descriptor = RuleDescriptors.Rules[rule.Id];
            _severity = rule.Severity == "error" ? DiagnosticSeverity.Error : rule.Severity == "info" ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning;
            _message = rule.Report.GetString("message")!;
            _appendPath = rule.Report.GetBool("appendPath") ?? true;
            _producers = producers;
            _group = group;
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

            // 사이트가 닿아야 하는 끝 자리: report의 입력, 또는 Groups면 개수 사슬을 거슬러 묶기 노드의 입력(by: from, pair: a · b)
            GroupSpec? group = null;
            var head = rule.Find(rule.Report.Inputs["from"][0])!;
            string[] slotInputs;
            if (head.Spec.Output == Socket.Groups)
            {
                var min = 1;
                for (; head.Kind == "groups.count"; head = rule.Find(head.Inputs["from"][0])!)
                    min = Math.Max(min, head.GetInt("min") ?? 1);
                var pair = head.Kind == "groups.pair";
                group = new GroupSpec(pair, head.GetString("key") == "methodAndTarget", min);
                slotInputs = pair ? new[] { head.Inputs["a"][0], head.Inputs["b"][0] } : new[] { head.Inputs["from"][0] };
            }
            else
            {
                slotInputs = new[] { head.Name };
            }

            // 사이트가 흐르는 노드(Sites를 내보내는 노드)와 끝 자리를 번호로 편다
            var flow = rule.Nodes.Where(n => reached.Contains(n.Name) && n.Spec.Output == Socket.Sites).ToList();
            var index = flow.Select((n, i) => (n.Name, i)).ToDictionary(x => x.Name, x => x.i);
            var filters = new SiteFilter?[flow.Count + slotInputs.Length];
            var namePatterns = new List<NamePattern>();
            for (var i = 0; i < flow.Count; i++)
            {
                switch (flow[i].Kind)
                {
                    case "filter.targetType":
                        INamedTypeSymbol? type = null;
                        var typeName = flow[i].GetString("type");
                        if (typeName != null && (type = Type(typeName)) == null)
                            return null;
                        filters[i] = new TargetTypeFilter(type, flow[i].GetString("category"));
                        break;
                    case "filter.define":
                        filters[i] = new DefineFilter(flow[i].GetString("define")!, flow[i].GetString("where") == "inside", guards);
                        break;
                    case "filter.context":
                        filters[i] = new ContextFilter(flow[i].GetString("context") == "loop", flow[i].GetString("where") == "inside");
                        break;
                    case "filter.name":
                        // 정규식 · 경로 패턴은 검증(RuleIr)을 통과했으면 모두 읽힌다
                        var pattern = flow[i].GetString("pattern");
                        var regex = pattern == null ? null : NamePattern.ParseRegex(pattern, out _);
                        var namePattern = new NamePattern(flow[i].GetStrings("startsWith"), regex);
                        namePatterns.Add(namePattern);
                        filters[i] = new NameFilter(namePattern, flow[i].GetString("keep") == "matching");
                        break;
                    case "filter.location":
                        var paths = flow[i].GetStrings("paths").Select(p => PathPattern.Parse(p, out _)).OfType<PathPattern>().ToArray();
                        filters[i] = new LocationFilter(paths, flow[i].GetStrings("types"), flow[i].GetString("where") == "inside");
                        break;
                }
            }
            var consumers = flow
                .Select(n => flow.Where(c => c.AllInputs.Contains(n.Name)).Select(c => index[c.Name])
                    .Concat(Enumerable.Range(0, slotInputs.Length).Where(slot => slotInputs[slot] == n.Name).Select(slot => flow.Count + slot))
                    .ToArray())
                .Concat(slotInputs.Select(_ => new int[0]))
                .ToArray();
            var graph = new SiteGraph(filters, consumers, flow.Count);

            var producers = new List<ProducerPlan>();
            foreach (var node in flow)
            {
                // 사이트를 만드는 노드만 (필터 · 합치기는 그래프에서 지나간다)
                if (node.Kind == "sites.union" || node.Kind.StartsWith("filter.", StringComparison.Ordinal))
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
                            var names = new HashSet<string>(upstream.GetStrings("names"), StringComparer.Ordinal);
                            context.Entry = method => names.Contains(method.Name);
                            break;
                        case "methods.coroutine":
                            // 반환형만 본다 (IEnumerator · IEnumerator<T>)
                            var enumerator = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerator);
                            var genericEnumerator = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerator_T);
                            context.Entry = method => method.MethodKind == MethodKind.Ordinary
                                && (SymbolEqualityComparer.Default.Equals(method.ReturnType, enumerator)
                                    || SymbolEqualityComparer.Default.Equals(method.ReturnType.OriginalDefinition, genericEnumerator));
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
            return producers.Count == 0 ? null : new RulePlan(rule, owner.Name, producers, group, namePatterns);
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
                case "sites.new":
                    return new NewProducer();
                case "sites.boxing":
                    return new BoxingProducer();
                case "sites.stringConcat":
                    return new StringConcatProducer();
                case "sites.lambda":
                    return new LambdaProducer(node.GetBool("capturingOnly") ?? true);
                case "sites.declaration":
                    return new DeclarationProducer(node.GetStrings("kinds"), node.GetBool("static"), node.GetBool("extension"), node.GetStrings("accessibility"));
                default:
                    return null;
            }
        }

        public void Register(CompilationStartAnalysisContext start)
        {
            // 이름 필터 정규식은 처음 시간을 넘기면 꺼진다 — 컴파일 끝에 알린다 (꺼진 뒤 그 필터를 지나는 사이트는 빠졌으므로)
            if (_namePatterns.Count > 0)
            {
                start.RegisterCompilationEndAction(end =>
                {
                    if (_namePatterns.Any(p => p.TimedOut))
                        end.ReportDiagnostic(Diagnostic.Create(RuleDescriptors.RuleInvalid, Location.None, _owner, _id,
                            $"이름 필터의 정규식이 {NamePattern.Timeout.TotalMilliseconds}ms를 넘겨 이 컴파일에서 그 필터를 껐습니다 (그 뒤 사이트는 보고되지 않음) — 정규식을 단순하게 고치세요"));
                });
            }

            if (_group != null)
            {
                start.RegisterSymbolStartAction(OnGroupTypeStart, SymbolKind.NamedType);
                return;
            }

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

            if (plan.Context.Entry == null)
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
                CollectCalls(context, type, calls);
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

        /// <summary>같은 클래스 일반 메서드 호출 (따라가기용).</summary>
        private static void CollectCalls(SymbolStartAnalysisContext context, INamedTypeSymbol type, ConcurrentQueue<(IMethodSymbol Caller, IMethodSymbol Callee)> calls)
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

        /// <summary>
        /// Groups 규칙: 규칙의 사이트 노드 전부를 타입 하나 안에서 모아 두고, 타입 끝에서 메서드(와 대상)마다 묶어 판정한다.
        /// 짝 묶기는 a · b 사이트를 같은 타입 끝에서 함께 봐야 해서 규칙마다 SymbolStart를 한 번만 등록한다.
        /// </summary>
        private void OnGroupTypeStart(SymbolStartAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            var plans = _producers.Where(p => p.Context.Types.All(target => SymbolUtil.IsOrDerivesFrom(type, target))).ToList();
            if (plans.Count == 0)
                return;

            var hits = new ConcurrentQueue<(ProducerPlan Plan, IMethodSymbol? Container, Site Site)>();
            var calls = new ConcurrentQueue<(IMethodSymbol Caller, IMethodSymbol Callee)>();
            foreach (var plan in plans)
            {
                var producer = plan.Producer;
                void Collect(Site? site, System.Threading.CancellationToken cancellationToken)
                {
                    if (site == null)
                        return;
                    if (plan.Context.Entry == null)
                    {
                        if (SymbolEqualityComparer.Default.Equals(SymbolUtil.ContainingType(site.Node, site.Model, cancellationToken), type))
                            hits.Enqueue((plan, null, site));
                        return;
                    }
                    var container = SymbolUtil.ContainingMethod(site.Node, site.Model, cancellationToken);
                    if (container != null && SymbolEqualityComparer.Default.Equals(container.ContainingType?.OriginalDefinition, type))
                        hits.Enqueue((plan, container, site));
                }
                if (producer.OperationKinds.Length > 0)
                    context.RegisterOperationAction(c => Collect(producer.OnOperation(c.Operation), c.CancellationToken), producer.OperationKinds);
                if (producer.SyntaxKinds.Length > 0)
                    context.RegisterSyntaxNodeAction(c => Collect(producer.OnSyntax(c.Node, c.SemanticModel, c.CancellationToken), c.CancellationToken), producer.SyntaxKinds);
            }
            if (plans.Any(p => p.Context.Depth > 0))
                CollectCalls(context, type, calls);
            context.RegisterSymbolEndAction(end => ReportGroups(type, hits, calls, end.ReportDiagnostic, end.CancellationToken));
        }

        private void ReportGroups(
            INamedTypeSymbol type,
            IEnumerable<(ProducerPlan Plan, IMethodSymbol? Container, Site Site)> hits,
            IEnumerable<(IMethodSymbol Caller, IMethodSymbol Callee)> calls,
            Action<Diagnostic> report,
            System.Threading.CancellationToken cancellationToken)
        {
            var group = _group!;
            var paths = new Dictionary<SiteContext, Dictionary<IMethodSymbol, string>>();
            var groups = new Dictionary<(SyntaxNode Body, string? Target), Dictionary<TextSpan, GroupMember>>();
            foreach (var hit in hits)
            {
                string? path = null;
                if (hit.Container != null)
                {
                    if (!paths.TryGetValue(hit.Plan.Context, out var map))
                        paths[hit.Plan.Context] = map = FollowPaths(type, hit.Plan.Context, calls);
                    if (!map.TryGetValue(hit.Container, out path))
                        continue;
                }
                if (_paths != null && _paths.Skips(hit.Site.Location))
                    continue;
                var inA = hit.Plan.Passes(hit.Site, 0, cancellationToken);
                var inB = group.Pair && hit.Plan.Passes(hit.Site, 1, cancellationToken);
                if ((!inA && !inB) || !hit.Plan.Producer.Confirm(hit.Site, cancellationToken))
                    continue;
                var body = SymbolUtil.EnclosingBody(hit.Site.Node);
                var target = group.ByTarget ? SymbolUtil.TargetKey(hit.Site.Instance) : null;
                if (body == null || (group.ByTarget && target == null))
                    continue;

                if (!groups.TryGetValue((body, target), out var members))
                    groups[(body, target)] = members = new Dictionary<TextSpan, GroupMember>();
                // 합치기로 같은 위치가 두 번 오면 하나로 (a · b 표시는 합친다)
                if (members.TryGetValue(hit.Site.Location.SourceSpan, out var existing))
                    members[hit.Site.Location.SourceSpan] = new GroupMember(existing.Site, existing.Path, existing.InA || inA, existing.InB || inB);
                else
                    members[hit.Site.Location.SourceSpan] = new GroupMember(hit.Site, path, inA, inB);
            }

            var reports = new List<GroupMember[]>();
            foreach (var members in groups.Values)
            {
                // 짝은 서로 다른 사이트 둘 이상 (a · b 둘 다에 걸린 사이트 하나만으로는 짝이 아니다)
                if (members.Count < group.Min || (group.Pair && (members.Count < 2 || !(members.Values.Any(m => m.InA) && members.Values.Any(m => m.InB)))))
                    continue;
                reports.Add(members.Values
                    .OrderBy(m => m.Site.Location.SourceTree?.FilePath, StringComparer.Ordinal)
                    .ThenBy(m => m.Site.Location.SourceSpan.Start)
                    .ToArray());
            }
            // 묶음마다 하나 — 첫 사이트 위치, 나머지는 추가 위치
            foreach (var members in reports.OrderBy(m => m[0].Site.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(m => m[0].Site.Location.SourceSpan.Start))
            {
                var first = members[0];
                var message = _appendPath && first.Path != null && first.Path.Contains(" → ") ? _message + " (" + first.Path + ")" : _message;
                report(Diagnostic.Create(_descriptor, first.Site.Location, _severity, members.Skip(1).Select(m => m.Site.Location), properties: null, message));
            }
        }

        private readonly struct GroupMember
        {
            public GroupMember(Site site, string? path, bool inA, bool inB)
            {
                Site = site;
                Path = path;
                InA = inA;
                InB = inB;
            }

            public Site Site { get; }

            public string? Path { get; }

            public bool InA { get; }

            public bool InB { get; }
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
            var frontier = type.GetMembers().OfType<IMethodSymbol>().Where(context.Entry!).ToList();
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
            if ((_paths != null && _paths.Skips(site.Location)) || !plan.Passes(site, 0, cancellationToken) || !plan.Producer.Confirm(site, cancellationToken))
                return;
            seen ??= new HashSet<TextSpan>();
            if (!seen.Add(site.Location.SourceSpan))
                return;
            var message = _appendPath && path != null && path.Contains(" → ") ? _message + " (" + path + ")" : _message;
            report(Diagnostic.Create(_descriptor, site.Location, _severity, additionalLocations: null, properties: null, message));
        }
    }
}
