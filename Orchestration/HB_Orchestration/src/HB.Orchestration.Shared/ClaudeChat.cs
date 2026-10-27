using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Shared;

/// <summary>채팅방 말풍선의 화자 (세션 기록에서 읽는 것만).</summary>
public enum ChatSpeaker
{
    User,
    Conductor,
    /// <summary>백그라운드 작업 알림 · 대화 압축 같은 안내.</summary>
    System,
}

/// <summary>
/// 세션 기록에서 읽은 말 하나.
/// ToolsBefore: 바로 앞 말 이후 지휘자가 쓴 도구 수 (채팅방에 "도구 N개"로 접어 보인다).
/// </summary>
public sealed record ChatEntry(int Line, DateTimeOffset? Timestamp, ChatSpeaker Speaker, string Text, int ToolsBefore);

/// <summary>
/// 채팅방용 세션 기록 (계획서 2026-10-27_에이전트-채팅방). <see cref="TranscriptSummary"/>와 달리 대화 본문을 담는다:
/// 사용자 글 · 지휘자 글(text 블록)만. 도구 호출 · 결과 · thinking · system-reminder · isMeta 줄은 넣지 않는다.
/// </summary>
public sealed record ChatTranscript
{
    public required string FilePath { get; init; }
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }

    /// <summary>세션 이름 (custom-title → ai-title 순, 마지막 값).</summary>
    public string? Title { get; init; }

    public IReadOnlyList<ChatEntry> Entries { get; init; } = [];

    public int UnknownLines { get; init; }
    public string? FormatError { get; init; }

    public string FileSessionId => Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>이어 읽기 위치: 읽은 바이트 수 · 줄 수와 읽던 상태. 밖에서는 Offset만 본다.</summary>
public sealed class ChatResume
{
    public long Offset { get; internal set; }
    internal int LineNo;
    internal readonly ClaudeChatReader.State State = new();
}

/// <summary>
/// 세션 기록에서 채팅방에 쓸 대화 본문만 읽는다. 읽기 전용이고 예외를 던지지 않는다 (형식은 <see cref="ClaudeTranscriptReader"/>와 같은 관찰에 기댄다).
/// </summary>
public static partial class ClaudeChatReader
{
    public static ChatTranscript Read(string path, CancellationToken ct = default)
    {
        var s = new State();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var (lineNo, line) in ClaudeTranscriptReader.Utf8Lines(fs))
                Feed(lineNo, line, s, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ChatTranscript { FilePath = path, FormatError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        return s.ToTranscript(path);
    }

    /// <summary>
    /// 이어 읽기 (계획서 2026-10-27_채팅방-실시간-갱신): from이 있으면 그 바이트 위치부터 붙은 줄만 읽어 앞 결과에 더한다.
    /// '\n'으로 끝난 줄만 읽고 쓰는 중인 마지막 줄은 다음 읽기로 넘긴다. 파일이 from보다 작아졌으면(다시 쓰임) 처음부터.
    /// from은 이 호출이 고쳐 쓴다 — 같은 from을 두 곳에서 동시에 넘기지 않는다 (호출자가 잠근다).
    /// 읽다 실패하면(FormatError · 취소 예외) from에는 일부 줄이 이미 더해졌을 수 있다 — 그 from은 버리고 다음엔 처음부터 읽는다.
    /// </summary>
    public static (ChatTranscript Chat, ChatResume? Resume) ReadMore(string path, ChatResume? from, CancellationToken ct = default)
    {
        var resume = from ?? new ChatResume();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < resume.Offset) resume = new ChatResume();
            fs.Seek(resume.Offset, SeekOrigin.Begin);
            var consumed = new System.Runtime.CompilerServices.StrongBox<long>();
            var lastLine = 0;
            foreach (var (lineNo, line) in ClaudeTranscriptReader.Utf8Lines(fs, consumed))
            {
                lastLine = lineNo;
                Feed(resume.LineNo + lineNo, line, resume.State, ct);
            }
            resume.Offset += consumed.Value;
            resume.LineNo += lastLine;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (new ChatTranscript { FilePath = path, FormatError = $"{ex.GetType().Name}: {ex.Message}" }, null);
        }
        return (resume.State.ToTranscript(path), resume);
    }

    private static void Feed(int lineNo, string? line, State s, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (line is null) { s.Unknown++; return; }
        if (string.IsNullOrWhiteSpace(line)) return;
        ReadLine(line, lineNo, s);
    }

    internal sealed class State
    {
        public string? SessionId, Cwd, CustomTitle, AiTitle;
        public int Unknown, PendingTools;
        public readonly List<ChatEntry> Entries = [];

        /// <summary>목록은 복사한다 — 이어 읽기가 같은 State에 계속 더하므로, 앞서 돌려준 결과가 바뀌지 않게.</summary>
        public ChatTranscript ToTranscript(string path) => new()
        {
            FilePath = path,
            SessionId = SessionId,
            Cwd = Cwd,
            Title = CustomTitle ?? AiTitle,
            Entries = Entries.ToList(),
            UnknownLines = Unknown,
        };

        public void Add(int line, DateTimeOffset? ts, ChatSpeaker speaker, string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            Entries.Add(new ChatEntry(line, ts, speaker, text, PendingTools));
            PendingTools = 0;
        }
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
                s.Cwd ??= Str(root, "cwd");
                // 서브에이전트(사이드체인) · 메타 줄 · 압축 요약은 대화가 아니다.
                if (Bool(root, "isSidechain") || Bool(root, "isMeta")) return;
                var ts = Str(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;

                switch (Str(root, "type"))
                {
                    case "custom-title":
                        s.CustomTitle = Str(root, "customTitle") ?? s.CustomTitle;
                        break;
                    case "ai-title":
                        s.AiTitle = Str(root, "aiTitle") ?? s.AiTitle;
                        break;
                    case "attachment":
                        // 작업 중에 보낸 사용자 글 (2026-10-27 세션 기록 확인: attachment.type = queued_command, commandMode = prompt).
                        if (root.TryGetProperty("attachment", out var att) && att.ValueKind == JsonValueKind.Object
                            && Str(att, "type") == "queued_command" && Str(att, "commandMode") is null or "prompt"
                            && Str(att, "prompt") is { } queued)
                            s.Add(lineNo, ts, ChatSpeaker.User, CleanUserText(queued));
                        break;
                    case "assistant":
                        ReadAssistant(root, lineNo, ts, s);
                        break;
                    case "user":
                        if (Bool(root, "isCompactSummary")) { s.Add(lineNo, ts, ChatSpeaker.System, "대화가 압축됐습니다 (앞 내용 요약)"); break; }
                        ReadUser(root, lineNo, ts, s);
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                s.Unknown++;
            }
        }
    }

    private static void ReadAssistant(JsonElement root, int lineNo, DateTimeOffset? ts, State s)
    {
        foreach (var block in ContentBlocks(root))
        {
            switch (Str(block, "type"))
            {
                case "text" when Str(block, "text") is { } text:
                    s.Add(lineNo, ts, ChatSpeaker.Conductor, text);
                    break;
                case "tool_use":
                    s.PendingTools++;
                    break;
            }
        }
    }

    private static void ReadUser(JsonElement root, int lineNo, DateTimeOffset? ts, State s)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        if (!message.TryGetProperty("content", out var content)) return;

        if (content.ValueKind == JsonValueKind.String)
        {
            ReadUserString(content.GetString()!, lineNo, ts, s);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) { s.Unknown++; return; }

        var text = new StringBuilder();
        foreach (var block in ContentBlocks(root))
        {
            switch (Str(block, "type"))
            {
                case "text" when Str(block, "text") is { } t:
                    text.Append(t).Append('\n');
                    break;
                case "image":
                    text.Append("[이미지]\n");
                    break;
            }
        }
        if (text.Length > 0) ReadUserString(text.ToString(), lineNo, ts, s);
    }

    private static void ReadUserString(string raw, int lineNo, DateTimeOffset? ts, State s)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            var summary = TagText(trimmed, "summary");
            s.Add(lineNo, ts, ChatSpeaker.System, summary is null ? "백그라운드 작업 알림" : $"백그라운드 작업: {summary}");
            return;
        }
        if (trimmed.StartsWith("<command-name>", StringComparison.Ordinal) || trimmed.StartsWith("<command-message>", StringComparison.Ordinal))
        {
            var name = TagText(trimmed, "command-name") ?? "";
            var args = TagText(trimmed, "command-args") ?? "";
            s.Add(lineNo, ts, ChatSpeaker.User, $"`{(name.StartsWith('/') ? name : "/" + name)}` {args}");
            return;
        }
        if (trimmed.StartsWith("<bash-input>", StringComparison.Ordinal))
        {
            s.Add(lineNo, ts, ChatSpeaker.User, $"`! {TagText(trimmed, "bash-input")}`");
            return;
        }
        // 명령 · 셸 출력은 말이 아니다.
        if (trimmed.StartsWith("<local-command-stdout>", StringComparison.Ordinal) || trimmed.StartsWith("<local-command-stderr>", StringComparison.Ordinal)
            || trimmed.StartsWith("<bash-stdout>", StringComparison.Ordinal) || trimmed.StartsWith("<bash-stderr>", StringComparison.Ordinal))
            return;
        // 하네스가 넣는 중단 문구 ("[Request interrupted by user]" · "… for tool use]")는 사용자가 쓴 글이 아니다.
        if (InterruptedRegex().IsMatch(trimmed.TrimEnd()))
        {
            s.Add(lineNo, ts, ChatSpeaker.System, "사용자가 중단함");
            return;
        }

        s.Add(lineNo, ts, ChatSpeaker.User, CleanUserText(raw));
    }

    /// <summary>
    /// system-reminder를 빼고, 붙여 넣은 글은 코드 블록으로 감싼다 (로그 · 코드의 줄과 #·*·- 기호를 원문 그대로 보이게).
    /// 울타리는 본문에 있는 가장 긴 백틱 줄보다 하나 길게.
    /// </summary>
    internal static string CleanUserText(string raw)
    {
        var text = SystemReminderRegex().Replace(raw, "");
        return PastedRegex().Replace(text, m =>
        {
            var body = m.Groups[1].Value.Trim('\r', '\n').Replace("\r\n", "\n");
            var longest = BacktickRunRegex().Matches(body).Select(r => r.Length).DefaultIfEmpty(0).Max();
            var fence = new string('`', Math.Max(3, longest + 1));
            return $"\n\n**붙여 넣은 글**\n\n{fence}text\n{body}\n{fence}\n\n";
        });
    }

    private static string? TagText(string text, string tag) =>
        Regex.Match(text, $"<{Regex.Escape(tag)}>(.*?)</{Regex.Escape(tag)}>", RegexOptions.Singleline) is { Success: true } m
            ? m.Groups[1].Value.Trim()
            : null;

    private static IEnumerable<JsonElement> ContentBlocks(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) yield break;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object) yield return block;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminderRegex();

    [GeneratedRegex(@"<pasted_content[^>]*>(.*?)</pasted_content[^>]*>", RegexOptions.Singleline)]
    private static partial Regex PastedRegex();

    [GeneratedRegex(@"`+")]
    private static partial Regex BacktickRunRegex();

    [GeneratedRegex(@"^\[Request interrupted by user[^\]\n]*\]$")]
    private static partial Regex InterruptedRegex();
}
