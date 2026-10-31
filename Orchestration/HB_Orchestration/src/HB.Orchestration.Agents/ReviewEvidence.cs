using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HB.Orchestration.Agents;

public sealed record ReviewEvidenceSource(string Root, string[] DenyPatterns);

/// <summary>서버가 지정한 루트에서 제외 규칙을 적용해 읽는다. 셸 실행은 제공하지 않는다.</summary>
public sealed partial class ReviewEvidence
{
    public const int MaxCalls = 48;
    public const int MaxReturnedChars = 200_000;
    public const int PageChars = 6_000;
    private const int MaxFileBytes = 2_000_000;
    private const int MaxScanBytes = 8_000_000;
    private readonly string _root;
    private readonly string[] _files;
    private readonly Dictionary<string, string> _paths;
    private readonly string[] _denyPatterns;
    private readonly bool _original;
    private readonly bool _inventoryTruncated;
    private const int OriginalFileLimit = 50_000;
    private static readonly HashSet<string> GeneratedFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".claude", ".codex", ".agents", "review_tmp", "subagent_in", "bin", "obj", "node_modules", "Library", "Temp", "Logs", "UserSettings" };
    private readonly Lock _gate = new();
    private int _calls, _returned;
    private readonly Dictionary<string, (long Length, DateTime Modified)> _failedSearchFiles = new(StringComparer.Ordinal);
    private const int MaxSearchFiles = 256;
    private const int MaxSkippedPerPage = 20;
    private sealed class UnreadableTextException(string message) : IOException(message);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ReviewEvidence(string root, IEnumerable<string>? denyPatterns = null, bool original = false)
    {
        _original = original;
        _denyPatterns = ContextOptions.BuiltInDenyPatterns.Concat(denyPatterns ?? [])
            .Concat(original ? ContextOptions.BuiltInRepoCopyDenyPatterns : []).Distinct().ToArray();
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root) || (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("리뷰 사본 폴더가 없거나 링크입니다.");
        var files = new List<string>();
        var denied = SafePath.DenyMatcher(_denyPatterns);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_root, 0));
        while (pending.TryPop(out var dir))
        {
            if (dir.Depth > 64) throw new IOException("사본 폴더 깊이 상한을 넘었습니다.");
            foreach (var path in Directory.EnumerateFileSystemEntries(dir.Path))
            {
                var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
                if (_original && (GeneratedFolders.Contains(Path.GetFileName(path)) || ReviewWorkFolder.IsExcludedFromCopy(relative))) continue;
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                if ((attr & FileAttributes.Directory) != 0)
                {
                    if (denied(relative + "/__entry__") is null) pending.Push((path, dir.Depth + 1));
                }
                else
                {
                    if (denied(relative) is null && SafePath.WindowsNameProblem(relative) is null
                        && !ReviewWorkFolder.IsCodexInstructionPath(relative)) files.Add(relative);
                }
                if (_original && files.Count >= OriginalFileLimit) { _inventoryTruncated = true; break; }
                // 사본 생성기가 추적 파일 상한과 context 추가분을 관리한다. 별도 상한으로 정상 사본을 거절하지 않는다.
            }
            if (_inventoryTruncated) break;
        }
        _files = files.Order(StringComparer.Ordinal).ToArray();
        _paths = _files.ToDictionary(p => p, p => p, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public string List(string contains = "", int offset = 0) => Execute(() =>
    {
        if (contains.Length > 256 || offset < 0) throw new ArgumentException("필터 또는 위치가 잘못됐습니다.");
        var matches = _files.Where(p => p.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = new List<string>();
        var size = 0;
        var i = Math.Min(offset, matches.Length);
        while (i < matches.Length && page.Count < 80 && size + matches[i].Length < PageChars)
        {
            size += matches[i].Length;
            page.Add(matches[i++]);
        }
        if (i < matches.Length && page.Count == 0) throw new IOException("파일 경로가 응답 상한을 넘습니다.");
        return new { files = page, total = matches.Length, nextOffset = i < matches.Length ? (int?)i : null };
    });

    public string Read(string path, int offset = 0, int maxChars = PageChars) => Execute(() =>
    {
        if (maxChars < 2 || maxChars > PageChars) throw new ArgumentException("페이지 크기가 잘못됐습니다.");
        var (body, hash) = Load(path);
        if (offset < 0 || offset > body.Length || (offset > 0 && offset < body.Length && char.IsLowSurrogate(body[offset])))
            throw new ArgumentException("본문 위치가 잘못됐습니다. 반환된 nextOffset을 사용하세요.");
        var end = Math.Min(body.Length, offset + maxChars);
        if (end < body.Length && end > offset && char.IsHighSurrogate(body[end - 1])) end--;
        return new { path, sha256 = hash, offset, startLine = 1 + body.AsSpan(0, offset).Count('\n'),
            text = body[offset..end], nextOffset = end < body.Length ? (int?)end : null, totalChars = body.Length };
    });

    public string Search(string query, int fileIndex = 0, int offset = 0, string pathPrefix = "", string extension = "", bool retrySkipped = false) => Execute(() =>
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256 || fileIndex < 0 || fileIndex > _files.Length || offset < 0)
            throw new ArgumentException("검색어 또는 위치가 잘못됐습니다.");
        if (pathPrefix.Length > 512 || Path.IsPathRooted(pathPrefix) || pathPrefix.IndexOfAny([':', '*', '?']) >= 0 || pathPrefix.Any(char.IsControl)
            || pathPrefix.Split('/', '\\').Any(p => p is "." or "..") || extension.Length > 32 || extension.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || extension.Any(char.IsControl))
            throw new ArgumentException("검색 범위가 잘못됐습니다.");
        pathPrefix = pathPrefix.Replace('\\', '/').TrimEnd('/');
        if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;
        if (extension == ".") throw new ArgumentException("확장자가 비어 있습니다.");
        var hits = new List<object>();
        var skipped = new List<object>();
        long scanned = 0;
        var examined = 0;
        var cacheHits = 0;
        object Page(int? nextFileIndex, int nextOffset) => new { hits, skipped, incomplete = skipped.Count > 0,
            nextFileIndex, nextOffset, pathPrefix, extension, filesExamined = examined, scannedBytes = scanned, cacheHits };
        var i = fileIndex;
        for (; i < _files.Length; i++, offset = 0)
        {
            var path = _files[i];
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if ((pathPrefix.Length > 0 && !path.StartsWith(pathPrefix + "/", comparison))
                || (extension.Length > 0 && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) continue;
            if (examined >= MaxSearchFiles || scanned >= MaxScanBytes) return Page(i, offset);
            examined++;
            string body;
            (long Length, DateTime Modified)? signature = null;
            var cached = false;
            try
            {
                // 캐시된 실패도 매번 경로 정책을 확인한다. 실패 캐시는 읽기 권한을 대신하지 않는다.
                var full = ResolveFile(path);
                var info = new FileInfo(full);
                signature = (info.Length, info.LastWriteTimeUtc);
                if (retrySkipped) _failedSearchFiles.Remove(path);
                cached = _failedSearchFiles.TryGetValue(path, out var old) && old == signature.Value;
                if (cached)
                {
                    cacheHits++;
                    throw new UnreadableTextException("이전 검색에서 읽지 못한 텍스트 파일입니다.");
                }
                _failedSearchFiles.Remove(path);
                scanned += Math.Min(info.Length, MaxFileBytes);
                (body, _) = LoadFile(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 형식/크기 실패만 캐시한다. 잠금·접근 오류 등은 다음 검색에서 다시 시도한다.
                if (signature is { } stamp && ex is UnreadableTextException or DecoderFallbackException) _failedSearchFiles[path] = stamp;
                skipped.Add(new { fileIndex = i, path, cached, reason = "파일을 읽을 수 없거나 텍스트 형식·크기 제한에 맞지 않습니다." });
                if (skipped.Count >= MaxSkippedPerPage) return Page(i + 1 < _files.Length ? i + 1 : null, 0);
                continue;
            }
            if (offset > body.Length) throw new ArgumentException("검색 위치가 본문보다 큽니다.");
            while (offset < body.Length)
            {
                var at = body.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var end = Math.Min(body.Length, at + Math.Min(100, query.Length + 60));
                if (end < body.Length && char.IsHighSurrogate(body[end - 1])) end--;
                hits.Add(new { path, offset = at, line = 1 + body.AsSpan(0, at).Count('\n'), text = body[at..end] });
                offset = at + query.Length;
                if (hits.Count == 12) return Page(i, offset);
            }
        }
        return Page(null, 0);
    });

    private string ResolveFile(string path)
    {
        var (full, error) = SafePath.Resolve(path, _root, _denyPatterns, SafePath.Access.Read);
        if (error is not null || ReviewWorkFolder.IsCodexInstructionPath(path)) throw new ArgumentException("사본 안의 허용된 상대 파일 경로만 읽습니다.");
        var relative = Path.GetRelativePath(_root, full!).Replace('\\', '/');
        if (!_paths.TryGetValue(relative, out var canonical)) throw new ArgumentException("사본 목록에 없는 파일입니다.");
        return Path.Combine(_root, canonical);
    }

    private (string Text, string Hash) Load(string path) => LoadFile(ResolveFile(path));

    private static (string Text, string Hash) LoadFile(string full)
    {
        using var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > MaxFileBytes) throw new UnreadableTextException("파일 크기 상한을 넘었습니다.");
        var bytes = new byte[(int)source.Length];
        source.ReadExactly(bytes);
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var body = reader.ReadToEnd();
        if (body.Contains('\0')) throw new UnreadableTextException("이진 파일은 읽지 않습니다.");
        return (body, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private string Execute(Func<object> action, Action? onSuccess = null)
    {
        lock (_gate)
        {
            if (++_calls > MaxCalls || _returned >= MaxReturnedChars)
                return JsonSerializer.Serialize(new { error = "자료 도구 예산 소진. 확보한 근거로 마무리하고 남은 항목은 needsInfo로 반환하세요.", exhausted = true }, Json);
            try
            {
                var data = JsonSerializer.SerializeToElement(action(), Json);
                // 예산 단위는 성공 응답 data의 직렬화된 UTF-16 문자 수. 바깥 메타데이터와 오류는 제외한다.
                var size = data.GetRawText().Length;
                if (_returned + size > MaxReturnedChars)
                {
                    return JsonSerializer.Serialize(new { error = "남은 응답량보다 큽니다. 읽기는 maxChars를 줄이거나 남은 항목을 needsInfo로 반환하세요.",
                        exhausted = false, remainingCalls = MaxCalls - _calls, remainingChars = MaxReturnedChars - _returned }, Json);
                }
                var response = JsonSerializer.Serialize(new { data, source = _original ? "original" : "snapshot", inventoryTruncated = _inventoryTruncated,
                    remainingCalls = MaxCalls - _calls, remainingChars = MaxReturnedChars - _returned - size }, Json);
                onSuccess?.Invoke();
                _returned += size;
                return response;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                return JsonSerializer.Serialize(new { error = "파일을 읽을 수 없거나 입력이 잘못됐습니다.",
                    code = ex is GitEvidenceException git ? git.Code : "invalid_or_unreadable", remainingCalls = MaxCalls - _calls }, Json);
            }
        }
    }
}
