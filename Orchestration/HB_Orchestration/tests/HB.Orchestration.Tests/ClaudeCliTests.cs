using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Tests;

/// <summary>
/// claude-cli 제공사 (claude --bare -p 읽기 전용 검토) · 작업 폴더 (계획서 2026-10-27_검토형-서브에이전트-API-전환).
/// 실제 CLI는 부르지 않는다 — 실행기는 가짜, 결과 JSON은 2026-10-27 시험 출력 모양을 줄인 합성 데이터.
/// </summary>
public sealed class ClaudeCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-orch-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly ClaudeCliSettings Settings = new("claude", "sk-test", "HB_REVIEWER_API_KEY", "claude-opus-5-5", 40, 2.5m, TimeSpan.FromMinutes(1));

    private static string ResultJson(string text = "## 지적\n- 문제 없음", string stopReason = "end_turn") => JsonSerializer.Serialize(new
    {
        type = "result", subtype = "success", is_error = false, result = text, stop_reason = stopReason, num_turns = 17, total_cost_usd = 0.908,
        usage = new { input_tokens = 14, cache_creation_input_tokens = 83704, cache_read_input_tokens = 128087, output_tokens = 23193, output_tokens_details = new { thinking_tokens = 16666 } },
        modelUsage = new Dictionary<string, object> { ["claude-opus-5-5"] = new { costUSD = 0.908 } },
        permission_denials = Array.Empty<object>(),
    });

    private static ClaudeCliProcessResult Ok(string json) => new(0, json, "");

    // ── 명령줄 · 환경 ─────────────────────────────────────────────────────

    [Fact]
    public void Arguments_are_bare_read_only_and_json()
    {
        var args = ClaudeCliChatClient.Arguments(Settings, ReasoningEffort.High, "sys.md", "set.json");

        Assert.Equal(["--bare", "--restricted", "-p", "--no-session-persistence", "--output-format", "json"], args.Take(6));
        Assert.Equal("Read,Glob,Grep", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("dontAsk", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
        Assert.Equal("2.5", args[args.IndexOf("--max-budget-usd") + 1]); // 지역 설정과 상관없이 점
        Assert.Equal("40", args[args.IndexOf("--max-turns") + 1]);
        Assert.Equal("sys.md", args[args.IndexOf("--append-system-prompt-file") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("bypass", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(ReasoningEffort.None, null)]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public void Effort_maps_or_is_omitted(ReasoningEffort? effort, string? expected)
    {
        var args = ClaudeCliChatClient.Arguments(Settings with { MaxBudgetUsd = null }, effort, "s", "t");
        Assert.Equal(expected, args.Contains("--effort") ? args[args.IndexOf("--effort") + 1] : null);
        Assert.DoesNotContain("--max-budget-usd", args);
    }

    [Fact]
    public void Child_environment_sets_api_key_and_removes_other_auth()
    {
        var env = ClaudeCliChatClient.ChildEnvironment("sk-test", "HB_REVIEWER_API_KEY",
            ["PATH", "OPENAI_API_KEY", "GEMINI_API_KEY", "OPENAI_ADMIN_KEY", "GITHUB_TOKEN", "HB_REVIEWER_API_KEY", "ANTHROPIC_API_KEY"]);

        Assert.Equal("sk-test", env["ANTHROPIC_API_KEY"]);
        foreach (var name in new[] { "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "ANTHROPIC_BASE_URL",
                     "HB_REVIEWER_API_KEY", "OPENAI_API_KEY", "GEMINI_API_KEY", "OPENAI_ADMIN_KEY", "GITHUB_TOKEN" })
            Assert.Null(env[name]);
        Assert.False(env.ContainsKey("PATH"));                                   // 나머지 환경은 그대로 물려받는다
    }

    // ── 결과 JSON ────────────────────────────────────────────────────────

    [Fact]
    public void Success_maps_text_tokens_model_and_raw_usage()
    {
        var response = ClaudeCliChatClient.Parse(Ok(ResultJson("## 지적\n- C1 [major] A.cs:1")), "fallback-model");

        Assert.Equal("## 지적\n- C1 [major] A.cs:1", response.Text);
        Assert.Equal("claude-opus-5-5", response.ModelId);
        Assert.Equal(14 + 83704 + 128087, response.Usage!.InputTokenCount);
        Assert.Equal(128087, response.Usage.CachedInputTokenCount);
        Assert.Equal(23193, response.Usage.OutputTokenCount);
        Assert.Equal(16666, response.Usage.ReasoningTokenCount);

        var raw = UsageExtractor.RawUsageJson(response);
        Assert.NotNull(raw);
        Assert.DoesNotContain("C1 [major]", raw);                      // 본문은 기록의 응답 칸에만
        Assert.Contains("total_cost_usd", raw);
        Assert.Equal(83704, UsageExtractor.CacheWriteTokens(raw));
    }

    [Fact]
    public void Cost_from_tokens_matches_cli_report()
    {
        // 2026-10-27 시험: CLI total_cost_usd 0.9080534 — 단가 4 / 0.2 / 5 / 20으로 같은 값이 나와야 대시보드 비용이 맞다.
        var r = ClaudeCliChatClient.Parse(Ok(ResultJson()), "m");
        var price = new ModelPrice { InputPerMillion = 4, CachedInputPerMillion = 0.2m, CacheWritePerMillion = 5, OutputPerMillion = 20 };
        var record = new HB.Orchestration.Shared.CallRecord
        {
            Id = "x", Timestamp = DateTimeOffset.Now, Tool = "sub_self", Model = "claude-opus-5-5", Prompt = "p", Success = true,
            InputTokens = r.Usage!.InputTokenCount, CachedInputTokens = r.Usage.CachedInputTokenCount, CacheWriteTokens = 83704, OutputTokens = r.Usage.OutputTokenCount,
        };
        Assert.Equal(0.9080534m, Math.Round(price.CostOf(record), 7));
    }

    [Theory]
    [InlineData("error_max_turns", null, "턴 상한", null, false)]
    [InlineData("error_max_budget_usd", null, "비용 상한", null, false)]
    [InlineData("error_during_execution", 429, "크레딧", ErrorKind.Quota, false)]
    [InlineData("error_during_execution", 529, "서버 오류", ErrorKind.Server, true)]
    public void Failures_are_classified(string subtype, int? status, string message, ErrorKind? kind, bool retryable)
    {
        var json = JsonSerializer.Serialize(new { type = "result", subtype, is_error = true, api_error_status = status, result = status == 429 ? "Your credit balance is too low" : "x" });

        var ex = Assert.Throws<ClaudeCliException>(() => ClaudeCliChatClient.Parse(new(1, json, ""), "m"));

        Assert.Contains(message, ex.Message);
        Assert.Equal(retryable, ex.Retryable);
        if (kind is not null) Assert.Equal(kind, ex.Kind);
        var info = RetryClassifier.Classify(ex, CancellationToken.None);
        Assert.Equal((ex.Kind, ex.Retryable), (info.Kind, info.Retryable));
    }

    [Fact]
    public void Refusal_is_a_failure_not_an_empty_success()
    {
        var ex = Assert.Throws<ClaudeCliException>(() => ClaudeCliChatClient.Parse(Ok(ResultJson("", "refusal")), "m"));
        Assert.Contains("refusal", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "type": "result", "subtype": "success", "result": "x" }""")]  // usage 없음 = CLI 판 변경
    [InlineData("""[1, 2]""")]
    public void Unexpected_output_fails_loudly(string stdout)
    {
        var ex = Assert.Throws<ClaudeCliException>(() => ClaudeCliChatClient.Parse(new(0, stdout, "stderr text"), "m"));
        Assert.False(ex.Retryable);
    }

    // ── 채팅 클라이언트 ──────────────────────────────────────────────────

    private static ChatOptions WithFolder(string folder, ReasoningEffort? effort = null) => new()
    {
        Instructions = "역할 프롬프트",
        Reasoning = effort is null ? null : new ReasoningOptions { Effort = effort },
        AdditionalProperties = new() { [ClaudeCliChatClient.WorkFolderKey] = folder },
    };

    [Fact]
    public async Task Runs_in_work_folder_with_prompt_on_stdin_and_system_prompt_file()
    {
        ClaudeCliInvocation? seen = null;
        string? systemText = null;
        var client = new ClaudeCliChatClient(Settings, (inv, _, _) =>
        {
            seen = inv;
            systemText = File.ReadAllText(inv.Arguments[inv.Arguments.ToList().IndexOf("--append-system-prompt-file") + 1]);
            return Task.FromResult(Ok(ResultJson()));
        });

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "## 요청\n리뷰해 줘")], WithFolder(_dir, ReasoningEffort.High));

        Assert.Equal(_dir, seen!.WorkingDirectory);
        Assert.Equal("## 요청\n리뷰해 줘", seen.Stdin);
        Assert.Equal("역할 프롬프트", systemText);
        Assert.Equal("sk-test", seen.Environment["ANTHROPIC_API_KEY"]);
        Assert.Contains("high", seen.Arguments);
        Assert.Equal("## 지적\n- 문제 없음", response.Text);
        Assert.False(Directory.Exists(Path.GetDirectoryName(seen.Arguments[seen.Arguments.ToList().IndexOf("--settings") + 1])));  // 임시 파일 정리
    }

    [Fact]
    public async Task Without_work_folder_it_does_not_run()
    {
        var ran = false;
        var client = new ClaudeCliChatClient(Settings, (_, _, _) => { ran = true; return Task.FromResult(Ok(ResultJson())); });

        await Assert.ThrowsAsync<ClaudeCliException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], new ChatOptions()));
        await Assert.ThrowsAsync<ClaudeCliException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], WithFolder(Path.Combine(_dir, "nope"))));
        Assert.False(ran);
    }

    [Fact]
    public async Task Follow_up_history_is_rejected()
    {
        var client = new ClaudeCliChatClient(Settings, (_, _, _) => Task.FromResult(Ok(ResultJson())));
        var ex = await Assert.ThrowsAsync<ClaudeCliException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.Assistant, "b"), new ChatMessage(ChatRole.User, "c")], WithFolder(_dir)));
        Assert.Contains("followUp", ex.Message);
    }

    [Fact]
    public void Provider_refuses_the_claude_code_key_name_and_missing_key()
    {
        var provider = new ClaudeCliProvider();
        var agent = new AgentOptions { Name = "c", Provider = "p", Model = "claude-opus-5-5" };

        Assert.Throws<ProviderConfigurationException>(() => provider.Create("p", new ProviderOptions { Type = "claude-cli", ApiKeyEnv = "ANTHROPIC_API_KEY" }, agent));
        Assert.Throws<ProviderConfigurationException>(() => provider.Create("p", new ProviderOptions { Type = "claude-cli", ApiKeyEnv = "HB_TEST_NO_SUCH_KEY_7f3" }, agent));
        Assert.Throws<ProviderConfigurationException>(() => provider.Create("p", new ProviderOptions { Type = "claude-cli" }, agent));
    }

    // ── 작업 폴더 ────────────────────────────────────────────────────────

    private string Src(string relative, string content)
    {
        var path = Path.Combine(_dir, "repo", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Work_folder_has_whole_files_and_focus_list()
    {
        Src("src/A.cs", "line1\nline2\nline3\n");
        Src("src/B.cs", "b\n");
        var baseDir = Path.Combine(_dir, "review_in");

        var (folder, focus, error) = ReviewWorkFolder.Create(baseDir,
            [new ContextRange("src/A.cs", 2, 2), new ContextRange("src/A.cs", 3, 3), new ContextRange("src\\B.cs", 1, 1)],
            new ContextOptions(), Path.Combine(_dir, "repo"));

        Assert.Null(error);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(Path.Combine(folder!, "src", "A.cs")));  // 범위가 아니라 파일 전체
        Assert.Equal("b\n", File.ReadAllText(Path.Combine(folder!, "src", "B.cs")));
        Assert.Contains("- src/A.cs:2-2", focus);
        Assert.Contains("- src/A.cs:3-3", focus);
        Assert.Contains("- src/B.cs:1-1", focus);
        Assert.DoesNotContain("line2", focus);                                            // 본문은 프롬프트에 넣지 않는다
        Assert.Matches(@"- src/A\.cs · [0-9a-f]{12} · 3줄", focus);
        Assert.Matches(@"- src/B\.cs · [0-9a-f]{12} · 1줄", focus);
        Assert.DoesNotContain("정의:", focus);                                           // 정의가 없는 파일은 색인 줄 없음
        Assert.Equal(2, Directory.EnumerateFiles(folder!, "*", SearchOption.AllDirectories).Count());

        ReviewWorkFolder.TryDelete(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Work_folder_list_has_definition_index_under_each_file()
    {
        Src("src/Runner.cs", "namespace X;\npublic sealed class Runner\n{\n    public void Run() { }\n}");
        Src("docs/plan.md", "# 제목\n## 계획\n");
        Src("notes.txt", "public class NotIndexed {}\n");

        var (folder, focus, error) = ReviewWorkFolder.Create(Path.Combine(_dir, "review_in"),
            [new ContextRange("src/Runner.cs", 1, 5), new ContextRange("docs/plan.md", 1, 2), new ContextRange("notes.txt", 1, 1)],
            new ContextOptions(), Path.Combine(_dir, "repo"));

        Assert.Null(error);
        var lines = focus!.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var runner = lines.FindIndex(l => l.StartsWith("- src/Runner.cs · "));
        Assert.EndsWith(" · 5줄", lines[runner]);                                             // 마지막 줄에 줄바꿈이 없어도 센다
        Assert.Equal("  정의: class Runner 2 · Run() 4", lines[runner + 1]);
        Assert.Equal("  정의: # 제목 1 · ## 계획 2", lines[lines.FindIndex(l => l.StartsWith("- docs/plan.md · ")) + 1]);
        var notes = lines.FindIndex(l => l.StartsWith("- notes.txt · "));
        Assert.False(lines[notes + 1].StartsWith("  정의:"));                                  // 모르는 확장자는 색인 없음
        ReviewWorkFolder.TryDelete(folder);
    }

    [Fact]
    public void Index_budget_follows_context_order_and_names_skipped_files()   // sub_self 535ca00022 C3 · C8
    {
        // 파일마다 정의 80개 × 약 70자 ≈ 5.6천 자 — 다섯 개면 전체 상한(2만 자)을 넘는다
        string Methods(string prefix) => "public class C\n{\n" + string.Concat(Enumerable.Range(1, 79)
            .Select(i => $"    public void {prefix}{new string('x', 55)}{i:D2}() {{ }}\n")) + "}\n";
        foreach (var name in new[] { "a", "b", "c", "d", "e" }) Src($"src/{name}.cs", Methods(name));

        var (folder, focus, error) = ReviewWorkFolder.Create(Path.Combine(_dir, "review_in"),
            new[] { "e", "d", "c", "b", "a" }.Select(n => new ContextRange($"src/{n}.cs", 1, 1)).ToList(), new ContextOptions(), Path.Combine(_dir, "repo"));

        Assert.Null(error);
        var lines = focus!.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        bool Indexed(string n) => lines[lines.FindIndex(l => l.StartsWith($"- src/{n}.cs · ")) + 1].StartsWith("  정의:");
        Assert.True(Indexed("e") && Indexed("d") && Indexed("c"));                               // 앞에 넘긴 파일이 먼저
        Assert.False(Indexed("a") || Indexed("b"));
        Assert.Contains(lines, l => l.Contains("색인을 뺀 파일: src/a.cs, src/b.cs"));
        Assert.True(lines.FindIndex(l => l.StartsWith("- src/a.cs")) < lines.FindIndex(l => l.StartsWith("- src/e.cs")));   // 목록은 경로 순
        ReviewWorkFolder.TryDelete(folder);
    }

    [Fact]
    public void Utf16_file_line_count_is_unknown()   // C7
    {
        var path = Path.Combine(_dir, "repo", "u16.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "function A {}\n갊\n", System.Text.Encoding.Unicode);   // '갊' U+AC0A — 하위 바이트 0x0A

        var (folder, focus, error) = ReviewWorkFolder.Create(Path.Combine(_dir, "review_in"), [new ContextRange("u16.ps1", 1, 1)], new ContextOptions(), Path.Combine(_dir, "repo"));

        Assert.Null(error);
        Assert.Matches(@"- u16\.ps1 · [0-9a-f]{12} · 줄 수 모름\(UTF-16\)", focus);
        Assert.DoesNotContain("정의:", focus);                                                  // UTF-8이 아니면 색인 없음
        ReviewWorkFolder.TryDelete(folder);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("missing.cs")]
    public void Bad_paths_fail_and_leave_no_folder(string path)
    {
        File.WriteAllText(Path.Combine(_dir, "outside.cs"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "repo"));
        var baseDir = Path.Combine(_dir, "review_in");

        var (folder, _, error) = ReviewWorkFolder.Create(baseDir, [new ContextRange(path, 1, 1)], new ContextOptions(), Path.Combine(_dir, "repo"));

        Assert.Null(folder);
        Assert.NotNull(error);
        Assert.Empty(Directory.Exists(baseDir) ? Directory.EnumerateDirectories(baseDir) : []);
    }

    [Fact]
    public void Context_is_required()
    {
        var (_, _, error) = ReviewWorkFolder.Create(Path.Combine(_dir, "review_in"), [], new ContextOptions(), _dir);
        Assert.Contains("context가 필요", error);
    }

    // ── ModeRunner 연결 ──────────────────────────────────────────────────

    private const string CliModes = """
        {
          "Roles": { "R": { "Prompt": "역할 R" } },
          "Modes": {
            "cli":   { "Steps": [ { "Role": "R", "Agent": "a1" } ] }
          }
        }
        """;

    [Fact]
    public async Task Mode_run_passes_work_folder_and_focus_only_then_cleans_up()
    {
        using var h = new ModeRunnerHarness(CliModes, providerType: ClaudeCliProvider.TypeName);
        var name = $"cli_ctx_{Guid.NewGuid():N}.cs";
        var file = Path.Combine(Environment.CurrentDirectory, name);
        File.WriteAllText(file, "class Secret { int BodyOnlyInFolder; }\n");
        string? folderDuringCall = null;
        var existedDuringCall = false;
        h.Client.Script = [_ =>
        {
            folderDuringCall = h.Client.Received[^1].Options?.AdditionalProperties?[ClaudeCliChatClient.WorkFolderKey] as string;
            existedDuringCall = folderDuringCall is not null && File.Exists(Path.Combine(folderDuringCall, name));
            return ScriptedChatClient.Reply("## 지적\n- 문제 없음")(default);
        }];
        try
        {
            var result = await h.Runner.RunAsync("cli", "변경 의도", null, null, [new ContextRange(name, 1, 1)], null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

            Assert.Contains("문제 없음", result);
            Assert.StartsWith("### 1.", result.Split('\n')[1]);                          // 첨부 여유 줄 없음 (첨부 본문을 보내지 않음 — sub_self 11b9c52eba C6)
            Assert.True(existedDuringCall);
            var received = h.Client.Received[^1];
            Assert.DoesNotContain(received.Messages, m => m.Role == ChatRole.System);   // 역할 프롬프트는 Instructions로만 (시스템 프롬프트 중복 없음 — 지휘자 K4)
            Assert.False(string.IsNullOrWhiteSpace(received.Options?.Instructions));
            var prompt = received.Messages.Last().Text;
            Assert.Contains($"- {name} · ", prompt);                                    // 파일 목록 + 해시 앞자리
            Assert.Contains($"- {name}:1-1", prompt);
            Assert.DoesNotContain("BodyOnlyInFolder", prompt);                       // 코드는 폴더에만
            Assert.False(Directory.Exists(folderDuringCall));                         // 실행 뒤 정리
            Assert.Equal([$"{name}:1-1"], h.Records().Single().Context);              // 기록에는 범위가 남는다
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Mode_without_context_is_rejected_before_any_call()
    {
        using var h = new ModeRunnerHarness(CliModes, providerType: ClaudeCliProvider.TypeName);

        var result = await h.Run("cli");

        Assert.StartsWith("[첨부 코드 오류]", result);
        Assert.Equal(0, h.Client.Calls);
        Assert.Empty(h.Records());
    }

    // ── /self-review 694a… 이후: sub_self 시범 1호 리뷰 반영 (a9785cf8c1 · f5f1948b66 · defef6240a) ──────

    [Fact]
    public void Executable_is_resolved_to_an_absolute_path_skipping_relative_path_entries()
    {
        var bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, OperatingSystem.IsWindows() ? "claude.exe" : "claude"), "");  // 현재 폴더의 가짜는 무시돼야 한다
        try
        {
            Assert.Equal(exe, ClaudeCliProvider.ResolveExecutable("p", null, string.Join(Path.PathSeparator, ".", "", bin)));
            Assert.Throws<ProviderConfigurationException>(() => ClaudeCliProvider.ResolveExecutable("p", null, "."));
            Assert.Equal(exe, ClaudeCliProvider.ResolveExecutable("p", exe));
            Assert.Throws<ProviderConfigurationException>(() => ClaudeCliProvider.ResolveExecutable("p", "claude.exe"));      // 설정 값은 절대 경로만
            Assert.Throws<ProviderConfigurationException>(() => ClaudeCliProvider.ResolveExecutable("p", Path.Combine(bin, "nope.exe")));
        }
        finally
        {
            File.Delete(Path.Combine(Environment.CurrentDirectory, OperatingSystem.IsWindows() ? "claude.exe" : "claude"));
        }
    }

    [Fact]
    public void Key_name_is_checked_before_reading_its_value()
    {
        var ex = Assert.Throws<ProviderConfigurationException>(() => new ClaudeCliProvider().Create("p",
            new ProviderOptions { Type = "claude-cli", ApiKeyEnv = "ANTHROPIC_API_KEY" }, new AgentOptions { Name = "c", Provider = "p", Model = "m" }));
        Assert.Contains("쓰지 않는다", ex.Message);                                   // "비어 있습니다"가 아니라 이름 문제를 알린다
    }

    [Fact]
    public void Records_do_not_print_the_api_key()
    {
        var invocation = new ClaudeCliInvocation("claude", ["-p"], _dir, ClaudeCliChatClient.ChildEnvironment("example-key-123", "K", []), "프롬프트");

        Assert.DoesNotContain("example-key-123", (Settings with { ApiKey = "example-key-123" }).ToString());
        Assert.DoesNotContain("example-key-123", invocation.ToString());
        Assert.Contains("ANTHROPIC_API_KEY", invocation.ToString());                  // 이름은 보인다
    }

    [Fact]
    public async Task Effort_none_is_rejected_without_running()
    {
        var ran = false;
        var client = new ClaudeCliChatClient(Settings, (_, _, _) => { ran = true; return Task.FromResult(Ok(ResultJson())); });

        var ex = await Assert.ThrowsAsync<ClaudeCliException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], WithFolder(_dir, ReasoningEffort.None)));

        Assert.Contains("none", ex.Message);
        Assert.False(ran);
    }

    [Fact]
    public void Credit_balance_is_quota_even_as_400()
    {
        var json = JsonSerializer.Serialize(new { type = "result", subtype = "error_during_execution", is_error = true, api_error_status = 400,
            result = "Your credit balance is too low to access the Anthropic API." });

        var ex = Assert.Throws<ClaudeCliException>(() => ClaudeCliChatClient.Parse(new(1, json, ""), "m"));

        Assert.Equal((ErrorKind.Quota, false), (ex.Kind, ex.Retryable));
    }

    [Fact]
    public void Failed_run_keeps_spent_tokens()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "result", subtype = "error_max_budget_usd", is_error = true, total_cost_usd = 2.1,
            usage = new { input_tokens = 10, cache_creation_input_tokens = 1000, cache_read_input_tokens = 50000, output_tokens = 9000 },
        });

        var ex = Assert.Throws<ClaudeCliException>(() => ClaudeCliChatClient.Parse(new(1, json, ""), "m"));

        Assert.Equal(new[] { 51010L, 50000, 1000, 9000 }, new[] { ex.Spent!.InputTokens, ex.Spent.CachedInputTokens, ex.Spent.CacheWriteTokens, ex.Spent.OutputTokens });
        Assert.Contains("total_cost_usd", ex.Spent.RawJson);
    }

    [Fact]
    public async Task Failed_run_tokens_reach_the_call_record()
    {
        using var h = new ModeRunnerHarness(CliModes, providerType: ClaudeCliProvider.TypeName);
        var name = $"cli_fail_{Guid.NewGuid():N}.cs";
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, name), "x\n");
        h.Client.Script = [ScriptedChatClient.Throw(new ClaudeCliException("[claude-cli] 비용 상한", ErrorKind.Client, false, null,
            new ClaudeCliSpent(51010, 50000, 1000, 9000, null, """{"total_cost_usd":2.1}""")))];
        try
        {
            await h.Runner.RunAsync("cli", "의도", null, null, [new ContextRange(name, 1, 1)], null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

            var r = h.Records().Single();
            Assert.False(r.Success);
            Assert.Equal((51010L, 50000L, 1000L, 9000L), (r.InputTokens, r.CachedInputTokens, r.CacheWriteTokens, r.OutputTokens));
            Assert.Contains("total_cost_usd", r.UsageRaw);
        }
        finally
        {
            File.Delete(Path.Combine(Environment.CurrentDirectory, name));
        }
    }

    [Fact]
    public void Denials_become_a_warning()
    {
        Assert.Null(ClaudeCliChatClient.DenialWarning("""{"permission_denials":[]}"""));
        Assert.Null(ClaudeCliChatClient.DenialWarning("""{"prompt_tokens":1}"""));
        Assert.Null(ClaudeCliChatClient.DenialWarning(null));

        var warning = ClaudeCliChatClient.DenialWarning("""{"permission_denials":[{"tool_name":"Read","tool_input":{"file_path":"C:\\data\\calls.jsonl"}}]}""");

        Assert.StartsWith("[경고] claude-cli 도구 사용 1건이 거절됨", warning);
        Assert.Contains("calls.jsonl", warning);
    }

    [Theory]
    [InlineData("@foo.cs", "@foo.cs")]          // 메인 루트의 '@'로 시작하는 파일 (예전: 범위 밖 예외)
    [InlineData("@lib/x.cs", "@lib/x.cs")]      // 등록 안 된 '@lib' 폴더 (예전: @lib/@lib/x.cs)
    public void At_paths_that_are_not_extra_roots_stay_as_they_are(string path, string inside)
    {
        Src(path, "x\n");

        var (folder, focus, error) = ReviewWorkFolder.Create(Path.Combine(_dir, "review_in"), [new ContextRange(path, 1, 1)], new ContextOptions(), Path.Combine(_dir, "repo"));

        Assert.Null(error);
        Assert.True(File.Exists(Path.Combine(folder!, inside)));
        Assert.Contains($"- {inside}:1-1", focus);
        ReviewWorkFolder.TryDelete(folder);
    }

    [Fact]
    public void Total_copy_size_is_capped_and_file_list_has_hashes()
    {
        Src("big1.bin.txt", new string('a', (int)(ReviewWorkFolder.MaxTotalBytes / 2) + 10));
        Src("big2.bin.txt", new string('b', (int)(ReviewWorkFolder.MaxTotalBytes / 2) + 10));
        Src("small.cs", "abc");
        var options = new ContextOptions { MaxFileBytes = 0 };
        var baseDir = Path.Combine(_dir, "review_in");

        var (_, _, error) = ReviewWorkFolder.Create(baseDir, [new ContextRange("big1.bin.txt", 1, 1), new ContextRange("big2.bin.txt", 1, 1)], options, Path.Combine(_dir, "repo"));
        Assert.Contains("합계", error);
        Assert.Empty(Directory.EnumerateDirectories(baseDir));

        var (folder, focus, ok) = ReviewWorkFolder.Create(baseDir, [new ContextRange("small.cs", 1, 1)], options, Path.Combine(_dir, "repo"));
        Assert.Null(ok);
        Assert.Contains("- small.cs · ba7816bf8f01", focus);                       // sha256("abc") 앞 12자
        ReviewWorkFolder.TryDelete(folder);
    }

    // ── 2차 설계: 크레딧 소진 대체 ────────────────────────────────────────

    private static HB.Orchestration.Shared.CallRecord Rec(DateTimeOffset at, string provider, bool success, string? kind = null) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = at, Tool = "sub_self", Mode = "sub_self", Provider = provider,
        Model = "claude-opus-5-5", Prompt = "p", Success = success, ErrorKind = kind,
    };

    [Fact]
    public async Task Recent_quota_is_found_only_within_hold_and_after_last_success()
    {
        var log = Path.Combine(_dir, "calls.jsonl");
        var now = DateTimeOffset.Now;
        var hold = ModeRunner.ClaudeCliQuotaHold;
        Assert.Equal(TimeSpan.FromHours(1), hold);

        await HB.Orchestration.Shared.CallLog.AppendAsync(Rec(now.AddHours(-3), "cli", true), log);
        Assert.Null(ModeRunner.RecentQuota(["cli"], log, hold, now));

        await HB.Orchestration.Shared.CallLog.AppendAsync(Rec(now.AddMinutes(-40), "cli", false, "Quota"), log);
        await HB.Orchestration.Shared.CallLog.AppendAsync(Rec(now.AddMinutes(-20), "cli", false, "Timeout"), log);   // 크레딧 상태를 알려 주지 않음 → 건너뜀
        await HB.Orchestration.Shared.CallLog.AppendAsync(Rec(now.AddMinutes(-10), "other", true), log);           // 다른 제공사
        Assert.NotNull(ModeRunner.RecentQuota(["cli"], log, hold, now));
        Assert.Null(ModeRunner.RecentQuota(["cli"], log, hold, now.AddMinutes(30)));                           // 1시간 지나면 다시 시도

        await HB.Orchestration.Shared.CallLog.AppendAsync(Rec(now.AddMinutes(-5), "cli", true), log);            // 충전 뒤 성공
        Assert.Null(ModeRunner.RecentQuota(["cli"], log, hold, now));
    }

    [Fact]
    public async Task Cli_mode_after_recent_quota_fails_fast_without_calling()
    {
        using var h = new ModeRunnerHarness(CliModes, providerType: ClaudeCliProvider.TypeName);
        await HB.Orchestration.Shared.CallLog.AppendAsync(Rec(DateTimeOffset.Now.AddMinutes(-5), "fake", false, "Quota"), h.LogPath);
        var name = $"cli_quota_{Guid.NewGuid():N}.cs";
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, name), "x\n");
        try
        {
            var result = await h.Runner.RunAsync("cli", "의도", null, null, [new ContextRange(name, 1, 1)], null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

            Assert.StartsWith(AgentRunner.QuotaFailurePrefix, result);
            Assert.Contains("서브에이전트", result);
            Assert.Equal(0, h.Client.Calls);
            Assert.Empty(Directory.Exists(ReviewWorkFolder.BaseFor(h.LogPath)) ? Directory.EnumerateDirectories(ReviewWorkFolder.BaseFor(h.LogPath)) : []);
        }
        finally
        {
            File.Delete(Path.Combine(Environment.CurrentDirectory, name));
        }
    }

    [Fact]
    public void Template_has_all_sub_review_modes_on_the_cli_agent()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HB.Orchestration.Mcp", "modes.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var (mode, role) in new[] { ("sub_self", "SubSelf"), ("sub_perf", "SubPerf"), ("sub_security", "SubSecurity"), ("sub_direction", "SubDirection") })
        {
            var step = doc.RootElement.GetProperty("Modes").GetProperty(mode).GetProperty("Steps")[0];
            Assert.Equal((role, "claude_cli_opus"), (step.GetProperty("Role").GetString(), step.GetProperty("Agent").GetString()));
            var prompt = doc.RootElement.GetProperty("Roles").GetProperty(role).GetProperty("Prompt").GetString()!;
            Assert.Contains("작업 폴더", prompt);
            Assert.DoesNotContain("subagent_in", prompt);                                 // 서브에이전트용 폴더 지시가 남지 않게
            Assert.Contains("## 지적", prompt);
        }
    }

    [Fact]
    public async Task Cli_mode_is_not_limited_by_unsent_excerpt_length()
    {
        const string modes = """
            { "Roles": { "R": { "Prompt": "역할 R" } },
              "Modes": { "cli": { "MaxInputChars": 200, "Steps": [ { "Role": "R", "Agent": "a1" } ] } } }
            """;
        using var h = new ModeRunnerHarness(modes, providerType: ClaudeCliProvider.TypeName);
        var name = $"cli_big_{Guid.NewGuid():N}.cs";
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, name), string.Concat(Enumerable.Repeat("// 긴 줄로 첨부 본문이 상한을 넘게 한다\n", 40)));
        try
        {
            var result = await h.Runner.RunAsync("cli", "의도", null, null, [new ContextRange(name, 1, 40)], null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

            Assert.DoesNotContain("[입력이 너무 깁니다]", result);
            Assert.DoesNotContain("[첨부 코드 오류]", result);
            Assert.Equal(1, h.Client.Calls);
        }
        finally
        {
            File.Delete(Path.Combine(Environment.CurrentDirectory, name));
        }
    }
}
