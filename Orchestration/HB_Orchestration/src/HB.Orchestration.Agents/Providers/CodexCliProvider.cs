using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents.Providers;

/// <summary>
/// OpenAI Codex CLI(<c>codex exec --json -s read-only</c>)를 읽기 전용 리뷰 에이전트로 띄운다.
/// 과금은 API 키가 아니라 ChatGPT 구독 로그인(CODEX_HOME의 auth.json)이다 — 키 변수는 자식 환경에서 지운다.
/// 에이전트는 작업 폴더(<see cref="ReviewWorkFolder"/>, 저장소 추적 파일 사본)에서 셸 명령(rg · Get-Content)으로 직접 찾아 읽는다.
/// 읽기 전용 샌드박스는 쓰기만 막는다 — 디스크 전체를 읽을 수는 있다 (HB_Chat 실측). 그래서 웹 검색 · 앱 · 플러그인 · 브라우저를 끄고, 사용자 설정을 읽지 않는다.
/// 실패 종류 · 쓴 토큰 · 기록 원문은 claude-cli와 같은 형(<see cref="ClaudeCliException"/> · <see cref="ClaudeCliRawResponse"/>)으로 넘겨
/// AgentRunner · RetryClassifier · UsageExtractor 경로를 그대로 쓴다.
/// </summary>
public sealed class CodexCliProvider : IChatClientProvider
{
    public const string TypeName = "codex-cli";

    public string Type => TypeName;

    public IChatClient Create(string providerName, ProviderOptions options, AgentOptions agent)
    {
        // 키로 돌리면 구독을 쓰려는 목적과 어긋나고, 자식 환경의 키 변수는 어차피 지운다.
        if (!string.IsNullOrWhiteSpace(options.ApiKeyEnv))
            throw new ProviderConfigurationException($"제공사 '{providerName}': codex-cli는 ChatGPT 로그인으로만 돈다 — ApiKeyEnv를 비우세요 (로그인은 'codex login').");

        var settings = new CodexCliSettings(
            Executable: ResolveExecutable(providerName, options.ExecutablePath),
            Model: agent.Model,
            Timeout: agent.TimeoutSeconds is > 0 || options.TimeoutSeconds is > 0 ? AgentSettings.ResolveTimeout(agent, options) : CodexCliSettings.DefaultTimeout);
        return new CodexCliChatClient(settings, CodexCliProcess.RunAsync, CodexCliProcess.VersionAsync);
    }

    /// <summary>
    /// 실행 파일을 절대 경로로 정한다: 설정(절대 경로) → npm 전역 설치의 네이티브 codex.exe → PATH의 절대 경로 폴더.
    /// npm이 PATH에 두는 codex.cmd는 cmd를 거쳐야 해 UseShellExecute=false로 직접 실행할 수 없다 (HB_Chat ChatOptions.ResolveCodex와 같은 규칙).
    /// 상대 PATH 항목은 건너뛴다 — 서버의 현재 폴더(프로젝트 루트)의 codex.exe가 실행되지 않게 (ClaudeCliProvider.ResolveExecutable과 같음).
    /// </summary>
    public static string ResolveExecutable(string providerName, string? configured, string? pathVariable = null, string? appData = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new ProviderConfigurationException($"제공사 '{providerName}': ExecutablePath '{configured}'는 있는 파일의 절대 경로여야 합니다.");
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            var npmRoot = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "node_modules", "@openai");
            foreach (var (package, triple) in new[] { ("codex-win32-x64", "x86_64-pc-windows-msvc"), ("codex-win32-arm64", "aarch64-pc-windows-msvc") })
            {
                var npm = Path.Combine(npmRoot, package, "vendor", triple, "bin", "codex.exe");
                if (File.Exists(npm)) return npm;
            }
        }

        var name = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(d)) continue;
            var candidate = Path.Combine(d, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new ProviderConfigurationException($"제공사 '{providerName}': Codex CLI 실행 파일을 찾지 못했습니다. 'npm i -g @openai/codex'로 설치하거나 ExecutablePath에 codex.exe 절대 경로를 넣으세요.");
    }
}

/// <param name="Timeout">프로세스 전체 시간 상한 (에이전트 → 제공사 TimeoutSeconds).</param>
public sealed record CodexCliSettings(string Executable, string Model, TimeSpan Timeout)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
}

public sealed class CodexCliChatClient(CodexCliSettings settings, ClaudeCliRunner run, Func<string, Task<string?>>? version = null) : IChatClient
{
    /// <summary>ChatOptions.AdditionalProperties 키: 구조화 출력 스키마 JSON (AgentRunner가 Schema 모드일 때 넣는다 → --output-schema).</summary>
    public const string OutputSchemaKey = "hb.outputSchema";

    /// <summary>
    /// 끄는 Codex 기능 — 셸 샌드박스(-s read-only) 밖에서 도는 도구 (HB_Chat CodexRunner.DisabledFeatures와 같음)
    /// + 리뷰에 필요 없는 하위 에이전트 · 이미지 생성 (2026-10-29 0단계에서 받아들임 확인).
    /// </summary>
    public static readonly IReadOnlyList<string> DisabledFeatures =
        ["apps", "plugins", "remote_plugin", "browser_use", "browser_use_external", "computer_use", "in_app_browser", "multi_agent", "image_generation"];

    /// <summary>
    /// 자식 프로세스에 남기는 환경 변수 (허용 목록, 대소문자 무시). 나머지는 모두 지운다 — Codex는 셸 명령을 쓸 수 있어
    /// 이름 끝 금지 목록으로는 AWS_SECRET_ACCESS_KEY · *_PASSWORD · DATABASE_URL 같은 값이 모델 문맥에 들어갈 수 있다
    /// (/cross-review 90978dee2f C6 · e37c6e41c9 C2). 프록시 변수는 Codex가 OpenAI에 닿는 데 필요해 남긴다.
    /// </summary>
    public static readonly IReadOnlySet<string> KeptEnvironment = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA", "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "PSModulePath", "DriverData",
        "LANG", "LC_ALL", "TERM", "NO_COLOR", "CODEX_HOME",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR",
    };

    /// <summary>
    /// developer_instructions는 명령줄 인자로 간다 — Windows 명령줄 상한(32,767자)에 닿기 전에 막는다.
    /// 넘으면 조용히 자리를 옮기지 않고 실패한다 (시스템 프롬프트 자리가 바뀌면 리뷰 조건이 달라진다).
    /// 길이는 Windows 인자 인코딩(따옴표 · 그 앞 역슬래시를 한 번 더 이스케이프) 뒤의 상한으로 잰다 (/cross-review b476ab3099 1-F2).
    /// </summary>
    public const int MaxInstructionsChars = 24_000;

    /// <summary>TOML 문자열이 Windows 명령줄 인자가 됐을 때의 길이 상한 (따옴표 · 역슬래시마다 한 글자씩 더).</summary>
    public static int CommandLineLength(string tomlValue) => tomlValue.Length + tomlValue.Count(c => c is '"' or '\\') + 2;

    /// <summary>모델 용량 초과 뒤 다시 보낼 때까지의 대기 (기본 2초 · 5초는 용량이 풀리기에 짧다).</summary>
    public static readonly TimeSpan CapacityRetryDelay = TimeSpan.FromSeconds(20);

    /// <summary>앞에 붙는 개발 중 기능 경고 — error 항목으로 오지만 실패가 아니다 (HB_Chat 0단계 실측).</summary>
    private const string UnstableWarning = "Under-development features enabled";

    /// <summary>기록 원문에 남기는 명령 수 · 명령 길이 상한 (명령 출력 전문은 남기지 않는다 — 디스크 어디든 읽을 수 있어 비밀이 섞일 수 있다).</summary>
    public const int MaxRecordedCommands = 60;
    public const int MaxCommandChars = 300;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Any(m => m.Role == ChatRole.Assistant))
            throw new ClaudeCliException("[codex-cli] 이전 응답을 이어 받는 호출(followUp · 토론)은 지원하지 않습니다.", ErrorKind.Client, retryable: false);
        string? folder = null;
        if (options?.AdditionalProperties?.TryGetValue(ClaudeCliChatClient.WorkFolderKey, out var f) == true)
        {
            if (f is not string s || !Directory.Exists(s))
                throw new ClaudeCliException("[codex-cli] 작업 폴더가 없습니다.", ErrorKind.Client, retryable: false);
            folder = s;
        }
        if (options?.Reasoning?.Effort == ReasoningEffort.None)
            throw new ClaudeCliException("[codex-cli] 추론 강도 none은 쓸 수 없습니다 (low · medium · high · xhigh).", ErrorKind.Client, retryable: false);

        var system = string.Join("\n\n", new[] { options?.Instructions }
            .Concat(list.Where(m => m.Role == ChatRole.System).Select(m => m.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t)));
        var user = string.Join("\n\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        if (CommandLineLength(TomlString(system)) > MaxInstructionsChars)
            throw new ClaudeCliException($"[codex-cli] 시스템 프롬프트가 {MaxInstructionsChars:N0}자(명령줄로 넘기는 상한)를 넘습니다 — 리뷰 지침 · 모델별 덧붙임을 줄이세요.",
                ErrorKind.Client, retryable: false);

        var temp = Directory.CreateTempSubdirectory("hb-codex-cli-").FullName;
        try
        {
            // 작업 폴더가 없으면(골든의 고정 프롬프트 — 코드가 프롬프트에 들어 있음) 빈 폴더에서 돌린다.
            var workspace = folder ?? Directory.CreateDirectory(Path.Combine(temp, "empty")).FullName;
            string? schemaFile = null;
            if (options?.AdditionalProperties?.TryGetValue(OutputSchemaKey, out var sc) == true && sc is string schema)
            {
                schemaFile = Path.Combine(temp, "schema.json");
                await File.WriteAllTextAsync(schemaFile, schema, new UTF8Encoding(false), cancellationToken);
            }

            var invocation = new ClaudeCliInvocation(settings.Executable,
                Arguments(settings, options?.Reasoning?.Effort, system, workspace, schemaFile), workspace, ChildEnvironment(), user);
            var result = await run(invocation, settings.Timeout, cancellationToken);
            var codexVersion = version is null ? null : await version(settings.Executable);
            return Parse(result, settings.Model, codexVersion);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception) { /* 임시 파일 정리 실패는 무시 */ }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    /// <summary>
    /// 명령줄 인자 (프롬프트는 stdin <c>-</c>). 사용자 설정(~/.codex/config.toml)은 읽지 않는다 — 그 안의 MCP 서버 · 훅이 붙지 않게.
    /// 그래서 Windows 샌드박스도 여기서 고른다: unelevated (MXC는 git이 작업 폴더로 chdir 하지 못함, HB_Chat 실측).
    /// --ephemeral: ~/.codex/sessions에 명령 출력(소스 사본)이 든 세션 기록을 남기지 않는다 (0단계 확인).
    /// </summary>
    public static List<string> Arguments(CodexCliSettings s, ReasoningEffort? effort, string system, string workspace, string? schemaFile)
    {
        var args = new List<string>
        {
            "exec", "--json", "--ephemeral", "--ignore-user-config",
            "-c", "windows.sandbox=\"unelevated\"",
            // 서버 쪽 웹 검색 — 기능 플래그가 아니라 --disable로는 꺼지지 않는다 (HB_Chat sub_security 9b151bf46c C2)
            "-c", "web_search=\"disabled\"",
            // CODEX_HOME의 저장된 인증이 API 키여도 쓰지 않는다 — 구독 로그인만 (/cross-review b476ab3099 1-N3, 0.165.0에서 받아들임 실측)
            "-c", "forced_login_method=\"chatgpt\"",
            "-s", "read-only", "--skip-git-repo-check", "-C", workspace,
            "-m", s.Model,
        };
        foreach (var feature in DisabledFeatures) args.AddRange(["--disable", feature]);
        if (ClaudeCliChatClient.EffortName(effort) is { } e) args.AddRange(["-c", "model_reasoning_effort=" + TomlString(e)]);
        if (!string.IsNullOrWhiteSpace(system)) args.AddRange(["-c", "developer_instructions=" + TomlString(system)]);
        if (schemaFile is not null) args.AddRange(["--output-schema", schemaFile]);
        args.Add("-");
        return args;
    }

    /// <summary>TOML 기본 문자열 (-c 값은 TOML로 읽힌다): 역슬래시 · 따옴표 · 제어 문자를 이스케이프 (HB_Chat CodexRunner.TomlString).</summary>
    public static string TomlString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// 자식 프로세스 환경: <see cref="KeptEnvironment"/> 밖의 변수는 모두 지운다 — OPENAI_API_KEY로 API 과금에 넘어가지 않게,
    /// 그리고 샌드박스 셸 명령(env · Get-ChildItem Env:)으로 비밀이 모델 문맥에 들어가지 않게. 인증은 CODEX_HOME에서 읽는다.
    /// </summary>
    public static Dictionary<string, string?> ChildEnvironment(IEnumerable<string>? currentNames = null, bool npmMarker = true)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in currentNames ?? Environment.GetEnvironmentVariables().Keys.Cast<string>())
            if (!KeptEnvironment.Contains(name)) env[name] = null;
        if (npmMarker) env["CODEX_MANAGED_BY_NPM"] = "1"; // npm 감싸개(codex.js)가 넣는 값 — 업데이트 안내 판단용 (HB_Chat과 같음)
        return env;
    }

    /// <summary>
    /// JSONL 이벤트 → 응답. 마지막 agent_message가 본문, turn.completed의 usage가 토큰 (입력 = 캐시 포함, OpenAI 기록과 같은 뜻).
    /// turn.failed · 답 없음 · usage 없음은 실패 — 0 토큰 성공으로 조용히 기록하지 않는다. 실패여도 usage가 있으면 예외에 싣는다.
    /// </summary>
    public static ChatResponse Parse(ClaudeCliProcessResult result, string model, string? codexVersion = null)
    {
        // lastError = 최상위 error 이벤트, itemWarning = item 안의 경고성 오류 (예: 모델 정보 없음).
        // 경고 글은 분류 근거로 앞세우지 않는다 — "usage limit" 같은 안내가 다른 실패를 Quota로 바꾸지 않게 (지휘자 K4).
        string? thread = null, answer = null, failed = null, lastError = null, itemWarning = null;
        JsonObject? usage = null;
        var commands = new JsonArray();
        var commandCount = 0;
        foreach (var raw in result.Stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            JsonNode? ev;
            try { ev = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (ev is not JsonObject) continue;
            switch (Str(ev["type"]))
            {
                case "thread.started":
                    thread = Str(ev["thread_id"]) ?? thread;
                    break;
                case "item.completed":
                    var item = ev["item"];
                    switch (Str(item?["type"]))
                    {
                        case "agent_message":
                            answer = Str(item?["text"]) ?? answer;
                            break;
                        case "command_execution":
                            commandCount++;
                            if (commands.Count < MaxRecordedCommands)
                                commands.Add(new JsonObject { ["command"] = Head(Str(item?["command"]) ?? "", MaxCommandChars), ["exit_code"] = item?["exit_code"]?.DeepClone() });
                            break;
                        case "error" when Str(item?["message"]) is { } message && !message.Contains(UnstableWarning, StringComparison.Ordinal):
                            itemWarning ??= message; // 경고성 항목 — 다른 이유가 없을 때만 실패 이유로
                            break;
                    }
                    break;
                case "turn.completed":
                    usage = ev["usage"] as JsonObject;
                    break;
                case "turn.failed":
                    failed = Str(ev["error"]?["message"]) ?? "턴 실패";
                    break;
                case "error":
                    lastError = Str(ev["message"]) ?? lastError;
                    break;
            }
        }

        var rawJson = new JsonObject
        {
            ["usage"] = usage?.DeepClone(),
            ["thread_id"] = thread,
            ["command_count"] = commandCount,
            ["commands"] = commands,
            ["exit_code"] = result.ExitCode,
            ["codex_version"] = codexVersion,
        }.ToJsonString();
        var spent = SpentOf(usage, rawJson);

        if (failed is not null || answer is null)
        {
            var why = failed ?? lastError ?? (LastLine(result.Stderr) is { Length: > 0 } tail ? tail : null)
                      ?? itemWarning ?? $"답 없이 끝났습니다 (종료 코드 {result.ExitCode})";
            throw Failure(why, spent, started: commandCount > 0 || spent is not null || answer is not null);
        }
        if (spent is null)
            throw new ClaudeCliException("[codex-cli] turn.completed의 usage가 없습니다 (Codex CLI 판이 바뀌었을 수 있음).", null, retryable: false);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
        {
            ModelId = model,
            Usage = new UsageDetails
            {
                InputTokenCount = spent.InputTokens,
                CachedInputTokenCount = spent.CachedInputTokens,
                OutputTokenCount = spent.OutputTokens,
                ReasoningTokenCount = spent.ReasoningTokens,
            },
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = new ClaudeCliRawResponse(rawJson),
        };
    }

    /// <summary>
    /// 실패 문구 → 종류. 한도 소진 문구는 실제로 만들 수 없어 Codex 실행 파일 안의 오류 이름(usage_limit_reached · usage_limit_exceeded ·
    /// credits_depleted)과 사람이 읽는 문구("usage limit")로 본다 (추정 — 2026-10-29 0단계). quota · insufficient_quota · usage_not_included는
    /// /cross-review 90978dee2f C3에서 더함. 분류되지 않은 실패도 문구는 기록 오류 칸에 남는다 (나중에 키워드를 고치는 근거).
    /// 모델 용량 초과("Selected model is at capacity", 2026-10-30 실제 문구)는 곧 풀리므로 <see cref="CapacityRetryDelay"/> 뒤 다시 보낸다.
    /// <paramref name="started"/>: 이미 일을 시작한 뒤의 실패 — 다시 보내면 리뷰가 처음부터 돌아 시간과 구독 한도를 또 쓰므로 재시도하지 않는다.
    /// </summary>
    public static ClaudeCliException Failure(string message, ClaudeCliSpent? spent, bool started = false)
    {
        var detail = Head(message.Trim(), 300);
        bool Has(params string[] words) => words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase));
        return true switch
        {
            _ when Has("usage limit", "usage_limit", "credits_depleted", "credits depleted", "out of credits", "quota", "usage_not_included")
                => new ClaudeCliException($"[codex-cli] 구독 한도 소진. {detail}", ErrorKind.Quota, false, null, spent),
            _ when Has("at capacity", "overloaded")
                => new ClaudeCliException($"[codex-cli] 모델 용량 초과. {detail}", ErrorKind.Server, !started, null, spent) { RetryAfter = CapacityRetryDelay },
            _ when Has("429", "rate limit", "rate_limit", "too many requests")
                => new ClaudeCliException($"[codex-cli] 요청 과다. {detail}", ErrorKind.RateLimit, !started, 429, spent),
            _ when Has("not supported", "model_not_found", "invalid", "unexpected argument", "unrecognized", "not logged in", "login")
                => new ClaudeCliException($"[codex-cli] 요청 · 설정 오류. {detail}", ErrorKind.Client, false, null, spent),
            _ => new ClaudeCliException($"[codex-cli] 실패. {detail}", null, false, null, spent),
        };
    }

    private static ClaudeCliSpent? SpentOf(JsonObject? usage, string rawJson)
    {
        if (usage is null) return null;
        long N(string name) => usage[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        return new ClaudeCliSpent(
            InputTokens: N("input_tokens"),
            CachedInputTokens: N("cached_input_tokens"),
            CacheWriteTokens: N("cache_write_input_tokens"),
            OutputTokens: N("output_tokens"),
            ReasoningTokens: usage["reasoning_output_tokens"] is JsonValue r && r.TryGetValue<long>(out var reasoning) ? reasoning : null,
            RawJson: rawJson);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Head(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}

/// <summary>
/// 실제 프로세스 실행. Job 객체로 자손까지 묶고, 부모가 끝난 뒤 <see cref="ExitGrace"/>가 지나면 Job째 끝내 파이프를 닫는다
/// (자손이 출력 파이프를 물고 남으면 읽기가 시간 상한까지 막힌다 — HB_Chat sol 리뷰 ②-P2). 시간 초과 · 취소면 모두 끝내고 기다린다.
/// </summary>
public static class CodexCliProcess
{
    public static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    /// <summary>stderr는 끝부분만 (실패 이유 한 줄용).</summary>
    private const int StderrTail = 4000;

    public static async Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation inv, TimeSpan timeout, CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo(inv.Executable)
        {
            WorkingDirectory = inv.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
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
            throw new ClaudeCliException($"[codex-cli] '{inv.Executable}'를 실행하지 못했습니다 ({ex.Message}). Codex CLI 설치 · 제공사 ExecutablePath를 확인하세요.", null, false);
        }

        // 입력을 쓰기 전에 넣는다: codex는 프롬프트를 다 받기 전에는 명령을 띄우지 않는다.
        using var job = ProcessJob.Attach(process);
        void KillAll()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { /* 이미 끝남 */ }
            job?.Terminate();
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var kill = linked.Token.Register(KillAll);
        _ = ReapAsync(process, job);
        // stdout은 받는 대로 모은다 — 자손이 파이프를 물고 남아 끝까지 읽지 못해도 이미 받은 이벤트(답 · usage)는 쓸 수 있게 (/cross-review 90978dee2f C4).
        var collected = new StringBuilder();
        var collectedGate = new Lock();
        var stdout = Task.Run(async () =>
        {
            var buf = new char[8192];
            int n;
            try
            {
                while ((n = await process.StandardOutput.ReadAsync(buf)) > 0)
                    lock (collectedGate) collected.Append(buf, 0, n);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }, CancellationToken.None);
        var stderr = ReadTail(process.StandardError);
        try
        {
            await process.StandardInput.WriteAsync(inv.Stdin.AsMemory(), linked.Token);
            process.StandardInput.Close();
        }
        catch (IOException) { /* 바로 끝난 프로세스 — 아래에서 출력으로 본다 */ }
        catch (OperationCanceledException) { /* 아래에서 시간 초과 · 취소로 본다 */ }

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillAll();
            await WaitExitQuietly(process);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            // TimeoutException은 재시도 대상이다 — 재시도를 켠 에이전트에서 시간 상한 × 시도 횟수만큼 멈추지 않게 재시도 없는 실패로 둔다.
            throw new ClaudeCliException($"[codex-cli] {timeout.TotalSeconds:0}초 안에 끝나지 않아 멈췄습니다. appsettings의 TimeoutSeconds를 늘리세요.", ErrorKind.Timeout, retryable: false);
        }

        // 부모가 끝나도 자손이 파이프를 물고 있으면 ReapAsync가 ExitGrace 뒤 Job째 끝낸다. Job이 없으면(Attach 실패) 끝낼 수 없어
        // 기다리지 않고 받은 만큼으로 판단한다 — 시간 초과 실패로 답을 버리지 않는다.
        string? note = null;
        try { await stdout.WaitAsync(ExitGrace + ExitWait); }
        catch (TimeoutException)
        {
            KillAll();
            note = $"[codex-cli] 끝난 뒤에도 자손 프로세스가 출력 파이프를 물고 있어 기다리지 않았다 (Job {(job is null ? "없음 — 자손이 남았을 수 있음" : "있음")}).";
        }
        string output;
        lock (collectedGate) output = collected.ToString();

        string tail;
        try { tail = await stderr.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { tail = ""; KillAll(); }
        return new ClaudeCliProcessResult(process.ExitCode, output, note is null ? tail : $"{tail}\n{note}");
    }

    private static readonly ConcurrentDictionary<string, string> Versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>codex --version</c> 첫 줄 (기록용). 성공한 값만 실행 파일마다 기억한다 — 실패(null)는 다음 실행에서 다시 묻는다.
    /// 본 실행과 같은 환경 정리를 쓰고, 시간 초과 · 오류면 프로세스를 끝낸다 (/cross-review 90978dee2f C7 · e37c6e41c9 C5 · b476ab3099 1-F3).
    /// </summary>
    public static async Task<string?> VersionAsync(string executable)
    {
        if (Versions.TryGetValue(executable, out var known)) return known;
        // stdin을 받아서 닫는다: 서버의 stdin을 물려주면 안 된다 (ReviewWorkFolder.GitStartInfo).
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--version");
        foreach (var (k, v) in CodexCliChatClient.ChildEnvironment(npmMarker: false))
            if (v is null) psi.Environment.Remove(k);
        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Close();
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var errors = p.StandardError.ReadToEndAsync(wait.Token);
            var text = await p.StandardOutput.ReadToEndAsync(wait.Token);
            await p.WaitForExitAsync(wait.Token);
            await errors;
            var version = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (version is not null) Versions[executable] = version;
            return version;
        }
        catch (Exception)
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch (Exception) { /* 이미 끝남 */ }
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static async Task ReapAsync(Process process, ProcessJob? job)
    {
        if (job is null) return;
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.Delay(ExitGrace);
            job.Terminate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { }
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

    private static async Task<string> ReadTail(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        try
        {
            while ((n = await reader.ReadAsync(buf)) > 0)
            {
                sb.Append(buf, 0, n);
                if (sb.Length > StderrTail * 2) sb.Remove(0, sb.Length - StderrTail);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return sb.Length > StderrTail ? sb.ToString(sb.Length - StderrTail, StderrTail) : sb.ToString();
    }
}
