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
    /// 저장소 사본 상한 (codex-cli). 넘으면 사본을 만들지 않고 context 파일만 둔다 — 큰 Unity 저장소에서 복사가 리뷰보다 오래 걸리지 않게.
    /// </summary>
    public const int MaxTrackedFiles = 20_000;
    public const long MaxTrackedBytes = 64_000_000;

    /// <summary>작업 폴더 모양: claude-cli는 context 파일만 · Read 안내, codex-cli는 저장소 추적 파일 사본 + diff 본문 첨부 · 셸 명령 안내.</summary>
    public enum Style { ClaudeRead, Codex }

    /// <summary>
    /// 폴더를 만들고 파일을 복사한다. Focus = 프롬프트의 첨부 절 대신 넣을 "파일 목록 + 주목할 범위" (Codex는 + diff 본문).
    /// 실패하면(예상 밖 예외 포함) 만든 폴더를 지우고 오류 문구를 돌려준다. 취소는 폴더를 지운 뒤 그대로 던진다.
    /// </summary>
    public static (string? Folder, string? Focus, string? Error) Create(
        string baseDir, IReadOnlyList<ContextRange>? ranges, ContextOptions options, string rootDir, CancellationToken ct = default, Style style = Style.ClaudeRead)
    {
        if (ranges is null || ranges.Count == 0)
            return (null, null, "작업 폴더를 쓰는 단계(claude-cli · codex-cli)는 context가 필요합니다 — 검토할 파일(변경이면 diff 파일 먼저)과 판단용 파일을 넘기세요.");
        if (options.MaxRanges > 0 && ranges.Count > options.MaxRanges)
            return (null, null, $"범위가 {ranges.Count}개 / 상한 {options.MaxRanges}개. 필요한 범위만 남기세요.");

        SweepStale(baseDir);
        var folder = Path.Combine(baseDir, Guid.NewGuid().ToString("N")[..12]);
        try
        {
            return Fill(folder, ranges, options, rootDir, ct, style);
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
        string folder, IReadOnlyList<ContextRange> ranges, ContextOptions options, string rootDir, CancellationToken ct, Style style)
    {
        Directory.CreateDirectory(folder);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        // Codex: 저장소 추적 파일을 먼저 깔고 context 파일을 위에 덮는다 (작업 트리 상태라 내용은 같고, 비추적 diff 파일 · 추가 루트 파일이 여기서 들어간다).
        var tracked = style == Style.Codex ? CopyTracked(root, folder, options, ct) : default;
        var diffs = new List<(string Inside, string Dest)>();
        var instructionSkipped = new List<string>();
        var copied = new Dictionary<string, (string Inside, string Sha, int? Lines)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var copyOrder = new List<string>();   // 지휘자가 넘긴 순서 (색인 예산을 이 순서로 쓴다)
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
            // Codex: 지휘자가 넘겼어도 Codex가 지시 · 설정으로 읽는 파일은 작업 폴더에 두지 않는다 (diff 본문에는 남는다 — b476ab3099 1-N1).
            if (style == Style.Codex && IsCodexInstructionPath(inside))
            {
                instructionSkipped.Add(inside);
                continue;
            }
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
                    using var target = new FileStream(dest, FileMode.Create, FileAccess.Write);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read, lines = 0;
                    byte last = (byte)'\n';
                    bool first = true, utf16 = false;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        target.Write(buffer, 0, read);
                        sha.AppendData(buffer, 0, read);
                        // UTF-16(BOM FF FE · FE FF)은 글자 안에 0x0A 바이트가 있어 바이트로 세면 부풀려진다 → 줄 수 모름 (sub_self 535ca00022 C7)
                        if (first) utf16 = read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
                        first = false;
                        lines += buffer.AsSpan(0, read).Count((byte)'\n');
                        last = buffer[read - 1];
                    }
                    if (last != (byte)'\n') lines++;   // 마지막 줄에 줄바꿈이 없으면 그 줄도 센다 (빈 파일은 0)
                    copied[dest] = (inside, Convert.ToHexStringLower(sha.GetHashAndReset())[..12], utf16 ? null : lines);
                    copyOrder.Add(dest);
                    if (style == Style.Codex && IsDiff(inside)) diffs.Add((inside, dest));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail(folder, $"'{range.Path}': 복사 실패 ({ex.GetType().Name}: {ex.Message})");
                }
            }
            focus.Add($"- {inside}:{range.StartLine}-{range.EndLine}");
        }

        var text = new StringBuilder();
        if (style == Style.Codex)
        {
            var skipped = tracked.Skipped > 0 ? $", 뺀 파일 {tracked.Skipped:N0}개" : "";
            var why = tracked.Note is { } note ? $" ({note})" : "";
            text.AppendLine().AppendLine("## 작업 폴더 (서버가 복사함)")
                .AppendLine(tracked.Files > 0
                    ? $"지금 작업 폴더는 저장소 사본이다 — git 추적 파일 {tracked.Files:N0}개를 지금 작업 트리 상태 그대로 같은 상대 경로로 복사했다 (비추적 · 금지 패턴 · 이진 · 큰 파일 · Codex 지시 · 설정 파일은 사본에 넣지 않았다{skipped}). 아래 '중심' 목록에서 시작하고, 판단에 필요한 정의 · 호출부 · 시험은 rg · Get-Content로 직접 찾아 읽는다."
                    : $"지금 작업 폴더에는 지휘자가 고른 파일만 아래 경로 그대로 복사돼 있다{why}. 판단에 필요하면 목록의 다른 파일 · 다른 부분도 읽는다.")
                .AppendLine("작업 폴더 밖은 읽지 않는다. 파일을 만들거나 고치지 않는다. 명령은 읽기 · 검색만 쓴다.")
                .AppendLine("확인할 수 없는 것은 추측하지 말고 자료 요청(needsInfo)으로 남긴다.")
                .AppendLine().AppendLine("### 지휘자가 넘긴 파일 (경로 · SHA-256 앞 12자 · 줄 수)");
        }
        else
        {
            text.AppendLine().AppendLine("## 작업 폴더 (서버가 복사함)")
                .AppendLine("지금 작업 폴더에 지휘자가 고른 파일이 파일 전체로, 아래 경로 그대로 복사돼 있다. 폴더에 있는 파일은 아래 목록이 전부다.")
                .AppendLine("Read로 연다 (검색 도구가 없을 수 있다). 판단에 필요하면 목록의 다른 파일 · 다른 부분도 읽는다. 폴더 밖은 읽지 않는다.")
                .AppendLine("'정의' 줄은 서버가 정규식으로 뽑은 대략의 위치(이름 줄번호)다. 큰 파일은 필요한 범위만 Read(offset · limit)로 열고, 판단 전에 그 줄을 직접 확인한다.")
                .AppendLine().AppendLine("### 파일 (경로 · SHA-256 앞 12자 · 줄 수)");
        }
        // 정의 색인 (계획서 2026-10-27_작업폴더-정의-색인): 예산은 지휘자가 넘긴 순서(보통 중심 파일이 앞)로 쓰고,
        // 상한에 걸린 파일은 건너뛰고 다음 파일을 본다. 목록은 경로 순 (sub_self 535ca00022 C3).
        var indexes = new Dictionary<string, string>(copied.Comparer);
        var overBudget = new List<string>();
        var indexChars = 0;
        // Codex는 rg로 직접 찾으므로 정의 색인을 넣지 않는다.
        foreach (var dest in style == Style.Codex ? [] : copyOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (DefinitionIndex.ReadText(dest) is not { } body) continue;
            if (DefinitionIndex.Format(DefinitionIndex.Outline(copied[dest].Inside, body)) is not { } index) continue;
            if (indexChars + index.Length > DefinitionIndex.MaxTotalChars) { overBudget.Add(copied[dest].Inside); continue; }
            indexes[dest] = index;
            indexChars += index.Length;
        }
        foreach (var (dest, (inside, sha, lines)) in copied.OrderBy(v => v.Value.Inside, StringComparer.Ordinal))
        {
            text.AppendLine($"- {inside} · {sha} · {(lines is { } n ? $"{n}줄" : "줄 수 모름(UTF-16)")}");
            if (indexes.TryGetValue(dest, out var index)) text.AppendLine(index);
        }
        if (overBudget.Count > 0)
            text.AppendLine($"(정의 색인 상한 {DefinitionIndex.MaxTotalChars:N0}자를 넘어 색인을 뺀 파일: {string.Join(", ", overBudget.Order(StringComparer.Ordinal))})");
        if (instructionSkipped.Count > 0)
            text.AppendLine($"(Codex가 지시 · 설정으로 읽는 파일이라 작업 폴더에 두지 않음: {string.Join(", ", instructionSkipped.Distinct())})");
        text.AppendLine().AppendLine("### 이번 검토의 중심 (경로:줄)");
        foreach (var line in focus) text.AppendLine(line);
        // Codex: 변경 diff는 본문을 붙인다 — 실험에서 'diff 첨부 + 나머지는 직접 찾기'가 시간 · 발견 균형이 가장 좋았다 (계획서 2026-10-29_Codex-외부-리뷰어).
        foreach (var (inside, dest) in diffs)
        {
            var body = File.ReadAllText(dest);
            var fence = new string('`', Math.Max(3, LongestBacktickRun(body) + 1));
            text.AppendLine().AppendLine($"## 변경 diff ({inside})").AppendLine(fence + "diff").AppendLine(body.TrimEnd('\r', '\n')).AppendLine(fence);
        }
        return (folder, text.ToString(), null);
    }

    private static bool IsDiff(string path) =>
        path.EndsWith(".diff", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".patch", StringComparison.OrdinalIgnoreCase);

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        return longest;
    }

    /// <summary>
    /// 루트의 git 추적 파일(<c>git ls-files</c> — intent-to-add 포함, 비추적 제외)을 지금 작업 트리 상태로 복사한다.
    /// 금지 패턴(비밀 파일 · .git) · AGENTS.md(Codex가 지시로 읽음) · 링크 · 파일 크기 상한 · 이진(앞 8KB에 NUL) · 지워진 파일은 건너뛴다.
    /// git이 없거나 저장소가 아니거나 상한(<see cref="MaxTrackedFiles"/> · <see cref="MaxTrackedBytes"/>)을 넘으면 복사하지 않고 Note에 이유를 남긴다
    /// (그때는 context 파일만으로 돈다). 반쯤 복사한 파일은 지운다.
    /// </summary>
    internal static (int Files, int Skipped, string? Note) CopyTracked(string root, string folder, ContextOptions options, CancellationToken ct)
    {
        var (paths, gitError) = GitTrackedFiles(root, ct);
        if (gitError is not null) return (0, 0, gitError);
        if (paths!.Count == 0) return (0, 0, "git 추적 파일 없음");
        if (paths.Count > MaxTrackedFiles) return (0, 0, $"추적 파일 {paths.Count:N0}개 / 사본 상한 {MaxTrackedFiles:N0}개라 저장소 사본을 만들지 않음");

        // git이 돌려준 경로는 실제 이름이라 SafePath.Resolve(구성 요소마다 디렉터리 열거 · 파일마다 패턴 컴파일)보다 가벼운 검사로 본다:
        // 대체 데이터 스트림 · 장치 이름 · 루트 밖 · 금지 패턴(한 번 컴파일) · 링크(폴더마다 한 번) (/cross-review 90978dee2f C2).
        var denied = SafePath.DenyMatcher(options.AllDenyPatterns);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var linkFree = new Dictionary<string, bool>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int files = 0, skipped = 0;
        long total = 0;
        var buffer = new byte[8192];
        foreach (var relative in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (IsCodexInstructionPath(relative) || relative.Contains(':') || SafePath.WindowsNameProblem(relative) is not null || denied(relative) is not null) { skipped++; continue; }
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                var info = new FileInfo(full);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !NoLinkBetween(root, info.DirectoryName!, linkFree)) { skipped++; continue; }
                if (options.MaxFileBytes > 0 && info.Length > options.MaxFileBytes) { skipped++; continue; }
                using (var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = source.Read(buffer, 0, buffer.Length);
                    if (buffer.AsSpan(0, head).Contains((byte)0)) { skipped++; continue; }
                }
                total += info.Length;
                if (total > MaxTrackedBytes)
                {
                    DeleteChildren(folder);
                    return (0, 0, $"추적 파일 합계가 사본 상한 {MaxTrackedBytes / 1_000_000:N0}MB를 넘어 저장소 사본을 만들지 않음");
                }
                var dest = Path.GetFullPath(Path.Combine(folder, Path.GetRelativePath(root, full)));
                if (!dest.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(full, dest, overwrite: true);
                files++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { skipped++; }
        }
        return (files, skipped, null);
    }

    /// <summary>
    /// Codex가 작업 폴더에서 지시 · 설정으로 읽는 자리: AGENTS.md · AGENTS.override.md · .codex/ (프로젝트 설정) · .agents/ (스킬).
    /// 신뢰하지 않는 리뷰 대상 저장소의 글이 지시나 설정이 되지 않게 사본에서 뺀다 — 2026-10-29 실측으로는 작업 폴더의
    /// .codex/config.toml · .agents/skills가 적용되지 않았지만(신뢰하지 않은 폴더), 판이 바뀌어도 막히게 둔다 (지휘자 K1 · e37c6e41c9 C3).
    /// </summary>
    internal static bool IsCodexInstructionPath(string relative)
    {
        var parts = relative.Split('/', '\\');
        var name = parts[^1];
        return name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) || name.Equals("AGENTS.override.md", StringComparison.OrdinalIgnoreCase)
            || parts[..^1].Any(p => p.Equals(".codex", StringComparison.OrdinalIgnoreCase) || p.Equals(".agents", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>루트(제외)부터 dir까지 링크 · junction이 없는지. 폴더마다 한 번만 본다.</summary>
    private static bool NoLinkBetween(string root, string dir, Dictionary<string, bool> cache)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(dir), root, StringComparison.OrdinalIgnoreCase)) return true;
        if (cache.TryGetValue(dir, out var known)) return known;
        var parent = Path.GetDirectoryName(dir);
        var ok = new DirectoryInfo(dir).LinkTarget is null && parent is not null && NoLinkBetween(root, parent, cache);
        cache[dir] = ok;
        return ok;
    }

    private static void DeleteChildren(string folder)
    {
        foreach (var dir in Directory.EnumerateDirectories(folder)) TryDelete(dir);
        foreach (var file in Directory.EnumerateFiles(folder))
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// <c>git ls-files -z</c> (루트 기준 상대 경로). git은 PATH의 절대 경로 폴더에서만 찾는다 — 서버의 현재 폴더(프로젝트 루트)의 git.exe가 실행되지 않게.
    /// </summary>
    private static (List<string>? Paths, string? Error) GitTrackedFiles(string root, CancellationToken ct)
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        if (git is null) return (null, "git을 찾지 못함");

        var psi = new System.Diagnostics.ProcessStartInfo(git)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        // fsmonitor 끔: 리뷰 대상 .git/config의 core.fsmonitor 명령이 실행되지 않게. 환경도 Codex 자식과 같이 정리 (/cross-review e37c6e41c9 C4).
        foreach (var a in new[] { "-c", "core.fsmonitor=false", "-c", "core.quotepath=off", "ls-files", "-z" }) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Providers.CodexCliChatClient.ChildEnvironment(npmMarker: false))
            if (v is null) psi.Environment.Remove(k);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (null, "git을 실행하지 못함");
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            _ = p.StandardError.ReadToEndAsync(wait.Token); // 읽어 두지 않으면 stderr 파이프가 차서 git이 멈출 수 있다
            string output;
            try
            {
                output = p.StandardOutput.ReadToEndAsync(wait.Token).GetAwaiter().GetResult();
                p.WaitForExitAsync(wait.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
                return (null, "git ls-files가 30초 안에 끝나지 않음");
            }
            return p.ExitCode == 0
                ? ([.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)], null)
                : (null, "git 저장소가 아님");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (null, $"git 실행 실패 ({ex.Message})");
        }
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
