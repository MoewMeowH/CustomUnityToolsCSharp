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
    public string Root =>
        string.IsNullOrWhiteSpace(options.Value.ClaudeProjectsDir) ? ClaudeTranscriptReader.DefaultProjectsRoot() : options.Value.ClaudeProjectsDir!;

    /// <summary>
    /// 캐시에 없는 기록을 동시에 읽는 수. 첫 열기가 기록 수백 개(1GB 남짓)를 차례로 읽느라 몇 초 걸렸다 — 디스크 · CPU를 다 잡지 않을 만큼만.
    /// </summary>
    public const int Parallelism = 4;

    /// <summary>파일을 실제로 읽은 횟수 (캐시 적중 제외 — 시험용).</summary>
    public int ReadCount => Volatile.Read(ref _reads);
    private int _reads;

    /// <summary>지금 읽는 중인 파일 (경로|크기|시각) — 미리 읽기와 화면 요청이 겹쳐도 같은 판을 두 번 읽지 않게.</summary>
    private readonly ConcurrentDictionary<string, Lazy<TranscriptSummary>> _reading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>최근 수정된 세션 기록 N개(Dashboard:ClaudeSessionLimit)를 읽고 호출 기록과 연결한다.</summary>
    public Snapshot Load(IReadOnlyList<CallRecord> calls)
    {
        var root = Root;
        List<FileInfo> files;
        try
        {
            files = ListFiles(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Snapshot(root, $"{ex.GetType().Name}: {ex.Message}", [], [], new Dictionary<TranscriptSummary, List<RunLink>>(), 0);
        }

        // 연결은 전체 세션으로 계산하고(한 번 읽은 파일은 캐시), 목록만 최근 N개로 자른다.
        var all = ReadAll(files.OrderByDescending(SafeWrite).ToList(), CancellationToken.None);
        var links = RunLinker.LinkAll(all, calls);
        var limit = Math.Max(1, options.Value.ClaudeSessionLimit);
        return new Snapshot(root, Directory.Exists(root) ? null : "폴더가 없습니다", all.Take(limit).ToList(), all, links, files.Count);
    }

    /// <summary>
    /// 캐시만 채운다 (연결 계산 없음) — 대시보드가 켜질 때 뒤에서 한 번 (<see cref="ClaudeSessionsWarmup"/>). 폴더를 못 읽으면 조용히 끝낸다.
    /// </summary>
    /// <returns>목록에 있던 기록 수.</returns>
    public int WarmUp(CancellationToken ct)
    {
        List<FileInfo> files;
        try { files = ListFiles(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        ReadAll(files, ct);
        return files.Count;
    }

    private static List<FileInfo> ListFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly))
                .Where(f => ClaudeTranscriptReader.IsSessionFileName(Path.GetFileName(f)))
                .Select(f => new FileInfo(f))
                .ToList()
            : [];

    /// <summary>순서를 지켜 요약을 모은다. 캐시에 없는 것만 <see cref="Parallelism"/>개씩 동시에 읽는다.</summary>
    private List<TranscriptSummary> ReadAll(List<FileInfo> files, CancellationToken ct)
    {
        var summaries = new TranscriptSummary[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            i => summaries[i] = Read(files[i]));
        return [.. summaries];
    }

    private TranscriptSummary Read(FileInfo file)
    {
        var length = SafeLength(file);
        var write = SafeWrite(file);
        if (_cache.TryGetValue(file.FullName, out var hit) && hit.Length == length && hit.WriteUtc == write) return hit.Summary;

        // 같은 판을 읽고 있는 쪽이 있으면 그 결과를 기다린다 (먼저 온 쪽만 읽음)
        var key = $"{file.FullName}|{length}|{write.Ticks}";
        var reading = _reading.GetOrAdd(key, _ => new Lazy<TranscriptSummary>(() =>
        {
            // 위 캐시 확인과 등록 사이에 다른 쪽이 읽기를 끝내고 등록을 지웠을 수 있다 — 다시 본다 (/self-review c44c9441c7 C1)
            if (_cache.TryGetValue(file.FullName, out var done) && done.Length == length && done.WriteUtc == write) return done.Summary;
            Interlocked.Increment(ref _reads);
            var summary = ClaudeTranscriptReader.Read(file.FullName);
            if (summary.FormatError is not null)
                logger.LogWarning("세션 기록 형식을 알 수 없음: {Path} — {Error}", file.FullName, summary.FormatError);
            _cache[file.FullName] = (length, write, summary);
            return summary;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return reading.Value;
        }
        finally
        {
            _reading.TryRemove(KeyValuePair.Create(key, reading));
        }
    }

    private static long SafeLength(FileInfo f) { try { f.Refresh(); return f.Exists ? f.Length : 0; } catch (IOException) { return 0; } }
    private static DateTime SafeWrite(FileInfo f) { try { return f.LastWriteTimeUtc; } catch (IOException) { return default; } }
}
