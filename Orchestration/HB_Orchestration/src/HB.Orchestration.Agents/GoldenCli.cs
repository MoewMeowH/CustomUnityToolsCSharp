using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// 골든 세트 회귀 평가 (MCP dll의 --golden). 서버를 띄우지 않고 실행·채점 후 끝낸다.
///   --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--dir 폴더] [--label 이름] [--yes]
///   --golden score &lt;사례&gt; --from &lt;파일&gt; [--dir 폴더] [--label 이름]
/// run은 --yes가 없으면 사례·에이전트·입력 크기만 보여 주고 외부 호출 없이 멈춘다.
/// 종료 코드: 0 성공 / 1 일부 사례 실패(호출 실패·지적을 못 읽음) / 2 사용법·사례 형식 오류(실행 전 거절).
/// </summary>
public static class GoldenCli
{
    public const string Flag = "--golden";

    /// <summary>호출 기록의 Tool 값 (대시보드·비용 집계에서 run_mode와 구분).</summary>
    public const string Tool = "golden";

    private const string Usage =
        "사용법: HB.Orchestration.Mcp.dll --golden run [사례|all] [--mode 모드] [--agents from=to,...] [--dir 폴더] [--label 이름] [--yes]\n" +
        "        HB.Orchestration.Mcp.dll --golden score <사례> --from <파일> [--dir 폴더] [--label 이름]";

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한국어를 그대로 (사람이 직접 연다)
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal sealed record Args(string Verb, string Which, string? Mode, IReadOnlyList<string>? Agents, string? Dir, string? Label, string? From, bool Yes);

    internal static (Args? Parsed, string? Error) Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != Flag || args[1] is not ("run" or "score")) return (null, null);
        string? which = null, mode = null, dir = null, label = null, from = null;
        List<string>? agents = null;
        var yes = false;

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
                    case "--dir": dir = Next(); break;
                    case "--label": label = Next(); break;
                    case "--from": from = Next(); break;
                    case "--yes": yes = true; break;
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
        if (verb == "score" && (yes || agents is not null || mode is not null)) return (null, "score는 --yes · --agents · --mode를 받지 않습니다 (외부 호출 없음).");
        if (label is not null && label.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))) return (null, "--label은 글자·숫자·_-. 만 씁니다.");
        return (new Args(verb, which!, mode, agents, dir, label, from, yes), null);
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
        // 실행 전 확인: 모드·에이전트·구조화 출력·입력 상한·비용 상한 오류는 여기서 전부 거절한다 (토큰 0).
        logPath ??= runner.LogPath; // 실제로 기록하는 파일에서 provenance를 찾는다
        var plans = new List<(GoldenCase Case, string Mode, (string Role, string Agent, string Model) Step)>();
        foreach (var c in cases)
        {
            var mode = a.Mode ?? c.Info.Mode;
            var (step, previewError) = runner.PreviewFixedPrompt(mode, a.Agents, c.Input.Length);
            if (previewError is not null)
            {
                error.WriteLine($"[{c.Info.Id}] {previewError}");
                return 2;
            }
            plans.Add((c, mode, step!.Value));
        }

        output.WriteLine($"[골든 실행 계획] 사례 {plans.Count}개 · 폴더 {root}");
        foreach (var (c, mode, step) in plans)
            output.WriteLine($"  {c.Info.Id} ({c.Info.Kind}) · {mode}: {step.Role}@{step.Agent}({step.Model}) · 입력 {c.Input.Length:N0}자 · 기대 {c.Expected.Count} · 함정 {c.Traps.Count}");
        var chars = plans.Sum(p => p.Case.Input.Length);
        output.WriteLine($"  입력 합계 {chars:N0}자 (시스템 프롬프트 제외). 입력 토큰 추정 ≈ {chars / 3:N0} (글자 수 ÷ 3, 대략값)");
        foreach (var model in plans.Select(p => p.Step.Model).Distinct())
        {
            if (!options.Pricing.Models.TryGetValue(model, out var price)) { output.WriteLine($"  {model}: 단가 없음"); continue; }
            var modelChars = plans.Where(p => p.Step.Model == model).Sum(p => p.Case.Input.Length);
            output.WriteLine($"  {model}: 입력만 ≈ ${modelChars / 3 / 1_000_000m * price.InputPerMillion:0.000} (출력 단가 ${price.OutputPerMillion}/1M은 응답 길이에 따라 더해짐)");
        }

        if (!a.Yes)
        {
            output.WriteLine("외부 호출 없이 멈췄습니다. 실행하려면 같은 명령에 --yes를 붙이세요.");
            return 0;
        }

        // 사례마다 결과 파일에 바로 붙인다 (중간에 실패·취소돼도 이미 비용을 쓴 사례의 점수는 남게).
        var label = a.Label ?? string.Join("+", plans.Select(p => p.Step.Agent).Distinct());
        string? path = null;
        void Save(ResultRecord line) => path = AppendResults(root, label, [line], error) ?? path;

        var scores = new List<(GoldenCase, GoldenScore)>();
        var incomplete = new List<string>();
        var failed = false;
        foreach (var (c, mode, step) in plans)
        {
            if (ct.IsCancellationRequested) { error.WriteLine("[취소] 남은 사례는 돌리지 않았습니다."); failed = true; break; }

            string? runId; StepResult? result; string? runError;
            try { (runId, result, runError) = await runner.RunFixedPromptAsync(mode, c.Input, a.Agents, Tool, $"golden {c.Info.Id}", ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                error.WriteLine($"[취소] {c.Info.Id} 실행 중 취소. 남은 사례는 돌리지 않았습니다.");
                failed = true;
                break;
            }

            var baseLine = new ResultRecord { CaseId = c.Info.Id, Kind = c.Info.Kind, Mode = mode, RunId = runId, Agent = step.Agent, Model = step.Model,
                InputSha256 = Sha(c.Input), KeySha256 = c.KeySha256 };
            if (runError is not null || result is null || !result.Success)
            {
                var why = runError ?? result?.Text ?? "결과 없음";
                error.WriteLine($"[{c.Info.Id}] 실행 실패: {Short(why)}");
                Save(baseLine with { Error = Short(why) });
                failed = true;
                if (runError is not null && runError.StartsWith("[오늘", StringComparison.Ordinal)) break; // 비용·토큰 상한: 나머지도 같은 이유로 거절된다
                continue;
            }

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
                Mode = mode, RunId = runId, Agent = result.Agent, Model = record?.Model ?? step.Model, OutputState = structured.State,
                InputTokens = result.InputTokens, OutputTokens = result.OutputTokens, ElapsedMs = result.ElapsedMs,
                Source = "run", Provenance = record?.Provenance,
            });
        }

        Summary(output, scores, path);
        // 잘린 출력·자료 부족은 점수가 낮게 나올 수 있다 — 합계에 넣되 따로 알린다 (비교할 때 제외할지는 사람이 판단).
        if (incomplete.Count > 0) output.WriteLine($"출력 상태가 complete가 아닌 사례 {incomplete.Count}개 (합계에 포함): {string.Join(", ", incomplete)}");
        return failed ? 1 : 0;
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
        }
        o.WriteLine("자동 짝은 틀릴 수 있습니다. 근거(강도·파일·키워드)를 보고 확인하세요. 줄만 맞은 짝은 찾음에 세지 않습니다 (맞는 지적이면 정답지 키워드를 보강).");
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
    }

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

    private static CallRecord? FindRecord(string runId, string? logPath)
    {
        try
        {
            return CallLog.ReadSince(DateTimeOffset.Now.AddHours(-1), logPath).LastOrDefault(r => r.RunId == runId && r.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // provenance는 부가 정보. 없으면 결과 줄에서 빠진다.
        }
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
