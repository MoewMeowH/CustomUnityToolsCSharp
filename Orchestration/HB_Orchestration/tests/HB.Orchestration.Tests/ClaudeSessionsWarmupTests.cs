using System.Text;
using HB.Orchestration.Dashboard;
using Microsoft.Extensions.Logging.Abstractions;

namespace HB.Orchestration.Tests;

/// <summary>Claude 세션 기록 미리 읽기 · 병렬 읽기: 읽은 파일은 다시 읽지 않고, 겹쳐 불러도 한 번만 읽는다.</summary>
public sealed class ClaudeSessionsWarmupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-sessions-warmup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (ClaudeSessionsService Service, string Folder) Make(int files)
    {
        var root = Path.Combine(_dir, "projects");
        var folder = Directory.CreateDirectory(Path.Combine(root, "F--proj")).FullName;
        for (var i = 0; i < files; i++)
        {
            var id = $"00000000-0000-0000-0000-{i:000000000000}";
            var line = $$$"""{"type":"user","sessionId":"{{{id}}}","timestamp":"2026-11-04T0{{{i % 10}}}:00:00Z","cwd":"F:\\proj","message":{"role":"user","content":"말 {{{i}}}"}}""";
            File.WriteAllText(Path.Combine(folder, id + ".jsonl"), line + "\n", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(Path.Combine(folder, id + ".jsonl"), new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }
        File.WriteAllText(Path.Combine(folder, "not-a-session.jsonl"), "{}\n");
        var options = Microsoft.Extensions.Options.Options.Create(new DashboardOptions
        {
            LogPath = Path.Combine(_dir, "calls.jsonl"), ClaudeProjectsDir = root, ClaudeSessionLimit = 3,
        });
        return (new ClaudeSessionsService(options, NullLogger<ClaudeSessionsService>.Instance), folder);
    }

    [Fact]
    public void WarmUp_FillsCache_LoadDoesNotReread()
    {
        var (service, folder) = Make(12);
        Assert.Equal(12, service.WarmUp(CancellationToken.None));
        Assert.Equal(12, service.ReadCount);

        var snap = service.Load([]);
        Assert.Equal(12, service.ReadCount);                    // 미리 읽은 것은 다시 읽지 않음
        Assert.Equal((12, 12, 3), (snap.TotalFiles, snap.AllSessions.Count, snap.Sessions.Count));
        // 병렬로 읽어도 순서는 수정 시각 내림차순 그대로
        Assert.Equal(Enumerable.Range(0, 12).Reverse().Select(i => $"00000000-0000-0000-0000-{i:000000000000}"),
            snap.AllSessions.Select(s => Path.GetFileNameWithoutExtension(s.FilePath)));

        var changed = Path.Combine(folder, "00000000-0000-0000-0000-000000000005.jsonl");
        File.AppendAllText(changed, "\n");                       // 바뀐 파일만 다시 읽음
        service.Load([]);
        Assert.Equal(13, service.ReadCount);
    }

    [Fact]
    public void WarmUp_AndLoadTogether_ReadEachFileOnce()
    {
        var (service, _) = Make(40);
        var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) service.WarmUp(CancellationToken.None);
            else service.Load([]);
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.Equal(40, service.ReadCount);
    }

    [Fact]
    public void WarmUp_Cancelled_Throws_MissingRoot_ReturnsZero()
    {
        var (service, _) = Make(5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => service.WarmUp(cts.Token));

        var missing = new ClaudeSessionsService(
            Microsoft.Extensions.Options.Options.Create(new DashboardOptions { ClaudeProjectsDir = Path.Combine(_dir, "없음") }),
            NullLogger<ClaudeSessionsService>.Instance);
        Assert.Equal(0, missing.WarmUp(CancellationToken.None));
    }
}
