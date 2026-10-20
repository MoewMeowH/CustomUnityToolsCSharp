using System.Reflection;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Logging;

namespace HB.Orchestration.Agents;

/// <summary>
/// 이 MCP 서버 프로세스(= Claude Code 세션 하나)의 모드별 실행 횟수.
/// 메모리에서 세고, 대시보드가 볼 수 있게 sessions/{pid}.json에 남긴다. 한도는 없다 (2026-10-20 세션 한도 제거).
/// </summary>
public sealed class SessionTracker
{
    private readonly ILogger<SessionTracker> _logger;
    /// <summary>null이면 상태 파일을 남기지 않는다 (<see cref="Unsaved"/>).</summary>
    private readonly string? _folder;
    private readonly Dictionary<string, int> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int Pid { get; } = Environment.ProcessId;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? ServerBuild { get; }

    public SessionTracker(ILogger<SessionTracker> logger) : this(logger, SessionFiles.DefaultFolder) { }

    /// <summary>
    /// 상태 파일을 쓰지 않는 추적기. MCP 서버가 아닌 일회성 실행(--golden)용이다.
    /// 대시보드 '세션' 화면에 Claude Code 세션처럼 보이지 않게 한다.
    /// </summary>
    public static SessionTracker Unsaved(ILogger<SessionTracker> logger) => new(logger, folder: null);

    /// <param name="folder">세션 상태 파일 폴더. 테스트에서 실제 폴더를 건드리지 않도록 바꿔 끼운다. null이면 파일을 쓰지 않는다.</param>
    internal SessionTracker(ILogger<SessionTracker> logger, string? folder)
    {
        _logger = logger;
        _folder = folder;

        var dll = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(dll) && File.Exists(dll))
            ServerBuild = new DateTimeOffset(File.GetLastWriteTime(dll));

        lock (_gate) Save();
    }

    public int Get(string mode)
    {
        lock (_gate) return _runs.TryGetValue(mode, out var n) ? n : 0;
    }

    /// <summary>실행 1회를 센다.</summary>
    public void Count(string mode)
    {
        lock (_gate)
        {
            _runs[mode] = (_runs.TryGetValue(mode, out var n) ? n : 0) + 1;
            Save();
        }
    }

    /// <summary>서버 종료 시 상태 파일을 지운다 (강제 종료로 남은 파일은 대시보드가 정리).</summary>
    public void Remove()
    {
        if (_folder is not null) SessionFiles.Delete(_folder, Pid);
    }

    // _gate 안에서만 호출
    private void Save()
    {
        if (_folder is null) return;
        try
        {
            SessionFiles.Write(_folder, new SessionState
            {
                Pid = Pid,
                StartedAt = StartedAt,
                UpdatedAt = DateTimeOffset.Now,
                ProjectDir = Environment.CurrentDirectory,
                ServerBuild = ServerBuild,
                Runs = new Dictionary<string, int>(_runs, StringComparer.OrdinalIgnoreCase),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 상태 파일은 대시보드 표시용이라, 못 써도 실행은 계속한다.
            _logger.LogWarning(ex, "세션 상태 파일 저장 실패");
        }
    }
}
