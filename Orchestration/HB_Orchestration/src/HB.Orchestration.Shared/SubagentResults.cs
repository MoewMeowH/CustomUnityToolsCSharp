using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Shared;

/// <summary>
/// 검토형 서브에이전트가 지휘자에게 넘긴 결과 하나 (SubagentHandback 한 번 = 보관 파일 하나).
/// 이어 부른 서브에이전트(SendMessage)는 넘길 때마다 따로 남는다 (계획서 2026-10-27_서브에이전트-결과-보존).
/// </summary>
public sealed record SubagentResult
{
    /// <summary>보관 파일 이름 (확장자 제외): &lt;agentId&gt;_&lt;Handback tool_use id&gt;.</summary>
    public required string Key { get; init; }
    public required string AgentId { get; init; }
    public required string AgentType { get; init; }
    public string? SessionId { get; init; }

    /// <summary>서브에이전트의 작업 폴더 (보통 subagent_in/&lt;실행&gt;/).</summary>
    public string? Cwd { get; init; }

    /// <summary>Cwd에서 subagent_in 앞까지 = 지휘자 프로젝트 폴더.</summary>
    public string? ProjectDir { get; init; }

    /// <summary>지휘자가 붙인 위임 설명 (meta.json description).</summary>
    public string? Description { get; init; }

    public DateTimeOffset? ReturnedAt { get; init; }

    /// <summary>지휘자에게 간 결과 원문 (SubagentHandback input.message 그대로).</summary>
    public required string Message { get; init; }

    /// <summary>보관할 때 읽은 세션 기록 파일. Claude Code가 지우면 더는 없다.</summary>
    public string? SourcePath { get; init; }

    public string? Version { get; init; }
}

/// <summary>
/// Claude Code 서브에이전트 기록(~/.claude/projects/&lt;프로젝트&gt;/&lt;세션&gt;/subagents/agent-&lt;id&gt;.jsonl + .meta.json) 읽기.
/// 비공식 형식 — 2026-10-27 확인한 모양(Claude Code 2.1.302~2.1.310, 검토형 101건 모두 SubagentHandback)에만 의존한다. 예외를 던지지 않는다.
/// </summary>
public static partial class SubagentTranscriptReader
{
    public const string HandbackToolName = "SubagentHandback";

    public sealed record Meta(string? AgentType, string? Description, bool? Finished);

    /// <summary>meta.json. 읽지 못하면 null.</summary>
    public static Meta? ReadMeta(string metaPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(metaPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new Meta(Str(root, "agentType"), Str(root, "description"),
                root.TryGetProperty("finished", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>서브에이전트 기록 하나에서 넘긴 결과들. Error는 파일을 읽지 못했을 때만.</summary>
    public static (List<SubagentResult> Results, string? Error) Read(string jsonlPath, Meta meta)
    {
        var results = new List<SubagentResult>();
        string? sessionId = null, cwd = null, agentId = null, version = null;
        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var (_, line) in ClaudeTranscriptReader.Utf8Lines(fs))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    sessionId ??= Str(root, "sessionId");
                    cwd ??= Str(root, "cwd");
                    agentId ??= Str(root, "agentId");
                    version ??= Str(root, "version");
                    if (Str(root, "type") != "assistant") continue;
                    if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object || Str(block, "type") != "tool_use" || Str(block, "name") != HandbackToolName) continue;
                        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object || Str(input, "message") is not { } text) continue;
                        var id = agentId ?? AgentIdFromFile(jsonlPath);
                        results.Add(new SubagentResult
                        {
                            Key = KeyFor(id, Str(block, "id") ?? $"n{results.Count + 1}"),
                            AgentId = id,
                            AgentType = meta.AgentType ?? "",
                            SessionId = sessionId,
                            Cwd = cwd,
                            ProjectDir = ProjectDirOf(cwd),
                            Description = meta.Description,
                            ReturnedAt = Str(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var ts) ? ts : null,
                            Message = text,
                            SourcePath = jsonlPath,
                            Version = version,
                        });
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (results, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (results, null);
    }

    /// <summary>보관 파일 이름으로 쓸 수 있게 영숫자 · '_' · '-' 밖의 문자는 '-'로 바꾼다.</summary>
    public static string KeyFor(string agentId, string handbackId)
    {
        var key = UnsafeChars().Replace($"{agentId}_{handbackId}", "-");
        return key.Length <= 120 ? key : key[..120];
    }

    /// <summary>…\subagent_in\&lt;실행&gt; → 그 앞 폴더. subagent_in이 없으면 cwd 그대로.</summary>
    public static string? ProjectDirOf(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return cwd;
        var m = SubagentInSegment().Match(cwd);
        return m.Success ? cwd[..m.Index] : cwd.TrimEnd('\\', '/');
    }

    private static string AgentIdFromFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("agent-", StringComparison.Ordinal) ? name["agent-".Length..] : name;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"[^A-Za-z0-9_-]")]
    private static partial Regex UnsafeChars();

    [GeneratedRegex(@"[\\/]subagent_in(?:[\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SubagentInSegment();
}

/// <summary>
/// 검토형 서브에이전트 결과 보관 (호출 기록 옆 subagents/). Claude Code가 세션 기록을 지워도(cleanupPeriodDays) 남도록 복사한다.
/// MCP 서버 시작 때와 대시보드가 읽을 때 Sync한다. 보관 파일은 한 번 쓰면 바꾸지 않는다 (여러 프로세스가 같이 써도 결과가 같다).
/// </summary>
public static class SubagentArchive
{
    /// <summary>보관 대상 = 검토형 서브에이전트 (record_verdicts selfReview.agent로 받는 이름과 같다).</summary>
    public static readonly IReadOnlyList<string> ReviewerTypes = ["self-reviewer", "perf-reviewer", "security-reviewer", "direction-reviewer"];

    /// <summary>
    /// 이미 보관한 검토형 기록 (경로 → 크기:수정 시각). 바뀐 파일만 다시 읽는다. 보관 폴더의 하위 폴더에 둔다 —
    /// 이 파일을 바꿔도 보관 폴더 시각이 바뀌지 않아 대시보드가 보관본을 다시 읽지 않는다 (/self-review C6).
    /// </summary>
    public static string StatePath(string archiveFolder) => Path.Combine(archiveFolder, "_state", "scanned.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 사람이 파일을 열어 볼 때 한글이 그대로 보이게
    };

    public sealed record SyncReport(int Scanned, int Added, int NoResult, IReadOnlyList<string> Errors);

    public static string FolderFor(string callLogPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(callLogPath))!, "subagents");

    public static string PathFor(string folder, string key) => Path.Combine(folder, key + ".json");

    /// <summary>
    /// projectsRoot 아래 모든 세션의 검토형 서브에이전트 기록에서 아직 보관하지 않은 결과를 복사한다. 예외를 던지지 않는다 (취소만).
    /// Scanned = 이번에 읽은 검토형 기록 수, NoResult = 아직 결과를 넘기지 않은 기록 (실행 중 · 중단).
    /// 형식이 바뀐 것으로 보이면(meta.json 없음 · agentType 없음 · 끝났는데 결과 없음) 오류로 내고 도장을 남기지 않는다 —
    /// 조용히 건너뛰지 않고, 리더를 고치면 다시 읽힌다 (/self-review C2). 도장은 보관을 마친 검토형 기록에만 남긴다.
    /// </summary>
    public static SyncReport Sync(string projectsRoot, string archiveFolder, CancellationToken ct = default)
    {
        var errors = new List<string>();
        int scanned = 0, added = 0, noResult = 0;
        void Error(string e) { if (errors.Count < 20) errors.Add(e); }

        var statePath = StatePath(archiveFolder);
        var state = ReadState(statePath);
        // 이번에 본 기록만 다음 상태로 넘긴다 — 지워진 세션 기록의 항목이 쌓이지 않게 (지휘자 K3).
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonl in AgentFiles(projectsRoot, Error))
        {
            ct.ThrowIfCancellationRequested();
            string stamp;
            try
            {
                var info = new FileInfo(jsonl);
                if (!info.Exists) continue;
                stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error($"{jsonl}: {ex.Message}"); continue; }
            if (state.TryGetValue(jsonl, out var seen) && seen == stamp) { next[jsonl] = stamp; continue; }

            var metaPath = jsonl[..^".jsonl".Length] + ".meta.json";
            if (!File.Exists(metaPath)) { Error($"{jsonl}: meta.json이 없습니다 (형식이 바뀌었을 수 있음)"); continue; }
            var meta = SubagentTranscriptReader.ReadMeta(metaPath);
            if (meta is null) { Error($"{metaPath}: meta.json을 읽지 못했습니다"); continue; }
            if (meta.AgentType is null) { Error($"{metaPath}: agentType이 없습니다 (형식이 바뀌었을 수 있음)"); continue; }
            // 검토형이 아닌 기록은 도장을 남기지 않는다: 나중에 검토형 이름이 늘어도 다시 본다 (meta.json만 읽으므로 싸다).
            if (!ReviewerTypes.Contains(meta.AgentType)) continue;

            scanned++;
            var (results, error) = SubagentTranscriptReader.Read(jsonl, meta);
            if (error is not null) { Error($"{jsonl}: {error}"); continue; }
            if (results.Count == 0)
            {
                if (meta.Finished == true) Error($"{jsonl}: 끝난 기록인데 넘긴 결과({SubagentTranscriptReader.HandbackToolName})가 없습니다 (형식이 바뀌었을 수 있음)");
                else noResult++;
                continue;
            }

            var ok = true;
            foreach (var r in results)
            {
                switch (TryStore(archiveFolder, r))
                {
                    case null: break;
                    case "": added++; break;
                    case var e: Error($"{r.Key}: {e}"); ok = false; break;
                }
            }
            if (ok) next[jsonl] = stamp;
        }

        if (next.Count != state.Count || next.Any(kv => !state.TryGetValue(kv.Key, out var v) || v != kv.Value))
            WriteState(statePath, next);
        return new SyncReport(scanned, added, noResult, errors);
    }

    /// <summary>보관된 결과 전체 (최근 순). 읽지 못한 파일 수를 함께 준다.</summary>
    public static (List<SubagentResult> Results, int Unreadable) ReadAll(string archiveFolder)
    {
        var list = new List<SubagentResult>();
        var bad = 0;
        if (!Directory.Exists(archiveFolder)) return (list, 0);
        foreach (var path in Directory.EnumerateFiles(archiveFolder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<SubagentResult>(File.ReadAllBytes(path), Json) is { } r) list.Add(r);
                else bad++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                bad++;
            }
        }
        return (list.OrderByDescending(r => r.ReturnedAt).ToList(), bad);
    }

    /// <summary>새로 썼으면 "", 이미 있으면 null, 실패면 오류 문구. 임시 파일에 쓴 뒤 덮어쓰지 않는 이동 (BlobStore와 같은 방식).</summary>
    private static string? TryStore(string folder, SubagentResult result)
    {
        string? tmp = null;
        try
        {
            var path = PathFor(folder, result.Key);
            if (File.Exists(path)) return null;
            Directory.CreateDirectory(folder);
            tmp = Path.Combine(folder, $"{result.Key}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(result, Json));
            try
            {
                File.Move(tmp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return null; // 다른 프로세스가 먼저 보관했다
            }
            return "";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (tmp is not null)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { /* 임시 파일 정리 실패는 무시 */ }
            }
        }
    }

    /// <summary>&lt;root&gt;/&lt;프로젝트&gt;/&lt;세션&gt;/subagents/agent-*.jsonl. 폴더 하나를 못 읽어도 나머지는 계속.</summary>
    private static IEnumerable<string> AgentFiles(string root, Action<string> error)
    {
        IEnumerable<string> Safe(Func<IEnumerable<string>> list, string what)
        {
            try { return list().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error($"{what}: {ex.Message}"); return []; }
        }

        if (!Directory.Exists(root)) yield break;
        foreach (var project in Safe(() => Directory.EnumerateDirectories(root), root))
        foreach (var session in Safe(() => Directory.EnumerateDirectories(project), project))
        {
            var folder = Path.Combine(session, "subagents");
            if (!Directory.Exists(folder)) continue;
            foreach (var jsonl in Safe(() => Directory.EnumerateFiles(folder, "agent-*.jsonl"), folder))
                yield return jsonl;
        }
    }

    private static Dictionary<string, string> ReadState(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path), Json) is { } d
                    ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase); // 다시 모두 읽는다 — 보관 파일은 있으면 건너뛰므로 결과는 같다
        }
    }

    /// <summary>
    /// 실패는 알리지 않는다: 여러 프로세스(MCP 서버 여러 개 · 대시보드)가 동시에 바꾸면 Windows에서 이동이 거절될 수 있는데,
    /// 잃는 것은 도장뿐이라 다음 Sync가 다시 읽고 보관 결과는 같다 (/self-review C4 — 오류 소음만 생긴다).
    /// </summary>
    private static void WriteState(string path, Dictionary<string, string> state)
    {
        var tmp = $"{path}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { /* 무시 */ }
        }
    }
}
