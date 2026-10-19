using System.ClientModel;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace HB.Orchestration.Tests;

/// <summary>크레딧·한도 소진(Quota) 재시도·연속 실행 방지 (계획서 2026-10-19_크레딧소진-재시도방지).</summary>
public sealed class QuotaTests
{
    /// <summary>2026-10-19 실제 오류 문구 그대로.</summary>
    private static ClientResultException QuotaError() =>
        new("HTTP 429 (insufficient_quota: credit_balance_exhausted)", new FakeResponse(429));

    // ── 분류 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Insufficient_quota_429_is_quota_and_not_retried()
    {
        var f = RetryClassifier.Classify(QuotaError(), CancellationToken.None);
        Assert.Equal((ErrorKind.Quota, false, 429), (f.Kind!.Value, f.Retryable, f.Status));
    }

    [Fact]
    public void Quota_code_only_in_response_body_is_also_quota()
    {
        var body = """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""";
        var f = RetryClassifier.Classify(new ClientResultException("HTTP 429", new FakeResponse(429, body: body)), CancellationToken.None);
        Assert.Equal(ErrorKind.Quota, f.Kind);
    }

    [Fact]
    public void Plain_429_stays_rate_limit_and_retried()
    {
        var body = """{"error":{"message":"Rate limit reached","code":"rate_limit_exceeded"}}""";
        var f = RetryClassifier.Classify(new ClientResultException("HTTP 429", new FakeResponse(429, body: body)), CancellationToken.None);
        Assert.Equal((ErrorKind.RateLimit, true), (f.Kind!.Value, f.Retryable));
    }

    [Fact]
    public async Task Quota_failure_calls_once_and_says_do_not_rerun()
    {
        using var h = new ModeRunnerHarness();
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(QuotaError()));
        var agent = new AgentEntry(new AgentOptions { Name = "a", Provider = "openai", Model = "m", MaxAttempts = 3 }, client);
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, h.LogPath);

        var result = await runner.RunStepAsync(new StepCall("r000000001", "m", 1, "R", agent, "i", "질문", new CallMeta(null, [])), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, client.Calls);                                     // MaxAttempts 3이어도 다시 보내지 않음
        Assert.StartsWith($"{AgentRunner.QuotaFailurePrefix} — 크레딧/한도 소진, 충전 전까지 재실행 금지 (openai)", result.Text);
        Assert.Equal("Quota", h.Records().Single().ErrorKind);
    }

    [Fact]
    public async Task Rate_limit_then_quota_stops_retrying_with_quota_text()
    {
        using var h = new ModeRunnerHarness();
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(FakeResponse.Error(429)), ScriptedChatClient.Throw(QuotaError()));
        var agent = new AgentEntry(new AgentOptions { Name = "a", Provider = "openai", Model = "m", MaxAttempts = 5 }, client);
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, h.LogPath);

        var result = await runner.RunStepAsync(new StepCall("r000000001", "m", 1, "R", agent, "i", "질문", new CallMeta(null, [])), CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.StartsWith(AgentRunner.QuotaFailurePrefix, result.Text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Text, System.Text.RegularExpressions.Regex.Escape(AgentRunner.QuotaFailurePrefix))); // 머리는 한 번
    }

    [Fact]
    public async Task Plain_429_is_retried_up_to_max_attempts()
    {
        using var h = new ModeRunnerHarness();
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(FakeResponse.Error(429)));
        var agent = new AgentEntry(new AgentOptions { Name = "a", Provider = "openai", Model = "m", MaxAttempts = 3 }, client);
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, h.LogPath);

        await runner.RunStepAsync(new StepCall("r000000001", "m", 1, "R", agent, "i", "질문", new CallMeta(null, [])), CancellationToken.None);

        Assert.Equal(3, client.Calls);
    }

    // ── 골든 일괄 실행 ──────────────────────────────────────────────────

    private const string Modes = """
        {
          "Roles": { "Rev": { "Prompt": "역할", "Output": "findings" }, "Tri": { "Prompt": "분류자" }, "Sea": { "Prompt": "검색자", "UseGuidelines": false } },
          "Modes": {
            "r": { "Steps": [ { "Role": "Rev", "Agent": "a1" } ] },
            "c": { "Steps": [ { "Role": "Tri", "Agent": "a1" }, { "Role": "Sea", "Agent": "a1", "Sees": "InputAndPrevious", "WebSearch": { "MaxCalls": 3 } } ] }
          }
        }
        """;

    private sealed class Dir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hb-golden-quota-").FullName;

        public void Add(string id, string kind = "seeded", string mode = "r")
        {
            var dir = Directory.CreateDirectory(Path.Combine(Root, id)).FullName;
            File.WriteAllText(Path.Combine(dir, "case.json"), $$"""{ "id": "{{id}}", "kind": "{{kind}}", "mode": "{{mode}}" }""");
            File.WriteAllText(Path.Combine(dir, "input.md"), "## 요청\n봐 줘");
            File.WriteAllText(Path.Combine(dir, "expected.json"), kind == "crash"
                ? """[ { "id": "T", "step": "triage", "keywords": ["원인"] } ]"""
                : """[ { "id": "E1", "keywords": ["k"] } ]""");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<(int Code, string Out, string Err)> Cli(ModeRunnerHarness h, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await GoldenCli.RunAsync(["--golden", .. args], h.Options, () => h.Runner, h.LogPath, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Quota_in_first_case_stops_remaining_cases()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new Dir();
        d.Add("a"); d.Add("b"); d.Add("c");
        h.Client.Script = [ScriptedChatClient.Throw(QuotaError()), ScriptedChatClient.Reply("""{"findings":[],"needsInfo":[]}""")];

        var (code, _, err) = await Cli(h, "run", "all", "--dir", d.Root, "--yes");

        Assert.Equal(3, code);
        Assert.Equal(1, h.Client.Calls);  // b · c는 호출 0
        Assert.Contains("[Quota]", err);
    }

    /// <param name="errorKind">null이면 성공 기록.</param>
    private static Task SeedRecord(ModeRunnerHarness h, string? errorKind, string provider = "fake") =>
        CallLog.AppendAsync(new CallRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12], Timestamp = DateTimeOffset.Now, Tool = "run_mode", Provider = provider, Agent = "a1", Model = "m", Prompt = "p",
            Success = errorKind is null, ErrorKind = errorKind,
        }, h.LogPath);

    [Theory]
    [InlineData("Timeout", "fake")]   // 판정 불가 실패는 건너뜀
    [InlineData("Network", "fake")]
    [InlineData(null, "other")]       // 다른 제공사의 성공으로는 안 풀림
    public async Task Quota_block_is_not_cleared_by_undecisive_failure_or_other_provider(string? laterKind, string laterProvider)
    {
        using var h = new ModeRunnerHarness(Modes, providerType: OpenAIResponsesProvider.TypeName);
        using var d = new Dir();
        d.Add("x", kind: "crash", mode: "c");
        await SeedRecord(h, "Quota");
        await SeedRecord(h, laterKind, laterProvider);

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(3, code);
        Assert.Equal(0, h.Client.Calls);
        Assert.Contains("[Quota] 제공사 fake", err); // 푸는 방법을 제공사와 함께
    }

    [Fact]
    public async Task Quota_during_crash_search_step_stops_remaining_cases()
    {
        using var h = new ModeRunnerHarness(Modes, providerType: OpenAIResponsesProvider.TypeName);
        using var d = new Dir();
        d.Add("x1", kind: "crash", mode: "c");
        d.Add("x2", kind: "crash", mode: "c");
        h.Client.Script = [ScriptedChatClient.Reply("원인 후보"), ScriptedChatClient.Throw(QuotaError()), ScriptedChatClient.Reply("다음 사례")];

        var (code, _, err) = await Cli(h, "run", "all", "--dir", d.Root, "--yes");

        Assert.Equal(3, code);
        Assert.Equal(2, h.Client.Calls);  // x1의 분류·검색만, x2는 호출 0 (2026-10-19 사고 경로)
        Assert.Contains("[Quota] 제공사 fake", err);
    }

    [Fact]
    public async Task Crash_case_does_not_call_any_step_when_a_step_provider_last_failed_with_quota()
    {
        using var h = new ModeRunnerHarness(Modes, providerType: OpenAIResponsesProvider.TypeName);
        using var d = new Dir();
        d.Add("x", kind: "crash", mode: "c");
        await SeedRecord(h, "Quota");

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(3, code);
        Assert.Equal(0, h.Client.Calls);  // 1단계(분류)도 돌리지 않음
        Assert.Contains("직전 호출이 Quota", err);
    }

    [Fact]
    public async Task Crash_case_runs_once_the_provider_has_a_later_success()
    {
        using var h = new ModeRunnerHarness(Modes, providerType: OpenAIResponsesProvider.TypeName);
        using var d = new Dir();
        d.Add("x", kind: "crash", mode: "c");
        await SeedRecord(h, "Quota");
        await SeedRecord(h, null); // 충전 뒤 작은 확인 호출 성공
        h.Client.Script = [ScriptedChatClient.Reply("원인 후보"), ScriptedChatClient.Reply("검색 결과")];

        var (_, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.True(h.Client.Calls == 2, err); // 가장 최근 기록(성공)을 본다
        Assert.DoesNotContain("[Quota]", err);
    }
}
