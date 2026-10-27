using System.Security.Cryptography;
using System.Text;

namespace HB.Orchestration.Agents;

/// <summary>
/// claude-cli 단계의 작업 폴더 (계획서 2026-10-27_검토형-서브에이전트-API-전환 "결정 뒤 설계 (B)").
/// context가 가리키는 파일을 <b>파일 전체</b>로 상대 경로 그대로 복사한다 — 에이전트가 그 안을 직접 읽는다.
/// 경로 규칙 · 추가 루트 · 전송 금지 · 파일 크기 상한은 기존 첨부(ContextLoader)와 같다. 지휘자가 하던 subagent_in 복사를 서버가 대신하고,
/// 복사한 파일 목록과 SHA-256 앞자리를 프롬프트(= 호출 기록)에 남긴다 — 폴더는 실행 뒤 지워지므로 에이전트가 무엇을 받았는지 나중에 대조할 수 있게.
/// </summary>
public static class ReviewWorkFolder
{
    /// <summary>남은 옛 폴더를 지우는 기준 (실행이 강제 종료돼 지우지 못한 것).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// 복사 총량 상한 (바이트). 본문을 프롬프트로 보내지 않으므로 MaxInputChars 대신 이것으로 막는다 (/self-review C9 · sub_self C8).
    /// 에이전트는 필요한 파일만 읽는다 — 폴더가 커도 비용이 그만큼 늘지는 않지만, 실수로 큰 폴더를 통째로 넘기는 것을 막는다.
    /// </summary>
    public const long MaxTotalBytes = 4_000_000;

    /// <summary>호출 기록 폴더 아래 review_in/.</summary>
    public static string BaseFor(string callLogPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(callLogPath))!, "review_in");

    /// <summary>
    /// 폴더를 만들고 파일을 복사한다. Focus = 프롬프트의 첨부 절 대신 넣을 "파일 목록 + 주목할 범위".
    /// 실패하면(예상 밖 예외 포함) 만든 폴더를 지우고 오류 문구를 돌려준다. 취소는 폴더를 지운 뒤 그대로 던진다.
    /// </summary>
    public static (string? Folder, string? Focus, string? Error) Create(
        string baseDir, IReadOnlyList<ContextRange>? ranges, ContextOptions options, string rootDir, CancellationToken ct = default)
    {
        if (ranges is null || ranges.Count == 0)
            return (null, null, "claude-cli 단계는 context가 필요합니다 — 검토할 파일과 판단용 파일(정의 · 호출부)을 넘기세요.");
        if (options.MaxRanges > 0 && ranges.Count > options.MaxRanges)
            return (null, null, $"범위가 {ranges.Count}개 / 상한 {options.MaxRanges}개. 필요한 범위만 남기세요.");

        SweepStale(baseDir);
        var folder = Path.Combine(baseDir, Guid.NewGuid().ToString("N")[..12]);
        try
        {
            return Fill(folder, ranges, options, rootDir, ct);
        }
        catch (OperationCanceledException)
        {
            TryDelete(folder);
            throw;
        }
        catch (Exception ex)
        {
            return Fail(folder, $"작업 폴더를 만들지 못했습니다 ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static (string? Folder, string? Focus, string? Error) Fill(
        string folder, IReadOnlyList<ContextRange> ranges, ContextOptions options, string rootDir, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        var copied = new Dictionary<string, (string Inside, string Sha)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var focus = new List<string>();
        long total = 0;

        foreach (var range in ranges)
        {
            ct.ThrowIfCancellationRequested();
            if (range is null) return Fail(folder, "context에 빈 항목이 있습니다.");
            if (range.StartLine < 1 || range.EndLine < range.StartLine)
                return Fail(folder, $"'{range}': 줄 범위가 잘못됐습니다 (1 ≤ startLine ≤ endLine).");

            // ContextLoader와 같은 규칙: "@이름/…"이고 등록된 이름이면 그 추가 루트. 폴더 안 자리도 "@이름/…" (로컬 절대 경로는 보이지 않음).
            // 접두는 이 분기에서만 정한다 — '@'로 시작하지만 추가 루트가 아닌 경로(예: 메인 루트의 "@lib/x.cs")를 다시 해석하지 않게 (/self-review C3).
            var relative = range.Path;
            var rangeRoot = root;
            var prefix = "";
            if (ContextLoader.TrySplitRootPrefix(range.Path, out var name, out var rest) && options.TryGetExtraRoot(name, out var key, out var rootPath))
            {
                if (ContextLoader.ExtraRootProblem(key, rootPath) is { } problem) return Fail(folder, $"'{range.Path}': ExtraRoots '{key}' — {problem}");
                rangeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
                relative = rest;
                prefix = $"@{key}/";
            }

            var (full, pathError) = SafePath.Resolve(relative, rangeRoot, options.AllDenyPatterns, SafePath.Access.Read);
            if (pathError is not null) return Fail(folder, $"'{range.Path}': {pathError}");

            // 폴더 안 자리: 루트 기준 정규화한 상대 경로 (a/../b 같은 표기가 폴더 밖으로 나가지 않게 다시 확인).
            var inside = prefix + Path.GetRelativePath(rangeRoot, full!).Replace('\\', '/');
            var dest = Path.GetFullPath(Path.Combine(folder, inside));
            if (!dest.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Fail(folder, $"'{range.Path}': 작업 폴더 밖으로 나가는 경로입니다.");

            if (!copied.ContainsKey(dest))
            {
                try
                {
                    using var source = new FileStream(full!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (options.MaxFileBytes > 0 && source.Length > options.MaxFileBytes)
                        return Fail(folder, $"'{range.Path}': 파일이 {source.Length:N0}바이트 / 상한 {options.MaxFileBytes:N0}바이트.");
                    total += source.Length;
                    if (total > MaxTotalBytes)
                        return Fail(folder, $"복사할 파일이 합계 {total:N0}바이트 / 상한 {MaxTotalBytes:N0}바이트. 판단에 필요한 파일만 남기세요.");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var target = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        target.Write(buffer, 0, read);
                        sha.AppendData(buffer, 0, read);
                    }
                    copied[dest] = (inside, Convert.ToHexStringLower(sha.GetHashAndReset())[..12]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail(folder, $"'{range.Path}': 복사 실패 ({ex.GetType().Name}: {ex.Message})");
                }
            }
            focus.Add($"- {inside}:{range.StartLine}-{range.EndLine}");
        }

        var text = new StringBuilder();
        text.AppendLine().AppendLine("## 작업 폴더 (서버가 복사함)")
            .AppendLine("지금 작업 폴더에 지휘자가 고른 파일이 파일 전체로, 아래 경로 그대로 복사돼 있다. 폴더에 있는 파일은 아래 목록이 전부다.")
            .AppendLine("Read로 연다 (검색 도구가 없을 수 있다). 판단에 필요하면 목록의 다른 파일 · 다른 부분도 읽는다. 폴더 밖은 읽지 않는다.")
            .AppendLine().AppendLine("### 파일 (경로 · SHA-256 앞 12자)");
        foreach (var (inside, sha) in copied.Values.OrderBy(v => v.Inside, StringComparer.Ordinal))
            text.AppendLine($"- {inside} · {sha}");
        text.AppendLine().AppendLine("### 이번 검토의 중심 (경로:줄)");
        foreach (var line in focus) text.AppendLine(line);
        return (folder, text.ToString(), null);
    }

    /// <summary>실패해도 무시한다 (남은 폴더는 다음 Create가 StaleAfter 뒤 지운다).</summary>
    public static void TryDelete(string? folder)
    {
        if (folder is null) return;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 다음 정리 때 */ }
    }

    private static (string?, string?, string?) Fail(string folder, string error)
    {
        TryDelete(folder);
        return (null, null, error);
    }

    private static void SweepStale(string baseDir)
    {
        try
        {
            if (!Directory.Exists(baseDir)) return;
            var cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
                if (Directory.GetCreationTimeUtc(dir) < cutoff) TryDelete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 정리는 덤 */ }
    }
}
