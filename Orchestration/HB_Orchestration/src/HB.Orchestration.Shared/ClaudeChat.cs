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
    /// <summary>서브에이전트(Agent 도구)의 최종 답 — <see cref="ChatEntry.Agent"/> · <see cref="ChatEntry.Title"/>.</summary>
    Subagent,
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

    /// <summary>Subagent: 서브에이전트 종류 (Agent 입력의 subagent_type, 없으면 null).</summary>
    public string? Agent { get; init; }

    /// <summary>Subagent: 지휘자가 붙인 설명 (Agent 입력의 description).</summary>
    public string? Title { get; init; }

    /// <summary>Subagent: 서브에이전트가 자기 백그라운드 작업을 남기고 멈춰 낸 중간 보고 (알림의 &lt;note&gt;) — 같은 작업이 다시 알릴 수 있다.</summary>
    public bool Interim { get; init; }

    /// <summary>Subagent: 그 서브에이전트의 agentId (전경 결과 꼬리 "agentId: …" · 백그라운드 알림 &lt;task-id&gt;, 모르면 null) — 대시보드가 같은 결과를 겹쳐 그리지 않게.</summary>
    public string? AgentId { get; init; }

    /// <summary>값 비교: Tools는 목록 내용으로 (기본 레코드 비교는 참조라 이어 읽기 = 전체 읽기 검사가 깨진다).</summary>
    public bool Equals(ChatEntry? other) =>
        other is not null && Line == other.Line && Timestamp == other.Timestamp && Speaker == other.Speaker && Text == other.Text
        && ToolsBefore == other.ToolsBefore && Narration == other.Narration && Tools.SequenceEqual(other.Tools) && ToolTargets.SequenceEqual(other.ToolTargets)
        && Agent == other.Agent && Title == other.Title && Interim == other.Interim && AgentId == other.AgentId;

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

        /// <summary>
        /// 서브에이전트 호출 (Agent · 옛 이름 Task): tool_use ID → (종류, 설명). 전경은 tool_result에서 지우고,
        /// 백그라운드는 알림이 여러 번 올 수 있어(중간 보고) 남긴다 — 세션 하나의 호출 수만큼만 자란다.
        /// </summary>
        public readonly Dictionary<string, (string? Agent, string? Title)> AgentCalls = new(StringComparer.Ordinal);

        /// <summary>
        /// agentId → (종류, 설명): 백그라운드로 띄운 결과의 "agentId: …" · 답을 SubagentHandback으로 따로 넘겼다는 안내에서.
        /// 답이 따로 오는 &lt;agent-message from="agentId"&gt;를 그 서브에이전트 말풍선으로 만들 때 쓴다.
        /// </summary>
        public readonly Dictionary<string, (string? Agent, string? Title)> AgentIds = new(StringComparer.Ordinal);

        /// <summary>서브에이전트 답 — 지휘자 도구 칩(PendingTools)은 먹지 않고 다음 지휘자 말에 남긴다.</summary>
        public void AddSubagent(int line, DateTimeOffset? ts, (string? Agent, string? Title) who, string text, bool interim, string? agentId)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            Entries.Add(new ChatEntry(line, ts, ChatSpeaker.Subagent, text, 0) { Agent = who.Agent, Title = who.Title, Interim = interim, AgentId = agentId });
        }

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
                if (Bool(root, "isSidechain")) return;
                // 메타 줄은 대화가 아니다 — 단, 백그라운드 서브에이전트가 SubagentHandback으로 넘긴 답("Another Claude session sent a message:
                // <agent-message …>")은 isMeta 사용자 줄로 온다 (2026-11-03 이 앱 세션 기록 확인)
                if (Bool(root, "isMeta"))
                {
                    if (Str(root, "type") == "user" && root.TryGetProperty("message", out var meta) && meta.ValueKind == JsonValueKind.Object
                        && Str(meta, "content") is { } metaText && AgentMessageRegex().Match(metaText) is { Success: true } metaMessage)
                        AddAgentMessage(metaMessage, lineNo, TimeOf(root), s);
                    return;
                }
                var ts = TimeOf(root);

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
                        {
                            // 작업 중에 큐로 들어온 백그라운드 알림 · 서브에이전트 답도 같은 모양으로 온다 — 사용자 글이 아니다
                            if (queued.TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal) || AgentMessageRegex().IsMatch(queued))
                                ReadUserString(queued, lineNo, ts, s);
                            else
                                s.Add(lineNo, ts, ChatSpeaker.User, CleanUserText(queued));
                        }
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
                    if (Str(block, "name") is AgentTool or OldAgentTool && Str(block, "id") is { } agentId)
                        s.AgentCalls[agentId] = block.TryGetProperty("input", out var agentInput) && agentInput.ValueKind == JsonValueKind.Object
                            ? (Str(agentInput, "subagent_type"), Str(agentInput, "description"))
                            : (null, null);
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
                case "tool_result" when Str(block, "tool_use_id") is { } agentId && s.AgentCalls.TryGetValue(agentId, out var who):
                    // 백그라운드로 띄운 결과("Async agent launched …")는 답이 아니다 — 답은 나중 알림의 <result>. 호출은 그때까지 남긴다
                    var report = ResultText(block);
                    if (report.TrimStart().StartsWith(AsyncLaunched, StringComparison.Ordinal))
                    {
                        if (LaunchedIdRegex().Match(report) is { Success: true } launched) s.AgentIds[launched.Groups[1].Value] = who;
                        break;
                    }
                    s.AgentCalls.Remove(agentId);
                    // 답을 SubagentHandback으로 따로 넘김 — 답은 뒤의 <agent-message>로 온다
                    if (DeliveredRegex().Match(report) is { Success: true } delivered)
                    {
                        s.AgentIds[delivered.Groups[1].Value] = who;
                        break;
                    }
                    // 오류 결과(앱 단계에서 거절 · 도는 중 중지 — "[Request interrupted …]" 등)는 서브에이전트가 한 말이 아니다
                    if (Bool(block, "is_error")) break;
                    s.AddSubagent(lineNo, ts, who, HandBack(report), interim: false, AgentIdOf(report));
                    break;
            }
        }
        if (text.Length > 0) ReadUserString(text.ToString(), lineNo, ts, s);
    }

    private const string AskTool = "AskUserQuestion";
    private const string AgentTool = "Agent", OldAgentTool = "Task";
    private const string AsyncLaunched = "Async agent launched";

    /// <summary>
    /// 전경 서브에이전트 결과에서 답만 (CLI 2.1.316 실측 모양): 머리("… The report follows:" 줄까지)와
    /// 꼬리(마지막 "agentId: " 줄부터)를 걷고, 하네스가 줄마다 넣은 들여쓰기 2칸을 뺀다. 머리가 없으면(다른 판) 꼬리만 걷은 원문.
    /// </summary>
    public static string HandBack(string report)
    {
        var text = report.Replace("\r\n", "\n");
        // 머리는 "[Subagent hand-back]" 뒤의 첫 "The report follows:" — 본문이 그 문구를 인용해도 거기서 자르지 않게
        var frame = text.IndexOf(HandBackMark, StringComparison.Ordinal);
        var head = frame < 0 ? -1 : text.IndexOf(ReportFollows, frame, StringComparison.Ordinal);
        var framed = head >= 0;
        if (framed)
        {
            var lineEnd = text.IndexOf('\n', head);
            text = lineEnd < 0 ? "" : text[(lineEnd + 1)..];
        }
        var tail = text.LastIndexOf("\nagentId: ", StringComparison.Ordinal);
        if (tail >= 0) text = text[..tail];
        else if (text.StartsWith("agentId: ", StringComparison.Ordinal)) text = "";
        if (framed) text = string.Join('\n', text.Split('\n').Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l[2..] : l));
        return text;
    }

    private const string ReportFollows = "The report follows:", HandBackMark = "[Subagent hand-back]";

    /// <summary>
    /// 서브에이전트가 SubagentHandback으로 넘긴 답 (&lt;agent-message from="agentId"&gt; … &lt;/agent-message&gt;, 앞에 "Another Claude session sent a message:"가
    /// 붙을 수 있다). 종류 · 설명은 앞서 본 agentId에서 (모르면 없이). 머리 · 들여쓰기는 <see cref="HandBack"/>로 걷는다.
    /// </summary>
    private static void AddAgentMessage(Match m, int lineNo, DateTimeOffset? ts, State s)
    {
        var id = m.Groups[1].Value;
        var who = s.AgentIds.TryGetValue(id, out var known) ? known : (null, null);
        s.AddSubagent(lineNo, ts, who, HandBack(m.Groups[2].Value), interim: false, id);
    }

    private static DateTimeOffset? TimeOf(JsonElement root) =>
        Str(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    /// <summary>
    /// agent-message로 시작하는 글 (앞의 "… sent a message:" 줄 · 공백 허용). 메타 줄은 닫는 태그 뒤에 하네스 안내 문단이 붙는다 — 본문은 첫 닫는 태그까지
    /// (하네스가 본문 줄을 들여 써서 본문 안의 줄머리 태그는 위조할 수 없다).
    /// </summary>
    [GeneratedRegex(@"\A\s*(?:Another Claude session sent a message[^\n]*\n)?\s*<agent-message from=""([0-9A-Za-z_-]{1,80})"">\n?(.*?)\n?</agent-message>", RegexOptions.Singleline)]
    private static partial Regex AgentMessageRegex();

    [GeneratedRegex(@"This agent's report was delivered to you as a message from ""([0-9A-Za-z_-]{1,80})""")]
    private static partial Regex DeliveredRegex();

    [GeneratedRegex(@"(?m)^agentId: ([0-9A-Za-z_-]{1,80})")]
    private static partial Regex LaunchedIdRegex();

    /// <summary>전경 결과 꼬리의 agentId (마지막 "agentId: " 줄). 없으면 null.</summary>
    public static string? AgentIdOf(string report) =>
        AgentIdRegex().Matches(report.Replace("\r\n", "\n")) is { Count: > 0 } m ? m[^1].Groups[1].Value : null;

    [GeneratedRegex(@"(?m)^agentId: ([0-9A-Za-z_-]{1,80})\s*$")]
    private static partial Regex AgentIdRegex();

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
            // 서브에이전트의 끝 알림: <result>가 그 최종 답 (CLI 2.1.316 실측 — 글은 HTML 이스케이프됨)
            if (TagText(trimmed, "tool-use-id") is { } noticeId && s.AgentCalls.TryGetValue(noticeId, out var who)
                && TagText(trimmed, "result") is { Length: > 0 } result)
            {
                // 답을 SubagentHandback으로 따로 넘겼다는 안내 — 답은 <agent-message>가 이미(또는 곧) 말풍선으로 만든다
                if (DeliveredRegex().Match(result) is { Success: true } handedOff)
                {
                    s.AgentIds[handedOff.Groups[1].Value] = who;
                    return;
                }
                s.AddSubagent(lineNo, ts, who, System.Net.WebUtility.HtmlDecode(result), interim: TagText(trimmed, "note") is not null,
                    TagText(trimmed, "task-id")); // 서브에이전트 작업 ID = agentId (subagents/agent-<id>.jsonl, 2.1.316 실측)
                return;
            }
            var summary = TagText(trimmed, "summary");
            s.Add(lineNo, ts, ChatSpeaker.System, summary is null ? "백그라운드 작업 알림" : $"백그라운드 작업: {summary}");
            return;
        }
        if (AgentMessageRegex().Match(raw) is { Success: true } agentMessage)
        {
            AddAgentMessage(agentMessage, lineNo, ts, s);
            return;
        }
        if (trimmed.StartsWith("<command-name>", StringComparison.Ordinal) || trimmed.StartsWith("<command-message>", StringComparison.Ordinal))
        {
            var name = TagText(trimmed, "command-name") ?? "";
            var args = TagText(trimmed, "command-args") ?? "";
            s.Add(lineNo, ts, ChatSpeaker.User, $"`{(name.StartsWith('/') ? name : "/" + name)}` {args}");
            return;
        }
        // 사용자가 직접 돌린 셸 명령 (CLI bash 모드 · HB_Chat '!' 명령): 명령 · 출력 묶음과 뒤따르는 말. 앞에 방 규칙 알림이 붙을 수 있다 —
        // 맨 앞 알림만 걷는다 (출력 속 알림 모양 글을 지우면 지휘자는 받고 사용자 기록에는 안 보인다, HB_Chat /self-review bd240df0ac C2).
        if (ShellBlocks(LeadingReminderRegex().Replace(raw, "")) is { } shell)
        {
            if (shell.Length > 0) s.Add(lineNo, ts, ChatSpeaker.User, shell); // 빈 출력만 있는 CLI 줄은 건너뛴다
            return;
        }
        // 슬래시 명령 출력은 말이 아니다.
        if (trimmed.StartsWith("<local-command-stdout>", StringComparison.Ordinal) || trimmed.StartsWith("<local-command-stderr>", StringComparison.Ordinal))
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

    /// <summary>셸 출력 말풍선에 보일 최대 줄 수 (넘으면 "… N줄 더"). 원문은 세션 기록에 그대로 있다.</summary>
    internal const int ShellShownLines = 40;

    /// <summary>
    /// 맨 앞의 <c>&lt;bash-input&gt;</c> · <c>&lt;bash-stdout&gt;</c> · <c>&lt;bash-stderr&gt;</c> 묶음을 "`! 명령`" + 출력 코드 블록으로, 뒤 글은 사용자 말로.
    /// 묶음으로 시작하지 않으면 null, 보일 것이 없으면(빈 출력 묶음만) 빈 글. 출력이 빈 묶음(CLI가 stderr를 늘 붙임)은 보이지 않는다.
    /// </summary>
    internal static string? ShellBlocks(string text)
    {
        var rest = text;
        var parts = new List<string>();
        var output = new List<string>();
        void FlushOutput()
        {
            if (output.Count == 0) return;
            var lines = string.Join("\n", output).Replace("\r\n", "\n").Split('\n');
            var shown = string.Join("\n", lines.Take(ShellShownLines));
            if (lines.Length > ShellShownLines) shown += $"\n… {lines.Length - ShellShownLines}줄 더";
            var longest = BacktickRunRegex().Matches(shown).Select(r => r.Length).DefaultIfEmpty(0).Max();
            var fence = new string('`', Math.Max(3, longest + 1));
            parts.Add($"{fence}text\n{shown}\n{fence}");
            output.Clear();
        }
        while (ShellTagRegex().Match(rest) is { Success: true } m)
        {
            var body = m.Groups[2].Value.Trim('\r', '\n');
            if (m.Groups[1].Value == "bash-input")
            {
                FlushOutput();
                parts.Add($"`! {body.Trim()}`");
            }
            else if (body.Trim().Length > 0)
                output.Add(body);
            rest = rest[m.Length..];
        }
        if (rest.Length == text.Length) return null;
        FlushOutput();
        var tail = CleanUserText(rest).Trim();
        if (tail.Length > 0) parts.Add(tail);
        return string.Join("\n\n", parts);
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

    [GeneratedRegex(@"^\s*<(bash-input|bash-stdout|bash-stderr)>(.*?)</\1>", RegexOptions.Singleline)]
    private static partial Regex ShellTagRegex();

    [GeneratedRegex(@"^\s*(?:<system-reminder>.*?</system-reminder>\s*)+", RegexOptions.Singleline)]
    private static partial Regex LeadingReminderRegex();

    [GeneratedRegex(@"^\[Request interrupted by user[^\]\n]*\]$")]
    private static partial Regex InterruptedRegex();

    /// <summary>셸 명령 속 <c>.md</c>로 끝나는 낱말 — 공백 · 따옴표 · 셸 기호에서 끊는다 (<c>.mdx</c>는 아님).</summary>
    [GeneratedRegex(@"[^\s""'`<>|;&()=*?,]+\.md(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownWordRegex();
}
