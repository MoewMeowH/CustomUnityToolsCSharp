using System.Diagnostics;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard.Pages;

/// <summary>살아 있는 MCP 서버(= Claude Code 세션)별 모드 실행 횟수와 초기화.</summary>
public sealed class SessionsModel(IOptions<DashboardOptions> options, ModesConfigService modes) : PageModel
{
    public sealed record ModeUsage(string Mode, int Used, int Limit);

    public sealed record SessionView(SessionState State, bool ResetPending, bool OldBuild, List<ModeUsage> Usage);

    public List<SessionView> Sessions { get; private set; } = [];
    public string Folder => SessionFiles.FolderFor(options.Value.ResolvedLogPath);
    public DateTimeOffset? CurrentBuild { get; private set; }
    public string? ModesError { get; private set; }
    public int Cleaned { get; private set; }

    [BindProperty(SupportsGet = true)] public bool Requested { get; set; }

    public void OnGet() => Build();

    /// <summary>초기화 요청. mode가 비어 있으면 그 세션의 모든 모드.</summary>
    public IActionResult OnPostReset(int pid, string? mode)
    {
        SessionFiles.RequestReset(Folder, pid, mode);
        return RedirectToPage(new { requested = true });
    }

    private void Build()
    {
        CurrentBuild = modes.CurrentServerBuild;

        // 모드별 한도는 지금 modes.json 기준으로 보여준다.
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var defaultLimit = 5;
        try
        {
            var snap = modes.Load();
            defaultLimit = snap.DefaultMaxRunsPerSession;
            foreach (var (name, mode) in snap.Typed.Modes)
                limits[name] = mode.MaxRunsPerSession ?? defaultLimit;
        }
        catch (Exception ex) when (ex is ModesConfigException or IOException)
        {
            ModesError = ex.Message;
        }

        foreach (var state in SessionFiles.ReadAll(Folder).OrderByDescending(s => s.StartedAt))
        {
            // 강제 종료된 서버가 남긴 파일은 정리한다.
            if (!IsAlive(state))
            {
                SessionFiles.Delete(Folder, state.Pid);
                Cleaned++;
                continue;
            }

            var usage = limits.Keys
                .Union(state.Runs.Keys, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(m => new ModeUsage(
                    m,
                    state.Runs.TryGetValue(m, out var used) ? used : 0,
                    limits.TryGetValue(m, out var limit) ? limit : defaultLimit))
                .ToList();

            var oldBuild = CurrentBuild is { } current && state.ServerBuild is { } build
                           && (current - build).Duration() > TimeSpan.FromSeconds(2);

            Sessions.Add(new SessionView(state, SessionFiles.HasPendingReset(Folder, state.Pid), oldBuild, usage));
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
