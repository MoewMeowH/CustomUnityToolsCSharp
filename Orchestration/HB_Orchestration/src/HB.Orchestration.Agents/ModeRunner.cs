using System.Collections.Concurrent;
using System.Text;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// modes.json의 모드를 실행한다.
/// MAF 내장 순차 워크플로는 대화 전체를 다음 에이전트에 넘기므로, 토큰을 아끼려고
/// 단계마다 무엇을 넘길지(Sees)를 직접 제어하는 얇은 실행기를 둔다. 각 단계 자체는 MAF 에이전트다.
/// </summary>
public sealed class ModeRunner(OrchestrationOptions options, ModeStore store, AgentCatalog catalog, AgentRunner runner)
{
    // MCP 서버 프로세스는 Claude Code 세션마다 하나라서, 메모리 카운터 = 세션당 실행 횟수.
    private readonly ConcurrentDictionary<string, int> _runsThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="agentOverrides">
    /// 이번 실행에서만 에이전트를 바꾼다. "원래이름=새이름" 또는 "*=새이름"(모든 단계).
    /// modes.json은 그대로 두고, 같은 모드를 다른 모델로 한 번 돌려볼 때 쓴다.
    /// </param>
    public async Task<string> RunAsync(
        string modeName, string input, IReadOnlyList<string>? files, IReadOnlyList<string>? agentOverrides, CancellationToken ct)
    {
        // ── 1. 실행 전 검증: 여기서 걸리면 토큰을 한 개도 쓰지 않는다 ──────────────
        ModeStore.Loaded loaded;
        try { loaded = store.Load(); }
        catch (ModeConfigException ex) { return $"[모드 설정 오류] {ex.Message}"; }

        if (!loaded.File.Modes.TryGetValue(modeName, out var mode))
            return $"[알 수 없는 모드 '{modeName}'] 사용 가능: {string.Join(", ", loaded.File.Modes.Keys)}";

        if (mode.Steps.Count == 0)
            return $"[모드 '{modeName}'] Steps가 비어 있습니다.";

        if (mode.Flow == ModeFlow.Debate && mode.Steps.Count is not (2 or 3))
            return $"[모드 '{modeName}'] Debate는 Steps가 2개(토론자 둘) 또는 3개(+판정자)여야 합니다.";

        var maxChars = mode.MaxInputChars ?? options.Budget.MaxInputChars;
        if (maxChars > 0 && input.Length > maxChars)
            return $"[입력이 너무 깁니다] {input.Length:N0}자 / 상한 {maxChars:N0}자. " +
                   "변경된 부분과 꼭 필요한 주변 코드만 남겨 다시 호출하세요.";

        var budgetMessage = CheckBudget();
        if (budgetMessage is not null) return budgetMessage;

        var (swap, swapError) = ParseOverrides(agentOverrides, mode);
        if (swapError is not null) return $"[모드 '{modeName}'] {swapError}";

        var guidelines = GuidelinesLoader.Load(options);
        var plan = new List<(StepDefinition Def, AgentEntry Agent, string Instructions)>();
        foreach (var step in mode.Steps)
        {
            var agentName = swap.TryGetValue(step.Agent, out var to) ? to
                          : swap.TryGetValue("*", out var all) ? all
                          : step.Agent;

            if (!catalog.TryGet(agentName, out var agent))
                return $"[모드 '{modeName}'] 에이전트 '{agentName}' 사용 불가: {catalog.WhyUnavailable(agentName)}";

            (string Prompt, bool UseGuidelines) role;
            try { role = ModeStore.ResolveRole(loaded, step.Role); }
            catch (ModeConfigException ex) { return $"[모드 '{modeName}'] {ex.Message}"; }

            // 시스템 프롬프트 = 역할 + 공통 규칙 (+ 지침). 매번 같은 앞부분이라 제공사 프롬프트 캐시에 걸린다.
            plan.Add((step, agent, role.Prompt + Prompts.CommonRules + (role.UseGuidelines ? guidelines : "")));
        }

        // 세션당 실행 한도: 검증을 다 통과한 실행만 센다 (설정 오류로 거절된 호출은 횟수에 안 들어감).
        var limit = RunLimit(mode);
        var count = _runsThisSession.AddOrUpdate(modeName, 1, (_, n) => n + 1);
        if (limit > 0 && count > limit)
        {
            _runsThisSession.AddOrUpdate(modeName, 0, (_, n) => n - 1);
            return $"[이 세션의 '{modeName}' 실행 한도 도달] {limit}회. 외부 에이전트 없이 진행하세요. " +
                   "더 필요하면 사용자가 modes.json의 MaxRunsPerSession을 올리거나 Claude Code를 새 세션으로 시작할 수 있습니다.";
        }

        // ── 2. 실행 ──────────────────────────────────────────────────────────
        var runId = Guid.NewGuid().ToString("N")[..10];

        string? note = null;
        List<StepResult> results;
        switch (mode.Flow)
        {
            case ModeFlow.Concurrent:
                results = await RunConcurrentAsync(runId, modeName, plan, input, files, ct);
                break;
            case ModeFlow.Debate:
                (results, note) = await RunDebateAsync(runId, modeName, mode, plan, input, files, ct);
                break;
            default:
                results = await RunSequentialAsync(runId, modeName, plan, input, files, ct);
                if (results.Count < plan.Count)
                    note = $"(단계 {results.Count}에서 실패해 이후 {plan.Count - results.Count}단계는 실행하지 않음)";
                break;
        }

        // ── 3. 반환: Last면 마지막 결과만 (Claude 쪽 토큰 절약) ─────────────────
        var returnAll = mode.Return == ModeReturn.All || mode.Flow == ModeFlow.Concurrent;
        var failed = results.FirstOrDefault(r => !r.Success);

        if (swap.Count > 0)
        {
            var swapNote = "(에이전트 교체: " + string.Join(", ", swap.Select(kv => $"{kv.Key}→{kv.Value}")) + ")";
            note = note is null ? swapNote : $"{note}\n{swapNote}";
        }

        if (!returnAll && failed is null)
            return note is null ? results[^1].Text : $"{results[^1].Text}\n\n{note}";

        var toShow = returnAll ? results : new List<StepResult> { failed! };
        var sb = new StringBuilder();
        foreach (var r in toShow)
            sb.AppendLine($"### {r.Step}. {r.Role} · {r.Agent}").AppendLine(r.Text).AppendLine();

        if (note is not null) sb.AppendLine(note);

        return sb.ToString().TrimEnd();
    }

    private async Task<List<StepResult>> RunSequentialAsync(
        string runId, string mode, List<(StepDefinition Def, AgentEntry Agent, string Instructions)> plan,
        string input, IReadOnlyList<string>? files, CancellationToken ct)
    {
        var results = new List<StepResult>();
        for (var i = 0; i < plan.Count; i++)
        {
            var (def, agent, instructions) = plan[i];
            var prompt = BuildPrompt(input, results, i == 0 ? StepSees.Input : def.Sees);

            var result = await runner.RunStepAsync(
                new StepCall(runId, mode, i + 1, def.Role, agent, instructions, prompt, files), ct);
            results.Add(result);

            // 앞 단계가 실패하면 뒤 단계는 의미가 없으므로 토큰을 쓰지 않고 멈춘다.
            if (!result.Success) break;
        }
        return results;
    }

    private async Task<List<StepResult>> RunConcurrentAsync(
        string runId, string mode, List<(StepDefinition Def, AgentEntry Agent, string Instructions)> plan,
        string input, IReadOnlyList<string>? files, CancellationToken ct)
    {
        var prompt = BuildPrompt(input, [], StepSees.Input);
        var tasks = plan.Select((p, i) => runner.RunStepAsync(
            new StepCall(runId, mode, i + 1, p.Def.Role, p.Agent, p.Instructions, prompt, files), ct));
        return [.. await Task.WhenAll(tasks)];
    }

    private async Task<(List<StepResult> Results, string? Note)> RunDebateAsync(
        string runId, string mode, ModeDefinition def,
        List<(StepDefinition Def, AgentEntry Agent, string Instructions)> plan,
        string input, IReadOnlyList<string>? files, CancellationToken ct)
    {
        var rounds = Math.Clamp(def.MaxRounds ?? 2, 1, ModeDefinition.MaxRoundsHardCap);
        var window = Math.Max(1, def.HistoryWindow);
        var debaters = plan.Take(2).ToList();
        var turns = new List<StepResult>();
        var step = 0;
        string? note = null;

        for (var round = 1; round <= rounds && note is null; round++)
        {
            foreach (var (stepDef, agent, instructions) in debaters)
            {
                var prompt = BuildDebatePrompt(input, turns.TakeLast(window), round, rounds);
                var result = await runner.RunStepAsync(
                    new StepCall(runId, mode, ++step, stepDef.Role, agent, instructions + Prompts.DebateRules, prompt, files), ct);
                turns.Add(result);

                if (!result.Success) return (turns, $"(턴 {step}에서 실패해 토론 중단)");
                if (EndsWithAgreement(result.Text))
                {
                    note = $"({round}/{rounds}회차 {stepDef.Role}의 [합의]로 조기 종료)";
                    break;
                }
            }
        }

        if (plan.Count == 3)
        {
            // 판정자는 토론 전체를 본다 (회차 상한이 있으므로 분량도 상한이 있다).
            var (judgeDef, judgeAgent, judgeInstructions) = plan[2];
            var judgePrompt = BuildDebatePrompt(input, turns, round: 0, rounds: rounds);
            var verdict = await runner.RunStepAsync(
                new StepCall(runId, mode, ++step, judgeDef.Role, judgeAgent, judgeInstructions, judgePrompt, files), ct);
            turns.Add(verdict);
        }

        return (turns, note);
    }

    private static bool EndsWithAgreement(string text)
    {
        var lastLine = text.TrimEnd().Split('\n').LastOrDefault() ?? "";
        return lastLine.Contains(Prompts.AgreementMarker, StringComparison.Ordinal);
    }

    /// <summary>round=0이면 판정자용 (회차 안내 없음).</summary>
    private static string BuildDebatePrompt(string input, IEnumerable<StepResult> history, int round, int rounds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 요청").AppendLine(input).AppendLine();

        var list = history.ToList();
        if (list.Count > 0)
        {
            sb.AppendLine(round == 0 ? "## 토론 기록 (전체)" : "## 토론 기록 (최근)");
            foreach (var t in list)
                sb.AppendLine($"### 턴 {t.Step} · {t.Role}").AppendLine(t.Text).AppendLine();
        }

        if (round > 0)
            sb.AppendLine($"(현재 {round}/{rounds}회차{(round == rounds ? " — 마지막 회차" : "")})");

        return sb.ToString().TrimEnd();
    }

    private static string BuildPrompt(string input, List<StepResult> previous, StepSees sees)
    {
        var includeInput = sees is StepSees.Input or StepSees.InputAndPrevious or StepSees.All;
        IEnumerable<StepResult> prior = sees switch
        {
            StepSees.Previous or StepSees.InputAndPrevious => previous.TakeLast(1),
            StepSees.All => previous,
            _ => Enumerable.Empty<StepResult>(),
        };

        var sb = new StringBuilder();
        if (includeInput) sb.AppendLine("## 요청").AppendLine(input).AppendLine();

        var priorList = prior.ToList();
        if (priorList.Count > 0)
        {
            sb.AppendLine("## 앞 단계 결과");
            foreach (var r in priorList)
                sb.AppendLine($"### {r.Step}. {r.Role}").AppendLine(r.Text).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>"from=to" 목록을 검증된 사전으로. 오류면 메시지 (실행 전이라 토큰 0).</summary>
    private (Dictionary<string, string> Map, string? Error) ParseOverrides(IReadOnlyList<string>? overrides, ModeDefinition mode)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (overrides is null) return (map, null);

        var used = mode.Steps.Select(s => s.Agent).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in overrides.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            var parts = raw.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return (map, $"에이전트 교체 형식 오류 '{raw}' — 'from=to' 또는 '*=to'");
            if (parts[0] != "*" && !used.Contains(parts[0]))
                return (map, $"이 모드는 '{parts[0]}'을(를) 쓰지 않습니다. 사용 중: {string.Join(", ", used)}");
            if (!catalog.TryGet(parts[1], out _))
                return (map, $"교체 대상 '{parts[1]}' 사용 불가: {catalog.WhyUnavailable(parts[1])}");
            map[parts[0]] = parts[1];
        }
        return (map, null);
    }

    private int RunLimit(ModeDefinition mode) => mode.MaxRunsPerSession ?? options.Budget.DefaultMaxRunsPerSession;

    /// <summary>오늘 쓴 토큰이 상한을 넘었으면 메시지, 아니면 null.</summary>
    private string? CheckBudget()
    {
        var limit = options.Budget.DailyTokenLimit;
        if (limit <= 0) return null;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var free = new HashSet<string>(options.Budget.FreeProviders, StringComparer.OrdinalIgnoreCase);
        var used = CallLog.ReadAll()
            .Where(c => DateOnly.FromDateTime(c.Timestamp.LocalDateTime) == today)
            .Where(c => c.Provider is null || !free.Contains(c.Provider))
            .Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0));

        return used >= limit
            ? $"[오늘 토큰 상한 도달] {used:N0} / {limit:N0}. 외부 에이전트 없이 진행하거나 appsettings.json의 Budget.DailyTokenLimit을 조정하세요."
            : null;
    }

    /// <summary>list_modes용 요약. 짧게 유지할수록 Claude 쪽 토큰 절약.</summary>
    public string Describe()
    {
        ModeStore.Loaded loaded;
        try { loaded = store.Load(); }
        catch (ModeConfigException ex) { return $"[모드 설정 오류] {ex.Message}"; }

        var sb = new StringBuilder();
        foreach (var (name, mode) in loaded.File.Modes)
        {
            var arrow = mode.Flow switch { ModeFlow.Concurrent => " | ", ModeFlow.Debate => " ⇄ ", _ => " → " };
            var steps = string.Join(arrow, mode.Steps.Select(s =>
                catalog.TryGet(s.Agent, out _) ? $"{s.Role}@{s.Agent}" : $"{s.Role}@{s.Agent}(사용불가)"));
            var limit = RunLimit(mode);
            var used = _runsThisSession.TryGetValue(name, out var n) ? n : 0;
            var quota = limit > 0 ? $" (남은 실행 {Math.Max(0, limit - used)}/{limit})" : "";
            var rounds = mode.Flow == ModeFlow.Debate ? $" 최대 {Math.Clamp(mode.MaxRounds ?? 2, 1, ModeDefinition.MaxRoundsHardCap)}회차" : "";
            sb.AppendLine($"- {name}: {mode.Description} [{steps}]{rounds}{quota}");
        }
        if (sb.Length == 0) return "정의된 모드가 없습니다.";

        // run_mode의 agents 교체에 쓸 수 있는 이름 목록 (짧게)
        sb.AppendLine("에이전트: " + string.Join(", ", catalog.Agents.Select(a => $"{a.Name}({a.Model})")));
        return sb.ToString().TrimEnd();
    }
}
