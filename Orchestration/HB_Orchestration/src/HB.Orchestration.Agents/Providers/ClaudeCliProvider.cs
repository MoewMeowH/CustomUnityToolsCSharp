using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// Claude Code 헤드리스(<c>claude --bare --restricted -p</c>)를 읽기 전용 검토 에이전트로 띄운다 (계획서 2026-10-27_검토형-서브에이전트-API-전환).
/// 에이전트는 작업 폴더(<see cref="ReviewWorkFolder"/>) 안의 파일을 직접 읽는다. 이 PC의 Claude Code 2.1.311(Windows)은 --tools에 Grep · Glob을 줘도
/// Read만 준다 (2026-10-27 확인) — 그래서 작업 폴더가 프롬프트에 파일 목록을 넣는다.
/// 과금은 제공사 키 환경 변수의 API 키로만 한다: <c>--bare</c>는 구독 로그인(OAuth)을 읽지 않고, 키가 없으면 구독으로 넘어가지 않고 실패한다.
/// <c>--restricted</c>는 사용자 · 프로젝트 · 로컬 설정 파일을 무시한다 — 리뷰 대상의 .claude/settings.json이 작업 폴더에 복사돼도 적용되지 않는다 (확인).
/// IChatClient로 감싸 AgentRunner의 기록 · 재시도 · 호출 중 표시 · 비용 경로를 그대로 쓴다.
/// </summary>
public sealed class ClaudeCliProvider : IChatClientProvider
{
    public const string TypeName = "claude-cli";

    /// <summary>Claude Code 자체가 쓰는 키 이름. 이 이름을 사용자 환경에 두면 대화형 Claude Code도 API 과금으로 바뀐다.</summary>
    public const string ClaudeCodeKeyName = "ANTHROPIC_API_KEY";

    public string Type => TypeName;

    public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKeyEnv))
            throw new ProviderConfigurationException($"제공사 '{providerName}': claude-cli는 ApiKeyEnv가 필요합니다 (구독 대신 API 키로 과금).");
        // 이름 검사를 값 읽기보다 먼저: 비어 있다는 안내를 보고 그 변수를 채우면 막으려던 일이 생긴다.
        if (string.Equals(options.ApiKeyEnv, ClaudeCodeKeyName, StringComparison.OrdinalIgnoreCase))
            throw new ProviderConfigurationException(
                $"제공사 '{providerName}': ApiKeyEnv에 {ClaudeCodeKeyName}를 쓰지 않는다 — 사용자 환경에 두면 Claude Code 자체가 API 과금으로 바뀐다. 다른 이름(예: Custom_CLAUDE_API_KEY)을 쓴다.");
        var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnv) is { Length: > 0 } k
            ? k
            : throw new ProviderConfigurationException($"제공사 '{providerName}': 환경 변수 {options.ApiKeyEnv} 이(가) 비어 있습니다.");

        var settings = new ClaudeCliSettings(
            Executable: ResolveExecutable(providerName, options.ExecutablePath),
            ApiKey: apiKey,
            KeyEnvName: options.ApiKeyEnv!,
            Model: agent.Model,
            MaxTurns: agent.MaxTurns ?? ClaudeCliSettings.DefaultMaxTurns,
            MaxBudgetUsd: agent.MaxBudgetUsd,
            // SDK 기본값(100초)은 여러 턴 검토에 짧다 — 설정이 없으면 15분.
            Timeout: agent.TimeoutSeconds is > 0 || options.TimeoutSeconds is > 0 ? AgentSettings.ResolveTimeout(agent, options) : ClaudeCliSettings.DefaultTimeout);
        return new ClaudeCliChatClient(settings, ClaudeCliProcess.RunAsync);
    }

    /// <summary>
    /// 실행 파일을 절대 경로로 정한다. 상대 이름을 그대로 넘기면 Windows CreateProcess가 서버의 현재 폴더(= 프로젝트 루트)를 먼저 찾아,
    /// 거기 놓인 claude.exe가 API 키를 받고 실행될 수 있다 (/self-review security C3). 설정 값은 절대 경로여야 하고, 비우면 PATH의 절대 경로 폴더에서만 찾는다.
    /// npm 설치본(claude.cmd)은 UseShellExecute=false로 직접 실행할 수 없어 찾지 않는다 — 그런 PC는 ExecutablePath에 실행 파일을 지정한다.
    /// </summary>
    public static string ResolveExecutable(string providerName, string? configured, string? pathVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new ProviderConfigurationException($"제공사 '{providerName}': ExecutablePath '{configured}'는 있는 파일의 절대 경로여야 합니다.");
            return configured;
        }

        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(d)) continue; // "." 같은 상대 항목은 현재 폴더를 뜻한다
            var candidate = Path.Combine(d, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new ProviderConfigurationException($"제공사 '{providerName}': PATH에서 {name}을(를) 찾지 못했습니다. Claude Code를 설치하거나 ExecutablePath에 절대 경로를 넣으세요.");
    }
}

/// <param name="KeyEnvName">키를 담은 환경 변수 이름 (자식 환경에서 지운다).</param>
/// <param name="Timeout">프로세스 전체 시간 상한 (에이전트 → 제공사 TimeoutSeconds).</param>
public sealed record ClaudeCliSettings(string Executable, string ApiKey, string KeyEnvName, string Model, int MaxTurns, decimal? MaxBudgetUsd, TimeSpan Timeout)
{
    public const int DefaultMaxTurns = 40;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>ToString · 디버거 · 예외 메시지에 키 값이 찍히지 않게 (지휘자 K1).</summary>
    private bool PrintMembers(StringBuilder b)
    {
        b.Append($"Executable = {Executable}, ApiKey = ***, KeyEnvName = {KeyEnvName}, Model = {Model}, MaxTurns = {MaxTurns}, MaxBudgetUsd = {MaxBudgetUsd}, Timeout = {Timeout}");
        return true;
    }
}

/// <summary>한 번의 CLI 실행. Environment는 자식 프로세스에만 덮어쓸 값 (null = 지움).</summary>
public sealed record ClaudeCliInvocation(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, string Stdin)
{
    /// <summary>환경은 이름만 찍는다 (키 값 보호).</summary>
    private bool PrintMembers(StringBuilder b)
    {
        b.Append($"Executable = {Executable}, Arguments = [{string.Join(' ', Arguments)}], WorkingDirectory = {WorkingDirectory}, ");
        b.Append($"Environment = [{string.Join(", ", Environment.Keys)}], Stdin = {Stdin.Length}자");
        return true;
    }
}

public sealed record ClaudeCliProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>프로세스 실행 (테스트에서 바꿔 끼운다). 시간 초과는 TimeoutException, 사용자 취소는 OperationCanceledException.</summary>
public delegate Task<ClaudeCliProcessResult> ClaudeCliRunner(ClaudeCliInvocation invocation, TimeSpan timeout, CancellationToken ct);

/// <summary>실패한 실행이 이미 쓴 토큰 (결과 JSON에 usage가 있을 때). AgentRunner가 실패 기록에 남겨 비용에서 빠지지 않게 한다.</summary>
public sealed record ClaudeCliSpent(long InputTokens, long CachedInputTokens, long CacheWriteTokens, long OutputTokens, long? ReasoningTokens, string RawJson);

/// <summary>CLI가 돌려준 실패. RetryClassifier가 Kind · Retryable을 그대로 쓴다.</summary>
public sealed class ClaudeCliException(string message, ErrorKind? kind, bool retryable, int? status = null, ClaudeCliSpent? spent = null) : Exception(message)
{
    public ErrorKind? Kind { get; } = kind;
    public bool Retryable { get; } = retryable;
    public int? Status { get; } = status;
    public ClaudeCliSpent? Spent { get; } = spent;

    /// <summary>MAF가 감쌌어도 안쪽의 ClaudeCliException을 찾는다.</summary>
    public static ClaudeCliException? Find(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is ClaudeCliException cli) return cli;
        return null;
    }
}

/// <summary>usage 원문 (결과 JSON에서 본문 result만 뺀 것). UsageExtractor가 기록용 usageRaw로 꺼낸다.</summary>
public sealed record ClaudeCliRawResponse(string Json);

public sealed class ClaudeCliChatClient(ClaudeCliSettings settings, ClaudeCliRunner run) : IChatClient
{
    /// <summary>ChatOptions.AdditionalProperties 키: 작업 폴더 (ModeRunner → AgentRunner가 넣는다).</summary>
    public const string WorkFolderKey = "hb.workFolder";

    /// <summary>읽기 전용 도구. 쓰기 · 실행 · 웹 · 하위 에이전트는 주지 않는다 (지금 판은 Read만 실제로 준다 — 클래스 설명).</summary>
    public const string Tools = "Read,Glob,Grep";

    /// <summary>자식 프로세스에서 지우는 인증 · 클라우드 변수: API 키보다 먼저 쓰이거나 구독 로그인으로 이어질 수 있다 (인증 순서는 Claude Code 문서).</summary>
    public static readonly IReadOnlyList<string> RemovedEnvironment =
    [
        "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
        "ANTHROPIC_BASE_URL", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT",
    ];

    /// <summary>이 이름 끝을 가진 변수도 지운다 — 다른 제공사 키 · 토큰을 자식에게 넘기지 않는다 (지휘자 K2).</summary>
    public static readonly IReadOnlyList<string> RemovedSuffixes = ["_API_KEY", "_ADMIN_KEY", "_TOKEN", "_SECRET"];

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Any(m => m.Role == ChatRole.Assistant))
            throw new ClaudeCliException("[claude-cli] 이전 응답을 이어 받는 호출(followUp · 토론)은 지원하지 않습니다.", ErrorKind.Client, retryable: false);
        if (options?.AdditionalProperties?.TryGetValue(WorkFolderKey, out var f) != true || f is not string folder || !Directory.Exists(folder))
            throw new ClaudeCliException("[claude-cli] 작업 폴더가 없습니다 — context로 검토할 파일을 넘기세요 (도구 없이 돌지 않게 실행하지 않음).", ErrorKind.Client, retryable: false);
        // none은 CLI에 보낼 값이 없다 (Opus 5.5는 thinking을 끌 수 없음). 그냥 빼면 기록의 "none"과 실제 조건이 달라진다 (/self-review C10).
        if (options.Reasoning?.Effort == ReasoningEffort.None)
            throw new ClaudeCliException("[claude-cli] 추론 강도 none은 쓸 수 없습니다 (low · medium · high · xhigh).", ErrorKind.Client, retryable: false);

        var system = string.Join("\n\n", new[] { options.Instructions }
            .Concat(list.Where(m => m.Role == ChatRole.System).Select(m => m.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t)));
        var user = string.Join("\n\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text));

        var temp = Directory.CreateTempSubdirectory("hb-claude-cli-").FullName;
        try
        {
            var systemFile = Path.Combine(temp, "system.md");
            var settingsFile = Path.Combine(temp, "settings.json");
            await File.WriteAllTextAsync(systemFile, system, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(settingsFile, """{ "permissions": { "blockReadsOutsideWorkingDirectories": true } }""", new UTF8Encoding(false), cancellationToken);

            var invocation = new ClaudeCliInvocation(settings.Executable,
                Arguments(settings, options.Reasoning?.Effort, systemFile, settingsFile), folder, ChildEnvironment(settings.ApiKey, settings.KeyEnvName), user);
            var result = await run(invocation, settings.Timeout, cancellationToken);
            return Parse(result, settings.Model);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception) { /* 임시 파일 정리 실패는 무시 */ }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // CLI는 끝난 뒤 결과 하나를 준다 — 스트리밍을 원하는 쪽에도 한 번에 넘긴다.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    /// <summary>
    /// 명령줄 인자 (프롬프트는 stdin). effort가 없으면 넘기지 않는다 (모델 기본값).
    /// --restricted: 설정 파일(사용자 · 프로젝트 · 로컬) 무시, 파일 도구를 작업 폴더로 제한 (두 겹 — settings의 폴더 밖 읽기 차단과 함께).
    /// --no-session-persistence: ~/.claude/projects에 소스 사본이 든 세션 기록을 남기지 않는다 (2026-10-27 확인).
    /// </summary>
    public static List<string> Arguments(ClaudeCliSettings s, ReasoningEffort? effort, string systemFile, string settingsFile)
    {
        var args = new List<string>
        {
            "--bare", "--restricted", "-p", "--no-session-persistence", "--output-format", "json",
            "--model", s.Model,
            "--max-turns", s.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--permission-mode", "dontAsk",
            "--tools", Tools,
            "--settings", settingsFile,
            "--append-system-prompt-file", systemFile,
        };
        if (EffortName(effort) is { } e) args.AddRange(["--effort", e]);
        if (s.MaxBudgetUsd is { } budget) args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        return args;
    }

    /// <summary>MEAI 추론 강도 → CLI 값. none은 GetResponseAsync가 먼저 거절한다.</summary>
    public static string? EffortName(ReasoningEffort? effort) => effort switch
    {
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => null,
    };

    /// <summary>
    /// 자식 프로세스 환경: API 키만 넣는다. 인증 · 클라우드 우회 변수, 키를 담은 원래 변수, 지금 프로세스의 키 · 토큰 모양 변수는 지운다.
    /// </summary>
    public static Dictionary<string, string?> ChildEnvironment(string apiKey, string keyEnvName, IEnumerable<string>? currentNames = null)
    {
        var env = RemovedEnvironment.ToDictionary(k => k, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        env[keyEnvName] = null;
        foreach (var name in currentNames ?? System.Environment.GetEnvironmentVariables().Keys.Cast<string>())
            if (RemovedSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) env[name] = null;
        env[ClaudeCliProvider.ClaudeCodeKeyName] = apiKey;
        return env;
    }

    /// <summary>
    /// 결과 JSON(<c>--output-format json</c>)을 응답으로. 필요한 필드(result · usage)가 없으면 CLI 판이 바뀐 것으로 보고 실패 —
    /// 0 토큰 성공으로 조용히 기록하지 않는다. 입력 토큰 = 캐시 안 됨 + 캐시 쓰기 + 캐시 읽기 (OpenAI 기록과 같은 뜻, 비용 계산이 나눠 매긴다).
    /// 실패여도 usage가 있으면 예외에 실어 기록 비용에서 빠지지 않게 한다 (/self-review sub_self C2).
    /// </summary>
    public static ChatResponse Parse(ClaudeCliProcessResult result, string model)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(result.Stdout); }
        catch (JsonException)
        {
            throw new ClaudeCliException($"[claude-cli] 결과가 JSON이 아닙니다 (종료 코드 {result.ExitCode}): {Head(result.Stderr.Length > 0 ? result.Stderr : result.Stdout)}",
                null, retryable: false);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ClaudeCliException("[claude-cli] 결과 JSON 모양이 다릅니다 (객체 아님).", null, retryable: false);

            var spent = SpentOf(root);
            var subtype = Str(root, "subtype");
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var text = Str(root, "result");
            if (isError || (subtype is not null && subtype != "success"))
                throw Failure(subtype, Number(root, "api_error_status"), text, result, spent);

            if (Str(root, "stop_reason") == "refusal")
                throw new ClaudeCliException("[claude-cli] 모델이 요청을 거절했습니다 (stop_reason: refusal). 서브에이전트로 다시 하거나 범위를 바꾸세요.",
                    ErrorKind.Client, retryable: false, spent: spent);

            if (text is null || spent is null)
                throw new ClaudeCliException("[claude-cli] 결과 JSON에 result · usage가 없습니다 (Claude Code 판이 바뀌었을 수 있음).", null, retryable: false, spent: spent);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                ModelId = ModelOf(root) ?? model,
                Usage = new UsageDetails
                {
                    InputTokenCount = spent.InputTokens,
                    CachedInputTokenCount = spent.CachedInputTokens,
                    OutputTokenCount = spent.OutputTokens,
                    ReasoningTokenCount = spent.ReasoningTokens,
                },
                FinishReason = Str(root, "stop_reason") == "max_tokens" ? ChatFinishReason.Length : ChatFinishReason.Stop,
                RawRepresentation = new ClaudeCliRawResponse(spent.RawJson),
            };
        }
    }

    /// <summary>기록 원문(usageRaw)의 permission_denials 수 → 경고 한 줄. claude-cli 기록이 아니거나 0이면 null.</summary>
    public static string? DenialWarning(string? usageRaw)
    {
        if (string.IsNullOrWhiteSpace(usageRaw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(usageRaw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("permission_denials", out var d) || d.ValueKind != JsonValueKind.Array) return null;
            var n = d.GetArrayLength();
            if (n == 0) return null;
            var paths = d.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object
                    && ti.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String) is { Value.ValueKind: JsonValueKind.String } p0
                    ? Path.GetFileName(p0.Value.GetString()) : null)
                .OfType<string>().Distinct().Take(3);
            return $"[경고] claude-cli 도구 사용 {n}건이 거절됨 (작업 폴더 밖 · 막힌 파일: {string.Join(", ", paths)}) — 에이전트가 필요한 파일을 못 읽었을 수 있습니다";
        }
        catch (JsonException) { return null; }
    }

    private static ClaudeCliSpent? SpentOf(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        long N(string name) => Number(usage, name) ?? 0;
        var cacheRead = N("cache_read_input_tokens");
        var cacheWrite = N("cache_creation_input_tokens");
        return new ClaudeCliSpent(
            InputTokens: N("input_tokens") + cacheWrite + cacheRead,
            CachedInputTokens: cacheRead,
            CacheWriteTokens: cacheWrite,
            OutputTokens: N("output_tokens"),
            ReasoningTokens: usage.TryGetProperty("output_tokens_details", out var od) && od.ValueKind == JsonValueKind.Object ? Number(od, "thinking_tokens") : null,
            RawJson: WithoutResult(root));
    }

    private static ClaudeCliException Failure(string? subtype, long? status, string? text, ClaudeCliProcessResult result, ClaudeCliSpent? spent)
    {
        var detail = Head(text ?? (result.Stderr.Length > 0 ? result.Stderr : result.Stdout));
        return subtype switch
        {
            "error_max_turns" => new ClaudeCliException($"[claude-cli] 턴 상한(--max-turns)에 닿아 멈췄습니다. 범위를 줄이거나 에이전트 MaxTurns를 올리세요. {detail}", ErrorKind.Client, false, null, spent),
            "error_max_budget_usd" => new ClaudeCliException($"[claude-cli] 실행 1회 비용 상한(--max-budget-usd)에 닿아 멈췄습니다. {detail}", ErrorKind.Client, false, null, spent),
            // 잔액 부족은 400(invalid_request_error)으로 올 수 있다 — 상태 코드와 상관없이 문구로 본다 (/self-review C6 · sub_self C7).
            _ when detail.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
                => new ClaudeCliException($"[claude-cli] 크레딧 · 한도 소진. {detail}", ErrorKind.Quota, false, status is { } s ? (int)s : null, spent),
            _ when status == 429 => new ClaudeCliException($"[claude-cli] 요청 과다 (429). {detail}", ErrorKind.RateLimit, true, 429, spent),
            _ when status >= 500 => new ClaudeCliException($"[claude-cli] 서버 오류 ({status}). {detail}", ErrorKind.Server, true, (int)status.Value, spent),
            _ when status >= 400 => new ClaudeCliException($"[claude-cli] 요청 오류 ({status}). {detail}", ErrorKind.Client, false, (int)status.Value, spent),
            _ => new ClaudeCliException($"[claude-cli] 실패 ({subtype ?? "is_error"}, 종료 코드 {result.ExitCode}). {detail}", null, false, null, spent),
        };
    }

    private static string? ModelOf(JsonElement root) =>
        root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object
            ? mu.EnumerateObject().Select(p => p.Name).FirstOrDefault()
            : null;

    /// <summary>기록용 원문: 본문(result)은 응답 칸에 따로 남으므로 뺀다.</summary>
    private static string WithoutResult(JsonElement root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var p in root.EnumerateObject())
                if (p.Name != "result") p.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Head(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}

/// <summary>실제 프로세스 실행. 시간 초과 · 취소면 프로세스 트리를 끝내고 종료를 기다린다.</summary>
public static class ClaudeCliProcess
{
    /// <summary>Kill 뒤 · 입력 실패 뒤 종료를 기다리는 시간 — 작업 폴더가 잠긴 채 지우려 하지 않게 (/self-review C8 · sub_self C4).</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    public static async Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation inv, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(inv.Executable)
        {
            WorkingDirectory = inv.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in inv.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in inv.Environment)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ClaudeCliException($"[claude-cli] '{inv.Executable}'를 실행하지 못했습니다 ({ex.Message}). Claude Code 설치 · 제공사 ExecutablePath를 확인하세요.", null, false);
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(inv.Stdin.AsMemory(), linked.Token);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // CLI가 입력을 읽기 전에 끝났다 (인자 오류 · 판 변경 등). 네트워크 오류로 재시도하지 말고 CLI가 남긴 출력으로 원인을 보인다 (/self-review C7 · sub_self C5).
                await WaitExitQuietly(process);
                return new ClaudeCliProcessResult(process.HasExited ? process.ExitCode : -1, await Quiet(stdout), await Quiet(stderr));
            }
            await process.WaitForExitAsync(linked.Token);
            return new ClaudeCliProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { /* 이미 끝남 */ }
            await WaitExitQuietly(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"[claude-cli] {timeout.TotalSeconds:0}초 안에 끝나지 않아 멈췄습니다.");
        }
    }

    private static async Task WaitExitQuietly(Process process)
    {
        try
        {
            using var wait = new CancellationTokenSource(ExitWait);
            await process.WaitForExitAsync(wait.Token);
        }
        catch (Exception) { /* 끝내 못 기다리면 폴더 정리는 다음 정리 때 */ }
    }

    private static async Task<string> Quiet(Task<string> read)
    {
        try { return await read; }
        catch (Exception) { return ""; }
    }
}
