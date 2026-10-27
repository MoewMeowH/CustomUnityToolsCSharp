using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Shared;

/// <summary>세션 시작 때(또는 도중) 지휘자가 불러온 지침 파일 하나. 크기·해시는 기록된 content의 UTF-8 바이트 기준.</summary>
public sealed record TranscriptInstructionFile(string Path, string? Type, long Bytes, string Sha256);

/// <summary>instructions 항목 하나 (세션에 여러 번 있을 수 있다).</summary>
public sealed record TranscriptInstructions(int Line, DateTimeOffset? Timestamp, IReadOnlyList<TranscriptInstructionFile> Files);

/// <summary>
/// 세션에서 부른 run_mode 하나. RunId는 결과 머리말("runId: …")에서 읽은 값 (없으면 null).
/// ResultSource: 결과를 찾은 곳 — result(같은 tool_result) · task-notification(백그라운드) · persisted(미리보기) · null(못 찾음).
/// </summary>
public sealed record TranscriptRunCall(int Line, DateTimeOffset? Timestamp, string ToolUseId, string? Mode, string? RunId, string? ResultSource);

/// <summary>
/// Claude Code 세션 기록 파일(~/.claude/projects/&lt;인코딩&gt;/&lt;sessionId&gt;.jsonl) 하나에서 뽑은 것.
/// 대화 본문은 담지 않는다 — 지침 목록과 run_mode 호출(모드 이름 · runId)만.
/// </summary>
public sealed record TranscriptSummary
{
    public required string FilePath { get; init; }
    public required long FileBytes { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public string? Version { get; init; }
    public string? Cwd { get; init; }
    public IReadOnlyList<TranscriptInstructions> Instructions { get; init; } = [];
    public IReadOnlyList<TranscriptRunCall> RunCalls { get; init; } = [];

    /// <summary>JSON이 아니거나, 아는 항목인데 필드 모양이 다른 줄 수. 건너뛰고 센다.</summary>
    public int UnknownLines { get; init; }

    /// <summary>파일 단위로 형식을 알 수 없을 때(읽기 실패, sessionId·timestamp가 하나도 없음) 그 이유. 정상이면 null.</summary>
    public string? FormatError { get; init; }

    /// <summary>파일 이름(확장자 제외). 세션 ID와 같아야 정상이다.</summary>
    public string FileSessionId => System.IO.Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>
/// Claude Code 세션 기록 읽기 (비공식 형식 — 4장·5장에서 확인한 모양에만 의존한다). 읽기 전용이고 예외를 던지지 않는다.
/// 반드시 UTF-8로 읽는다 (4장: cp949로 읽혀 한글 절 제목을 전부 "없음"으로 오판).
/// </summary>
public static partial class ClaudeTranscriptReader
{
    /// <summary>run_mode 도구 이름 접미사. 서버 이름("hb-orchestration")이 바뀌어도 잡히도록 접미사로 본다.</summary>
    public const string RunModeToolSuffix = "__run_mode";

    /// <summary>
    /// 잘못된 바이트를 대체 문자로 바꾸지 않는다: 바뀐 내용으로 지침 크기·해시를 계산하면 대조가 조용히 틀린다.
    /// 디코딩에 실패한 줄만 알 수 없는 줄로 센다 (쓰는 중인 마지막 줄 하나로 파일 전체를 버리지 않도록 줄 단위).
    /// </summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>세션 기록 파일 이름: &lt;GUID&gt;.jsonl (orphaned 등 다른 이름은 대상이 아니다).</summary>
    public static bool IsSessionFileName(string fileName) => SessionFileRegex().IsMatch(fileName);

    /// <summary>세션 기록 폴더 기본값: CLAUDE_CONFIG_DIR/projects → %USERPROFILE%/.claude/projects (MCP 서버 · 대시보드 공통).</summary>
    public static string DefaultProjectsRoot() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
            ? Path.Combine(config, "projects")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// 프로젝트 경로 → 폴더 이름. 영숫자가 아닌 문자를 모두 '-'로 바꾼다
    /// (확인: F:\AI\ToolCreate → F--AI-ToolCreate, …\HB_Orchestration → …-HB-Orchestration). 되돌릴 수 없는 변환이다.
    /// </summary>
    public static string EncodeProjectDir(string path) =>
        string.Concat(path.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));

    public static TranscriptSummary Read(string path, CancellationToken ct = default)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) { return Failed(path, 0, default, $"{ex.GetType().Name}: {ex.Message}"); }

        var state = new State();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var (lineNo, line) in Utf8Lines(fs))
            {
                ct.ThrowIfCancellationRequested();
                if (line is null) { state.Unknown++; continue; } // UTF-8이 아닌 줄
                if (string.IsNullOrWhiteSpace(line)) continue;
                ReadLine(line, lineNo, state);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Failed(path, SafeLength(info), SafeWrite(info), $"{ex.GetType().Name}: {ex.Message}");
        }

        var summary = new TranscriptSummary
        {
            FilePath = path,
            FileBytes = SafeLength(info),
            LastWriteUtc = SafeWrite(info),
            SessionId = state.SessionId,
            StartedAt = state.StartedAt,
            Version = state.Version,
            Cwd = state.Cwd,
            Instructions = state.Instructions,
            RunCalls = state.Calls.Select(c => c.ToRecord()).ToList(),
            UnknownLines = state.Unknown,
        };

        return state.SessionId is null && state.StartedAt is null
            ? summary with { FormatError = "sessionId · timestamp가 있는 줄이 없습니다" }
            : summary;
    }

    /// <summary>
    /// '\n'으로 나눈 줄을 엄격한 UTF-8로 디코딩한다 (끝의 '\r'은 뗀다). 디코딩에 실패한 줄은 null.
    /// 줄 하나를 통째로 메모리에 올리므로 큰 파일도 줄 크기만큼만 쓴다.
    /// </summary>
    internal static IEnumerable<(int LineNo, string? Text)> Utf8Lines(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        var lineNo = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                if (nl < 0)
                {
                    pending.Write(buffer, start, read - start);
                    break;
                }
                pending.Write(buffer, start, nl - start);
                yield return (++lineNo, Decode(pending));
                pending.SetLength(0);
                start = nl + 1;
            }
        }
        if (pending.Length > 0) yield return (++lineNo, Decode(pending));

        static string? Decode(MemoryStream line)
        {
            var bytes = line.GetBuffer();
            var length = (int)line.Length;
            if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
            try { return Utf8.GetString(bytes, 0, length); }
            catch (DecoderFallbackException) { return null; }
        }
    }

    private static TranscriptSummary Failed(string path, long bytes, DateTime write, string error) =>
        new() { FilePath = path, FileBytes = bytes, LastWriteUtc = write, FormatError = error };

    private static long SafeLength(FileInfo info) { try { return info.Exists ? info.Length : 0; } catch (Exception) { return 0; } }
    private static DateTime SafeWrite(FileInfo info) { try { return info.Exists ? info.LastWriteTimeUtc : default; } catch (Exception) { return default; } }

    private sealed class PendingCall(int line, DateTimeOffset? timestamp, string toolUseId, string? mode)
    {
        public string? RunId;
        public string? Source;
        public TranscriptRunCall ToRecord() => new(line, timestamp, toolUseId, mode, RunId, Source);
    }

    private sealed class State
    {
        public string? SessionId, Version, Cwd;
        public DateTimeOffset? StartedAt;
        public int Unknown;
        public readonly List<TranscriptInstructions> Instructions = [];
        public readonly List<PendingCall> Calls = [];
        public readonly Dictionary<string, PendingCall> ByToolUse = new(StringComparer.Ordinal);
        public readonly Dictionary<string, PendingCall> ByTask = new(StringComparer.Ordinal);
    }

    private static void ReadLine(string line, int lineNo, State s)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { s.Unknown++; return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { s.Unknown++; return; }

            try
            {
                s.SessionId ??= Str(root, "sessionId");
                s.Version ??= Str(root, "version");
                s.Cwd ??= Str(root, "cwd");
                var ts = Str(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
                s.StartedAt ??= ts;

                switch (Str(root, "type"))
                {
                    case "attachment":
                        if (root.TryGetProperty("attachment", out var att) && att.ValueKind == JsonValueKind.Object && Str(att, "type") == "instructions")
                            ReadInstructions(att, lineNo, ts, s);
                        break;
                    case "assistant":
                        if (!HasMessageContent(root)) { s.Unknown++; break; }
                        foreach (var block in ContentBlocks(root))
                            if (Str(block, "type") == "tool_use" && Str(block, "name") is { } name && name.EndsWith(RunModeToolSuffix, StringComparison.Ordinal)
                                && Str(block, "id") is { } id)
                            {
                                var mode = ModeNames.Normalize(block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object ? Str(input, "mode") : null);
                                var call = new PendingCall(lineNo, ts, id, mode);
                                s.Calls.Add(call);
                                s.ByToolUse[id] = call;
                            }
                        break;
                    case "user":
                        if (!HasMessageContent(root)) { s.Unknown++; break; }
                        ReadUser(root, s);
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                // 아는 항목인데 필드 타입이 예상과 다르다 (형식 변경). 줄 하나만 건너뛴다.
                s.Unknown++;
            }
        }
    }

    private static void ReadInstructions(JsonElement att, int lineNo, DateTimeOffset? ts, State s)
    {
        if (!att.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) { s.Unknown++; return; }

        var list = new List<TranscriptInstructionFile>();
        foreach (var f in files.EnumerateArray())
        {
            // 파일 하나가 이상해도 같은 줄의 다른 파일은 남긴다.
            try
            {
                if (f.ValueKind != JsonValueKind.Object || Str(f, "path") is not { } path || Str(f, "content") is not { } content)
                {
                    s.Unknown++;
                    continue;
                }
                var bytes = Utf8.GetBytes(content);
                list.Add(new TranscriptInstructionFile(path, Str(f, "type"), bytes.Length, BlobStore.Sha256Hex(bytes)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or EncoderFallbackException)
            {
                s.Unknown++; // 짝 없는 서로게이트(\uD800 등): 원본 바이트를 알 수 없다
            }
        }
        s.Instructions.Add(new TranscriptInstructions(lineNo, ts, list));
    }

    private static void ReadUser(JsonElement root, State s)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (!message.TryGetProperty("content", out var content)) return;

        // 백그라운드로 넘어간 도구의 결과: user 메시지 문자열의 <task-notification>. 블록마다 자기 task-id와 <result>를 짝짓는다
        // (한 문자열에 알림이 여럿이어도 첫 task-id·첫 <result>를 섞지 않도록). tool_result 안의 알림 문구는 보지 않는다:
        // grep 출력처럼 그 문자열이 도구 결과에 그냥 섞여 들어온다 (2026-10-19 세션 기록 조사).
        if (content.ValueKind == JsonValueKind.String)
        {
            foreach (var notification in NotificationBlocks(content.GetString()!))
            {
                if (NotificationTaskId(notification) is { Success: true } task
                    && s.ByTask.TryGetValue(task.Groups[1].Value, out var bg) && bg.RunId is null)
                {
                    Resolve(bg, notification, "task-notification");
                }
            }
            return;
        }

        foreach (var block in ContentBlocks(root))
        {
            if (Str(block, "type") != "tool_result" || Str(block, "tool_use_id") is not { } useId) continue;
            if (!s.ByToolUse.TryGetValue(useId, out var call)) continue;

            var text = ResultText(block);
            // 백그라운드 안내는 결과 맨 앞의 Claude Code 문구일 때만 (결과 본문에 같은 문구가 인용돼도 오분류하지 않도록).
            // 머리말(runId:) 검사는 지금 정규식(\A 고정)에서는 겹치는 방어다 — 정규식이 나중에 느슨해져도 실행 결과를 백그라운드로 보지 않게 둔다.
            if (!StartsWithRunIdHeader(text) && BackgroundRegex().Match(text) is { Success: true } moved)
            {
                s.ByTask[moved.Groups[1].Value] = call;
                continue;
            }
            Resolve(call, text, text.Contains("<persisted-output>", StringComparison.Ordinal) ? "persisted" : "result");
        }
    }

    /// <summary>실행 전에 거절된 호출의 결과 위치 표시. 실행이 없으므로 추정 연결하지 않는다.</summary>
    public const string RejectedSource = "rejected";

    /// <summary>
    /// 결과 첫 줄만 머리말로 본다 (에이전트 응답 본문에 "runId: …" 줄이 있어도 확정 연결하지 않도록).
    /// task-notification은 &lt;result&gt; 다음, persisted는 미리보기 다음이 결과의 시작이다.
    /// </summary>
    private static void Resolve(PendingCall call, string text, string source)
    {
        var body = source switch
        {
            "task-notification" => After(text, ResultStartRegex()),
            "persisted" => After(text, PreviewStartRegex()),
            _ => text,
        };

        // 실행 전 거절("[reason 필요]" 등)은 머리말 없이 '['로 시작한다. 실행 결과는 항상 "runId:" 또는 "### N."으로 시작한다.
        call.Source = body is not null && body.TrimStart().StartsWith('[') ? RejectedSource : source;

        if (body is null) return;
        var firstLine = body.Split('\n', 2)[0];
        if (RunIdRegex().Match(firstLine) is { Success: true } m) call.RunId = m.Groups[1].Value;
    }

    private static bool StartsWithRunIdHeader(string text) => RunIdRegex().IsMatch(text.Split('\n', 2)[0]);

    /// <summary>
    /// 문자열 안의 최상위 &lt;task-notification&gt; 블록들. 결과 본문(&lt;result&gt; 안)이 알림 태그를 인용해도 블록으로 세지 않는다
    /// (인용된 알림이 아직 연결 안 된 다른 백그라운드 호출에 틀린 runId를 붙이지 않도록 — /cross-review 2e59bbb0db 공통 지적).
    /// 블록 끝 = 닫는 태그 중 그 뒤가 공백뿐이거나 공백 뒤 다음 알림이 시작하는 첫 곳. 그런 곳이 없으면 문자열 끝까지 한 블록.
    /// </summary>
    private static IEnumerable<string> NotificationBlocks(string text)
    {
        const string Open = "<task-notification>", Close = "</task-notification>";
        var start = text.IndexOf(Open, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = text.Length;
            var next = -1;
            for (var close = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal); close >= 0;
                 close = text.IndexOf(Close, close + Close.Length, StringComparison.Ordinal))
            {
                var after = close + Close.Length;
                var rest = after;
                while (rest < text.Length && char.IsWhiteSpace(text[rest])) rest++;
                if (rest == text.Length) { end = after; break; }
                if (string.CompareOrdinal(text, rest, Open, 0, Open.Length) == 0) { end = after; next = rest; break; }
            }
            yield return text[start..end];
            start = next;
        }
    }

    /// <summary>알림 블록의 task-id. &lt;result&gt; 앞(머리 부분)에서만 찾는다 — 결과 본문에 인용된 task-id를 쓰지 않도록.</summary>
    private static Match NotificationTaskId(string block)
    {
        var head = ResultStartRegex().Match(block) is { Success: true } r ? block[..r.Index] : block;
        return TaskIdRegex().Match(head);
    }

    private static string? After(string text, Regex start) =>
        start.Match(text) is { Success: true } m ? text[(m.Index + m.Length)..] : null;

    /// <summary>message.content가 배열 또는 문자열이어야 아는 모양이다.</summary>
    private static bool HasMessageContent(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content)
        && content.ValueKind is JsonValueKind.Array or JsonValueKind.String;

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString()!;
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (part.ValueKind == JsonValueKind.Object && Str(part, "text") is { } t) sb.Append(t);
        return sb.ToString();
    }

    private static IEnumerable<JsonElement> ContentBlocks(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) yield break;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object) yield return block;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.jsonl$")]
    private static partial Regex SessionFileRegex();

    [GeneratedRegex(@"<result>\r?\n?")]
    private static partial Regex ResultStartRegex();

    [GeneratedRegex(@"Preview[^\n]*:\r?\n")]
    private static partial Regex PreviewStartRegex();

    /// <summary>결과 머리말 (ResultFormatter가 첫 줄에 쓴다). 결과 첫 줄 전체가 이 모양일 때만.</summary>
    [GeneratedRegex(@"^runId: ([0-9a-f]{10})\r?$")]
    private static partial Regex RunIdRegex();

    /// <summary>
    /// Claude Code가 도구를 백그라운드로 넘길 때 결과 자리에 쓰는 안내. 맨 앞이 'MCP tool "…run_mode"'이고 그 첫 줄 안에 문구가 있을 때만.
    /// 판마다 문구가 다르다: 'is still running after 120s. It was moved to the background as task X' / 'was moved to the background as task X so that …'.
    /// </summary>
    [GeneratedRegex(@"\A\s*MCP tool ""[^""\r\n]*run_mode""[^\r\n]*?moved to the background as task (\w+)")]
    private static partial Regex BackgroundRegex();

    [GeneratedRegex(@"<task-id>(\w+)</task-id>")]
    private static partial Regex TaskIdRegex();
}

/// <summary>세션 기록의 지침 content와 지금 디스크 파일의 대조 결과.</summary>
public enum InstructionDiskMatch
{
    /// <summary>바이트가 같다.</summary>
    Same,
    /// <summary>디스크 바이트 = content 바이트 + LF(0x0A) 정확히 1개. 세션 기록이 끝 줄바꿈을 빼고 저장하는 것으로 보인다 (파일 3개로 확인한 관찰).</summary>
    TrailingNewlineOnly,
    /// <summary>그 밖의 차이 (이후 변경, CRLF, 줄바꿈 2개 이상 등).</summary>
    Different,
    Missing,
    Unreadable,
}

public static class InstructionDiskCompare
{
    public static InstructionDiskMatch Compare(TranscriptInstructionFile file)
    {
        try
        {
            if (!File.Exists(file.Path)) return InstructionDiskMatch.Missing;
            var disk = File.ReadAllBytes(file.Path);
            if (disk.Length == file.Bytes && BlobStore.Sha256Hex(disk) == file.Sha256) return InstructionDiskMatch.Same;
            if (disk.Length == file.Bytes + 1 && disk[^1] == 0x0A && BlobStore.Sha256Hex(disk[..^1]) == file.Sha256)
                return InstructionDiskMatch.TrailingNewlineOnly;
            return InstructionDiskMatch.Different;
        }
        catch (Exception)
        {
            return InstructionDiskMatch.Unreadable;
        }
    }

    public static string Label(InstructionDiskMatch m) => m switch
    {
        InstructionDiskMatch.Same => "같음",
        InstructionDiskMatch.TrailingNewlineOnly => "끝 줄바꿈만 다름",
        InstructionDiskMatch.Different => "다름",
        InstructionDiskMatch.Missing => "파일 없음",
        _ => "읽기 실패",
    };
}

/// <summary>run_mode 호출 ↔ calls.jsonl 실행(RunId) 연결.</summary>
public enum RunLinkKind
{
    /// <summary>결과 머리말의 runId로 연결.</summary>
    Confirmed,
    /// <summary>머리말이 없어 모드 · 프로젝트 · 시각으로 찾은 후보가 정확히 하나.</summary>
    Estimated,
    /// <summary>후보가 둘 이상이라 연결하지 않음.</summary>
    Ambiguous,
    /// <summary>후보 없음.</summary>
    None,
    /// <summary>
    /// 후보는 하나였지만 같은 실행을 다른 호출(같은 세션이든 다른 세션이든)도 추정해서 연결하지 않음.
    /// RunLink.Candidates = 그 실행을 추정한 호출 수. 어느 쪽인지 고르지 않는다 (틀린 연결보다 연결 없음).
    /// </summary>
    Contested,
}

public sealed record RunLink(TranscriptRunCall Call, RunLinkKind Kind, string? RunId, int Candidates);

public static class RunLinker
{
    /// <summary>추정 연결의 시각 창: tool_use 시각 기준 -2초 ~ +60초 안에 그 실행의 첫 기록이 시작돼야 한다.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan After = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 여러 세션을 한꺼번에 연결한다. 먼저 모든 세션의 확정 연결을 모으고, 그 runId들은 추정 후보에서 뺀다.
    /// </summary>
    public static Dictionary<TranscriptSummary, List<RunLink>> LinkAll(IReadOnlyList<TranscriptSummary> sessions, IReadOnlyList<CallRecord> calls)
    {
        var runs = calls.Where(c => c.RunId is not null)
            .GroupBy(c => c.RunId!)
            .Select(g => (RunId: g.Key, Start: g.Min(c => c.Timestamp), Mode: ModeNames.Normalize(g.First().Mode ?? g.First().Tool), ProjectDir: g.First().ProjectDir))
            .ToList();

        var confirmed = sessions.SelectMany(s => s.RunCalls).Where(c => c.RunId is not null).Select(c => c.RunId!).ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<TranscriptSummary, List<RunLink>>();
        foreach (var s in sessions)
        {
            var links = new List<RunLink>();
            foreach (var call in s.RunCalls)
            {
                if (call.RunId is { } id)
                {
                    links.Add(new RunLink(call, RunLinkKind.Confirmed, id, 1));
                    continue;
                }
                // 실행 전에 거절된 호출은 실행이 없다. 같은 시각 다른 세션의 실행을 붙이지 않도록 추정하지 않는다.
                if (call.Timestamp is not { } at || call.Mode is null || s.Cwd is null || call.ResultSource == ClaudeTranscriptReader.RejectedSource)
                {
                    links.Add(new RunLink(call, RunLinkKind.None, null, 0));
                    continue;
                }

                var candidates = runs.Where(r =>
                        !confirmed.Contains(r.RunId)
                        && string.Equals(r.Mode, call.Mode, StringComparison.OrdinalIgnoreCase)
                        && SamePath(r.ProjectDir, s.Cwd)
                        && InWindow(r.Start, at))
                    .ToList();

                links.Add(candidates.Count switch
                {
                    1 => new RunLink(call, RunLinkKind.Estimated, candidates[0].RunId, 1),
                    0 => new RunLink(call, RunLinkKind.None, null, 0),
                    _ => new RunLink(call, RunLinkKind.Ambiguous, null, candidates.Count),
                });
            }
            result[s] = links;
        }

        // 2단계: 같은 실행을 둘 이상의 호출이 추정했으면 그 호출들을 모두 겹침으로 내린다 (세션을 가리지 않음).
        // 호출은 tool_use id로 센다: 세션을 이어 열면 앞 대화가 새 기록 파일로 복사돼 같은 호출이 두 파일에 나온다
        // (2026-10-19 실측: 기록 47개 중 run_mode id 7개가 두 파일에) — 그건 겹침이 아니다 (/cross-review 1341a362bd self-reviewer C1).
        // 후보 여럿(Ambiguous)인 호출은 실행을 가져간 것으로 치지 않는다 — 기존 규칙 유지 (사용자 결정, C2는 알려진 한계).
        var estimatedBy = result.Values.SelectMany(l => l).Where(l => l.Kind == RunLinkKind.Estimated)
            .GroupBy(l => l.RunId!, StringComparer.Ordinal)
            .Select(g => (RunId: g.Key, Calls: g.Select(l => l.Call.ToolUseId).Distinct(StringComparer.Ordinal).Count()))
            .Where(x => x.Calls > 1)
            .ToDictionary(x => x.RunId, x => x.Calls, StringComparer.Ordinal);
        if (estimatedBy.Count > 0)
        {
            foreach (var links in result.Values)
            {
                for (var i = 0; i < links.Count; i++)
                {
                    if (links[i] is { Kind: RunLinkKind.Estimated, RunId: { } runId } l && estimatedBy.TryGetValue(runId, out var count))
                        links[i] = new RunLink(l.Call, RunLinkKind.Contested, null, count);
                }
            }
        }
        return result;
    }

    /// <summary>차이로 비교한다: at ± 창을 직접 계산하면 0001년 · 9999년 같은 극단 시각에서 범위 초과 예외가 난다.</summary>
    private static bool InWindow(DateTimeOffset start, DateTimeOffset at)
    {
        var delta = start - at; // 두 DateTimeOffset의 차이는 TimeSpan 범위 안이다
        return delta >= -Before && delta <= After;
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
