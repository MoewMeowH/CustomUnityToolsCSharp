using System.Text;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>
/// CallLog: 한글을 이스케이프 없이 기록, 예전(\uXXXX) 기록도 읽기,
/// ReadSince는 끝에서부터 읽어 since 이후만 최신순으로 돌려주고, 청크 경계·깨진 줄·약간 어긋난 순서를 견딘다.
/// </summary>
public sealed class CallLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-orch-log-").FullName;
    private string Log => Path.Combine(_dir, "calls.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CallRecord Record(string id, DateTimeOffset at, string response = "ok") => new()
    {
        Id = id, Timestamp = at, Tool = "review", Model = "m", Prompt = "p", Success = true, Response = response,
    };

    [Fact]
    public async Task Append_WritesKoreanUnescaped()
    {
        await CallLog.AppendAsync(Record("a", DateTimeOffset.Now, "한글 응답 <b>&'\""), Log);

        var line = File.ReadAllText(Log, Encoding.UTF8);
        Assert.Contains("한글 응답", line);
        Assert.DoesNotContain("\\u", line.Replace("\\\"", ""));
        Assert.Equal("한글 응답 <b>&'\"", CallLog.ReadAll(Log).Single().Response);
    }

    [Fact]
    public void ReadAll_ReadsLegacyEscapedRecords()
    {
        File.WriteAllText(Log, """{"id":"old","timestamp":"2026-10-01T00:00:00+09:00","tool":"review","model":"m","prompt":"p","response":"한글","success":true}""" + "\n");
        Assert.Equal("한글", CallLog.ReadAll(Log).Single().Response);
    }

    [Fact]
    public async Task ReadSince_ReturnsOnlyRecentNewestFirst_AcrossChunks()
    {
        var now = DateTimeOffset.Now;
        var big = new string('가', 30_000);   // 한 줄이 90KB 넘게: 64KB 청크 경계를 넘는다
        await CallLog.AppendAsync(Record("old2", now.AddDays(-2)), Log);
        await CallLog.AppendAsync(Record("old1", now.AddHours(-3)), Log);
        await CallLog.AppendAsync(Record("r1", now.AddMinutes(-50), big), Log);
        await CallLog.AppendAsync(Record("r2", now.AddMinutes(-40)), Log);
        File.AppendAllText(Log, "{broken\n\n");
        await CallLog.AppendAsync(Record("r3", now.AddMinutes(-10), big), Log);

        var since = now.AddHours(-1);
        var recent = CallLog.ReadSince(since, Log);

        Assert.Equal(["r3", "r2", "r1"], recent.Select(r => r.Id));
        Assert.Equal(big, recent[0].Response);
        Assert.Equal(CallLog.ReadAll(Log).Where(r => r.Timestamp >= since).Select(r => r.Id), recent.Select(r => r.Id));
    }

    [Fact]
    public async Task ReadSince_KeepsRecordsSlightlyOutOfOrder()
    {
        var since = DateTimeOffset.Now.AddHours(-1);
        // 오래 걸린 호출: since 직전에 시작해 늦게 기록됐다. 그 앞에 since 이후 기록이 있다.
        await CallLog.AppendAsync(Record("before", since.AddHours(-5)), Log);
        await CallLog.AppendAsync(Record("in1", since.AddMinutes(5)), Log);
        await CallLog.AppendAsync(Record("late", since.AddMinutes(-3)), Log);
        await CallLog.AppendAsync(Record("in2", since.AddMinutes(10)), Log);

        Assert.Equal(["in2", "in1"], CallLog.ReadSince(since, Log).Select(r => r.Id));
    }

    [Fact]
    public void ReadSince_MissingOrEmptyFile_ReturnsEmpty()
    {
        Assert.Empty(CallLog.ReadSince(DateTimeOffset.MinValue, Log));
        File.WriteAllText(Log, "");
        Assert.Empty(CallLog.ReadSince(DateTimeOffset.MinValue, Log));
    }

    [Fact]
    public async Task Line_cut_by_interrupted_write_does_not_swallow_next_record()
    {
        File.WriteAllText(Log, "{\"id\":\"broken\",\"tool\":");   // 개행 없이 끊긴 줄 (review 7c780ad3cd R5와 같은 경우)
        await CallLog.AppendAsync(Record("ok", DateTimeOffset.Now), Log);

        Assert.Equal("ok", Assert.Single(CallLog.ReadAll(Log)).Id);
    }
}
