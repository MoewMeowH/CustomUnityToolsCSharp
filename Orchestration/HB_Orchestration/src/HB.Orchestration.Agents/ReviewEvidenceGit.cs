using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HB.Orchestration.Agents;

public sealed partial class ReviewEvidence
{
    private string? _gitHead;
    private readonly Dictionary<string, HashSet<string>> _gitCommits = new(StringComparer.Ordinal);

    private sealed class GitEvidenceException(string code) : IOException
    {
        public string Code { get; } = code;
    }

    public string GitHistory(string path)
    {
        string? canonical = null;
        string[] page = [];
        return Execute(() =>
        {
            canonical = GitPath(path);
            var head = GitHead();
            GitBlob(canonical, head);
            var commits = GitText("log", "--no-follow", "--no-show-signature", "--format=%H", "-n", "21", head, "--", canonical)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (commits.Any(c => !ObjectId(c))) throw new IOException("잘못된 Git 응답입니다.");
            page = commits.Take(20).ToArray();
            return new { path = canonical, head, commits = page, truncated = commits.Length > 20 };
        }, () =>
        {
            if (!_gitCommits.TryGetValue(canonical!, out var allowed)) _gitCommits[canonical!] = allowed = new(StringComparer.Ordinal);
            allowed.UnionWith(page);
        });
    }

    public string GitRead(string path, string commit, int offset = 0, int maxChars = PageChars) => Execute(() =>
    {
        var canonical = GitPath(path);
        if (!ObjectId(commit) || !_gitCommits.TryGetValue(canonical, out var allowed) || !allowed.Contains(commit)
            || maxChars < 2 || maxChars > PageChars)
            throw new ArgumentException("이 경로의 Git 이력 도구가 반환한 커밋만 읽습니다.");
        GitBlob(canonical, GitHead());
        var blob = GitBlob(canonical, commit);
        if (!long.TryParse(GitText("cat-file", "-s", blob).Trim(), out var size) || size < 0 || size > MaxFileBytes)
            throw new IOException("Git 파일 크기 상한을 넘었습니다.");
        var bytes = GitBytes(MaxFileBytes, "cat-file", "blob", blob);
        var content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        var body = new UTF8Encoding(false, true).GetString(content);
        if (body.Contains('\0')) throw new IOException("이진 파일은 읽지 않습니다.");
        if (offset < 0 || offset > body.Length || (offset > 0 && offset < body.Length && char.IsLowSurrogate(body[offset])))
            throw new ArgumentException("반환된 nextOffset을 사용하세요.");
        var end = Math.Min(body.Length, offset + maxChars);
        if (end < body.Length && end > offset && char.IsHighSurrogate(body[end - 1])) end--;
        return new { path = canonical, commit, blob, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            offset, startLine = 1 + body.AsSpan(0, offset).Count('\n'), text = body[offset..end],
            nextOffset = end < body.Length ? (int?)end : null, totalChars = body.Length };
    });

    private string GitBlob(string canonical, string commit)
    {
        // 고정 HEAD와 과거 커밋 모두에서 일반 파일 모드만 허용한다.
        var entry = GitText("ls-tree", "-z", commit, "--", canonical);
        var tab = entry.IndexOf('\t');
        if (tab < 0 || entry[(tab + 1)..] != canonical + "\0") throw new IOException("해당 커밋에 파일이 없습니다.");
        var fields = entry[..tab].Split(' ');
        if (fields.Length != 3 || fields[0] is not ("100644" or "100755") || fields[1] != "blob" || !ObjectId(fields[2]))
            throw new IOException("일반 Git 파일만 읽습니다.");
        return fields[2];
    }

    private static bool ObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private string GitPath(string path)
    {
        if (!_original || path.Length > 512 || path.Any(char.IsControl) || path.IndexOfAny(['*', '?', '~']) >= 0
            || path.Split('/', '\\').Any(p => p is ".." or ".")) throw new ArgumentException("허용된 원본 파일만 조회합니다.");
        var full = ResolveFile(path);
        var canonical = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (canonical.Split('/').Any(GeneratedFolders.Contains) || ReviewWorkFolder.IsExcludedFromCopy(canonical))
            throw new ArgumentException("제외된 Git 경로입니다.");
        return canonical;
    }

    private string GitHead()
    {
        if (_gitHead is not null) return _gitHead;
        // 상위 저장소가 자동 발견돼 자료 루트 밖 이력으로 연결되는 것을 거절한다.
        if (GitText("rev-parse", "--show-prefix").Trim().Length != 0) throw new IOException("자료 루트가 저장소 최상위가 아닙니다.");
        var head = GitText("rev-parse", "--verify", "HEAD^{commit}").Trim();
        if (!ObjectId(head)) throw new IOException("Git HEAD를 확인할 수 없습니다.");
        return _gitHead = head;
    }

    private string GitText(params string[] args) => new UTF8Encoding(false, true).GetString(GitBytes(16_384, args));

    private byte[] GitBytes(int limit, params string[] args)
    {
        var path = ReviewExecutablePolicy.SearchPath(_root, Environment.GetEnvironmentVariable("PATH"));
        var executable = ReviewExecutablePolicy.Git(_root, path)
            ?? throw new GitEvidenceException("git_unavailable");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var kept = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }
            .Select(k => (Key: k, Value: Environment.GetEnvironmentVariable(k))).ToArray();
        start.Environment.Clear();
        foreach (var pair in kept) if (pair.Value is not null) start.Environment[pair.Key] = pair.Value;
        start.Environment["PATH"] = path;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        foreach (var arg in new[] { "--no-pager", "--no-replace-objects", "--literal-pathspecs", "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=", "-c", "protocol.allow=never", "-c", "log.showSignature=false" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Git을 실행할 수 없습니다.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            process.StandardInput.Close();
            var output = Drain(process.StandardOutput.BaseStream, limit, timeout.Token);
            var error = Drain(process.StandardError.BaseStream, 16_384, timeout.Token);
            Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                var stderr = Encoding.UTF8.GetString(error.GetAwaiter().GetResult());
                throw new GitEvidenceException(stderr.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)
                    ? "git_ownership" : stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                        ? "git_not_repository" : "git_failed");
            }
            return output.GetAwaiter().GetResult();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
    }

    private static async Task<byte[]> Drain(Stream stream, int limit, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > limit) throw new IOException("Git 응답 상한을 넘었습니다.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
