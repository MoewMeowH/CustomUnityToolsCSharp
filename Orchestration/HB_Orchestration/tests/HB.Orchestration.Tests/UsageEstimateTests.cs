using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace HB.Orchestration.Tests;

/// <summary>끊긴 호출 비용 추정: 읽을 때 추정 · 호출 중 표시 · Lost 기록.</summary>
public sealed class UsageEstimateTests : IDisposable
{
    private readonly string _log = Path.Combine(Directory.CreateTempSubdirectory("hb-orch-est-").FullName, "calls.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_log)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CallRecord Rec(string id, bool success, string? kind = null, int attempt = 1, long? input = null, long? output = null,
        string prompt = "p", string? agent = "a") => new()
    {
        Id = id, Timestamp = DateTimeOffset.Now, Tool = "review", RunId = "run1", Step = 1, Attempt = attempt, Agent = agent,
        Model = "m", Prompt = prompt, Success = success, ErrorKind = kind, InputTokens = input, OutputTokens = output,
    };

    private string InflightDir => InflightLog.FolderFor(_log);

    // ── 읽을 때 추정 ──

    [Fact]
    public void Network_with_successful_retry_uses_its_input_and_output()
    {
        var est = UsageEstimator.Apply([Rec("ok", true, attempt: 2, input: 12_000, output: 16_000), Rec("cut", false, "Network")]);

        var cut = est.Single(r => r.Id == "cut");
        Assert.Equal(12_000, cut.InputTokens);
        Assert.Equal(16_000, cut.OutputTokens);
        Assert.Equal(new UsageEstimate(EstimateBasis.Sibling, false), cut.Estimate);
        Assert.Null(est.Single(r => r.Id == "ok").Estimate);
    }

    [Theory]
    [InlineData("Canceled")]
    [InlineData(InflightLog.LostKind)]
    public void Canceled_or_lost_with_sibling_counts_input_only(string kind)
    {
        var cut = UsageEstimator.Apply([Rec("ok", true, input: 12_000, output: 16_000), Rec("cut", false, kind)]).Single(r => r.Id == "cut");

        Assert.Equal(12_000, cut.InputTokens);
        Assert.Null(cut.OutputTokens);
        Assert.Equal(EstimateBasis.Sibling, cut.Estimate!.Basis);
    }

    [Fact]
    public void Sibling_must_be_same_agent()
    {
        var cut = UsageEstimator.Apply([Rec("ok", true, input: 12_000, agent: "other"), Rec("cut", false, "Timeout")]).Single(r => r.Id == "cut");

        Assert.Equal(EstimateBasis.Bytes, cut.Estimate!.Basis);
    }

    [Fact]
    public void Without_sibling_estimates_input_from_bytes()
    {
        var prompt = new string('가', 1000); // 3,000바이트
        var timeout = Rec("t", false, "Timeout", prompt: prompt) with { Provenance = new CallProvenance { InstructionsBytes = 1_000 } };
        var canceled = Rec("c", false, "Canceled", prompt: prompt) with { RunId = "run2" };

        var est = UsageEstimator.Apply([timeout, canceled]);

        var t = est.Single(r => r.Id == "t");
        Assert.Equal((long)Math.Round(4_000 * UsageEstimator.TokensPerByte), t.InputTokens);
        Assert.Null(t.OutputTokens);
        Assert.Equal(new UsageEstimate(EstimateBasis.Bytes, OutputUnknown: true), t.Estimate);

        var c = est.Single(r => r.Id == "c");
        Assert.Equal((long)Math.Round(3_000 * UsageEstimator.TokensPerByte), c.InputTokens);
        Assert.False(c.Estimate!.OutputUnknown); // 취소는 생성이 멈춘 것으로 본다
    }

    [Fact]
    public void Blob_prompt_uses_recorded_byte_count()
    {
        var cut = UsageEstimator.Apply([Rec("cut", false, "Network", prompt: "앞부분…") with { PromptBytes = 10_000 }]).Single();

        Assert.Equal((long)Math.Round(10_000 * UsageEstimator.TokensPerByte), cut.InputTokens);
    }

    [Theory]
    [InlineData("Client")]
    [InlineData("Server")]
    [InlineData("Quota")]
    [InlineData("RateLimit")]
    [InlineData(null)]
    public void Rejected_failures_are_not_estimated(string? kind)
    {
        var r = UsageEstimator.Apply([Rec("ok", true, input: 100), Rec("f", false, kind)]).Single(r => r.Id == "f");

        Assert.Null(r.InputTokens);
        Assert.Null(r.Estimate);
    }

    [Fact]
    public void Failure_that_already_has_tokens_is_kept()
    {
        // 빈 응답 실패는 usage를 받았으므로 실측 그대로
        var r = UsageEstimator.Apply([Rec("e", false, "Timeout", input: 50, output: 0)]).Single();

        Assert.Equal(50, r.InputTokens);
        Assert.Null(r.Estimate);
    }

    [Fact]
    public void Lost_duplicate_of_written_record_is_dropped()
    {
        var est = UsageEstimator.Apply([Rec("same", true, input: 100), Rec("same", false, InflightLog.LostKind), Rec("other", false, InflightLog.LostKind) with { RunId = "run2" }]);

        Assert.Equal(["same", "other"], est.Select(r => r.Id));
        Assert.True(est[0].Success);
    }

    [Fact]
    public void Estimated_cost_is_counted()
    {
        var price = new PricingOptions { Models = { ["m"] = new ModelPrice { InputPerMillion = 2, OutputPerMillion = 10 } } };
        var est = UsageEstimator.Apply([Rec("ok", true, attempt: 2, input: 1_000_000, output: 100_000), Rec("cut", false, "Network")]);

        Assert.Equal(2 * (2m + 1m), new BudgetOptions().CountedCost(est, price));
    }

    // ── 호출 중 표시 ──

    [Fact]
    public async Task Marker_exists_during_call_with_same_id_and_is_removed_after()
    {
        string[]? during = null;
        var client = new ScriptedChatClient(_ => { during = Directory.GetFiles(InflightDir, "*.json"); return ScriptedChatClient.Reply("ok")(default); });

        await Runner().RunStepAsync(Call(client), CancellationToken.None);

        var record = Assert.Single(CallLog.ReadAll(_log));
        Assert.Equal(record.Id + ".json", Path.GetFileName(Assert.Single(during!)));
        Assert.Empty(Directory.GetFiles(InflightDir));
    }

    [Fact]
    public async Task Marker_is_removed_after_failure_and_cancellation()
    {
        await Runner().RunStepAsync(Call(new ScriptedChatClient(ScriptedChatClient.Throw(FakeResponse.Error(400)))), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var canceling = new ScriptedChatClient(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner().RunStepAsync(Call(canceling), cts.Token));

        Assert.Equal(2, CallLog.ReadAll(_log).Count);
        Assert.Empty(Directory.GetFiles(InflightDir));
    }

    [Fact]
    public async Task Sweep_moves_dead_process_marker_to_lost_record_once()
    {
        Assert.NotNull(InflightLog.Begin(Rec("dead1", false) with { Prompt = "보낸 프롬프트" }, _log, out _));

        Assert.Equal(1, await InflightLog.SweepAsync(_log, (_, _) => false));
        Assert.Equal(0, await InflightLog.SweepAsync(_log, (_, _) => false));

        var lost = Assert.Single(CallLog.ReadAll(_log));
        Assert.Equal("dead1", lost.Id);
        Assert.Equal(InflightLog.LostKind, lost.ErrorKind);
        Assert.False(lost.Success);
        Assert.Equal("보낸 프롬프트", lost.Prompt);
        Assert.Empty(Directory.GetFiles(InflightDir));
    }

    [Fact]
    public async Task Sweep_keeps_live_process_marker()
    {
        var path = InflightLog.Begin(Rec("live", false), _log, out _);

        Assert.Equal(0, await InflightLog.SweepAsync(_log)); // 이 테스트 프로세스는 살아 있다
        Assert.True(File.Exists(path));
        Assert.Empty(CallLog.ReadAll(_log));
    }

    [Fact]
    public async Task Runner_sweeps_leftover_marker_before_calling()
    {
        // 다른(죽은) 프로세스가 남긴 표시: PID를 존재하지 않을 값으로 바꿔 둔다.
        var path = InflightLog.Begin(Rec("orphan", false), _log, out _)!;
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"pid\":{Environment.ProcessId}", "\"pid\":-1"));

        await Runner().RunStepAsync(Call(new ScriptedChatClient(ScriptedChatClient.Reply("ok"))), CancellationToken.None);

        var records = CallLog.ReadAll(_log);
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Id == "orphan" && r.ErrorKind == InflightLog.LostKind);
    }

    // ── /cross-review 39552cf537 반영 ──

    [Fact]
    public void Http_408_timeout_is_not_estimated()
    {
        var r = UsageEstimator.Apply([Rec("t", false, "Timeout") with { Error = "[실패] Timeout 408 · ClientResultException: Request Timeout" }]).Single();

        Assert.Null(r.Estimate);
        Assert.NotNull(UsageEstimator.Apply([Rec("t", false, "Timeout") with { Error = "[실패] Timeout · TaskCanceledException: …" }]).Single().Estimate);
    }

    [Fact]
    public void Two_lost_records_with_same_id_count_once()
    {
        var est = UsageEstimator.Apply([Rec("x", false, InflightLog.LostKind), Rec("x", false, InflightLog.LostKind)]);

        Assert.Single(est);
    }

    [Fact]
    public async Task ReadSince_does_not_stop_at_late_lost_record()
    {
        var now = DateTimeOffset.Now;
        await CallLog.AppendAsync(Rec("recent", true, input: 1) with { Timestamp = now.AddMinutes(-10) }, _log);
        await CallLog.AppendAsync(Rec("lost", false, InflightLog.LostKind) with { Timestamp = now.AddHours(-5) }, _log); // 늦게 정리돼 끝에 붙음

        Assert.Equal(["recent"], CallLog.ReadSince(now.AddHours(-1), _log).Select(r => r.Id));
    }

    [Fact]
    public async Task Sweep_recovers_claimed_file_of_dead_sweeper_and_removes_dead_tmp()
    {
        var path = InflightLog.Begin(Rec("claimed1", false), _log, out _)!;
        File.Move(path, path + $".claimed-123-{DateTimeOffset.UtcNow.UtcTicks}-abc"); // 차지한 채 죽은 정리 프로세스
        var tmp = Path.Combine(InflightDir, $"tmp1.json.tmp-123-{DateTimeOffset.UtcNow.UtcTicks}");
        File.WriteAllText(tmp, "{");

        Assert.Equal(1, await InflightLog.SweepAsync(_log, (_, _) => false));

        Assert.Equal("claimed1", Assert.Single(CallLog.ReadAll(_log)).Id);
        Assert.Empty(Directory.GetFiles(InflightDir));
    }

    [Fact]
    public async Task Marker_is_kept_when_record_cannot_be_written()
    {
        Directory.CreateDirectory(_log); // 기록 파일 자리에 폴더 → AppendAsync 실패

        await Runner().RunStepAsync(Call(new ScriptedChatClient(ScriptedChatClient.Reply("ok"))), CancellationToken.None);

        Assert.Single(Directory.GetFiles(InflightDir, "*.json")); // 프로세스가 끝나면 Lost로 옮겨진다
    }

    [Fact]
    public void Label_marks_lower_bound_and_pre_send_failure()
    {
        var followUp = UsageEstimator.Apply([Rec("f", false, "Network") with { FollowUpOf = 1 }]).Single();
        var label = Dashboard.PricingService.EstimateLabel(followUp)!;

        Assert.Contains("하한", label);
        Assert.Contains("요청 전 실패", label);
        Assert.DoesNotContain("요청 전 실패", Dashboard.PricingService.EstimateLabel(UsageEstimator.Apply([Rec("c", false, "Canceled")]).Single())!);
    }

    private AgentRunner Runner() => new(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, _log);

    private static StepCall Call(ScriptedChatClient client) => new(
        RunId: "run0000001", Mode: "review", Step: 1, Role: "Reviewer",
        Agent: new AgentEntry(new AgentOptions { Name = "fake", Provider = "openai", Model = "m", MaxAttempts = 1 }, client),
        Instructions: "x", Prompt: "p", Meta: new CallMeta(null, []));
}
