using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// 호출 기록 전체 (최신순). 파일이 바뀌었을 때만 다시 읽는다 (자동 새로고침마다 전체를 다시 읽지 않게).
/// 기록은 불변 record라 여러 요청이 같은 목록을 함께 봐도 된다.
/// </summary>
public sealed class CallLogCache(IOptions<DashboardOptions> options)
{
    private readonly Lock _gate = new();
    private string? _stamp;
    private IReadOnlyList<CallRecord> _cached = [];

    public IReadOnlyList<CallRecord> All
    {
        get
        {
            var path = options.Value.ResolvedLogPath;
            // 읽기 전에 Stamp를 잡는다: 읽는 도중 기록이 붙으면 다음 요청에서 Stamp가 달라 다시 읽는다.
            var info = new FileInfo(path);
            var stamp = info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";

            lock (_gate)
            {
                if (stamp == _stamp) return _cached;
                _cached = CallLog.ReadAll(path);
                _stamp = stamp;
                return _cached;
            }
        }
    }
}
