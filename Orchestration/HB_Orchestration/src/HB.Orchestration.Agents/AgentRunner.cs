using System.Diagnostics;
using HB.Orchestration.Shared;
using HB.Orchestration.Agents.Providers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HB.Orchestration.Agents;

/// <summary>기록용 정보. Files는 에이전트에 안 보내는 링크, Context는 실제로 첨부한 코드 범위.</summary>
public sealed record CallMeta(IReadOnlyList<string>? Files, IReadOnlyList<string> Context)
{
    /// <summary>context가 실제로 읽은 추가 루트. 쓰지 않았으면 null (기록에서 필드 생략).</summary>
    public IReadOnlyDictionary<string, string>? ContextRoots { get; init; }

    /// <summary>실행 단위 지침·설정 해시 (단계별 Guidelines 포함). Instructions 해시는 AgentRunner가 채운다.</summary>
    public CallProvenance? Provenance { get; init; }

    /// <summary>지휘자가 모드를 부른 이유. 기록 전용 — 에이전트에게 보내지 않는다.</summary>
    public string? Reason { get; init; }

    /// <summary>지휘자가 결과를 보기 전에 완성한 자체 검토. 기록 전용 — 에이전트에게 보내지 않는다.</summary>
    public string? OwnReview { get; init; }
}

/// <summary>모드의 한 단계 실행 정보.</summary>
public sealed record StepCall(
    string RunId, string Mode, int Step, string Role,
    AgentEntry Agent, string Instructions, string Prompt,
    CallMeta Meta)
{
    /// <summary>응답 검사기 설정. 실행 시점의 appsettings 값을 넘긴다.</summary>
    public ResponseChecksOptions Checks { get; init; } = new();

    /// <summary>호출 기록의 Tool. null이면 Mode (run_mode). 골든 세트 실행은 "golden".</summary>
    public string? Tool { get; init; }

    /// <summary>
    /// 앞 대화 (followUp: 원래 프롬프트 + 원래 응답). 있으면 [History…, User(Prompt)]로 보낸다.
    /// 자동 접두 캐시 제공사(OpenAI·Gemini 호환)는 앞부분이 원래 호출과 같으면 저장 지점 없이도 캐시에 걸릴 수 있다.
    /// 저장 지점을 직접 다는 Context 방식은 쓰지 않는다 (ContextPrefix가 없어 Off) — 원래 호출이 두 블록으로 나갔으면 접두도 바이트 단위로 같지 않다.
    /// 이미지와는 함께 쓰지 않는다.
    /// </summary>
    public IReadOnlyList<ChatMessage>? History { get; init; }

    /// <summary>followUp 기록용 (CallRecord.FollowUpOf · FollowUpRequests · FollowUpSkipped). 보통 호출은 null.</summary>
    public int? FollowUpOf { get; init; }
    public IReadOnlyList<string>? FollowUpRequests { get; init; }
    public IReadOnlyList<string>? FollowUpSkipped { get; init; }

    /// <summary>Prompt의 앞부분 중 첨부 자료 (Prompt가 이 문자열로 시작한다). 첨부가 없거나 이 단계에 입력을 안 보내면 null.</summary>
    public string? ContextPrefix { get; init; }

    /// <summary>모드 설정(ModeDefinition.PromptCache). 있으면 제공사 설정보다 우선한다.</summary>
    public PromptCacheMode? ModePromptCache { get; init; }

    /// <summary>이 단계에 보낼 이미지 (run_mode images). 입력을 받는 단계에만 붙는다. 없으면 null.</summary>
    public IReadOnlyList<AttachedImage>? Images { get; init; }

    /// <summary>이 단계의 웹검색 설정 (modes.json 단계의 WebSearch). 없으면 검색 도구를 주지 않는다.</summary>
    public WebSearchSettings? WebSearch { get; init; }

    /// <summary>역할의 구조화 출력 형식. 있으면 응답을 해석해 지적 ID를 매기고 그린 텍스트를 넘긴다. 없으면 자유 텍스트.</summary>
    public FindingFormat? Output { get; init; }

    /// <summary>구조화 출력 요청 방식 (에이전트 → 제공사 → Prompt). Output이 없으면 의미 없다.</summary>
    public StructuredOutputMode OutputMode => AgentSettings.ResolveStructuredOutput(Agent.Options, Agent.ProviderOptions);

    /// <summary>
    /// 이 호출의 프롬프트 캐시 방식. 제공사가 ProviderDefault(정책 없음)면 그대로 두고, 아니면 모드 설정 → 제공사 설정.
    /// Context인데 첨부가 짧으면 Off (저장 지점은 최소 길이 1,024토큰에 못 미치면 쓰이지 않으므로 블록을 나눌 이유가 없다).
    /// </summary>
    public PromptCacheMode CacheMode
    {
        get
        {
            var provider = Agent.ProviderOptions?.PromptCache ?? PromptCacheMode.ProviderDefault;
            if (provider == PromptCacheMode.ProviderDefault) return provider;
            var mode = ModePromptCache is { } m && m != PromptCacheMode.ProviderDefault ? m : provider;
            if (mode != PromptCacheMode.Context) return mode;
            // 블록을 Prompt[ContextPrefix.Length..]로 자르므로 앞부분이 실제로 일치할 때만 나눈다.
            var usable = ContextPrefix is { Length: >= MinContextCacheChars } prefix && Prompt.StartsWith(prefix, StringComparison.Ordinal);
            return usable ? PromptCacheMode.Context : PromptCacheMode.Off;
        }
    }

    /// <summary>저장 지점을 둘 최소 첨부 길이(문자). 1,024토큰을 코드·한국어 혼합 기준으로 넉넉히 어림한 값.</summary>
    public const int MinContextCacheChars = 3_000;
}

/// <summary>한 단계의 결과. Text(응답 또는 '['로 시작하는 실패 문구)만 다음 단계에 넘어간다.</summary>
public sealed record StepResult(int Step, string Role, string Agent, bool Success, string Text)
{
    /// <summary>실제로 시도한 횟수.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>재시도 전에 실패한 시도들의 종류 (순서대로). 재시도가 없었으면 빈 목록.</summary>
    public IReadOnlyList<string> FailedKinds { get; init; } = [];

    /// <summary>마지막 시도의 토큰 (실패한 시도는 보통 0이라 마지막 시도 값을 쓴다).</summary>
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }

    /// <summary>재시도와 시도 사이 대기를 포함한 단계 전체 시간.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>응답 검사기 경고 ("[경고] …"). 응답은 그대로 전달되고 결과에 한 줄씩 붙는다.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>구조화 출력 해석 결과 (Output이 있는 역할이 성공했을 때). 겹침 후보 계산에 쓴다.</summary>
    public StructuredOutputRecord? Structured { get; init; }

    /// <summary>결과에 붙일 안내 줄: 재시도 알림 + 경고.</summary>
    public IEnumerable<string> Notices => RetryNote is null ? Warnings : Warnings.Prepend(RetryNote);

    /// <summary>재시도가 있었으면 "(재시도 1회 후 성공 · 1회차 Timeout)" 같은 한 줄, 없으면 null.</summary>
    public string? RetryNote => FailedKinds.Count == 0
        ? null
        : $"(재시도 {FailedKinds.Count}회 후 {(Success ? "성공" : "실패")} · " +
          string.Join(", ", FailedKinds.Select((k, i) => $"{i + 1}회차 {k}")) + ")";
}

/// <summary>
/// 에이전트 한 단계 실행 + 호출 기록. 실행은 전부 여기를 거친다.
/// SDK 자동 재시도는 꺼 두고 여기서 직접 재시도한다: 오류 종류로 재시도 여부를 정하고, 시도마다 기록을 한 줄씩 남긴다.
/// </summary>
public sealed class AgentRunner
{
    private readonly ILogger<AgentRunner> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string? _logPath;

    public AgentRunner(ILogger<AgentRunner> logger) : this(logger, Task.Delay, logPath: null) { }

    /// <param name="delay">시도 사이 대기. 테스트에서 실제로 기다리지 않도록 바꿔 끼운다.</param>
    /// <param name="logPath">호출 기록 파일. null이면 <see cref="CallLog.DefaultPath"/>.</param>
    internal AgentRunner(ILogger<AgentRunner> logger, Func<TimeSpan, CancellationToken, Task> delay, string? logPath)
    {
        _logger = logger;
        _delay = delay;
        _logPath = logPath;
    }

    /// <summary>호출 기록 파일 경로. 원문 저장 폴더의 기본값도 여기서 정해진다.</summary>
    public string LogPath => _logPath ?? CallLog.DefaultPath;

    public async Task<StepResult> RunStepAsync(StepCall call, CancellationToken ct)
    {
        var maxAttempts = AgentSettings.ResolveMaxAttempts(call.Agent.Options, call.Agent.ProviderOptions);
        var stepWatch = Stopwatch.StartNew();
        var failedKinds = new List<string>();

        // 에이전트에 넘길 문자열 그대로를 해시한다 (Debate 규칙 등 덧붙인 뒤의 최종본). 시도마다 같으므로 한 번만.
        var provenance = ProvenanceBuilder.WithInstructions(call.Meta.Provenance, call.Instructions, LogPath);
        if (provenance.StoreErrors is { Count: > 0 } storeErrors)
            _logger.LogWarning("[{Mode}#{Step}] 지침·설정 원문 저장 실패 (호출은 계속): {Errors}", call.Mode, call.Step, string.Join(" / ", storeErrors));

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await RunAttemptAsync(call, provenance, attempt, maxAttempts, ct);
            var record = outcome.Record;

            // 기록 실패가 결과 전달을 막으면 안 된다.
            try { await CallLog.AppendAsync(record, _logPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "호출 기록 저장 실패"); }

            string? finalText = null;
            TimeSpan wait = default;
            if (record.Success)
                finalText = outcome.Text;
            else if (outcome.Failure is not { Retryable: true } || attempt >= maxAttempts)
                finalText = FinalFailureText(call, outcome, attempt, maxAttempts);
            else if (RetryClassifier.DelayBeforeNext(outcome.Failure, attempt) is { } next)
                wait = next;
            else
                finalText = RetryAfterTooLongText(outcome); // 서버가 상한보다 긴 대기를 요구하면 기다리지 않는다

            if (finalText is not null)
            {
                return new StepResult(call.Step, call.Role, call.Agent.Name, record.Success, finalText)
                {
                    Attempts = attempt,
                    FailedKinds = failedKinds,
                    InputTokens = record.InputTokens,
                    OutputTokens = record.OutputTokens,
                    ElapsedMs = stepWatch.ElapsedMilliseconds,
                    Warnings = record.Warnings,
                    Structured = record.Structured,
                };
            }

            failedKinds.Add(record.ErrorKind ?? "Error");
            _logger.LogWarning("[{Mode}#{Step}] {Role}@{Agent} 시도 {Attempt}/{Max} 실패({Kind}) → {Wait:0.#}초 후 재시도",
                call.Mode, call.Step, call.Role, call.Agent.Name, attempt, maxAttempts, record.ErrorKind, wait.TotalSeconds);

            // 대기 중 사용자가 취소하면 OperationCanceledException이 그대로 올라간다 (재시도·기록 없음).
            await _delay(wait, ct);
        }
    }

    private sealed record AttemptOutcome(CallRecord Record, string Text, FailureInfo? Failure);

    /// <summary>한 번 호출. 사용자 취소만 예외로 올리고, 나머지 실패는 기록과 분류로 돌려준다.</summary>
    private async Task<AttemptOutcome> RunAttemptAsync(StepCall call, CallProvenance provenance, int attempt, int maxAttempts, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();

        try
        {
            // 역할 프롬프트는 단계마다 다르므로 실행 시점에 MAF 에이전트를 만든다 (가벼운 객체).
            AIAgent agent = call.WebSearch is not null
                ? new ChatClientAgent(call.Agent.Client, instructions: call.Instructions, name: call.Agent.Name, tools: [new HostedWebSearchTool()])
                : call.Output is { } schemaFormat && call.OutputMode == StructuredOutputMode.Schema
                ? new ChatClientAgent(call.Agent.Client, new ChatClientAgentOptions
                {
                    Name = call.Agent.Name,
                    ChatOptions = StructuredRequest.Options(call.Instructions, schemaFormat),
                })
                : new ChatClientAgent(call.Agent.Client, instructions: call.Instructions, name: call.Agent.Name);

            // 호출마다 새 스레드: 이전 대화가 섞이지 않는다 (= 쓸데없는 히스토리 토큰 없음).
            // Context면 첨부 자료와 나머지를 text 블록 둘로 나눠 보낸다. 정책이 첫 블록 끝에 저장 지점을 단다.
            var cacheMode = call.CacheMode;
            using var cacheScope = PromptCacheScope.Enter(cacheMode);
            using var searchScope = WebSearchScope.Enter(call.WebSearch);
            var response = call.History is { Count: > 0 } history
                ? await agent.RunAsync([.. history, new ChatMessage(ChatRole.User, call.Prompt)], cancellationToken: ct)
                : call.Images is { Count: > 0 }
                ? await agent.RunAsync(WithImages(call), cancellationToken: ct)
                : cacheMode == PromptCacheMode.Context
                ? await agent.RunAsync(new ChatMessage(ChatRole.User,
                    [new TextContent(call.ContextPrefix), new TextContent(call.Prompt[call.ContextPrefix!.Length..])]), cancellationToken: ct)
                : await agent.RunAsync(call.Prompt, cancellationToken: ct);
            var text = response.Text;
            var inTokens = response.Usage?.InputTokenCount;
            var outTokens = response.Usage?.OutputTokenCount;
            var usageRaw = UsageExtractor.RawUsageJson(response);
            var cachedTokens = response.Usage?.CachedInputTokenCount ?? UsageExtractor.CachedTokens(usageRaw);
            var cacheWriteTokens = UsageExtractor.CacheWriteTokens(usageRaw);
            var reasoningTokens = response.Usage?.ReasoningTokenCount;
            var webSearch = call.WebSearch is null ? null : WebSearchExtractor.From(response.Messages, text, response);

            // 빈 응답은 실패다 (다시 보내도 같으므로 재시도하지 않는다).
            if (ResponseChecker.EmptyFailure(text, response.FinishReason, outTokens, call.Agent.Options) is { } emptyFailure)
            {
                text = emptyFailure;

                _logger.LogWarning("[{Mode}#{Step}] {Role}@{Agent} 빈 응답 (출력 {Out})", call.Mode, call.Step, call.Role, call.Agent.Name, outTokens);
                return new AttemptOutcome(NewRecord() with
                {
                    InputTokens = inTokens,
                    CachedInputTokens = cachedTokens,
                    CacheWriteTokens = cacheWriteTokens,
                    OutputTokens = outTokens,
                    ReasoningTokens = reasoningTokens,
                    UsageRaw = usageRaw,
                    WebSearch = webSearch,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Success = false,
                    Error = text,
                }, text, null);
            }

            // 구조화 출력: 원문(JSON)은 기록에, 지휘자·다음 단계에는 그린 텍스트. 해석 실패면 원문 그대로 + 경고 (실패로 치지 않음).
            var raw = text;
            StructuredOutputRecord? structured = null;
            string? structuredWarning = null;
            if (call.Output is { } format)
            {
                structured = FindingParser.Parse(format, call.Step, text, response.FinishReason,
                    call.OutputMode == StructuredOutputMode.Schema ? "schema" : "prompt");
                if (structured.Parsed) text = FindingRenderer.Render(structured);
                else structuredWarning = $"[경고] 구조화 출력 해석 실패 ({structured.Error}) — 원문을 그대로 전달";
            }

            var warnings = CheckWarnings(call, text, response.FinishReason, outTokens);
            if (structuredWarning is not null) warnings = [.. warnings, structuredWarning];
            if (webSearch is { Unverified.Count: > 0 } searched)
            {
                // 출처 대조 경고: 응답은 그대로 전달하고 결과에 한 줄 붙인다 (지휘자가 해당 URL을 근거로 쓰지 않도록).
                var shown = string.Join(", ", searched.Unverified.Take(5)) + (searched.Unverified.Count > 5 ? $" 외 {searched.Unverified.Count - 5}개" : "");
                warnings = [.. warnings, $"[경고] 검색 결과에 없는 URL {searched.Unverified.Count}개 (지어냈을 수 있음): {shown}"];
                _logger.LogWarning("[{Mode}#{Step}] {Role}@{Agent} 검색 결과에 없는 URL {Count}개", call.Mode, call.Step, call.Role, call.Agent.Name, searched.Unverified.Count);
            }

            _logger.LogInformation("[{Mode}#{Step}] {Role}@{Agent} 완료 (시도 {Attempt}) {Elapsed}ms, 입력 {In} / 출력 {Out}, 경고 {Warnings}",
                call.Mode, call.Step, call.Role, call.Agent.Name, attempt, sw.ElapsedMilliseconds, inTokens, outTokens, warnings.Count);
            return new AttemptOutcome(NewRecord() with
            {
                Response = raw,
                Structured = structured,
                InputTokens = inTokens,
                CachedInputTokens = cachedTokens,
                CacheWriteTokens = cacheWriteTokens,
                OutputTokens = outTokens,
                ReasoningTokens = reasoningTokens,
                UsageRaw = usageRaw,
                WebSearch = webSearch,
                ElapsedMs = sw.ElapsedMilliseconds,
                Success = true,
                Warnings = warnings,
            }, text, null);
        }
        catch (Exception ex) when (RetryClassifier.IsUserCancellation(ex, ct))
        {
            // 사용자 취소: 재시도하지 않고 그대로 올린다. 요청은 이미 나갔으므로(비용이 생겼을 수 있음) 시도 기록은 남긴다.
            var canceled = NewRecord() with
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = "[취소] 사용자가 실행을 취소했습니다.",
                ErrorKind = "Canceled",
            };
            try { await CallLog.AppendAsync(canceled, _logPath); }
            catch (Exception logEx) { _logger.LogWarning(logEx, "호출 기록 저장 실패"); }
            throw;
        }
        catch (Exception ex)
        {
            // SDK 재시도를 끈 뒤로 네트워크 타임아웃은 TaskCanceledException으로 올라온다.
            // 사용자 취소가 아니면 여기서 잡아 Timeout으로 분류한다 (예외가 run_mode 밖으로 새지 않도록).
            var failure = RetryClassifier.Classify(ex, ct);
            var kind = failure.Kind?.ToString();
            var error = $"[실패] {Describe(failure)}{ex.GetType().Name}: {ex.Message}";

            _logger.LogError(ex, "[{Mode}#{Step}] {Role}@{Agent} 시도 {Attempt}/{Max} 실패 ({Kind})",
                call.Mode, call.Step, call.Role, call.Agent.Name, attempt, maxAttempts, kind ?? "분류 안 됨");
            return new AttemptOutcome(NewRecord() with
            {
                ElapsedMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = error,
                ErrorKind = kind,
            }, error, failure);
        }

        CallRecord NewRecord() => new()
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Timestamp = startedAt,
            Tool = call.Tool ?? call.Mode,
            RunId = call.RunId,
            Mode = call.Mode,
            Role = call.Role,
            Step = call.Step,
            Attempt = attempt,
            Agent = call.Agent.Name,
            Provider = call.Agent.Provider,
            Model = call.Agent.Model,
            ProjectDir = Environment.CurrentDirectory,
            Prompt = call.Prompt,
            Files = call.Meta.Files ?? [],
            Context = call.Meta.Context,
            ContextRoots = call.Meta.ContextRoots,
            Images = call.Images is { Count: > 0 } images ? [.. images.Select(i => i.RecordText)] : null,
            Provenance = provenance,
            Reason = call.Meta.Reason,
            OwnReview = call.Meta.OwnReview,
            FollowUpOf = call.FollowUpOf,
            FollowUpRequests = call.FollowUpRequests,
            FollowUpSkipped = call.FollowUpSkipped,
        };
    }

    /// <summary>
    /// 이미지가 있는 user 메시지: [첨부 자료] → [이미지마다 이름표 + 이미지] → [나머지(요청…)].
    /// 첨부 자료가 첫 text 블록으로 남아 Context 캐시 저장 지점은 지금처럼 그 끝에 붙는다 (이미지는 저장 지점 뒤라 캐시되지 않음).
    /// 첨부가 없거나 Prompt가 첨부로 시작하지 않으면 이미지를 맨 앞에 둔다.
    /// </summary>
    internal static ChatMessage WithImages(StepCall call)
    {
        var split = call.ContextPrefix is { Length: > 0 } prefix && call.Prompt.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : 0;
        var detail = call.Agent.ProviderOptions?.ImageDetail;
        var contents = new List<AIContent>(call.Images!.Count * 2 + 2);

        if (split > 0) contents.Add(new TextContent(call.Prompt[..split]));
        for (var i = 0; i < call.Images.Count; i++)
        {
            var image = call.Images[i];
            contents.Add(new TextContent($"[이미지 {i + 1}] {image.Path}"));
            var data = new DataContent(image.Data, image.MediaType);
            if (!string.IsNullOrWhiteSpace(detail)) data.AdditionalProperties = new() { ["detail"] = detail };
            contents.Add(data);
        }
        contents.Add(new TextContent(call.Prompt[split..]));
        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>경고 검사. 검사기가 예외를 던져도 응답 전달을 막지 않는다 (경고 없이 넘어가고 로그만 남김).</summary>
    private IReadOnlyList<string> CheckWarnings(StepCall call, string text, Microsoft.Extensions.AI.ChatFinishReason? finishReason, long? outTokens)
    {
        try
        {
            if (!ResponseChecker.IsSupportedLanguage(call.Checks.ExpectedLanguage))
                _logger.LogWarning("ResponseChecks.ExpectedLanguage '{Language}'은(는) 지원하지 않아 언어 검사를 건너뜁니다 (지원: ko).", call.Checks.ExpectedLanguage);

            var warnings = ResponseChecker.Warnings(text, finishReason, outTokens, call.Checks);
            foreach (var w in warnings)
                _logger.LogWarning("[{Mode}#{Step}] {Role}@{Agent} {Warning}", call.Mode, call.Step, call.Role, call.Agent.Name, w);
            return warnings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Mode}#{Step}] 응답 검사기 오류 — 경고 없이 진행", call.Mode, call.Step);
            return [];
        }
    }

    /// <summary>크레딧·한도 소진 실패 문구의 머리. 골든 일괄 실행이 이 머리로 남은 사례를 멈춘다.</summary>
    public const string QuotaFailurePrefix = "[실패] Quota";

    /// <summary>"Timeout · " / "Client 402 · " 처럼 실패 문구 앞에 붙는 분류.</summary>
    private static string Describe(FailureInfo failure) =>
        failure.Kind is null ? "" : $"{failure.Kind}{(failure.Status is { } s ? $" {s}" : "")} · ";

    /// <summary>
    /// 마지막 실패 문구. 재시도하지 않는 오류로 끝났으면 그 시도의 문구 그대로,
    /// 재시도 끝에 실패했으면 시도 횟수와 조정할 설정을 앞에 붙인다.
    /// </summary>
    private static string FinalFailureText(StepCall call, AttemptOutcome last, int attempts, int maxAttempts)
    {
        if (last.Failure?.Kind == ErrorKind.Quota)
        {
            // last.Text는 "[실패] Quota 429 · 예외: 메시지" — 머리를 다시 붙이지 않도록 예외 부분만
            var detail = last.Text.IndexOf(" · ", StringComparison.Ordinal) is var at and >= 0 ? last.Text[(at + 3)..] : last.Text;
            return $"{QuotaFailurePrefix} — 크레딧/한도 소진, 충전 전까지 재실행 금지 ({call.Agent.Provider}). 마지막 오류: {detail}";
        }
        if (last.Failure is not { Retryable: true } failure) return last.Text;

        var timeout = AgentSettings.ResolveTimeout(call.Agent.Options, call.Agent.ProviderOptions);
        var hint = failure.Kind == ErrorKind.Timeout
            ? $"응답이 {timeout.TotalSeconds:0}초 안에 오지 않았습니다. appsettings의 '{call.Agent.Name}' TimeoutSeconds를 늘리세요."
            : $"잠시 뒤 다시 실행하거나 appsettings의 '{call.Agent.Name}' MaxAttempts를 조정하세요.";
        return $"[실패] {failure.Kind} — {attempts}/{maxAttempts}회 시도 모두 실패. {hint} 마지막 오류: {last.Text}";
    }

    private static string RetryAfterTooLongText(AttemptOutcome last) =>
        $"[실패] {last.Failure!.Kind} · 서버가 {last.Failure.RetryAfter!.Value.TotalSeconds:0}초 뒤 재시도를 요구해 기다리지 않음 " +
        $"(상한 {RetryClassifier.MaxRetryAfter.TotalSeconds:0}초). 잠시 뒤 다시 실행하세요. 마지막 오류: {last.Text}";
}
