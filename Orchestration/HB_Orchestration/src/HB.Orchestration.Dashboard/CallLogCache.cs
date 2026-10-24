using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// 호출 기록 전체 (최신순). 파일이 바뀌었을 때만 다시 읽는다 (자동 새로고침마다 전체를 다시 읽지 않게).
/// 기록은 불변 record라 여러 요청이 같은 목록을 함께 봐도 된다.
/// 끊긴 호출에는 추정 사용량을 채운다 (<see cref="UsageEstimator.Apply"/>, 계획서 2026-10-24_끊긴-호출-비용-추정).
/// </summary>
public sealed class CallLogCache(IOptions<DashboardOptions> options, ILogger<CallLogCache> logger)
{
    /// <summary>호출 중 표시 정리 간격. 요청마다 돌면 표시 읽기 · 프로세스 조회가 요청 스레드를 막는다 (/cross-review 39552cf537 self C9).</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private string? _stamp;
    private IReadOnlyList<CallRecord> _cached = [];
    private long _lastSweepTicks;

    public IReadOnlyList<CallRecord> All
    {
        get
        {
            var path = options.Value.ResolvedLogPath;
            SweepIfDue(path);

            // 읽기 전에 Stamp를 잡는다: 읽는 도중 기록이 붙으면 다음 요청에서 Stamp가 달라 다시 읽는다.
            var info = new FileInfo(path);
            var stamp = info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";

            lock (_gate)
            {
                if (stamp == _stamp) return _cached;
                // 파일 순서는 대략 시각 순서지만 Lost 기록은 원래 시작 시각을 단 채 늦게 붙는다 → 시각순으로 맞춘다 (같은 시각은 파일 역순 유지).
                _cached = UsageEstimator.Apply(CallLog.ReadAll(path)).OrderByDescending(r => r.Timestamp).ToList();
                _stamp = stamp;
                return _cached;
            }
        }
    }

    /// <summary>강제 종료된 프로세스의 호출 중 표시를 Lost 기록으로 옮긴다 (기록 파일에 쓰므로 Stamp보다 먼저). 실패해도 화면은 보여 준다.</summary>
    private void SweepIfDue(string path)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < SweepInterval.Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;
        try { InflightLog.SweepAsync(path).GetAwaiter().GetResult(); }
        catch (Exception ex) { logger.LogWarning(ex, "호출 중 표시 정리 실패"); }
    }
}
