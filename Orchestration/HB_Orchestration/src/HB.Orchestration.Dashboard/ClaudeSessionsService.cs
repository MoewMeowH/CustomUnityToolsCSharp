using System.Collections.Concurrent;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// Claude Code 세션 기록(~/.claude/projects/*/&lt;sessionId&gt;.jsonl)을 읽어 지침 목록과 run_mode 연결을 보여 준다.
/// 읽기 전용이고, 대화 본문은 꺼내지 않는다. 파일은 (경로 · 크기 · 수정 시각)이 같으면 다시 읽지 않는다.
/// </summary>
public sealed class ClaudeSessionsService(IOptions<DashboardOptions> options, ILogger<ClaudeSessionsService> logger)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, TranscriptSummary Summary)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Sessions">목록에 보여 줄 최근 세션 (ClaudeSessionLimit개).</param>
    /// <param name="AllSessions">연결 계산과 상세 화면 조회에 쓰는 전체 세션. 제한 밖 세션이 확정한 runId도 추정 후보에서 빠진다.</param>
    public sealed record Snapshot(
        string Root,
        string? RootError,
        IReadOnlyList<TranscriptSummary> Sessions,
        IReadOnlyList<TranscriptSummary> AllSessions,
        IReadOnlyDictionary<TranscriptSummary, List<RunLink>> Links,
        int TotalFiles);

    /// <summary>설정 → CLAUDE_CONFIG_DIR/projects → %USERPROFILE%/.claude/projects 순.</summary>
    public string Root
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(options.Value.ClaudeProjectsDir)) return options.Value.ClaudeProjectsDir!;
            if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config) return Path.Combine(config, "projects");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        }
    }

    /// <summary>최근 수정된 세션 기록 N개(Dashboard:ClaudeSessionLimit)를 읽고 호출 기록과 연결한다.</summary>
    public Snapshot Load(IReadOnlyList<CallRecord> calls)
    {
        var root = Root;
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root)
                    .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly))
                    .Where(f => ClaudeTranscriptReader.IsSessionFileName(Path.GetFileName(f)))
                    .Select(f => new FileInfo(f))
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Snapshot(root, $"{ex.GetType().Name}: {ex.Message}", [], [], new Dictionary<TranscriptSummary, List<RunLink>>(), 0);
        }

        // 연결은 전체 세션으로 계산하고(한 번 읽은 파일은 캐시), 목록만 최근 N개로 자른다.
        var all = files.OrderByDescending(SafeWrite).Select(Read).ToList();
        var links = RunLinker.LinkAll(all, calls);
        var limit = Math.Max(1, options.Value.ClaudeSessionLimit);
        return new Snapshot(root, Directory.Exists(root) ? null : "폴더가 없습니다", all.Take(limit).ToList(), all, links, files.Count);
    }

    private TranscriptSummary Read(FileInfo file)
    {
        var length = SafeLength(file);
        var write = SafeWrite(file);
        if (_cache.TryGetValue(file.FullName, out var hit) && hit.Length == length && hit.WriteUtc == write) return hit.Summary;

        var summary = ClaudeTranscriptReader.Read(file.FullName);
        if (summary.FormatError is not null)
            logger.LogWarning("세션 기록 형식을 알 수 없음: {Path} — {Error}", file.FullName, summary.FormatError);
        _cache[file.FullName] = (length, write, summary);
        return summary;
    }

    private static long SafeLength(FileInfo f) { try { f.Refresh(); return f.Exists ? f.Length : 0; } catch (IOException) { return 0; } }
    private static DateTime SafeWrite(FileInfo f) { try { return f.LastWriteTimeUtc; } catch (IOException) { return default; } }
}
