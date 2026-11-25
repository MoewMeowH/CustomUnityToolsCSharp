using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HB.Chat;

/// <summary>
/// git 변경 한 줄 (porcelain v1). Path · OldPath는 저장소 루트 기준 '/' 경로. Index · Work는 두 칸 상태 글자(' ' · M · A · D · R · C · U · ?).
/// </summary>
public sealed record GitEntry(char Index, char Work, string Path, string? OldPath = null)
{
    public bool Untracked => Index == '?';

    /// <summary>인덱스(staged)에 올라간 변경.</summary>
    public bool Staged => Index is not (' ' or '?' or '!');

    /// <summary>화면 한 글자: 인덱스 쪽이 있으면 그것, 아니면 작업 트리 쪽 (미추적은 ?).</summary>
    public char Shown => Untracked ? '?' : Index != ' ' ? Index : Work;
}

/// <summary>
/// 저장소 변경: 루트(전체 경로) · 줄 전부 · 오류(git 없음 · 저장소 아님 · 시간 넘김 — 있으면 줄은 비어 있음) ·
/// HEAD 커밋 시각(없으면 null — 그 전 편집 기록은 이미 커밋된 일이라 출처 판정에서 뺀다).
/// </summary>
public sealed record GitStatus(string? Root, IReadOnlyList<GitEntry> Entries, string? Error, DateTimeOffset? HeadAt = null)
{
    /// <summary>저장소 루트 <see cref="GitChanges.IgnoreFile"/>로 숨긴 미추적 줄 수.</summary>
    public int Hidden { get; init; }
}

/// <summary>
/// 곁칸 변경 탭 · 같은 저장소 경고용 git 읽기. git CLI를 인자 목록으로(셸 없이) 부르고, GIT_OPTIONAL_LOCKS=0으로 status가 인덱스 잠금을 잡지 않게 한다
/// (지휘자가 같은 저장소에서 git add · commit을 돌리는 중일 수 있다). 읽기만 한다.
/// </summary>
public sealed class GitChanges
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>작업 폴더 → 저장소 루트를 기억하는 시간.</summary>
    public static readonly TimeSpan RootCache = TimeSpan.FromMinutes(5);

    /// <summary>diff · 새 파일 내용 상한 (글자 수 — 넘으면 앞부분만, git 출력은 이만큼만 읽고 끝낸다).</summary>
    public const int MaxDiffChars = 200 * 1024;

    private readonly string _git;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, string? Root)> _roots = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="git">git 실행 파일 (시험용, 비우면 PATH의 git).</param>
    public GitChanges(string? git = null) => _git = git ?? "git";

    /// <summary>
    /// 폴더가 든 저장소 루트 (전체 경로, 플랫폼 구분자). 저장소가 아니거나 git이 없거나 시간을 넘기면 null.
    /// git이 답한 결과(저장소 · 저장소 아님)만 <see cref="RootCache"/> 동안 기억한다 — git 없음 · 시간 넘김은 기억하지 않는다 (/self-review sub C3).
    /// </summary>
    public async Task<string?> RootAsync(string folder, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
            if (_roots.TryGetValue(folder, out var hit) && now - hit.At < RootCache) return hit.Root;
        if (!Directory.Exists(folder)) return null; // 없는 작업 폴더로 git을 띄우면 '찾지 못함'과 같은 예외가 난다
        var (exit, output, _, _) = await RunAsync(folder, ["rev-parse", "--show-toplevel"], ct);
        if (exit < 0) return null;
        var root = exit == 0 && output.Trim() is { Length: > 0 } top ? Path.GetFullPath(top) : null;
        lock (_gate) _roots[folder] = (now, root);
        return root;
    }

    /// <summary>폴더가 든 저장소 전체의 변경 (미추적 포함, 줄 전부 — 화면 상한은 부르는 쪽이) · HEAD 커밋 시각.</summary>
    public async Task<GitStatus> StatusAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return new(null, [], "작업 폴더가 없습니다.");
        var root = await RootAsync(folder, ct);
        if (root is null) return new(null, [], _missing ? "git을 찾지 못했습니다." : "git 저장소가 아니거나 git이 답하지 않았습니다.");
        var (exit, output, error, _) = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        if (exit != 0) return new(root, [], "git status 실패: " + FirstLine(error));
        var (headExit, head, _, _) = await RunAsync(root, ["log", "-1", "--format=%ct"], ct);
        DateTimeOffset? headAt = headExit == 0 && long.TryParse(head.Trim(), out var seconds) && seconds is > 0 and < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null; // 커밋이 없는 새 저장소면 null
        var entries = Parse(output);
        var hidden = 0;
        // 앱 전용 무시 파일 (결정 "앱 전용 무시 파일"): 미추적 줄만 숨긴다 — 추적 변경 · staged는 늘 보임 (인덱스 경고가 가려지지 않게).
        // gitignore 문법은 git이 풀게 한다: 무시 파일 패턴에 **맞는** 미추적 파일만 받아 그 줄을 뺀다 — 두 조회 사이에 누가 git add한 파일은
        // 이 목록에 없어 숨지 않는다 (빠진 것을 무시로 치지 않음, sub_self C8)
        var ignore = Path.Combine(root, IgnoreFile);
        if (entries.Any(e => e.Untracked) && File.Exists(ignore))
        {
            var (lsExit, matched, _, _) = await RunAsync(root, ["ls-files", "--others", "--ignored", "--exclude-from=" + ignore, "-z"], ct);
            if (lsExit == 0)
            {
                var hide = matched.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                var shown = entries.Where(e => !e.Untracked || !hide.Contains(e.Path)).ToList();
                hidden = entries.Count - shown.Count;
                entries = shown;
            }
        }
        return new(root, entries, null, headAt) { Hidden = hidden };
    }

    /// <summary>변경 탭에서 미추적 파일을 숨기는 앱 전용 무시 파일 (저장소 루트, gitignore 문법).</summary>
    public const string IgnoreFile = ".hbchatignore";

    /// <summary>
    /// 파일 하나의 diff (HEAD 대비 — staged와 작업 트리 둘 다). 미추적이면 파일 앞부분. 글(없으면 null)과 안내(이진 · 잘림 · 실패).
    /// entry는 <see cref="StatusAsync"/>가 돌려준 줄이어야 한다 (부르는 쪽이 확인 — 임의 경로를 읽지 않게).
    /// </summary>
    public async Task<(string? Text, string? Note)> DiffAsync(string root, GitEntry entry, CancellationToken ct = default)
    {
        if (entry.Untracked)
        {
            var full = Path.GetFullPath(Path.Combine(root, entry.Path));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return (null, "저장소 밖 경로입니다.");
            if (Directory.Exists(full)) return (null, "폴더입니다.");
            try
            {
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[Math.Min(fs.Length, MaxDiffChars)];
                var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                if (buffer.AsSpan(0, Math.Min(read, 8000)).Contains((byte)0)) return (null, "이진 파일입니다.");
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                return (string.Join('\n', text.Split('\n').Select(l => "+" + l.TrimEnd('\r'))), fs.Length > MaxDiffChars ? "앞부분만 보입니다 (새 파일이 큽니다)." : "새 파일 (git이 아직 모름)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "읽지 못했습니다: " + ex.Message); }
        }
        string[] paths = entry.OldPath is { } old ? [old, entry.Path] : [entry.Path];
        // 출력은 상한까지만 읽고 git을 끝낸다 — Unity 씬 · 프리팹 같은 큰 diff를 통째로 메모리에 올리지 않게 (/self-review sub C7)
        var (exit, output, error, cut) = await RunAsync(root, ["diff", "HEAD", "--no-color", "--no-ext-diff", "--", .. paths], ct, MaxDiffChars);
        if (exit != 0) // HEAD가 없는 새 저장소: 인덱스 대비
            (exit, output, error, cut) = await RunAsync(root, ["diff", "--cached", "--no-color", "--no-ext-diff", "--", .. paths], ct, MaxDiffChars);
        if (exit != 0) return (null, "git diff 실패: " + FirstLine(error));
        if (output.Length == 0) return (null, "보일 차이가 없습니다 (모드만 바뀜 등).");
        return (output, cut ? "앞부분만 보입니다 (차이가 큽니다)." : null);
    }

    /// <summary>porcelain v1 -z 출력 → 줄들. 이름 바꿈(R · C)은 새 경로 다음에 옛 경로가 온다.</summary>
    public static IReadOnlyList<GitEntry> Parse(string z)
    {
        var parts = z.Split('\0');
        var list = new List<GitEntry>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length < 4 || p[2] != ' ') continue;
            var (x, y, path) = (p[0], p[1], p[3..]);
            string? old = null;
            if ((x is 'R' or 'C' || y is 'R' or 'C') && i + 1 < parts.Length) old = parts[++i];
            list.Add(new GitEntry(x, y, path, old));
        }
        return list;
    }

    /// <summary>마지막 실행에서 git을 찾지 못했는지.</summary>
    private volatile bool _missing;

    /// <summary>
    /// git 한 번. Exit &lt; 0이면 git이 답하지 않은 것(없음 · 시간 넘김). maxChars가 있으면 출력을 그만큼만 읽고 git을 끝낸다 (Cut = 잘림, Exit 0).
    /// 부르는 쪽 ct가 취소되면 OperationCanceledException.
    /// </summary>
    private async Task<(int Exit, string Output, string Error, bool Cut)> RunAsync(string cwd, IReadOnlyList<string> args, CancellationToken ct, int? maxChars = null)
    {
        var psi = new ProcessStartInfo(_git)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in (string[])["-c", "core.quotepath=false", "-c", "color.ui=false", .. args]) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // status가 인덱스를 새로 쓰며 index.lock을 잡지 않게
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        Process process;
        try
        {
            process = Process.Start(psi)!;
            _missing = false;
        }
        catch (Win32Exception)
        {
            _missing = true;
            return (-1, "", "git을 찾지 못했습니다.", false);
        }
        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                var (output, cut) = await ReadAsync(process.StandardOutput, maxChars, timeout.Token);
                if (cut)
                {
                    Kill(process);
                    return (0, output, "", true);
                }
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, output, await error, false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                ct.ThrowIfCancellationRequested();
                return (-1, "", "git이 시간 안에 끝나지 않았습니다.", false);
            }
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    /// <summary>출력 읽기 — max가 있으면 그 글자 수를 넘는 순간 멈춘다 (잘린 앞부분, true).</summary>
    private static async Task<(string Text, bool Cut)> ReadAsync(StreamReader reader, int? max, CancellationToken ct)
    {
        if (max is not { } limit) return (await reader.ReadToEndAsync(ct), false);
        var text = new StringBuilder();
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer, ct) is var n and > 0)
        {
            text.Append(buffer, 0, n);
            if (text.Length > limit) return (text.ToString(0, limit), true);
        }
        return (text.ToString(), false);
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : "(오류 글 없음)";
}
