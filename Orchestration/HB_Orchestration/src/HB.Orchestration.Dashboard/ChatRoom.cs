using System.Collections.Concurrent;
using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>채팅방 말풍선의 화자 종류.</summary>
public enum RoomRole
{
    User,
    Conductor,
    /// <summary>외부 에이전트 (GPT · Gemini · DeepSeek 등 run_mode).</summary>
    External,
    /// <summary>API 리뷰 (sub_*, claude-cli).</summary>
    ApiReview,
    /// <summary>검토형 서브에이전트 (Agent 도구 · /self-review 기록).</summary>
    Subagent,
    /// <summary>지휘자가 record_verdicts로 남긴 판정 묶음.</summary>
    Verdict,
    /// <summary>실행 시작 · 백그라운드 알림 · 대화 압축 같은 안내.</summary>
    System,
}

/// <summary>
/// 말풍선 하나. Markdown은 화면이 Markdig로 그린 뒤 [[icon:…]]을 바꾼다. Sticker는 화면이 자동으로 붙인 스티커 (Files 태그).
/// RunId가 있으면 그 실행 스레드에 속한다. Link는 기존 대시보드 화면 (호출 상세 · 서브에이전트 결과 등).
/// </summary>
public sealed record RoomMessage
{
    public required DateTimeOffset? At { get; init; }
    public required RoomRole Role { get; init; }
    public required string Name { get; init; }
    public string? Detail { get; init; }
    public required string Markdown { get; init; }
    public string? Sticker { get; init; }
    public bool IsError { get; init; }
    public int ToolsBefore { get; init; }
    public string? RunId { get; init; }
    public string? Link { get; init; }
    public string? LinkLabel { get; init; }
}

/// <summary>방 하나 = Claude 세션 하나.</summary>
public sealed record ChatRoom
{
    public required ChatTranscript Transcript { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<RoomMessage> Messages { get; init; }

    /// <summary>이 방에 이어진 실행 (시작 순).</summary>
    public required IReadOnlyList<string> RunIds { get; init; }

    /// <summary>RunIds 중 추정 연결로만 이어진 실행 (확정 연결 · record_verdicts runId가 없음). 화면이 "추정"으로 표시한다.</summary>
    public IReadOnlySet<string> EstimatedRunIds { get; init; } = new HashSet<string>();

    public string SessionId => Transcript.FileSessionId;
}

/// <summary>
/// 세션 기록 · 호출 기록 · 서브에이전트 결과 · 판정을 시간순 말풍선으로 엮는다 (계획서 2026-10-27_에이전트-채팅방).
/// 읽기만 하고 파일을 건드리지 않는다. auto는 상황 → 스티커 태그 (설정이 없으면 null).
/// </summary>
public static class ChatRoomBuilder
{
    /// <summary>판정 묶음 스티커 문턱: (채택 + 부분 × 0.5) / (채택 + 부분 + 기각).</summary>
    public const double MostlyAdopted = 2.0 / 3, MostlyRejected = 1.0 / 3;

    public static ChatRoom Build(
        ChatTranscript transcript,
        IReadOnlyCollection<string> runIds,
        IReadOnlyList<CallRecord> calls,
        IReadOnlyList<SubagentView> subagents,
        IReadOnlyList<VerdictRecord> verdicts,
        Func<string, string?> auto)
    {
        var runSet = runIds.ToHashSet(StringComparer.Ordinal);
        var messages = new List<RoomMessage>();

        foreach (var e in transcript.Entries)
        {
            messages.Add(new RoomMessage
            {
                At = e.Timestamp,
                Role = e.Speaker switch { ChatSpeaker.User => RoomRole.User, ChatSpeaker.Conductor => RoomRole.Conductor, _ => RoomRole.System },
                Name = e.Speaker switch { ChatSpeaker.User => "나", ChatSpeaker.Conductor => "지휘자", _ => "안내" },
                Markdown = e.Text,
                ToolsBefore = e.ToolsBefore,
            });
        }

        // 세션을 이어 열면 기록 안 첫 sessionId(앞 세션)와 파일 이름이 다르다 — 둘 중 하나만 맞아도 이 방의 서브에이전트다.
        var roomSubagents = subagents.Where(v => string.Equals(v.Result.SessionId, transcript.FileSessionId, StringComparison.OrdinalIgnoreCase)
            || (transcript.SessionId is { } recorded && string.Equals(v.Result.SessionId, recorded, StringComparison.OrdinalIgnoreCase))).ToList();
        // /self-review 기록(self_review 호출)은 서브에이전트 결과 원문과 같은 글이다 — 서브에이전트 말풍선 하나만 둔다.
        var selfReviewShown = roomSubagents
            .Where(v => v.Link is { Kind: SubagentLinkKind.Exact or SubagentLinkKind.Near, Via: "selfReview", RunId: not null })
            .Select(v => v.Link.RunId!)
            .ToHashSet(StringComparer.Ordinal);

        var runCalls = calls.Where(c => c.RunId is not null && runSet.Contains(c.RunId)).ToList();
        foreach (var run in runCalls.GroupBy(c => c.RunId!))
        {
            var ordered = run.OrderBy(c => c.Timestamp).ToList();
            var first = ordered[0];
            var mode = ModeNames.Normalize(first.Mode ?? first.Tool) ?? first.Tool;
            if (first.Tool != VerdictRecorder.SelfReviewTool)
            {
                messages.Add(new RoomMessage
                {
                    At = first.Timestamp,
                    Role = RoomRole.System,
                    Name = "실행",
                    Detail = $"{mode} · {run.Key}",
                    Markdown = string.IsNullOrWhiteSpace(first.Reason) ? $"`{mode}` 실행 시작" : $"`{mode}` 실행 시작 — {first.Reason}",
                    RunId = run.Key,
                    Link = $"/Call/{first.Id}",
                    LinkLabel = "호출 상세",
                });
            }

            // 단계마다 마지막 시도만 (재시도 · 실패 뒤 성공은 상세 화면에서 본다).
            foreach (var call in ordered.GroupBy(c => c.Step ?? 1).Select(g => g.OrderBy(c => c.Attempt ?? 1).ThenBy(c => c.Timestamp).Last()))
            {
                if (call.Tool == VerdictRecorder.SelfReviewTool && selfReviewShown.Contains(run.Key)) continue;
                messages.Add(FromCall(call, mode, auto));
            }
        }

        foreach (var v in roomSubagents)
        {
            var linkedRun = v.Link.RunId is { } r && runSet.Contains(r) ? r : null;
            messages.Add(new RoomMessage
            {
                At = v.Result.ReturnedAt,
                Role = RoomRole.Subagent,
                Name = v.Result.AgentType,
                Detail = v.Result.Description,
                Markdown = v.Result.Message,
                Sticker = auto(AutoSticker.SubagentResult),
                RunId = linkedRun,
                Link = $"/Subagents?Id={Uri.EscapeDataString(v.Result.Key)}",
                LinkLabel = "서브에이전트 결과",
            });
        }

        // record_verdicts 한 번 = 판정 묶음 하나 (같은 실행 · 같은 기록 시각).
        var firstCallOfRun = runCalls.GroupBy(c => c.RunId!).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Timestamp).First().Id, StringComparer.Ordinal);
        foreach (var batch in verdicts.Where(v => runSet.Contains(v.RunId))
                     .GroupBy(v => (v.RunId, At: new DateTimeOffset(v.Timestamp.UtcTicks - v.Timestamp.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero))))
        {
            messages.Add(FromVerdicts(batch.Key.RunId, batch.ToList(), firstCallOfRun.GetValueOrDefault(batch.Key.RunId), auto));
        }

        // 시각이 없는 말(형식 변경)은 맨 앞으로 몰리지 않게 바로 앞 말의 시각을 이어 쓴다 (안정 정렬).
        DateTimeOffset last = DateTimeOffset.MinValue;
        var keyed = new List<(RoomMessage Message, int Index, DateTimeOffset At)>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].At is { } at) last = at;
            keyed.Add((messages[i], i, last));
        }
        var sorted = keyed.OrderBy(x => x.At).ThenBy(x => x.Index).Select(x => x.Message).ToList();

        var firstUser = transcript.Entries.FirstOrDefault(e => e.Speaker == ChatSpeaker.User)?.Text;
        var title = transcript.Title ?? (firstUser is null ? transcript.FileSessionId : Shorten(firstUser, 60));
        var orderedRuns = runCalls.GroupBy(c => c.RunId!).OrderBy(g => g.Min(c => c.Timestamp)).Select(g => g.Key).ToList();
        return new ChatRoom { Transcript = transcript, Title = title, Messages = sorted, RunIds = orderedRuns };
    }

    private static RoomMessage FromCall(CallRecord c, string mode, Func<string, string?> auto)
    {
        var role = c.Tool == VerdictRecorder.SelfReviewTool ? RoomRole.Subagent
            : string.Equals(c.Provider, "claude-cli", StringComparison.OrdinalIgnoreCase) || mode.StartsWith("sub_", StringComparison.Ordinal) ? RoomRole.ApiReview
            : RoomRole.External;
        var detail = string.Join(" · ", new[] { mode, c.Role, c.Step is { } s ? $"{s}단계" : null, c.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string body;
        string? situation;
        if (!c.Success)
        {
            body = $"**실패** ({c.ErrorKind ?? "알 수 없음"}): {c.Error ?? "오류 문구 없음"}";
            situation = c.ErrorKind switch
            {
                "Quota" => AutoSticker.CallQuota,
                "RateLimit" => AutoSticker.CallRateLimit,
                "Timeout" => AutoSticker.CallTimeout,
                "Canceled" => null,
                _ => AutoSticker.CallFailed,
            };
        }
        else
        {
            body = c.Structured is { Parsed: true } st ? FindingRenderer.Render(st) : c.Response ?? "";
            situation = c.Structured switch
            {
                { Parsed: true, Requests.Count: > 0 } => AutoSticker.CallRequests,
                _ when c.Warnings.Count > 0 => AutoSticker.CallWarning,
                { Parsed: true, Findings.Count: 0 } => AutoSticker.CallNoFindings,
                { Parsed: true } => AutoSticker.CallFindings,
                _ => null,
            };
        }
        if (c.Warnings.Count > 0) body += "\n\n" + string.Join("\n", c.Warnings.Select(w => "> " + w));

        return new RoomMessage
        {
            At = c.Timestamp.AddMilliseconds(Math.Max(0, c.ElapsedMs)),
            Role = role,
            Name = c.Agent ?? c.Model,
            Detail = detail,
            Markdown = body,
            Sticker = situation is null ? null : auto(situation),
            IsError = !c.Success,
            RunId = c.RunId,
            Link = $"/Call/{c.Id}",
            LinkLabel = "호출 상세",
        };
    }

    private static RoomMessage FromVerdicts(string runId, List<VerdictRecord> batch, string? firstCallId, Func<string, string?> auto)
    {
        var sb = new StringBuilder();
        foreach (var v in batch)
        {
            var ids = v.FindingIds is { Count: > 0 } f ? string.Join(", ", f) : v.FindingKey;
            sb.Append("- **").Append(Verdicts.Label(v.Verdict)).Append("** `").Append(ids).Append("` ").Append(v.Summary);
            if (!string.IsNullOrWhiteSpace(v.Reason)) sb.Append(" — ").Append(v.Reason);
            sb.Append('\n');
        }

        var findings = batch.Where(v => !v.IsRequest).ToList();
        var adopted = findings.Count(v => v.Verdict == Verdicts.Adopted) + findings.Count(v => v.Verdict == Verdicts.Partial) * Verdicts.PartialWeight;
        var denominator = findings.Count(v => v.Verdict is Verdicts.Adopted or Verdicts.Partial or Verdicts.Rejected);
        string? situation = denominator == 0 ? null
            : adopted / denominator >= MostlyAdopted ? AutoSticker.VerdictMostlyAdopted
            : adopted / denominator <= MostlyRejected ? AutoSticker.VerdictMostlyRejected
            : AutoSticker.VerdictMixed;

        return new RoomMessage
        {
            At = batch.Max(v => v.Timestamp),
            Role = RoomRole.Verdict,
            Name = "지휘자 판정",
            Detail = $"{batch.Count}건",
            Markdown = sb.ToString(),
            Sticker = situation is null ? null : auto(situation),
            RunId = runId,
            Link = firstCallId is null ? "/Verdicts" : $"/Call/{firstCallId}",
            LinkLabel = firstCallId is null ? "판정" : "호출 상세 · 판정",
        };
    }

    private static string Shorten(string text, int max)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length <= max ? line : line[..max] + "…";
    }
}

/// <summary>실행이 이어진 방: 세션 파일 ID와, 추정 연결로만 이어졌는지.</summary>
public sealed record RunRoom(string SessionId, bool IsEstimated);

/// <summary>
/// 채팅방 목록 · 방 하나 · 실행 → 방 찾기. 실행 → 방은 세션 요약(run_mode 연결 · record_verdicts runId)만 보고 본문은 읽지 않는다.
/// 본문은 방을 그릴 때만 읽고, 최근 쓴 것부터 Dashboard:ClaudeSessionLimit + 20개만 들고 있는다 (계획서 2026-10-27_채팅방-실행연결-색인).
/// </summary>
public sealed class ChatRoomService(
    CallLogCache log,
    VerdictLogCache verdicts,
    ClaudeSessionsService sessions,
    SubagentResultsService subagents,
    StickerCatalog stickers,
    Microsoft.Extensions.Options.IOptions<DashboardOptions> options)
{
    /// <summary>방 목록(최근 세션)을 매번 다 읽어도 캐시가 갈리지 않게 목록보다 이만큼 크게.</summary>
    public const int CacheSpare = 20;

    private readonly ChatTranscriptCache _cache = new(() => Math.Max(1, options.Value.ClaudeSessionLimit) + CacheSpare);

    public sealed record RoomSummary(TranscriptSummary Session, ChatTranscript Chat, int RunCount, string Title);

    public StickerCatalog Stickers => stickers;

    /// <summary>최근 세션 (Dashboard:ClaudeSessionLimit개)의 방 목록.</summary>
    public IReadOnlyList<RoomSummary> List()
    {
        var calls = log.All;
        var snap = sessions.Load(calls);
        return snap.Sessions.Select(s =>
        {
            var chat = _cache.Get(s);
            return new RoomSummary(s, chat, RunIdsFor(s, snap.Links).Count, TitleOf(chat));
        }).ToList();
    }

    /// <summary>세션 ID로 방 하나. 없으면 null.</summary>
    public ChatRoom? Room(string sessionId)
    {
        var calls = log.All;
        var snap = sessions.Load(calls);
        var session = snap.AllSessions.FirstOrDefault(s => string.Equals(s.FileSessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (session is null) return null;

        var runIds = RunIdsFor(session, snap.Links);
        return ChatRoomBuilder.Build(_cache.Get(session), runIds, calls, subagents.Load(calls).Views, verdicts.Latest, stickers.ForSituation)
            with { EstimatedRunIds = EstimatedRunIds(session, snap.Links) };
    }

    /// <summary>실행이 이어진 세션 ID. 어느 세션에도 없으면 null.</summary>
    public string? SessionOfRun(string runId) => RunRooms().GetValueOrDefault(runId)?.SessionId;

    /// <summary>runId → 이어진 방 (판정 화면 · 호출 상세의 방 링크). 세션 요약만 읽는다.</summary>
    public IReadOnlyDictionary<string, RunRoom> RunRooms() => RunRooms(sessions.Load(log.All));

    public static IReadOnlyDictionary<string, RunRoom> RunRooms(ClaudeSessionsService.Snapshot snap) => RunRooms(snap.AllSessions, snap.Links);

    /// <summary>
    /// 순위: run_mode 확정 연결(실행한 세션) > record_verdicts runId(판정만 남긴 세션 — 소급 판정은 다른 세션일 수 있다) > run_mode 추정 연결.
    /// 같은 순위가 여러 세션에 있으면(이어 연 세션은 앞 대화가 새 파일로 복사된다) 먼저 오는 세션 — 최근 수정된 것.
    /// 호출 상세 · 판정 화면 · /Room/Run이 모두 이 규칙을 쓴다 (/self-review 20b90a0a65 C1).
    /// </summary>
    public static IReadOnlyDictionary<string, RunRoom> RunRooms(
        IReadOnlyList<TranscriptSummary> sessions, IReadOnlyDictionary<TranscriptSummary, List<RunLink>> links)
    {
        const int Confirmed = 0, Verdict = 1, Estimated = 2;
        var best = new Dictionary<string, (string Session, int Rank)>(StringComparer.Ordinal);
        void Offer(string runId, string session, int rank)
        {
            if (!best.TryGetValue(runId, out var had) || rank < had.Rank) best[runId] = (session, rank);
        }

        foreach (var session in sessions)
        {
            var list = links.TryGetValue(session, out var l) ? l : [];
            foreach (var link in list)
                if (link.RunId is { } runId && link.Kind is RunLinkKind.Confirmed or RunLinkKind.Estimated)
                    Offer(runId, session.FileSessionId, link.Kind == RunLinkKind.Confirmed ? Confirmed : Estimated);
            foreach (var runId in session.VerdictRunIds)
                Offer(runId, session.FileSessionId, Verdict);
        }
        return best.ToDictionary(kv => kv.Key, kv => new RunRoom(kv.Value.Session, kv.Value.Rank == Estimated), StringComparer.Ordinal);
    }

    private static HashSet<string> EstimatedRunIds(TranscriptSummary session, IReadOnlyDictionary<TranscriptSummary, List<RunLink>> links)
    {
        if (!links.TryGetValue(session, out var list)) return [];
        var confirmed = list.Where(l => l.Kind == RunLinkKind.Confirmed && l.RunId is not null).Select(l => l.RunId!).ToHashSet(StringComparer.Ordinal);
        return list.Where(l => l.Kind == RunLinkKind.Estimated && l.RunId is { } id && !confirmed.Contains(id) && !session.VerdictRunIds.Contains(id))
            .Select(l => l.RunId!).ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> RunIdsFor(TranscriptSummary session, IReadOnlyDictionary<TranscriptSummary, List<RunLink>> links)
    {
        var ids = new HashSet<string>(session.VerdictRunIds, StringComparer.Ordinal);
        if (links.TryGetValue(session, out var list))
            foreach (var l in list)
                if (l.RunId is { } id && l.Kind is RunLinkKind.Confirmed or RunLinkKind.Estimated)
                    ids.Add(id);
        return ids.ToList();
    }

    private static string TitleOf(ChatTranscript chat) =>
        chat.Title ?? chat.Entries.FirstOrDefault(e => e.Speaker == ChatSpeaker.User)?.Text.Split('\n')[0] ?? chat.FileSessionId;
}

/// <summary>
/// 세션 본문 캐시. (경로 · 크기 · 수정 시각)이 같으면 다시 읽지 않고, 상한을 넘으면 가장 오래 안 쓴 것부터 뺀다.
/// 상한은 넣을 때마다 limit()으로 묻는다 (대시보드는 IOptions라 켤 때 값 그대로). 읽기에 실패한 본문(FormatError)은 넣지 않는다 —
/// 잠금 같은 일시적 실패가 파일이 바뀔 때까지 빈 방으로 남지 않게 (/self-review 20b90a0a65 C4).
/// </summary>
public sealed class ChatTranscriptCache(Func<int> limit, Func<string, ChatTranscript>? read = null)
{
    private sealed class Entry(long length, DateTime writeUtc, ChatTranscript chat)
    {
        public readonly long Length = length;
        public readonly DateTime WriteUtc = writeUtc;
        public readonly ChatTranscript Chat = chat;
        public long LastUsed;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, ChatTranscript> _read = read ?? (path => ClaudeChatReader.Read(path));
    private long _clock;

    public int Count => _entries.Count;

    public bool Contains(string path) => _entries.ContainsKey(path);

    public ChatTranscript Get(TranscriptSummary session)
    {
        var path = session.FilePath;
        if (_entries.TryGetValue(path, out var hit) && hit.Length == session.FileBytes && hit.WriteUtc == session.LastWriteUtc)
        {
            hit.LastUsed = Interlocked.Increment(ref _clock);
            return hit.Chat;
        }
        var chat = _read(path);
        if (chat.FormatError is not null)
        {
            _entries.TryRemove(path, out _);
            return chat;
        }
        _entries[path] = new Entry(session.FileBytes, session.LastWriteUtc, chat) { LastUsed = Interlocked.Increment(ref _clock) };
        Trim();
        return chat;
    }

    private void Trim()
    {
        var over = _entries.Count - Math.Max(1, limit());
        if (over <= 0) return;
        foreach (var path in _entries.OrderBy(kv => kv.Value.LastUsed).Take(over).Select(kv => kv.Key).ToList())
            _entries.TryRemove(path, out _);
    }
}
