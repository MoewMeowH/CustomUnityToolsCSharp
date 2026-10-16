using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace HB.Orchestration.Tests;

/// <summary>재시도 루프: 시도마다 기록이 남고, MaxAttempts를 넘지 않고, 재시도하지 않는 오류는 1회로 끝난다.</summary>
public sealed class AgentRunnerRetryTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"hb-orch-test-{Guid.NewGuid():N}.jsonl");
    private readonly List<TimeSpan> _waits = [];

    public void Dispose()
    {
        if (File.Exists(_log)) File.Delete(_log);
    }

    private AgentRunner Runner() => new(NullLogger<AgentRunner>.Instance, (t, _) => { _waits.Add(t); return Task.CompletedTask; }, _log);

    private static StepCall Call(ScriptedChatClient client, int? maxAttempts = null) => new(
        RunId: "run0000001", Mode: "review", Step: 1, Role: "Reviewer",
        Agent: new AgentEntry(new AgentOptions { Name = "fake", Provider = "openai", Model = "m", MaxAttempts = maxAttempts }, client),
        Instructions: "x", Prompt: "p", Meta: new CallMeta(null, []));

    private List<CallRecord> Records() => CallLog.ReadAll(_log).OrderBy(r => r.Attempt).ToList();

    [Fact]
    public async Task TimeoutThenSuccess_RecordsEachAttempt()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(new TaskCanceledException()), ScriptedChatClient.Reply("ok"));

        var result = await Runner().RunStepAsync(Call(client), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Text);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("(재시도 1회 후 성공 · 1회차 Timeout)", result.RetryNote);

        var records = Records();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => { Assert.Equal("run0000001", r.RunId); Assert.Equal(1, r.Step); });
        Assert.Equal([1, 2], records.Select(r => r.Attempt ?? 0));
        Assert.Equal("Timeout", records[0].ErrorKind);
        Assert.False(records[0].Success);
        Assert.True(records[1].Success);
        Assert.Equal([TimeSpan.FromSeconds(2)], _waits);
    }

    [Fact]
    public async Task AlwaysTimeout_StopsAtMaxAttempts()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(new TaskCanceledException()));

        var result = await Runner().RunStepAsync(Call(client, maxAttempts: 3), CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("[실패] Timeout — 3/3회 시도 모두 실패", result.Text);
        Assert.Equal(3, client.Calls);
        Assert.Equal(3, Records().Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], _waits);
    }

    [Fact]
    public async Task DefaultMaxAttempts_IsTwo()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(FakeResponse.Error(500)));

        var result = await Runner().RunStepAsync(Call(client), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, Records().Count);
    }

    [Theory]
    [InlineData(402)]
    [InlineData(401)]
    public async Task ClientError_IsNotRetried(int status)
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(FakeResponse.Error(status)));

        var result = await Runner().RunStepAsync(Call(client, maxAttempts: 5), CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith($"[실패] Client {status}", result.Text);
        Assert.Equal(1, client.Calls);
        var record = Assert.Single(Records());
        Assert.Equal("Client", record.ErrorKind);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task UserCancellation_Throws_AndRecordsTheAttempt()
    {
        using var cts = new CancellationTokenSource();
        var client = new ScriptedChatClient(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner().RunStepAsync(Call(client), cts.Token));
        Assert.Equal(1, client.Calls);
        var record = Assert.Single(Records());   // 요청은 나갔으므로 시도 기록은 남는다 (재시도는 없음)
        Assert.Equal("Canceled", record.ErrorKind);
        Assert.False(record.Success);
    }

    [Fact]
    public async Task RetryAfterTooLong_FailsWithoutWaiting()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Throw(FakeResponse.Error(429, new() { ["Retry-After"] = "600" })));

        var result = await Runner().RunStepAsync(Call(client, maxAttempts: 3), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("600초 뒤 재시도를 요구", result.Text);
        Assert.Equal(1, client.Calls);
        Assert.Empty(_waits);
    }
}
