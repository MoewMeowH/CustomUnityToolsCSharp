using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HB.Orchestration.Shared;
using PlanStep = (string Role, string Agent, string Model, HB.Orchestration.Shared.GuidelinesProvenance Guidelines, string? Effort);

namespace HB.Orchestration.Agents;

/// <summary>
/// 골든 세트 회귀 평가 (MCP dll의 --golden). 서버를 띄우지 않고 실행·채점 후 끝낸다.
///   --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--dir 폴더] [--label 이름] [--no-guidelines] [--yes]
///   --golden score &lt;사례&gt; --from &lt;파일&gt; [--dir 폴더] [--label 이름]
/// run은 --yes가 없으면 사례·에이전트·입력 크기만 보여 주고 외부 호출 없이 멈춘다.
/// 지침을 쓰는 역할인데 지침 파일을 못 찾으면 실행 전 거절한다. --no-guidelines는 파일이 있어도 지침을 빼고 돈다 (지침 있음·없음 비교용).
/// 종료 코드: 0 성공 / 1 일부 사례 실패(호출 실패·지적을 못 읽음) / 2 사용법·사례 형식 오류(실행 전 거절) /
/// 3 크레딧·한도 소진(Quota)으로 남은 사례를 멈춤 — 충전 뒤 작은 호출 1회로 확인하고 다시 돈다.
/// </summary>
public static class GoldenCli
{
    public const string Flag = "--golden";

    /// <summary>호출 기록의 Tool 값 (대시보드·비용 집계에서 run_mode와 구분).</summary>
    public const string Tool = "golden";

    private const string Usage =
        "사용법: HB.Orchestration.Mcp.dll --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--reasoning 에이전트=강도,...] [--dir 폴더] [--label 이름] [--no-guidelines] [--yes]\n" +
        "        HB.Orchestration.Mcp.dll --golden score <사례> --from <파일> [--dir 폴더] [--label 이름]";

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한국어를 그대로 (사람이 직접 연다)
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal sealed record Args(string Verb, string Which, string? Mode, IReadOnlyList<string>? Agents, string? Dir, string? Label, string? From, bool Yes,
        bool NoGuidelines = false, IReadOnlyList<string>? Reasoning = null);

    internal static (Args? Parsed, string? Error) Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag || args[1] is not ("run" or "score")) return (null, null);
        string? which = null, mode = null, dir = null, label = null, from = null;
        List<string>? agents = null, reasoning = null;
        bool yes = false, noGuidelines = false;

        for (var i = 2; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new FormatException($"{args[i]} 뒤에 값이 없습니다.");
            try
            {
                switch (args[i])
                {
                    case "--mode": mode = Next(); break;
                    case "--agents": (agents ??= []).AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--reasoning": (reasoning ??= []).AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--dir": dir = Next(); break;
                    case "--label": label = Next(); break;
                    case "--from": from = Next(); break;
                    case "--yes": yes = true; break;
                    case "--no-guidelines": noGuidelines = true; break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal)) return (null, $"알 수 없는 옵션 {args[i]}");
                        if (which is not null) return (null, $"사례는 하나만 (또는 all): '{which}', '{args[i]}'");
                        which = args[i];
                        break;
                }
            }
            catch (FormatException ex) { return (null, ex.Message); }
        }

        var verb = args[1];
        if (verb == "run") which ??= "all";
        if (verb == "score" && (which is null or "all" || from is null)) return (null, "score는 사례 하나와 --from <파일>이 필요합니다.");
        if (verb == "score" && (yes || agents is not null || reasoning is not null || mode is not null || noGuidelines))
            return (null, "score는 --yes · --agents · --reasoning · --mode · --no-guidelines를 받지 않습니다 (외부 호출 없음).");
        if (label is not null && label.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))) return (null, "--label은 글자·숫자·_-. 만 씁니다.");
        return (new Args(verb, which!, mode, agents, dir, label, from, yes, noGuidelines, reasoning), null);
    }

    /// <param name="runnerFactory">run에서만 부른다 (score는 에이전트·설정이 필요 없음).</param>
    /// <param name="logPath">호출 기록 파일 (결과에 provenance를 붙일 때 읽음). null이면 기본 경로.</param>
    public static async Task<int> RunAsync(string[] args, OrchestrationOptions options, Func<ModeRunner> runnerFactory, string? logPath,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        var (a, parseError) = Parse(args);
        if (a is null)
        {
            if (parseError is not null) error.WriteLine($"[오류] {parseError}");
            error.WriteLine(Usage);
            return 2;
        }

        var root = a.Dir ?? options.Golden.Folder;
        if (string.IsNullOrWhiteSpace(root))
        {
            error.WriteLine("[오류] 골든 폴더가 없습니다. --dir를 주거나 appsettings.json의 Orchestration:Golden:Folder를 채우세요.");
            return 2;
        }

        var (cases, caseErrors) = GoldenCaseLoader.LoadMany(root, a.Which);
        if (caseErrors.Count > 0)
        {
            // 사례 하나라도 틀리면 아무것도 돌리지 않는다 (일부만 돌린 점수가 전체처럼 보이지 않게).
            foreach (var e in caseErrors) error.WriteLine($"[사례 형식 오류] {e}");
            return 2;
        }

        if (a.Verb == "score" && cases[0].Info.IsCrash)
        {
            error.WriteLine($"[오류] score는 리뷰 사례만 됩니다 ('{cases[0].Info.Id}'는 crash — 검색 기록이 필요해 run으로만 채점).");
            return 2;
        }

        return a.Verb == "score"
            ? ScoreFile(a, root, cases[0], output, error)
            : await RunCasesAsync(a, root, cases, options, runnerFactory(), logPath, output, error, ct);
    }

    private static int ScoreFile(Args a, string root, GoldenCase c, TextWriter output, TextWriter error)
    {
        string text;
        try { text = GuidelinesLoader.DecodeLikeReadAllText(File.ReadAllBytes(a.From!)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"[오류] {a.From}: {ex.Message}");
            return 2;
        }

        var (findings, readError) = GoldenScorer.ReadFindings(text);
        if (readError is not null)
        {
            error.WriteLine($"[{c.Info.Id}] {readError}");
            return 1;
        }

        var score = GoldenScorer.Score(c, findings);
        Print(output, c, score, findings);
        var line = ResultLine(c, score, findings) with { Source = $"file:{Path.GetFileName(a.From)}" };
        var path = AppendResults(root, a.Label ?? "score", [line], error);
        Summary(output, [(c, score)], path);
        return 0;
    }

    private static async Task<int> RunCasesAsync(Args a, string root, List<GoldenCase> cases, OrchestrationOptions options, ModeRunner runner,
        string? logPath, TextWriter output, TextWriter error, CancellationToken ct)
    {
        // 실행 전 확인: 모드·에이전트·구조화 출력·입력 상한 오류는 여기서 전부 거절한다 (토큰 0).
        logPath ??= runner.LogPath; // 실제로 기록하는 파일에서 provenance를 찾는다
        // 리뷰 사례는 단계 하나(구조화 출력), crash 사례는 모드 전체(단계 여럿, 자유 형식 + 웹검색 기록으로 채점).
        var plans = new List<(GoldenCase Case, string Mode, IReadOnlyList<PlanStep> Steps)>();
        foreach (var c in cases)
        {
            var mode = a.Mode ?? c.Info.Mode;
            IReadOnlyList<PlanStep>? steps; string? previewError;
            if (c.Info.IsCrash) (steps, previewError) = runner.PreviewFixedMode(mode, a.Agents, c.Input.Length, a.NoGuidelines, requireSearchLast: true, a.Reasoning);
            else
            {
                var (step, e) = runner.PreviewFixedPrompt(mode, a.Agents, c.Input.Length, a.NoGuidelines, a.Reasoning);
                (steps, previewError) = (step is { } s ? [s] : null, e);
            }
            if (previewError is not null)
            {
                error.WriteLine($"[{c.Info.Id}] {previewError}");
                return 2;
            }
            plans.Add((c, mode, steps!));
        }

        output.WriteLine($"[골든 실행 계획] 사례 {plans.Count}개 · 폴더 {root}");
        foreach (var (c, mode, steps) in plans)
            output.WriteLine($"  {c.Info.Id} ({c.Info.Kind}) · {mode}: {string.Join(" → ", steps.Select(s => $"{s.Role}@{s.Agent}({s.Model}{(s.Effort is null ? "" : $", 추론 {s.Effort}")}) [{GuidelinesText(s.Guidelines)}]"))} · 입력 {c.Input.Length:N0}자 · 기대 {c.Expected.Count} · 함정 {c.Traps.Count}");
        var chars = plans.Sum(p => p.Case.Input.Length);
        output.WriteLine($"  입력 합계 {chars:N0}자 (시스템 프롬프트 제외). 입력 토큰 추정 ≈ {chars / 3:N0} (글자 수 ÷ 3, 대략값)");
        foreach (var model in plans.SelectMany(p => p.Steps).Select(s => s.Model).Distinct())
        {
            if (!options.Pricing.Models.TryGetValue(model, out var price)) { output.WriteLine($"  {model}: 단가 없음"); continue; }
            var modelChars = plans.Sum(p => p.Steps.Count(s => s.Model == model) * p.Case.Input.Length);
            output.WriteLine($"  {model}: 입력만 ≈ ${modelChars / 3 / 1_000_000m * price.InputPerMillion:0.000} (출력 단가 ${price.OutputPerMillion}/1M은 응답 길이에 따라 더해짐)");
        }
        if (plans.Any(p => p.Steps.Count > 1))
            output.WriteLine("  다단계(crash) 사례: 뒤 단계는 앞 단계 출력과 웹검색 결과를 더 받으므로 실제 입력은 위 추정보다 크다.");

        // 지침을 쓰는 역할인데 파일을 못 찾았으면 실제 run_mode와 다른 조건이다 (2026-10-19: 셸 현재 폴더가 프로젝트 루트가 아니어서 5회가 지침 없이 돎).
        // 계획은 먼저 보인 뒤 거절한다 (미리보기에서도 사례·비용을 볼 수 있게). --no-guidelines면 파일이 있어도 빼므로 이 경우가 생기지 않는다.
        var missing = plans.Where(p => p.Steps.Any(s => s.Guidelines.Reason == GuidelinesProvenance.ReasonNoFile)).Select(p => p.Case.Info.Id).ToList();
        if (missing.Count > 0)
        {
            error.WriteLine($"[리뷰 지침 없음] 지침을 쓰는 역할인데 지침 파일을 찾지 못했습니다: {string.Join(", ", missing)}. " +
                            $"현재 폴더 {Environment.CurrentDirectory}에 REVIEW_GUIDELINES.md가 없고 REVIEW_GUIDELINES_PATH · Orchestration:GuidelinesPath도 없거나 파일이 없습니다. " +
                            $"프로젝트 루트에서 실행하거나 경로를 지정하세요. 일부러 지침 없이 비교할 때만 --no-guidelines (파일이 있어도 뺌).");
            return 2;
        }

        if (!a.Yes)
        {
            output.WriteLine("외부 호출 없이 멈췄습니다. 실행하려면 같은 명령에 --yes를 붙이세요.");
            return 0;
        }

        // 사례마다 결과 파일에 바로 붙인다 (중간에 실패·취소돼도 이미 비용을 쓴 사례의 점수는 남게).
        var label = a.Label ?? string.Join("+", plans.SelectMany(p => p.Steps).Select(s => s.Agent).Distinct());
        string? path = null;
        void Save(ResultRecord line) => path = AppendResults(root, label, [line], error) ?? path;

        var scores = new List<(GoldenCase, GoldenScore)>();
        var crashScores = new List<(GoldenCase, GoldenCrashScore)>();
        var incomplete = new List<string>();
        var failed = false;
        var quota = false;
        string? ProviderOf(string agent) => options.Agents.FirstOrDefault(x => x.Name == agent)?.Provider;
        // 실행 시작 때 한 번만 읽는다 (사례마다 7일치 기록을 다시 읽지 않게). 실행 중 Quota가 나오면 바로 멈추고,
        // 리뷰 사례가 성공하면 그 제공사는 풀어 준다 (충전 뒤 확인 호출이 같은 실행 안에 있는 경우).
        var exhaustedProviders = QuotaProviders(logPath);
        foreach (var (c, mode, steps) in plans)
        {
            if (ct.IsCancellationRequested) { error.WriteLine("[취소] 남은 사례는 돌리지 않았습니다."); failed = true; break; }

            if (c.Info.IsCrash)
            {
                var (crashScore, stop, crashQuota) = await RunCrashCaseAsync(a, c, mode, steps, runner, logPath, ProviderOf, exhaustedProviders, Save, output, error, ct);
                if (crashScore is not null) crashScores.Add((c, crashScore));
                else failed = true;
                quota |= crashQuota;
                if (stop) break;
                continue;
            }

            var step = steps[0];
            string? runId; StepResult? result; string? runError;
            try { (runId, result, runError) = await runner.RunFixedPromptAsync(mode, c.Input, a.Agents, Tool, $"golden {c.Info.Id}", ct, a.NoGuidelines, step.Guidelines, a.Reasoning); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                error.WriteLine($"[취소] {c.Info.Id} 실행 중 취소. 남은 사례는 돌리지 않았습니다.");
                failed = true;
                break;
            }

            var baseLine = new ResultRecord { CaseId = c.Info.Id, Kind = c.Info.Kind, Mode = mode, RunId = runId, Agent = step.Agent, Model = step.Model,
                ReasoningEffort = step.Effort, InputSha256 = Sha(c.Input), KeySha256 = c.KeySha256,
                GuidelinesAttached = step.Guidelines.Attached, GuidelinesSha256 = step.Guidelines.Sha256, GuidelinesReason = step.Guidelines.Reason };
            if (runError is not null || result is null || !result.Success)
            {
                var why = runError ?? result?.Text ?? "결과 없음";
                error.WriteLine($"[{c.Info.Id}] 실행 실패: {Short(why)}");
                Save(baseLine with { Error = Short(why) });
                failed = true;
                if (IsQuota(why)) { quota = true; QuotaStopNotice(error, [ProviderOf(step.Agent) ?? step.Agent]); break; }
                if (StopsRemaining(runError)) break;
                continue;
            }

            if (ProviderOf(step.Agent) is { } okProvider) exhaustedProviders.Remove(okProvider);
            var structured = result.Structured;
            var record = FindRecord(runId!, logPath);
            if (structured is null || !structured.Parsed)
            {
                error.WriteLine($"[{c.Info.Id}] 구조화 출력이 없어 채점하지 못했습니다 (runId {runId}, {structured?.Error ?? "Output 없는 역할"}).");
                Save(baseLine with { Agent = result.Agent, Error = "구조화 출력 없음", Provenance = record?.Provenance });
                failed = true;
                continue;
            }

            var findings = structured.Findings.Select(GoldenFinding.From).ToList();
            var score = GoldenScorer.Score(c, findings);
            Print(output, c, score, findings);
            if (structured.State != OutputStates.Complete)
            {
                output.WriteLine($"  (출력 상태 {structured.State}{(structured.Error is null ? "" : $": {structured.Error}")})");
                incomplete.Add($"{c.Info.Id}({structured.State})");
            }
            scores.Add((c, score));
            Save(ResultLine(c, score, findings) with
            {
                Mode = mode, RunId = runId, Agent = result.Agent, Model = record?.Model ?? step.Model, ReasoningEffort = record is not null ? record.ReasoningEffort : step.Effort, // 기록이 있으면 "보내지 않음"(null)도 그대로
                OutputState = structured.State,
                InputTokens = result.InputTokens, OutputTokens = result.OutputTokens, ElapsedMs = result.ElapsedMs,
                Source = "run", Provenance = record?.Provenance,
                GuidelinesAttached = baseLine.GuidelinesAttached, GuidelinesSha256 = baseLine.GuidelinesSha256, GuidelinesReason = baseLine.GuidelinesReason,
            });
        }

        Summary(output, scores, path: null);
        CrashSummary(output, crashScores);
        if (path is not null) output.WriteLine($"결과: {path}");
        // 잘린 출력·자료 부족은 점수가 낮게 나올 수 있다 — 합계에 넣되 따로 알린다 (비교할 때 제외할지는 사람이 판단).
        if (incomplete.Count > 0) output.WriteLine($"출력 상태가 complete가 아닌 사례 {incomplete.Count}개 (합계에 포함): {string.Join(", ", incomplete)}");
        return quota ? 3 : failed ? 1 : 0;
    }

    /// <summary>크레딧·한도 소진 실패 문구인지 (AgentRunner가 붙이는 머리, 재시도 끝에 감싸져도 찾는다).</summary>
    private static bool IsQuota(string? text) => text is not null && text.Contains(AgentRunner.QuotaFailurePrefix, StringComparison.Ordinal);

    /// <summary>멈춘 이유와 푸는 방법. 차단은 그 제공사의 성공 기록으로만 풀리므로 제공사를 밝힌다.</summary>
    private static void QuotaStopNotice(TextWriter error, IEnumerable<string> providers) =>
        error.WriteLine($"[Quota] 제공사 {string.Join(", ", providers)} 크레딧·한도 소진 — 남은 사례는 돌리지 않았습니다. " +
                        "충전 뒤 그 제공사로 가는 가장 작은 호출 1회(그 제공사 에이전트의 리뷰 사례 하나, 또는 --agents로 그 제공사에 연결한 리뷰 사례)로 " +
                        "성공을 확인하고 다시 실행하세요 (다단계 사례의 사전 차단은 그 제공사의 성공 기록이 생겨야 풀립니다).");

    /// <summary>
    /// 최근 7일 호출 기록에서 제공사마다 마지막 "판정 가능한" 기록(성공 또는 Quota)이 Quota인 제공사들. 다단계 사례에서 뒤 단계 제공사가 소진 상태면
    /// 앞 단계(다른 제공사)를 돌려 비용만 쓰게 되므로 미리 막는다 (2026-10-19: luna 크레딧 소진 중 argon 분류만 24회 성공).
    /// Timeout·Network·Canceled 같은 실패는 충전 여부를 알려 주지 않으므로 건너뛴다. 기록을 못 읽으면 막지 않는다.
    /// </summary>
    internal static HashSet<string> QuotaProviders(string? logPath)
    {
        var exhausted = new HashSet<string>(StringComparer.Ordinal);
        var decided = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // ReadSince는 파일 끝(최신)부터 읽어 최신 기록이 앞에 온다.
            foreach (var r in CallLog.ReadSince(DateTimeOffset.Now.AddDays(-7), logPath))
            {
                if (r.Provider is not { } provider || decided.Contains(provider)) continue;
                var isQuota = r.ErrorKind == nameof(ErrorKind.Quota);
                if (!r.Success && !isQuota) continue;
                decided.Add(provider);
                if (isQuota) exhausted.Add(provider);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return exhausted;
    }

    /// <summary>지침이 실행 전 확인과 달라짐: 남은 사례도 다른 조건으로 돈다. (하루 비용·토큰 상한은 없앴다 — 계획서 2026-10-20_하루상한-제거)</summary>
    private static bool StopsRemaining(string? runError) =>
        runError is not null && runError.StartsWith(ModeRunner.GuidelinesChanged, StringComparison.Ordinal);

    /// <summary>
    /// crash 사례 하나: 모드 전체를 돌리고 분류(1단계) · 검색(마지막 단계) 본문과 검색 기록으로 채점한다.
    /// 점수는 실패면 null, Stop은 지침 변경·Quota처럼 남은 사례도 같은 이유로 막히는 경우, Quota는 크레딧·한도 소진.
    /// 실행 전에 단계마다 제공사의 직전 기록을 보고, 하나라도 Quota면 어느 단계도 호출하지 않는다.
    /// </summary>
    private static async Task<(GoldenCrashScore? Score, bool Stop, bool Quota)> RunCrashCaseAsync(Args a, GoldenCase c, string mode, IReadOnlyList<PlanStep> steps,
        ModeRunner runner, string? logPath, Func<string, string?> providerOf, IReadOnlySet<string> exhaustedProviders, Action<ResultRecord> save,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        var baseLine = new ResultRecord
        {
            CaseId = c.Info.Id, Kind = c.Info.Kind, Mode = mode,
            Agent = string.Join(" → ", steps.Select(s => s.Agent)), Model = string.Join(" → ", steps.Select(s => s.Model)),
            ReasoningEffort = steps.Any(s => s.Effort is not null) ? string.Join(" → ", steps.Select(s => s.Effort ?? "-")) : null,
            InputSha256 = Sha(c.Input), KeySha256 = c.KeySha256,
            GuidelinesAttached = steps[0].Guidelines.Attached, GuidelinesSha256 = steps[0].Guidelines.Sha256, GuidelinesReason = steps[0].Guidelines.Reason,
        };

        var exhausted = steps.Select(s => providerOf(s.Agent)).OfType<string>().Distinct().Where(exhaustedProviders.Contains).ToList();
        if (exhausted.Count > 0)
        {
            var why = $"제공사 {string.Join(", ", exhausted)}의 직전 호출이 Quota(크레딧·한도 소진) — 앞 단계도 호출하지 않음";
            error.WriteLine($"[{c.Info.Id}] {why}.");
            save(baseLine with { Error = why });
            QuotaStopNotice(error, exhausted);
            return (null, true, true);
        }

        string? runId; List<StepResult>? results; string? runError;
        try
        {
            (runId, results, runError) = await runner.RunFixedModeAsync(mode, c.Input, a.Agents, Tool, $"golden {c.Info.Id}", ct,
                a.NoGuidelines, [.. steps.Select(s => (s.Agent, s.Guidelines))], requireSearchLast: true, a.Reasoning);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error.WriteLine($"[취소] {c.Info.Id} 실행 중 취소. 남은 사례는 돌리지 않았습니다.");
            return (null, true, false);
        }

        if (runError is not null || results is null || results.Count < steps.Count || results.Any(r => !r.Success))
        {
            var why = runError ?? results?.FirstOrDefault(r => !r.Success)?.Text ?? $"단계 {results?.Count ?? 0}/{steps.Count}만 실행";
            error.WriteLine($"[{c.Info.Id}] 실행 실패: {Short(why)}");
            save(baseLine with { RunId = runId, Error = Short(why) });
            if (IsQuota(why))
            {
                var failedAgent = results?.FirstOrDefault(r => !r.Success)?.Agent;
                QuotaStopNotice(error, [failedAgent is null ? "?" : providerOf(failedAgent) ?? failedAgent]);
                return (null, true, true);
            }
            return (null, StopsRemaining(runError), false);
        }

        // 검색 기록은 채점의 필수 입력이다: 못 읽으면 "검색 0회"와 구분되지 않으므로 점수 대신 실패로 남긴다.
        var searchRecord = FindRecord(runId!, logPath, step: results.Count);
        if (searchRecord?.WebSearch is null)
        {
            var why = searchRecord is null ? "검색 단계 호출 기록을 찾지 못함" : "검색 단계 호출 기록에 WebSearch가 없음";
            error.WriteLine($"[{c.Info.Id}] 채점 실패: {why} (runId {runId}, 기록 {logPath ?? "기본 경로"}).");
            save(baseLine with { RunId = runId, Error = why, Provenance = searchRecord?.Provenance });
            return (null, false, false);
        }
        var score = GoldenCrashScorer.Score(c, results[0].Text, results[^1].Text, searchRecord.WebSearch);
        PrintCrash(output, c, score);
        // 추론 강도는 실행 전 계획이 아니라 단계별 호출 기록의 실제 값 (그 사이 설정이 다시 읽혔을 수 있다)
        var sentEfforts = Enumerable.Range(1, results.Count).Select(i => (i == results.Count ? searchRecord : FindRecord(runId!, logPath, step: i))?.ReasoningEffort).ToList();
        save(baseLine with
        {
            RunId = runId, Source = "run",
            ReasoningEffort = sentEfforts.Any(e => e is not null) ? string.Join(" → ", sentEfforts.Select(e => e ?? "-")) : null,
            Expected = score.ExpectedCount, Found = score.Found.Count, TrapHits = score.TrapHits.Count,
            Pairs = score.Found, Weak = score.KeywordOnly, Missed = score.Missed, Traps = score.TrapHits,
            InputTokens = results.Sum(r => r.InputTokens ?? 0), OutputTokens = results.Sum(r => r.OutputTokens ?? 0), ElapsedMs = results.Sum(r => r.ElapsedMs),
            SearchCalls = score.SearchCalls, SearchSources = score.SourceCount, UnverifiedUrls = score.Unverified,
            Provenance = searchRecord.Provenance,
        });
        return (score, false, false);
    }

    private static void PrintCrash(TextWriter o, GoldenCase c, GoldenCrashScore s)
    {
        o.WriteLine();
        o.WriteLine($"[{c.Info.Id}] 찾음 {s.Found.Count}/{s.ExpectedCount} · 확인 필요 {s.KeywordOnly.Count} · 함정 걸림 {s.TrapHits.Count} · 검색 {s.SearchCalls}회 · 참고 URL {s.SourceCount}개 · 검색 결과에 없는 URL {s.Unverified.Count}개");
        foreach (var e in c.Expected)
        {
            var pair = s.Found.FirstOrDefault(p => p.ItemId == e.Id);
            var weak = s.KeywordOnly.FirstOrDefault(p => p.ItemId == e.Id);
            o.WriteLine(pair is not null ? $"  찾음 {e.Id} [{e.Step}] ({pair.Basis})"
                : weak is not null ? $"  놓침(확인 필요) {e.Id} [{e.Step}] ({weak.Basis})"
                : $"  놓침 {e.Id} [{e.Step}]: {e.Summary}");
        }
        foreach (var t in s.TrapHits)
        {
            var trap = c.Traps.First(x => x.Id == t.ItemId);
            o.WriteLine($"  함정 걸림 — 검토 필요 {t.ItemId} [{t.FindingId}] ({t.Basis})");
            if (trap.Why is not null) o.WriteLine($"      틀린 이유: {trap.Why}");
        }
        foreach (var u in s.Unverified.Take(5)) o.WriteLine($"  검색 결과에 없는 URL: {u}");
    }

    /// <summary>crash 합계는 리뷰 재현율과 섞지 않고 따로 (단계별 찾음 · 지어낸 URL).</summary>
    private static void CrashSummary(TextWriter o, IReadOnlyList<(GoldenCase Case, GoldenCrashScore Score)> scores)
    {
        if (scores.Count == 0) return;
        int Count(string step, Func<GoldenCrashScore, IReadOnlyList<GoldenPair>> pick) => scores.Sum(s => pick(s.Score).Count(p => s.Case.Expected.First(e => e.Id == p.ItemId).Step == step));
        int Expected(string step) => scores.Sum(s => s.Case.Expected.Count(e => e.Step == step));
        o.WriteLine($"[crash 합계] 분류 찾음 {Count(GoldenItem.StepTriage, x => x.Found)}/{Expected(GoldenItem.StepTriage)} · " +
                    $"검색 찾음 {Count(GoldenItem.StepSearch, x => x.Found)}/{Expected(GoldenItem.StepSearch)} (확인 필요 {scores.Sum(s => s.Score.KeywordOnly.Count)}) · " +
                    $"함정 걸림 {scores.Sum(s => s.Score.TrapHits.Count)} · 검색 결과에 없는 URL {scores.Sum(s => s.Score.Unverified.Count)}개");
        o.WriteLine("crash 채점은 키워드·URL 규칙입니다. 키워드는 부정 문맥에서도 맞으니 근거를 보고 확인하세요. '확인 필요'(정답 URL 없이 내용만 맞음)는 찾음에 세지 않습니다.");
    }

    // ── 출력 ─────────────────────────────────────────────────────────────

    private static void Print(TextWriter o, GoldenCase c, GoldenScore s, IReadOnlyList<GoldenFinding> findings)
    {
        var byId = findings.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        o.WriteLine();
        o.WriteLine($"[{c.Info.Id}] 찾음 {s.Found.Count}/{s.ExpectedCount} · 줄만 맞음 {s.Weak.Count} · 함정 걸림 {s.TrapHits.Count} · 짝 없는 지적 {s.Unmatched.Count} · 지적 {s.FindingCount}개");
        foreach (var e in c.Expected)
        {
            var pair = s.Found.FirstOrDefault(p => p.ItemId == e.Id);
            var weak = s.Weak.FirstOrDefault(p => p.ItemId == e.Id);
            o.WriteLine(pair is not null
                ? $"  찾음 {e.Id} ← {pair.FindingId} (강도 {pair.Strength}: {pair.Basis}) {Short(byId[pair.FindingId].Problem)}"
                : weak is not null
                ? $"  놓침(줄만 맞음 — 확인 필요) {e.Id} ← {weak.FindingId} ({weak.Basis}) {Short(byId[weak.FindingId].Problem)}"
                : $"  놓침 {e.Id}: {e.Summary}");
        }
        foreach (var t in s.TrapHits)
        {
            var trap = c.Traps.First(x => x.Id == t.ItemId);
            o.WriteLine($"  함정 걸림 — 검토 필요 {t.ItemId} ← {t.FindingId} (강도 {t.Strength}: {t.Basis}) {Short(byId[t.FindingId].Problem)}");
            if (trap.Why is not null) o.WriteLine($"      과거 기각 이유: {trap.Why}");
        }
        foreach (var id in s.Unmatched)
        {
            var f = byId[id];
            o.WriteLine($"  짝 없음 {id} [{f.Severity}] {f.Location} — {Short(f.Problem)}");
        }
    }

    private static void Summary(TextWriter o, IReadOnlyList<(GoldenCase Case, GoldenScore Score)> scores, string? path)
    {
        o.WriteLine();
        if (scores.Count > 0)
        {
            var expected = scores.Sum(s => s.Score.ExpectedCount);
            var found = scores.Sum(s => s.Score.Found.Count);
            o.WriteLine($"[합계] 찾음 {found}/{expected} ({(expected == 0 ? 0 : 100.0 * found / expected):0}%) · 줄만 맞음 {scores.Sum(s => s.Score.Weak.Count)} · 함정 걸림 {scores.Sum(s => s.Score.TrapHits.Count)} · 짝 없는 지적 {scores.Sum(s => s.Score.Unmatched.Count)}");
            foreach (var kind in scores.GroupBy(s => s.Case.Info.Kind))
                o.WriteLine($"  {kind.Key}: 찾음 {kind.Sum(s => s.Score.Found.Count)}/{kind.Sum(s => s.Score.ExpectedCount)}");
            o.WriteLine("자동 짝은 틀릴 수 있습니다. 근거(강도·파일·키워드)를 보고 확인하세요. 줄만 맞은 짝은 찾음에 세지 않습니다 (맞는 지적이면 정답지 키워드를 보강).");
        }
        if (path is not null) o.WriteLine($"결과: {path}");
    }

    private static string Short(string s)
    {
        var line = s.ReplaceLineEndings(" ").Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    // ── 결과 파일 ─────────────────────────────────────────────────────────

    /// <summary>results/&lt;날짜&gt;_&lt;라벨&gt;.jsonl의 한 줄 (사례 하나).</summary>
    internal sealed record ResultRecord
    {
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
        public required string CaseId { get; init; }
        public required string Kind { get; init; }
        public string? Mode { get; init; }
        public string? Source { get; init; }
        public string? RunId { get; init; }
        public string? Agent { get; init; }
        public string? Model { get; init; }

        /// <summary>보낸 추론 강도 (crash는 단계별 "a → b", 보내지 않은 단계는 "-"). 하나도 보내지 않았으면 null.</summary>
        public string? ReasoningEffort { get; init; }
        public string? OutputState { get; init; }
        public string? Error { get; init; }
        public int? Expected { get; init; }
        public int? Found { get; init; }
        public int? TrapHits { get; init; }
        public int? Unmatched { get; init; }
        public int? Findings { get; init; }
        public IReadOnlyList<GoldenPair>? Pairs { get; init; }

        /// <summary>줄만 맞은 짝 (찾음에 안 셈).</summary>
        public IReadOnlyList<GoldenPair>? Weak { get; init; }
        public IReadOnlyList<string>? Missed { get; init; }
        public IReadOnlyList<GoldenPair>? Traps { get; init; }
        public IReadOnlyList<GoldenFinding>? UnmatchedFindings { get; init; }
        public long? InputTokens { get; init; }
        public long? OutputTokens { get; init; }
        public long? ElapsedMs { get; init; }

        /// <summary>사례 입력 원문(input.md) 해시. 같은 사례인데 값이 다르면 입력이 바뀐 것이다 (점수 비교 불가).</summary>
        public string? InputSha256 { get; init; }

        /// <summary>정답지(expected.json + traps.json) 해시. 값이 다르면 정답지가 바뀐 것이다 (점수 차이가 모델·프롬프트 때문이 아닐 수 있음).</summary>
        public string? KeySha256 { get; init; }

        /// <summary>그 호출의 지침·시스템 프롬프트·modes.json 해시 (호출 기록에서 가져옴).</summary>
        public CallProvenance? Provenance { get; init; }

        /// <summary>
        /// 실행 전 확인 때의 지침 (붙음 여부·원본 해시·안 붙은 사유). Provenance는 호출 기록을 다시 읽어 붙이므로 실패 줄에는 없을 수 있어,
        /// 지침 있음·없음 실행을 결과 파일만으로 구분하려고 따로 남긴다.
        /// </summary>
        public bool? GuidelinesAttached { get; init; }
        public string? GuidelinesSha256 { get; init; }
        public string? GuidelinesReason { get; init; }

        /// <summary>crash 사례: 검색 단계의 검색 수 · 실제로 본 URL 수 · 본문에 있는데 검색 결과에 없는 URL (지어냈을 수 있음).</summary>
        public int? SearchCalls { get; init; }
        public int? SearchSources { get; init; }
        public IReadOnlyList<string>? UnverifiedUrls { get; init; }
    }

    private static string GuidelinesText(GuidelinesProvenance g) => g.Attached ? $"지침 붙음: {g.Path}" : $"지침 없음 ({g.Reason})";

    private static ResultRecord ResultLine(GoldenCase c, GoldenScore s, IReadOnlyList<GoldenFinding> findings)
    {
        var unmatched = s.Unmatched.ToHashSet(StringComparer.Ordinal);
        return new ResultRecord
        {
            CaseId = c.Info.Id, Kind = c.Info.Kind, Mode = c.Info.Mode, InputSha256 = Sha(c.Input), KeySha256 = c.KeySha256,
            Expected = s.ExpectedCount, Found = s.Found.Count, TrapHits = s.TrapHits.Count, Unmatched = s.Unmatched.Count, Findings = s.FindingCount,
            Pairs = s.Found, Weak = s.Weak.Count == 0 ? null : s.Weak, Missed = s.Missed, Traps = s.TrapHits,
            UnmatchedFindings = [.. findings.Where(f => unmatched.Contains(f.Id))],
        };
    }

    private static string? AppendResults(string root, string label, IReadOnlyList<ResultRecord> lines, TextWriter error)
    {
        if (lines.Count == 0) return null;
        try
        {
            var dir = Path.Combine(root, GoldenCaseLoader.ResultsFolder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}_{label}.jsonl");
            var text = string.Concat(lines.Select(l => JsonSerializer.Serialize(l, ResultJson) + "\n"));
            File.AppendAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"[경고] 결과 파일을 쓰지 못했습니다: {ex.Message} (점수는 위 출력과 호출 기록에 있음)");
            return null;
        }
    }

    /// <param name="step">다단계(crash) 실행에서 그 단계의 기록. null이면 마지막 성공 기록.</param>
    private static CallRecord? FindRecord(string runId, string? logPath, int? step = null)
    {
        try
        {
            return CallLog.ReadSince(DateTimeOffset.Now.AddHours(-1), logPath).LastOrDefault(r => r.RunId == runId && r.Success && (step is null || r.Step == step));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // provenance는 부가 정보. 없으면 결과 줄에서 빠진다.
        }
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
