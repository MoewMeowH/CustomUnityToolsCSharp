using System.Diagnostics;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>살아 있는 MCP 서버(= Claude Code 세션)별 모드 실행 횟수. 한도는 없다 (2026-10-20 세션 한도 제거).</summary>
public sealed class SessionsModel(IOptions<DashboardOptions> options, ModesConfigService modes) : PageModel
{
    public sealed record ModeUsage(string Mode, int Used);

    public sealed record SessionView(SessionState State, bool OldBuild, List<ModeUsage> Usage);

    public List<SessionView> Sessions { get; private set; } = [];
    public string Folder => SessionFiles.FolderFor(options.Value.ResolvedLogPath);
    public DateTimeOffset? CurrentBuild { get; private set; }
    public int Cleaned { get; private set; }

    public void OnGet() => Build();

    private void Build()
    {
        CurrentBuild = modes.CurrentServerBuild;

        foreach (var state in SessionFiles.ReadAll(Folder).OrderByDescending(s => s.StartedAt))
        {
            // 강제 종료된 서버가 남긴 파일은 정리한다.
            if (!IsAlive(state))
            {
                SessionFiles.Delete(Folder, state.Pid);
                Cleaned++;
                continue;
            }

            // 이름을 바꾸기 전 서버(재연결 전)가 남긴 예전 모드 이름도 새 이름으로 묶는다.
            var usage = state.Runs
                .GroupBy(kv => ModeNames.Normalize(kv.Key)!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ModeUsage(g.Key, g.Sum(kv => kv.Value)))
                .Where(u => u.Used > 0)
                .OrderBy(u => u.Mode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var oldBuild = CurrentBuild is { } current && state.ServerBuild is { } build
                           && (current - build).Duration() > TimeSpan.FromSeconds(2);

            Sessions.Add(new SessionView(state, oldBuild, usage));
        }
    }

    /// <summary>PID가 살아 있고, 같은 PID를 다른 프로세스가 재사용한 경우가 아닌지 확인.</summary>
    private static bool IsAlive(SessionState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            if (process.HasExited) return false;
            return (process.StartTime - state.StartedAt.LocalDateTime).Duration() < TimeSpan.FromMinutes(1);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static string ProjectName(string? dir) =>
        string.IsNullOrEmpty(dir) ? "-" : Path.GetFileName(dir.TrimEnd('/', '\\'));
}
