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
/// Tools: 그 도구들의 이름 (호출 차례, HB_Chat 도구 칩).
/// </summary>
public sealed record ChatEntry(int Line, DateTimeOffset? Timestamp, ChatSpeaker Speaker, string Text, int ToolsBefore)
{
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Tools와 같은 차례의 짧은 대상 (<see cref="ToolTargets"/>, 모르면 null — HB_Chat 4차 C3: 끝난 턴 칩에도 대상).</summary>
    public IReadOnlyList<string?> ToolTargets { get; init; } = [];

    /// <summary>서버가 요약한 도구 호출 사이 글 (<see cref="ClaudeThinking"/>) — 지휘자가 쓴 원문이 아니다.</summary>
    public bool Narration { get; init; }

    /// <summary>값 비교: Tools는 목록 내용으로 (기본 레코드 비교는 참조라 이어 읽기 = 전체 읽기 검사가 깨진다).</summary>
    public bool Equals(ChatEntry? other) =>
        other is not null && Line == other.Line && Timestamp == other.Timestamp && Speaker == other.Speaker && Text == other.Text
        && ToolsBefore == other.ToolsBefore && Narration == other.Narration && Tools.SequenceEqual(other.Tools) && ToolTargets.SequenceEqual(other.ToolTargets);

    public override int GetHashCode() => HashCode.Combine(Line, Timestamp, Speaker, Text, ToolsBefore, Tools.Count);
}

/// <summary>
/// 지휘자 객관식 질문(AskUserQuestion)에 대한 사용자 답 (HB_Chat 4차 D1). Line은 답(tool_result)이 든 줄.
/// Items는 질문 차례의 (질문 · 머리 · 고른 답). Declined가 있으면 답하지 않고 끝남(거절 · 시간 초과 — 그 글)이고 Items는 질문만.
/// <see cref="ChatTranscript.Entries"/>와 따로 둔다 — 대시보드 채팅방은 쓰지 않는다 (4차 결정 "HB_Chat만").
/// </summary>
public sealed record ChatAnswer(int Line, DateTimeOffset? Timestamp, IReadOnlyList<ChatAnswerItem> Items, string? Declined = null);

public sealed record ChatAnswerItem(string Question, string? Header, string? Answer);

/// <summary>지휘자가 도구로 파일을 다룬 방식: 파일 도구로 읽음 · 고침, 셸 명령에 이름이 나옴 (읽기인지 쓰기인지 모름).</summary>
public enum FileMentionKind
{
    Read,
    Edit,
    Shell,
}

/// <summary>
/// 지휘자 도구 입력에 나온 파일 하나 (HB_Chat 곁칸 "문서" 탭 — 이 방에서 다룬 문서). Line은 tool_use가 든 줄.
/// Path는 파일 도구의 file_path 그대로(셸이면 null), Name은 파일 이름 — 셸 명령은 경로가 cwd에 기대므로 <c>.md</c> 이름만 남긴다.
/// </summary>
public sealed record FileMention(int Line, DateTimeOffset? Timestamp, FileMentionKind Kind, string? Path, string Name);

/// <summary>
/// 채팅방용 세션 기록. <see cref="TranscriptSummary"/>와 달리 대화 본문을 담는다:
/// 사용자 글 · 지휘자 글(text 블록과 서버가 요약한 중간 글 — <see cref="ChatEntry.Narration"/>)만. 도구 호출 · 결과 · 생각 · system-reminder · isMeta 줄은 넣지 않는다.
/// </summary>
public sealed record ChatTranscript
{
    public required string FilePath { get; init; }
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }

    /// <summary>세션 이름 (custom-title → ai-title 순, 마지막 값).</summary>
    public string? Title { get; init; }

    public IReadOnlyList<ChatEntry> Entries { get; init; } = [];

    /// <summary>객관식 질문에 대한 답 (HB_Chat 4차 D1, 줄 차례). 말 목록과 따로.</summary>
    public IReadOnlyList<ChatAnswer> Answers { get; init; } = [];

    /// <summary>지휘자 도구 입력에 나온 파일 (줄 차례). 말 목록과 따로 — 대시보드 채팅방은 쓰지 않는다.</summary>
    public IReadOnlyList<FileMention> FileMentions { get; init; } = [];

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
    /// 이어 읽기: from이 있으면 그 바이트 위치부터 붙은 줄만 읽어 앞 결과에 더한다.
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
        public readonly List<string> PendingToolNames = [];
        public readonly List<string?> PendingToolTargets = [];
        public readonly List<ChatEntry> Entries = [];
        public readonly List<ChatAnswer> Answers = [];
        public readonly List<FileMention> FileMentions = [];

        /// <summary>답을 기다리는 AskUserQuestion 호출: tool_use ID → 질문들 (답 줄에 toolUseResult가 없을 때 질문을 보이려고).</summary>
        public readonly Dictionary<string, List<ChatAnswerItem>> AskCalls = new(StringComparer.Ordinal);

        /// <summary>목록은 복사한다 — 이어 읽기가 같은 State에 계속 더하므로, 앞서 돌려준 결과가 바뀌지 않게.</summary>
        public ChatTranscript ToTranscript(string path) => new()
        {
            FilePath = path,
            SessionId = SessionId,
            Cwd = Cwd,
            Title = CustomTitle ?? AiTitle,
            Entries = Entries.ToList(),
            Answers = Answers.ToList(),
            FileMentions = FileMentions.ToList(),
            UnknownLines = Unknown,
        };

        public void Add(int line, DateTimeOffset? ts, ChatSpeaker speaker, string text, bool narration = false)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            Entries.Add(new ChatEntry(line, ts, speaker, text, PendingTools) { Tools = [.. PendingToolNames], ToolTargets = [.. PendingToolTargets], Narration = narration });
            PendingTools = 0;
            PendingToolNames.Clear();
            PendingToolTargets.Clear();
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
                case "thinking" when Str(block, "thinking") is { Length: > 0 } said && ClaudeThinking.IsNarration(Str(block, "signature")):
                    s.Add(lineNo, ts, ChatSpeaker.Conductor, said, narration: true);
                    break;
                case "tool_use":
                    s.PendingTools++;
                    s.PendingToolNames.Add(Str(block, "name") ?? "?");
                    s.PendingToolTargets.Add(Str(block, "name") is { } toolName && block.TryGetProperty("input", out var toolInput)
                        ? ToolTargets.Of(toolName, toolInput) : null);
                    if (Str(block, "name") == AskTool && Str(block, "id") is { } askId && block.TryGetProperty("input", out var askInput))
                        s.AskCalls[askId] = QuestionItems(askInput, null);
                    if (Str(block, "name") is { } usedName && block.TryGetProperty("input", out var usedInput))
                        AddFileMentions(usedName, usedInput, lineNo, ts, s.FileMentions);
                    break;
            }
        }
    }

    /// <summary>
    /// 파일 도구(Read · Edit · Write · MultiEdit)의 file_path, 셸(Bash · PowerShell) 명령 속 <c>.md</c> 낱말의 파일 이름.
    /// mcp 도구는 보지 않는다. 셸 명령 본문은 남기지 않는다 (heredoc 본문이 클 수 있다).
    /// </summary>
    private static void AddFileMentions(string tool, JsonElement input, int lineNo, DateTimeOffset? ts, List<FileMention> into)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        switch (tool)
        {
            case "Read" or "Edit" or "Write" or "MultiEdit" when Str(input, "file_path") is { Length: > 0 } path:
                into.Add(new FileMention(lineNo, ts, tool == "Read" ? FileMentionKind.Read : FileMentionKind.Edit, path, FileNameOf(path)));
                break;
            case "Bash" or "PowerShell" when Str(input, "command") is { Length: > 0 } command:
                foreach (var name in MarkdownWordRegex().Matches(command).Select(m => FileNameOf(m.Value)).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (name.Length > 3) into.Add(new FileMention(lineNo, ts, FileMentionKind.Shell, null, name));
                break;
        }
    }

    private static string FileNameOf(string path)
    {
        var cut = path.Replace('\\', '/').TrimEnd('/');
        return cut[(cut.LastIndexOf('/') + 1)..];
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
                case "tool_result" when Str(block, "tool_use_id") is { } id && s.AskCalls.Remove(id, out var asked):
                    s.Answers.Add(ReadAnswer(root, block, lineNo, ts, asked));
                    break;
            }
        }
        if (text.Length > 0) ReadUserString(text.ToString(), lineNo, ts, s);
    }

    private const string AskTool = "AskUserQuestion";

    /// <summary>
    /// 객관식 질문의 답 줄 (HB_Chat 3차 0번 실측 A: 답한 결과는 줄의 toolUseResult = { questions, answers: { 질문 글: 고른 답 } }).
    /// 오류 결과(거절 · 시간 초과 — 앱이 deny로 답함)면 답하지 않음 — 그 결과 글(앞 200자)을 Declined로.
    /// 오류가 아닌데 answers를 읽지 못하면 질문만 (답은 null).
    /// </summary>
    private static ChatAnswer ReadAnswer(JsonElement root, JsonElement block, int lineNo, DateTimeOffset? ts, List<ChatAnswerItem> asked)
    {
        if (!Bool(block, "is_error"))
        {
            if (!root.TryGetProperty("toolUseResult", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
                return new ChatAnswer(lineNo, ts, asked);
            // 결과에 questions가 없거나 비었으면 tool_use 입력의 질문 글로 답을 찾는다 (/self-review e8091295fe C5)
            return new ChatAnswer(lineNo, ts, QuestionItems(result, answers) is { Count: > 0 } items ? items
                : [.. asked.Select(i => i with { Answer = answers.TryGetProperty(i.Question, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null })]);
        }
        var note = ResultText(block).Trim();
        return new ChatAnswer(lineNo, ts, asked, note.Length == 0 ? "답 없음" : Cut(note, 200));
    }

    /// <summary>앞 max자 + "…" — 서로게이트 쌍을 가르지 않게 (/self-review e8091295fe C7).</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end] + "…";
    }

    /// <summary>입력(또는 결과)의 questions[] → 질문 · 머리 (answers가 있으면 그 답).</summary>
    private static List<ChatAnswerItem> QuestionItems(JsonElement obj, JsonElement? answers)
    {
        var items = new List<ChatAnswerItem>();
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array) return items;
        foreach (var q in qs.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object || Str(q, "question") is not { } question) continue;
            var answer = answers is { } a && a.TryGetProperty(question, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            items.Add(new ChatAnswerItem(question, Str(q, "header"), answer));
        }
        return items;
    }

    /// <summary>tool_result의 글 (문자열이거나 text 블록 배열).</summary>
    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", content.EnumerateArray()
            .Where(b => b.ValueKind == JsonValueKind.Object && Str(b, "type") == "text").Select(b => Str(b, "text") ?? ""));
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

    /// <summary>셸 명령 속 <c>.md</c>로 끝나는 낱말 — 공백 · 따옴표 · 셸 기호에서 끊는다 (<c>.mdx</c>는 아님).</summary>
    [GeneratedRegex(@"[^\s""'`<>|;&()=*?,]+\.md(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownWordRegex();
}
