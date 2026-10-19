using HB.Orchestration.Agents.Providers;
using System.Text;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// modes.json의 모드를 실행한다.
/// MAF 내장 순차 워크플로는 대화 전체를 다음 에이전트에 넘기므로, 토큰을 아끼려고
/// 단계마다 무엇을 넘길지(Sees)를 직접 제어하는 얇은 실행기를 둔다. 각 단계 자체는 MAF 에이전트다.
/// </summary>
public sealed class ModeRunner(ModeStore store, AgentCatalog catalog, AgentRunner runner, SessionTracker sessions)
{
    // 설정은 appsettings.json이 바뀌면 자동으로 다시 읽힌다 (AgentCatalog). 항상 최신 값을 쓰도록 매번 가져온다.
    private OrchestrationOptions Options => catalog.Options;

    /// <param name="agentOverrides">
    /// 이번 실행에서만 에이전트를 바꾼다. "원래이름=새이름" 또는 "*=새이름"(모든 단계).
    /// modes.json은 그대로 두고, 같은 모드를 다른 모델로 한 번 돌려볼 때 쓴다.
    /// </param>
    /// <param name="context">서버가 디스크에서 읽어 프롬프트 맨 앞(input 앞)에 둘 코드 범위. Claude가 코드를 input에 다시 쓰지 않게 한다.</param>
    /// <param name="images">서버가 읽어 보낼 이미지 경로 (프로젝트 루트 기준, image_prep.py shrink 결과). 입력을 받는 단계에만 붙는다.</param>
    /// <param name="reason">지휘자가 이 모드를 부른 이유 (필수). 기록에만 남고 에이전트에게는 보내지 않는다.</param>
    /// <param name="ownReview">결과를 보기 전의 자체 검토 (RequireOwnReview 모드에서 필수). 기록에만 남는다.</param>
    public async Task<string> RunAsync(
        string modeName, string input, IReadOnlyList<string>? files, IReadOnlyList<string>? agentOverrides,
        IReadOnlyList<ContextRange>? context, IReadOnlyList<string>? images, string? reason, string? ownReview, CancellationToken ct)
    {
        // ── 1. 실행 전 검증: 여기서 걸리면 토큰을 한 개도 쓰지 않는다 ──────────────
        sessions.ApplyResets();

        ModeStore.Loaded loaded;
        try { loaded = store.Load(); }
        catch (ModeConfigException ex) { return $"[모드 설정 오류] {ex.Message}"; }

        if (!loaded.File.Modes.TryGetValue(modeName, out var mode))
            return $"[알 수 없는 모드 '{modeName}'] 사용 가능: {string.Join(", ", loaded.File.Modes.Keys)}";

        if (mode.Steps.Count == 0)
            return $"[모드 '{modeName}'] Steps가 비어 있습니다.";

        if (mode.Flow == ModeFlow.Debate && mode.Steps.Count is not (2 or 3))
            return $"[모드 '{modeName}'] Debate는 Steps가 2개(토론자 둘) 또는 3개(+판정자)여야 합니다.";

        // 지휘자의 판단 근거: 기록에만 남긴다 (CallMeta → CallRecord). 프롬프트·시스템 프롬프트 조립에는 넘기지 않는다.
        if (CheckConductorNotes(modeName, mode, reason, ownReview) is { } notesError) return notesError;

        var maxChars = mode.MaxInputChars ?? Options.Budget.MaxInputChars;
        if (maxChars > 0 && input.Length > maxChars)
            return $"[입력이 너무 깁니다] {input.Length:N0}자 / 상한 {maxChars:N0}자. " +
                   "코드는 input 대신 context(경로·줄 범위)로 넘기고, 꼭 필요한 범위만 남겨 다시 호출하세요.";

        var (attached, contextError) = ContextLoader.Load(context, Options.Context, Environment.CurrentDirectory, maxChars, ct);
        if (contextError is not null) return $"[첨부 코드 오류] {contextError}";

        // 상한은 에이전트가 실제로 받는 양(input + 첨부) 기준이다.
        if (maxChars > 0 && input.Length + attached!.Text.Length > maxChars)
            return $"[입력이 너무 깁니다] input {input.Length:N0}자 + 첨부 코드 {attached.Text.Length:N0}자 / 상한 {maxChars:N0}자. " +
                   "context 범위를 줄여 다시 호출하세요.";

        var (loadedImages, imageError) = ImageLoader.Load(images, Options.Images, Options.Context, Environment.CurrentDirectory, ct);
        if (imageError is not null) return $"[첨부 이미지 오류] {imageError}";

        // 첨부 자료는 input과 따로 들고 다니다가 프롬프트 맨 앞에 둔다 (같은 자료로 다시 물을 때 앞부분이 같아야 캐시에 걸린다).
        var promptInput = new PromptInput(input, attached!.Text, loadedImages!);
        var meta = new CallMeta(files, attached.Sources)
        {
            ContextRoots = MergeRoots(attached.Roots, loadedImages!),
            Reason = reason,
            OwnReview = string.IsNullOrWhiteSpace(ownReview) ? null : ownReview,
        };

        var (prepared, planError) = BuildPlan(loaded, modeName, mode, agentOverrides);
        if (planError is not null) return planError;
        var (swap, plan) = (prepared!.Swap, prepared.Steps);

        if (loadedImages!.Count > 0 && plan.Where(p => !p.Agent.Options.Vision).Select(p => p.Agent.Name).Distinct().ToList() is { Count: > 0 } blind)
            return $"[모드 '{modeName}'] 이미지를 받지 않는 에이전트(Vision=false): {string.Join(", ", blind)}. images 없이 호출하거나 agents로 바꾸세요.";

        // 비용 상한은 단계별 모델을 알아야 하므로 에이전트를 정한 뒤에 검사한다.
        if (CheckBudget(plan.Select(p => p.Agent)) is { } budgetMessage) return budgetMessage;

        // 세션당 실행 한도: 검증을 다 통과한 실행만 센다 (설정 오류로 거절된 호출은 횟수에 안 들어감).
        var limit = RunLimit(mode);
        if (!sessions.TryReserve(modeName, limit))
        {
            return $"[이 세션의 '{modeName}' 실행 한도 도달] {limit}회. 외부 에이전트 없이 진행하세요. " +
                   "더 필요하면 사용자가 대시보드 '세션' 화면에서 이 세션의 횟수를 초기화하거나 modes.json의 MaxRunsPerSession을 올릴 수 있습니다.";
        }

        // ── 2. 실행 ──────────────────────────────────────────────────────────
        var runId = Guid.NewGuid().ToString("N")[..10];

        meta = meta with { Provenance = RunProvenance(prepared) };

        string? note = null;
        List<StepResult> results;
        switch (mode.Flow)
        {
            case ModeFlow.Concurrent:
                results = await RunConcurrentAsync(runId, modeName, plan, promptInput, meta, ct);
                break;
            case ModeFlow.Debate:
                (results, note) = await RunDebateAsync(runId, modeName, mode, plan, promptInput, meta, ct);
                break;
            default:
                results = await RunSequentialAsync(runId, modeName, plan, promptInput, meta, ct);
                if (results.Count < plan.Count)
                    note = $"(단계 {results.Count}에서 실패해 이후 {plan.Count - results.Count}단계는 실행하지 않음)";
                break;
        }

        // ── 3. 반환: Last면 마지막 결과만 (Claude 쪽 토큰 절약) ─────────────────
        // 맨 위 runId와 단계 제목의 토큰·시간은 항상 붙인다 (save_result에 넘길 값, 서버 로그를 뒤지지 않도록).
        var returnAll = mode.Return == ModeReturn.All || mode.Flow == ModeFlow.Concurrent;

        if (swap.Count > 0)
        {
            var swapNote = "(에이전트 교체: " + string.Join(", ", swap.Select(kv => $"{kv.Key}→{kv.Value}")) + ")";
            note = note is null ? swapNote : $"{note}\n{swapNote}";
        }

        // 겹침 후보: 서로 다른 단계가 같은 위치를 지적했으면 끝에 한 줄씩 (판단은 지휘자).
        if (FindingOverlap.Lines(results.SelectMany(r => r.Structured?.Findings ?? [])) is { Count: > 0 } overlaps)
            note = note is null ? string.Join("\n", overlaps) : $"{string.Join("\n", overlaps)}\n{note}";

        return ResultFormatter.Format(runId, results, returnAll, note);
    }

    /// <summary>
    /// Debug.DumpToolResults가 켜져 있으면 도구가 반환하는 문자열을 그대로 파일로 남긴다 (UTF-8, BOM 없음, 변환 없음).
    /// 덤프 실패는 결과 전달을 막지 않는다.
    /// </summary>
    public string? DumpToolResult(string tool, string mode, string result)
    {
        try
        {
            if (!Options.Debug.DumpToolResults) return null;
            var dir = Path.Combine(Path.GetDirectoryName(CallLog.DefaultPath)!, "dumps");
            Directory.CreateDirectory(dir);
            var safeMode = string.Concat(mode.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{tool}_{safeMode}.txt");
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(result));
            return path;
        }
        catch (Exception)
        {
            return null; // 조사용 부가 기능: 실패해도 무시
        }
    }

    /// <summary>save_result: 기록의 응답 원문을 파일로. 쓰기 금지 = context 전송 금지 목록(비밀) + 쓰기 전용 금지 목록(실행·설정 파일).</summary>
    public string SaveResult(string runId, int step, string path, int? codeBlock, bool overwrite) =>
        ResultSaver.Save(runId, step, path, codeBlock, overwrite, Options.Context.AllDenyPatterns, writeDenyPatterns: Options.Context.AllWriteDenyPatterns);

    /// <summary>record_verdicts: 지적마다 판정을 호출 기록 옆 verdicts.jsonl에. reason 상한은 run_mode reason과 같다.</summary>
    public string RecordVerdicts(string runId, IReadOnlyList<VerdictItem>? items) =>
        VerdictRecorder.Record(runId, items, Options.Records.MaxReasonChars, runner.LogPath);

    /// <summary>모드 하나의 실행 계획: 읽은 modes.json · 지침 · 에이전트 교체 · 단계별 에이전트와 시스템 프롬프트.</summary>
    private sealed record PreparedPlan(ModeStore.Loaded Loaded, GuidelinesLoader.Result Guidelines, Dictionary<string, string> Swap, List<PlannedStep> Steps);

    /// <summary>
    /// 단계마다 에이전트(교체 반영)·시스템 프롬프트를 정한다. run_mode와 --golden이 같은 조립을 쓴다
    /// (평가가 실제 실행과 다른 프롬프트로 돌면 의미가 없으므로). 문제가 있으면 Plan은 null, Error는 '['로 시작하는 문구.
    /// </summary>
    private (PreparedPlan? Plan, string? Error) BuildPlan(ModeStore.Loaded loaded, string modeName, ModeDefinition mode, IReadOnlyList<string>? agentOverrides)
    {
        var (swap, swapError) = ParseOverrides(agentOverrides, mode);
        if (swapError is not null) return (null, $"[모드 '{modeName}'] {swapError}");

        GuidelinesLoader.Result guidelines;
        try { guidelines = GuidelinesLoader.Load(Options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 지침이 빠진 채 보내면 리뷰 결과가 달라지므로 조용히 넘어가지 않고 거절한다 (실행 횟수 차감 전).
            return (null, $"[리뷰 지침 읽기 실패] {ex.GetType().Name}: {ex.Message} 파일을 닫거나 확인한 뒤 다시 호출하세요.");
        }

        var plan = new List<PlannedStep>();
        foreach (var step in mode.Steps)
        {
            var agentName = swap.TryGetValue(step.Agent, out var to) ? to
                          : swap.TryGetValue("*", out var all) ? all
                          : step.Agent;

            if (!catalog.TryGet(agentName, out var agent))
                return (null, $"[모드 '{modeName}'] 에이전트 '{agentName}' 사용 불가: {catalog.WhyUnavailable(agentName)}");

            (string Prompt, bool UseGuidelines, FindingFormat? Output) role;
            try { role = ModeStore.ResolveRole(loaded, step.Role); }
            catch (ModeConfigException ex) { return (null, $"[모드 '{modeName}'] {ex.Message}"); }

            if (step.WebSearch is { } search)
            {
                if (search.Problem() is { } searchProblem) return (null, $"[모드 '{modeName}'] {step.Role}@{agentName}: {searchProblem}");
                if (!string.Equals(agent.ProviderOptions?.Type, OpenAIResponsesProvider.TypeName, StringComparison.OrdinalIgnoreCase))
                    return (null, $"[모드 '{modeName}'] {step.Role}@{agentName}: WebSearch는 제공사 Type이 '{OpenAIResponsesProvider.TypeName}'인 에이전트에서만 됩니다 " +
                                  $"(지금 '{agent.ProviderOptions?.Type}'). appsettings의 웹검색용 에이전트로 바꾸세요.");
            }

            if (role.Output is not null && step.WebSearch is not null)
                return (null, $"[모드 '{modeName}'] {step.Role}@{agentName}: 구조화 출력(Output)과 WebSearch는 함께 쓸 수 없습니다 (웹검색 경로는 구조화 출력을 지원하지 않음).");

            // 시스템 프롬프트 = 역할 + 출력 형식(구조화 출력) + 공통 규칙 (+ 웹검색 규칙) (+ 지침). 매번 같은 앞부분이라 제공사 프롬프트 캐시에 걸린다.
            plan.Add(new PlannedStep(step, agent,
                role.Prompt + (role.Output?.PromptText ?? "") + Prompts.CommonRules + (step.WebSearch is null ? "" : Prompts.WebSearchRules) + (role.UseGuidelines ? guidelines.Text : ""),
                ProvenanceBuilder.ForStep(guidelines, role.UseGuidelines), mode.PromptCache, role.Output));
        }
        return (new PreparedPlan(loaded, guidelines, swap, plan), null);
    }

    /// <summary>이 실행의 modes.json · 서버 설정 · 지침 해시와 원문 저장. 실패는 기록에만 남기고 실행은 계속한다.</summary>
    private CallProvenance RunProvenance(PreparedPlan plan)
    {
        try
        {
            return ProvenanceBuilder.ForRun(
                plan.Loaded, plan.Guidelines, plan.Steps.Any(p => p.Guidelines.Attached),
                catalog.SettingsPath, catalog.SettingsBytes, catalog.ReloadError, Options.Records, runner.LogPath);
        }
        catch (Exception ex)
        {
            return new CallProvenance { StoreErrors = [$"provenance: {ex.GetType().Name}: {ex.Message}"] };
        }
    }

    /// <summary>
    /// 골든 세트 실행 (--golden run): 고정된 프롬프트 원문을 run_mode와 같은 시스템 프롬프트·에이전트로 보낸다.
    /// 세션 실행 한도·ownReview·context 첨부는 쓰지 않는다 (첨부는 프롬프트 원문에 이미 들어 있음). 비용 상한은 그대로 본다.
    /// 호출 기록의 Tool은 <paramref name="tool"/>, Mode는 원래 모드 이름이다.
    /// </summary>
    public async Task<(string? RunId, StepResult? Result, string? Error)> RunFixedPromptAsync(
        string modeName, string prompt, IReadOnlyList<string>? agentOverrides, string tool, string reason, CancellationToken ct)
    {
        var (plan, error) = PlanFixedPrompt(modeName, agentOverrides, prompt.Length);
        if (error is not null) return (null, null, error);

        var runId = Guid.NewGuid().ToString("N")[..10];
        var meta = new CallMeta(null, []) { Reason = reason, Provenance = RunProvenance(plan!) };
        var step = plan!.Steps[0];
        var call = NewCall(runId, modeName, 1, step, step.Instructions, new BuiltPrompt(prompt, null), meta) with { Tool = tool };
        return (runId, await runner.RunStepAsync(call, ct), null);
    }

    /// <summary>
    /// --golden run의 실행 전 확인: 보낼 역할·에이전트·모델. 외부 호출 없음.
    /// 실행 때와 같은 검사(모드·에이전트·구조화 출력·입력 상한·비용 상한)를 해서, 통과하면 실행도 설정 이유로는 거절되지 않는다.
    /// </summary>
    public ((string Role, string Agent, string Model)? Step, string? Error) PreviewFixedPrompt(string modeName, IReadOnlyList<string>? agentOverrides, int promptLength)
    {
        var (plan, error) = PlanFixedPrompt(modeName, agentOverrides, promptLength);
        return error is not null ? (null, error) : ((plan!.Steps[0].Def.Role, plan.Steps[0].Agent.Name, plan.Steps[0].Agent.Model), null);
    }

    /// <summary>호출 기록 파일 (골든 결과에 provenance를 붙일 때 같은 파일을 읽도록).</summary>
    public string LogPath => runner.LogPath;

    /// <summary>
    /// 점수는 한 단계의 구조화 지적으로 매기므로 단계가 하나이고 Output이 있는 모드만 받는다 (없으면 비용을 쓰고도 채점하지 못함).
    /// 입력 상한(MaxInputChars)과 비용 상한은 run_mode와 같다.
    /// </summary>
    private (PreparedPlan? Plan, string? Error) PlanFixedPrompt(string modeName, IReadOnlyList<string>? agentOverrides, int promptLength)
    {
        ModeStore.Loaded loaded;
        try { loaded = store.Load(); }
        catch (ModeConfigException ex) { return (null, $"[모드 설정 오류] {ex.Message}"); }

        if (!loaded.File.Modes.TryGetValue(modeName, out var mode))
            return (null, $"[알 수 없는 모드 '{modeName}'] 사용 가능: {string.Join(", ", loaded.File.Modes.Keys)}");
        if (mode.Steps.Count != 1 || mode.Flow == ModeFlow.Debate)
            return (null, $"[모드 '{modeName}'] 골든 실행은 단계가 하나인 모드만 됩니다 (지금 {mode.Steps.Count}단계, {mode.Flow}).");

        var maxChars = mode.MaxInputChars ?? Options.Budget.MaxInputChars;
        if (maxChars > 0 && promptLength > maxChars)
            return (null, $"[입력이 너무 깁니다] {promptLength:N0}자 / 상한 {maxChars:N0}자 (모드 '{modeName}').");

        var (plan, error) = BuildPlan(loaded, modeName, mode, agentOverrides);
        if (error is not null) return (null, error);
        if (plan!.Steps[0].Output is null)
            return (null, $"[모드 '{modeName}'] 역할 '{plan.Steps[0].Def.Role}'에 구조화 출력(Output)이 없어 채점할 수 없습니다.");
        if (CheckBudget(plan.Steps.Select(p => p.Agent)) is { } budgetMessage) return (null, budgetMessage);
        return (plan, null);
    }

    /// <summary>실행 계획의 한 단계. Guidelines는 이 단계 역할에 지침을 붙였는지 (기록용).</summary>
    private sealed record PlannedStep(StepDefinition Def, AgentEntry Agent, string Instructions, GuidelinesProvenance Guidelines, PromptCacheMode? PromptCache, FindingFormat? Output);

    /// <summary>단계 호출. 실행 단위 Provenance에 이 단계의 지침 표시를 얹는다 (Instructions 해시는 AgentRunner가 보낼 문자열로 계산).</summary>
    private StepCall NewCall(string runId, string mode, int step, PlannedStep p, string instructions, BuiltPrompt prompt, CallMeta meta) =>
        new(runId, mode, step, p.Def.Role, p.Agent, instructions, prompt.Text,
            meta with { Provenance = (meta.Provenance ?? new CallProvenance()) with { Guidelines = p.Guidelines } })
        {
            Checks = Options.ResponseChecks,
            ContextPrefix = prompt.ContextPrefix,
            ModePromptCache = p.PromptCache,
            Images = prompt.Images,
            WebSearch = p.Def.WebSearch,
            Output = p.Output,
        };

    /// <summary>사용자 input과 서버가 읽은 첨부 자료·이미지. 프롬프트에서는 첨부가 먼저 온다.</summary>
    private sealed record PromptInput(string Request, string Context, IReadOnlyList<AttachedImage> Images);

    /// <summary>보낼 프롬프트. ContextPrefix는 Text의 앞부분(첨부 자료)이고 없으면 null. Images는 입력을 받는 단계에만 (없으면 null).</summary>
    private sealed record BuiltPrompt(string Text, string? ContextPrefix, IReadOnlyList<AttachedImage>? Images = null);

    private async Task<List<StepResult>> RunSequentialAsync(
        string runId, string mode, List<PlannedStep> plan,
        PromptInput input, CallMeta meta, CancellationToken ct)
    {
        var results = new List<StepResult>();
        for (var i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            var prompt = BuildPrompt(input, results, i == 0 ? StepSees.Input : p.Def.Sees);

            var result = await runner.RunStepAsync(NewCall(runId, mode, i + 1, p, p.Instructions, prompt, meta), ct);
            results.Add(result);

            // 앞 단계가 실패하면 뒤 단계는 의미가 없으므로 토큰을 쓰지 않고 멈춘다.
            if (!result.Success) break;
        }
        return results;
    }

    private async Task<List<StepResult>> RunConcurrentAsync(
        string runId, string mode, List<PlannedStep> plan,
        PromptInput input, CallMeta meta, CancellationToken ct)
    {
        var prompt = BuildPrompt(input, [], StepSees.Input);
        var tasks = plan.Select((p, i) => runner.RunStepAsync(NewCall(runId, mode, i + 1, p, p.Instructions, prompt, meta), ct));
        return [.. await Task.WhenAll(tasks)];
    }

    private async Task<(List<StepResult> Results, string? Note)> RunDebateAsync(
        string runId, string mode, ModeDefinition def,
        List<PlannedStep> plan,
        PromptInput input, CallMeta meta, CancellationToken ct)
    {
        var rounds = Math.Clamp(def.MaxRounds ?? 2, 1, ModeDefinition.MaxRoundsHardCap);
        var window = Math.Max(1, def.HistoryWindow);
        var debaters = plan.Take(2).ToList();
        var turns = new List<StepResult>();
        var step = 0;
        string? note = null;

        for (var round = 1; round <= rounds && note is null; round++)
        {
            foreach (var p in debaters)
            {
                var prompt = BuildDebatePrompt(input, turns.TakeLast(window), round, rounds);
                var result = await runner.RunStepAsync(NewCall(runId, mode, ++step, p, p.Instructions + Prompts.DebateRules, prompt, meta), ct);
                turns.Add(result);

                if (!result.Success) return (turns, $"(턴 {step}에서 실패해 토론 중단)");
                if (EndsWithAgreement(result.Text))
                {
                    note = $"({round}/{rounds}회차 {p.Def.Role}의 [합의]로 조기 종료)";
                    break;
                }
            }
        }

        if (plan.Count == 3)
        {
            // 판정자는 토론 전체를 본다 (회차 상한이 있으므로 분량도 상한이 있다).
            var judge = plan[2];
            var judgePrompt = BuildDebatePrompt(input, turns, round: 0, rounds: rounds);
            var verdict = await runner.RunStepAsync(NewCall(runId, mode, ++step, judge, judge.Instructions, judgePrompt, meta), ct);
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
    private static BuiltPrompt BuildDebatePrompt(PromptInput input, IEnumerable<StepResult> history, int round, int rounds)
    {
        var sb = new StringBuilder();
        var prefix = AppendContext(sb, input);
        sb.AppendLine("## 요청").AppendLine(input.Request).AppendLine();

        var list = history.ToList();
        if (list.Count > 0)
        {
            sb.AppendLine(round == 0 ? "## 토론 기록 (전체)" : "## 토론 기록 (최근)");
            foreach (var t in list)
                sb.AppendLine($"### 턴 {t.Step} · {t.Role}").AppendLine(t.Text).AppendLine();
        }

        if (round > 0)
            sb.AppendLine($"(현재 {round}/{rounds}회차{(round == rounds ? " — 마지막 회차" : "")})");

        return new BuiltPrompt(sb.ToString().TrimEnd(), prefix, ImagesOf(input));
    }

    private static IReadOnlyList<AttachedImage>? ImagesOf(PromptInput input) => input.Images.Count > 0 ? input.Images : null;

    /// <summary>첨부 자료가 있으면 맨 앞에 쓰고 그 부분(뒤 빈 줄 포함)을 돌려준다. 뒤에 항상 "## 요청"이 오므로 TrimEnd에 잘리지 않는다.</summary>
    private static string? AppendContext(StringBuilder sb, PromptInput input)
    {
        var context = input.Context.Trim();
        if (context.Length == 0) return null;
        sb.AppendLine(context).AppendLine();
        return sb.ToString();
    }

    private static BuiltPrompt BuildPrompt(PromptInput input, List<StepResult> previous, StepSees sees)
    {
        var includeInput = sees is StepSees.Input or StepSees.InputAndPrevious or StepSees.All;
        IEnumerable<StepResult> prior = sees switch
        {
            StepSees.Previous or StepSees.InputAndPrevious => previous.TakeLast(1),
            StepSees.All => previous,
            _ => Enumerable.Empty<StepResult>(),
        };

        var sb = new StringBuilder();
        string? prefix = null;
        if (includeInput)
        {
            prefix = AppendContext(sb, input);
            sb.AppendLine("## 요청").AppendLine(input.Request).AppendLine();
        }

        var priorList = prior.ToList();
        if (priorList.Count > 0)
        {
            sb.AppendLine("## 앞 단계 결과");
            foreach (var r in priorList)
                sb.AppendLine($"### {r.Step}. {r.Role}").AppendLine(r.Text).AppendLine();
        }

        return new BuiltPrompt(sb.ToString().TrimEnd(), prefix, includeInput ? ImagesOf(input) : null);
    }

    /// <summary>context와 images가 읽은 추가 루트를 합친다. 둘 다 안 썼으면 null (기록에서 필드 생략).</summary>
    private static IReadOnlyDictionary<string, string>? MergeRoots(IReadOnlyDictionary<string, string>? contextRoots, IReadOnlyList<AttachedImage> images)
    {
        if (images.All(i => i.Root is null)) return contextRoots;
        var merged = contextRoots is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(contextRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in images.Where(i => i.Root is not null).Select(i => i.Root!.Value)) merged[name] = path;
        return merged;
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

    /// <summary>reason · ownReview 검사. 문제가 있으면 '['로 시작하는 문구 (실행 횟수 차감 전이라 거절해도 횟수가 그대로다).</summary>
    private string? CheckConductorNotes(string modeName, ModeDefinition mode, string? reason, string? ownReview)
    {
        var records = Options.Records;
        if (string.IsNullOrWhiteSpace(reason))
            return "[reason 필요] 이 모드를 부른 이유를 reason에 한 줄로 넣어 다시 호출하세요. 기록용이며 에이전트에게 가지 않습니다. (실행 횟수 차감 없음)";
        if (records.MaxReasonChars > 0 && reason.Length > records.MaxReasonChars)
            return $"[reason이 너무 깁니다] {reason.Length:N0}자 / 상한 {records.MaxReasonChars:N0}자. 한 줄로 줄여 다시 호출하세요. (실행 횟수 차감 없음)";

        if (mode.RequireOwnReview && string.IsNullOrWhiteSpace(ownReview))
            return $"[ownReview 필요] '{modeName}'은(는) 결과를 보기 전에 완성한 자체 검토가 필수입니다. ownReview에 원문을 넣어 다시 호출하세요. " +
                   "기록용이며 에이전트에게 가지 않습니다. (실행 횟수 차감 없음)";
        if (ownReview is not null && records.MaxOwnReviewChars > 0 && ownReview.Length > records.MaxOwnReviewChars)
            return $"[ownReview가 너무 깁니다] {ownReview.Length:N0}자 / 상한 {records.MaxOwnReviewChars:N0}자. 잘라서 남기지 않으니 줄여서 다시 호출하세요. (실행 횟수 차감 없음)";

        return null;
    }

    private int RunLimit(ModeDefinition mode) => mode.MaxRunsPerSession ?? Options.Budget.DefaultMaxRunsPerSession;

    /// <summary>
    /// 오늘 쓴 토큰·비용이 상한을 넘었거나, 비용 상한이 켜져 있는데 단가 없는 모델을 쓰려 하면 메시지, 아니면 null.
    /// 실행 전에 한 번만 본다 (이번 실행이 쓸 양은 미리 알 수 없으므로 실행 중에 넘을 수 있다).
    /// </summary>
    private string? CheckBudget(IEnumerable<AgentEntry> agents)
    {
        var budget = Options.Budget;
        if (budget.DailyTokenLimit <= 0 && budget.DailyCostLimitUsd <= 0) return null;

        var pricing = Options.Pricing;
        if (budget.DailyCostLimitUsd > 0)
        {
            var unpriced = agents.Where(a => !budget.IsFree(a.Provider) && !pricing.Models.ContainsKey(a.Model))
                .Select(a => $"{a.Name}({a.Model})").Distinct().ToList();
            if (unpriced.Count > 0)
                return $"[단가 없는 모델] {string.Join(", ", unpriced)}. 비용 상한(Budget.DailyCostLimitUsd)이 켜져 있어 비용을 셀 수 없는 모델은 실행하지 않습니다. " +
                       "대시보드 '모드' 화면의 모델 단가 표(appsettings.json의 Pricing.Models)에 단가를 넣으세요.";
        }

        var today = CallLog.ReadSince(new DateTimeOffset(DateTime.Today), runner.LogPath);

        if (budget.DailyTokenLimit > 0 && budget.CountedTokens(today) is var tokens && tokens >= budget.DailyTokenLimit)
            return $"[오늘 토큰 상한 도달] {tokens:N0} / {budget.DailyTokenLimit:N0}. 외부 에이전트 없이 진행하거나 appsettings.json의 Budget.DailyTokenLimit을 조정하세요.";

        if (budget.DailyCostLimitUsd > 0 && budget.CountedCost(today, pricing) is var cost && cost >= budget.DailyCostLimitUsd)
            return $"[오늘 비용 상한 도달] ${cost:0.00} / ${budget.DailyCostLimitUsd:0.00}. 외부 에이전트 없이 진행하거나 appsettings.json의 Budget.DailyCostLimitUsd를 조정하세요.";

        return null;
    }

    /// <summary>list_modes용 요약. 짧게 유지할수록 Claude 쪽 토큰 절약.</summary>
    public string Describe()
    {
        sessions.ApplyResets();

        ModeStore.Loaded loaded;
        try { loaded = store.Load(); }
        catch (ModeConfigException ex) { return $"[모드 설정 오류] {ex.Message}"; }

        var sb = new StringBuilder();
        if (catalog.ReloadError is { } reloadError)
            sb.AppendLine($"[주의] appsettings.json 다시 읽기 실패, 이전 설정으로 동작 중: {reloadError}");
        foreach (var (name, mode) in loaded.File.Modes)
        {
            var arrow = mode.Flow switch { ModeFlow.Concurrent => " | ", ModeFlow.Debate => " ⇄ ", _ => " → " };
            var steps = string.Join(arrow, mode.Steps.Select(s =>
                catalog.TryGet(s.Agent, out _) ? $"{s.Role}@{s.Agent}" : $"{s.Role}@{s.Agent}(사용불가)"));
            var limit = RunLimit(mode);
            var used = sessions.Get(name);
            var quota = limit > 0 ? $" (남은 실행 {Math.Max(0, limit - used)}/{limit})" : "";
            var rounds = mode.Flow == ModeFlow.Debate ? $" 최대 {Math.Clamp(mode.MaxRounds ?? 2, 1, ModeDefinition.MaxRoundsHardCap)}회차" : "";
            var ownReview = mode.RequireOwnReview ? " [ownReview 필수]" : "";
            sb.AppendLine($"- {name}: {mode.Description} [{steps}]{rounds}{quota}{ownReview}");
        }
        if (sb.Length == 0) return "정의된 모드가 없습니다.";

        // run_mode의 agents 교체에 쓸 수 있는 이름 목록 (짧게)
        sb.AppendLine("에이전트: " + string.Join(", ", catalog.Agents.Select(a => $"{a.Name}({a.Model})")));

        // context의 "@이름/…"에 쓸 추가 루트. 등록이 있을 때만, 경로 없이 이름만 (비어 있으면 출력이 이전과 같다).
        // 잘못된 항목은 경고로만 알리고, 그 루트를 참조한 호출만 거절된다.
        var roots = Options.Context.ExtraRoots;
        if (roots.Count > 0)
        {
            sb.AppendLine("추가 루트(context 읽기 전용): " + string.Join(", ", roots.Keys.Select(k => "@" + k)));
            foreach (var (name, path) in roots)
                if (ContextLoader.ExtraRootProblem(name, path) is { } problem)
                    sb.AppendLine($"[주의] ExtraRoots '{name}': {problem} 이 루트를 쓰는 context만 거절됩니다.");
        }

        // /work-order가 지시서를 남길 폴더. 값이 있을 때만 (비어 있으면 출력이 이전과 같다).
        // 서버는 이 폴더에 쓰지 않는다 — 지휘자가 직접 쓴다.
        var workOrders = Options.WorkOrders.Folder;
        if (!string.IsNullOrWhiteSpace(workOrders))
        {
            sb.AppendLine(WorkOrdersOptions.FolderProblem(workOrders) is { } folderProblem
                ? $"[주의] WorkOrders.Folder '{workOrders}': {folderProblem} /work-order는 기본 폴더를 씁니다."
                : $"지시서 폴더: {WorkOrdersOptions.Normalize(workOrders)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>server_info용: publish 후 새 서버가 떴는지, 설정이 언제 읽혔는지 확인.</summary>
    public string ServerInfo()
    {
        sessions.ApplyResets();
        var sb = new StringBuilder();
        sb.AppendLine($"서버 빌드: {(sessions.ServerBuild?.ToString("yyyy-MM-dd HH:mm:ss") ?? "알 수 없음")}");
        sb.AppendLine($"PID: {sessions.Pid} (시작 {sessions.StartedAt:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine($"설정 읽은 시각: {catalog.LoadedAt:yyyy-MM-dd HH:mm:ss} · 사용 가능 에이전트 {catalog.Agents.Count}개");
        if (catalog.ReloadError is { } err) sb.AppendLine($"설정 재로드 실패: {err}");
        sb.AppendLine($"modes.json: {(store.ResolvePath() ?? "없음")}");
        return sb.ToString().TrimEnd();
    }
}
