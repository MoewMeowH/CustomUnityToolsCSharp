namespace HB.Orchestration.Dashboard;

/// <summary>
/// 대시보드가 켜지고 잠깐 뒤 Claude 세션 기록을 한 번 미리 읽어 둔다 — 첫 Claude 세션 · 채팅방 화면이 기록 수백 개를 읽느라
/// 몇 초 멈추지 않게. 실패해도 화면은 그때 읽으므로 로그만 남긴다.
/// </summary>
public sealed class ClaudeSessionsWarmup(ClaudeSessionsService sessions, ILogger<ClaudeSessionsWarmup> logger) : BackgroundService
{
    /// <summary>켜진 직후 첫 화면 요청과 겹치지 않게 조금 기다린다.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Delay, stoppingToken);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var count = await Task.Run(() => sessions.WarmUp(stoppingToken), stoppingToken);
            logger.LogInformation("Claude 세션 기록 {Count}개 미리 읽음 ({Seconds:0.0}초)", count, started.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude 세션 기록 미리 읽기 실패 — 화면을 열 때 읽는다");
        }
    }
}
