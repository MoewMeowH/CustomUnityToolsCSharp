using System.Text.RegularExpressions;

namespace HB.Orchestration.Agents;

/// <summary>실행 계획에서 단계 하나의 연결 (단계 번호는 1부터).</summary>
/// <param name="WaitFor">끝나길 기다릴 앞 단계. 그중 하나라도 실패했거나 실행되지 않았으면 이 단계도 실행하지 않는다.</param>
/// <param name="Sees">프롬프트의 "앞 단계 결과"에 넣을 단계 (번호 차례). 늘 WaitFor 안에 있다.</param>
/// <param name="SeesInput">요청과 첨부를 프롬프트에 넣는지.</param>
public sealed record GraphNode(int Step, IReadOnlyList<int> WaitFor, IReadOnlyList<int> Sees, bool SeesInput);

/// <summary>
/// 모드의 흐름(Sequential · Concurrent · Graph)을 단계별 연결로 푼다. 실행기와 대시보드 저장 전 검증이 같이 쓴다.
/// Debate는 턴을 주고받는 따로 된 실행이라 풀지 않고 검사만 한다.
/// </summary>
public static partial class ModeGraph
{
    /// <summary>From에서 요청(과 첨부)을 가리키는 이름.</summary>
    public const string InputName = "input";

    // 끝은 $가 아니라 \z — $는 끝의 줄바꿈 앞에서도 맞아 "a\n"을 통과시킨다.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z")]
    private static partial Regex IdRegex();

    /// <summary>
    /// 단계마다 기다릴 단계 · 볼 단계를 정한다. 설정이 틀리면 Nodes는 null이고 Error에 이유 (모드 이름 없이).
    /// Debate는 Id · From 검사만 하고 빈 목록을 돌려준다.
    /// </summary>
    public static (IReadOnlyList<GraphNode>? Nodes, string? Error) Resolve(ModeDefinition mode)
    {
        var (stepById, idError) = CollectIds(mode);
        if (idError is not null) return (null, idError);

        if (mode.Flow != ModeFlow.Graph)
        {
            var withFrom = mode.Steps.FindIndex(s => s.From is not null);
            if (withFrom >= 0)
                return (null, $"{withFrom + 1}단계에 From이 있습니다. From은 Flow가 Graph인 모드에서만 씁니다 (지금 {mode.Flow}).");
        }

        return mode.Flow switch
        {
            ModeFlow.Graph => ResolveGraph(mode, stepById!),
            ModeFlow.Sequential => ([.. mode.Steps.Select((s, i) => Sequential(i + 1, s.Sees ?? StepSees.InputAndPrevious))], null),
            ModeFlow.Concurrent => ([.. mode.Steps.Select((_, i) => new GraphNode(i + 1, [], [], SeesInput: true))], null),
            ModeFlow.Debate => ([], null),
            // 열거형 변환기는 정의되지 않은 숫자("Flow": 7)도 받는다 — 실행 계획을 만들 수 없으니 설정 오류다.
            _ => (null, $"Flow 값 '{mode.Flow}'을(를) 모릅니다. 쓸 수 있는 값: {string.Join(" · ", Enum.GetNames<ModeFlow>())}."),
        };
    }

    /// <summary>끝 단계: 다른 단계가 기다리지 않는 단계. 반환 범위가 Last일 때 보이는 단계다.</summary>
    public static IReadOnlySet<int> Terminals(IReadOnlyList<GraphNode> nodes)
    {
        var waited = nodes.SelectMany(n => n.WaitFor).ToHashSet();
        return nodes.Select(n => n.Step).Where(s => !waited.Contains(s)).ToHashSet();
    }

    /// <summary>단계의 Id → 단계 번호. 형식이 틀렸거나 겹치면 오류.</summary>
    private static (Dictionary<string, int>? StepById, string? Error) CollectIds(ModeDefinition mode)
    {
        var stepById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mode.Steps.Count; i++)
        {
            if (mode.Steps[i].Id is not { } id) continue;
            if (!IdRegex().IsMatch(id))
                return (null, $"{i + 1}단계의 Id '{id}'은(는) 영문 · 숫자 · _ · - 1~32자여야 합니다.");
            if (string.Equals(id, InputName, StringComparison.OrdinalIgnoreCase))
                return (null, $"{i + 1}단계의 Id로 \"{InputName}\"은 쓸 수 없습니다 (요청을 뜻하는 이름).");
            if (!stepById.TryAdd(id, i + 1))
                return (null, $"{i + 1}단계의 Id '{id}'이(가) {stepById[id]}단계와 겹칩니다.");
        }
        return (stepById, null);
    }

    /// <summary>순차: 첫 단계는 요청만. 그 뒤는 앞 단계를 기다리고, 넣을 것은 Sees대로 (Input이면 기다리기만 하고 보지 않는다).</summary>
    private static GraphNode Sequential(int step, StepSees sees)
    {
        if (step == 1) return new GraphNode(step, [], [], SeesInput: true);

        int[] previous = [step - 1];
        return sees switch
        {
            StepSees.Input => new GraphNode(step, previous, [], SeesInput: true),
            StepSees.Previous => new GraphNode(step, previous, previous, SeesInput: false),
            StepSees.All => All(step),
            _ => new GraphNode(step, previous, previous, SeesInput: true),
        };

        static GraphNode All(int step)
        {
            int[] earlier = [.. Enumerable.Range(1, step - 1)];
            return new GraphNode(step, earlier, earlier, SeesInput: true);
        }
    }

    private static (IReadOnlyList<GraphNode>? Nodes, string? Error) ResolveGraph(ModeDefinition mode, Dictionary<string, int> stepById)
    {
        if (mode.Steps.Count > ModeDefinition.MaxGraphStepsHardCap)
            return (null, $"Graph 모드는 단계가 {ModeDefinition.MaxGraphStepsHardCap}개까지입니다 (지금 {mode.Steps.Count}개).");

        var nodes = new List<GraphNode>();
        for (var i = 0; i < mode.Steps.Count; i++)
        {
            var (def, step) = (mode.Steps[i], i + 1);
            if (def.Sees is not null)
                return (null, $"{step}단계에 Sees가 있습니다. Graph 모드는 무엇을 보는지를 From으로 적습니다.");

            var from = def.From ?? [InputName];
            if (from.Count == 0)
                return (null, $"{step}단계의 From이 비어 있습니다. 요청을 보려면 \"{InputName}\"을 넣습니다.");

            var seesInput = false;
            var sees = new List<int>();
            foreach (var name in from)
            {
                if (string.Equals(name, InputName, StringComparison.OrdinalIgnoreCase))
                {
                    if (seesInput) return (null, $"{step}단계의 From에 \"{InputName}\"이 두 번 있습니다.");
                    seesInput = true;
                }
                else if (name is null || !stepById.TryGetValue(name, out var target))
                    return (null, $"{step}단계의 From '{name}'은(는) 이 모드에 없는 Id입니다. From에는 \"{InputName}\"이나 앞에 있는 단계의 Id를 적습니다.");
                else if (target >= step)
                    return (null, $"{step}단계의 From '{name}'은(는) {(target == step ? "자기 자신" : $"뒤에 있는 {target}단계")}입니다. From은 앞에 있는 단계만 가리킵니다.");
                else if (sees.Contains(target))
                    return (null, $"{step}단계의 From에 '{name}'이(가) 두 번 있습니다.");
                else
                    sees.Add(target);
            }

            sees.Sort();
            nodes.Add(new GraphNode(step, sees, sees, seesInput));
        }
        return (nodes, null);
    }
}
