using System.Collections;
using HB.Orchestration.Dashboard;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

public sealed class ModesSaveConcurrencyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-modes-save-").FullName;
    private ModesConfigService Service() => new(Options.Create(new DashboardOptions { McpConfigDir = _dir }), _ => null);

    public ModesSaveConcurrencyTests()
    {
        File.WriteAllText(Path.Combine(_dir, "modes.json"), """
            {"Roles":{"r":{"Prompt":"review"}},"Modes":{"m":{"Steps":[{"Role":"r","Agent":"a"}],"Return":"Last"}}}
            """);
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            {"Orchestration":{"Providers":{"p":{"Type":"openai-compatible"}},"Agents":[{"Name":"a","Provider":"p","Model":"model"}],"Pricing":{"KrwPerUsd":1400}}}
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class BlockingList<T>(T item, ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int i] => i == 0 ? item : throw new IndexOutOfRangeException();
        public IEnumerator<T> GetEnumerator()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("저장 테스트 대기 초과");
            yield return item;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_mode_or_agent_saves_reject_the_stale_request(bool agents)
    {
        var first = Service();
        var second = Service(); // 다른 서비스 인스턴스도 같은 파일을 보호한다.
        var snap = first.Load();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var firstTask = Task.Factory.StartNew(() => agents
            ? first.SaveAgents(snap.SettingsStamp, snap.Stamp, new BlockingList<AgentEdit>(
                new("a", "renamed", "p", "model", "", true, false), entered, release))
            : first.Save(snap.Stamp, new BlockingList<ModeEdit>(
                new("m", ["a"], new Dictionary<string, string> { ["Return"] = "All" }), entered, release)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<SaveResult>? secondTask = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            secondTask = Task.Factory.StartNew(() =>
            {
                secondStarted.Set();
                return agents
                    ? second.SavePrices(snap.SettingsStamp, [], "1500")
                    : second.ReplaceAgent(snap.Stamp, "a", "a");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            // 첫 저장이 스냅샷 검사를 끝낸 채 멈췄다. 다른 진입점이 먼저 저장하면 안 된다.
            Assert.NotSame(secondTask, await Task.WhenAny(secondTask, Task.Delay(200)));
        }
        finally
        {
            release.Set();
            await firstTask;
            if (secondTask is not null) await secondTask;
        }
        Assert.True((await firstTask).Ok);
        Assert.False((await secondTask!).Ok);
        Assert.Equal(agents ? 2 : 1, first.WriteCount); // 이름 변경은 설정과 모드 두 파일에 쓴다.
        Assert.Equal(0, second.WriteCount);
        var final = first.Load();
        Assert.Equal(agents ? "renamed" : "a", final.Typed.Modes["m"].Steps[0].Agent);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Content_change_with_same_size_and_timestamp_is_rejected(bool settings)
    {
        var service = Service();
        var before = service.Load();
        var path = Path.Combine(_dir, settings ? "appsettings.json" : "modes.json");
        var timestamp = File.GetLastWriteTimeUtc(path);
        var original = File.ReadAllText(path);
        var changed = settings ? original.Replace("1400", "1500") : original.Replace("review", "Review");
        Assert.Equal(original.Length, changed.Length);
        File.WriteAllText(path, changed);
        File.SetLastWriteTimeUtc(path, timestamp);
        var result = settings
            ? service.SavePrices(before.SettingsStamp, [], "1600")
            : service.Save(before.Stamp, []);
        Assert.False(result.Ok);
        Assert.Equal(changed, File.ReadAllText(path));
    }

    [Fact]
    public void Rapid_saves_keep_distinct_backups()
    {
        var service = Service();
        var original = File.ReadAllText(Path.Combine(_dir, "appsettings.json"));
        Assert.True(service.SavePrices(service.Load().SettingsStamp, [], "1500").Ok);
        var intermediate = File.ReadAllText(Path.Combine(_dir, "appsettings.json"));
        Assert.True(service.SavePrices(service.Load().SettingsStamp, [], "1600").Ok);
        var backups = Directory.GetFiles(Path.Combine(_dir, "settings-backup"), "*.json");
        Assert.Equal(2, backups.Length);
        var texts = backups.Select(File.ReadAllText).ToArray();
        Assert.Contains(original, texts);
        Assert.Contains(intermediate, texts);
    }
}
