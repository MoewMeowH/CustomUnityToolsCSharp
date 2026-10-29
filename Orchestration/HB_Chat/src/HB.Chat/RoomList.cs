using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>
/// 방 목록 한 줄 (계획서 2-2 a): 제목 · 마지막 말 미리보기 · 마지막 활동 시각 · 안 읽은 지휘자 말 수 · 실행 상태.
/// Failed는 쉬는 중이고 마지막 실행이 실패한 방 (실행 중이면 실행 상태가 먼저).
/// </summary>
public sealed record RoomSummary(
    string Id, string Title, string? Preview, DateTimeOffset LastAt, int Unread, RoomActivity Activity, bool Failed,
    PermissionLevel Level, string Model)
{
    /// <summary>목록 아바타의 지휘자 표정 (끝난 턴의 잠깐 표정은 빼고 — <see cref="RoomHub.FaceOf"/>).</summary>
    public Face Face { get; init; } = Face.Idle;

    /// <summary>실행 중인 방의 진행 단계 ("2/5", 3차 b). 없으면 null.</summary>
    public string? Steps { get; init; }

    /// <summary>마지막 턴의 지휘자 상태 한 줄 (3차 b).</summary>
    public RoomStatus? Status { get; init; }

    /// <summary>작업 폴더 이름 (목록 폴더별 묶음, 3차 d).</summary>
    public string Workspace { get; init; } = "";

    /// <summary>이 방에 고른 스티커 팩 (4차 D2, 없으면 전체 설정) — 목록 표정 그림.</summary>
    public string? StickerPack { get; init; }

    /// <summary>
    /// 단톡방의 구성원 이름 (알림 · 미니 창, 계획서 2026-10-29_단톡방-차례-알림): 승인 · 질문을 기다리는 구성원, 실행 중이면 도는 구성원,
    /// 쉬면 마지막 사용자 말에 답한 구성원들 (", "로). 보통 방 · 없으면 null.
    /// </summary>
    public string? Member { get; init; }
}

/// <summary>말풍선 한 쪽: 보일 말들과 그 앞에 더 있는지 (계획서 2-2 b, 50개씩 위로 불러오기).</summary>
public sealed record MessagePage(IReadOnlyList<ChatEntry> Items, bool HasMore)
{
    /// <summary>새 말 받기(after)인데 너무 많아 맨 끝 쪽으로 바꿨다 — 화면은 붙이지 않고 쪽 전체를 바꾼다 (/self-review 95e8b1c242 C3).</summary>
    public bool Reset { get; init; }
}

/// <summary>검색 결과 하나 (계획서 2-2 c). Line이 없으면 방 제목에서 찾은 것.</summary>
public sealed record SearchHit(string RoomId, string Title, int? Line, ChatSpeaker? Speaker, DateTimeOffset? At, string Snippet)
{
    /// <summary>말한 사람 이름 (단톡방 구성원 답 — 2026-10-29_단톡방-보류-성능-검색). 없으면 Speaker로 나 · 지휘자 이름.</summary>
    public string? Name { get; init; }
}

/// <summary>왼쪽 방 목록 칸의 모델: 목록과 지금 연 방.</summary>
public sealed record SidebarView(IReadOnlyList<RoomSummary> Rooms, string? CurrentId)
{
    /// <summary>휴지통에 있는 방 수 (목록 아래 "휴지통 N").</summary>
    public int TrashCount { get; init; }
}

/// <summary>
/// 방 목록 만들기. 세션 기록은 <see cref="ChatTranscripts"/>의 이어 읽기 캐시로 읽는다 (붙은 줄만).
/// 휴지통에 있는 방은 넣지 않는다.
/// </summary>
public sealed partial class RoomList(RoomStore store, RoomHub hub, ChatTranscripts transcripts, GroupLog? groups = null)
{
    /// <summary>미리보기 글자 수 상한.</summary>
    public const int PreviewChars = 80;

    /// <summary>최근 활동 순. 단톡방 구성원의 숨은 자식 방은 빼고 단톡방 한 줄로 (계획서 2026-10-29_HB_Chat-단톡방).</summary>
    public IReadOnlyList<RoomSummary> All() =>
        [.. store.All().Where(r => r.DeletedAt is null && r.GroupId is null).Select(Summarize).OrderByDescending(s => s.LastAt)];

    public RoomSummary Summarize(Room room)
    {
        if (room.IsGroup) return SummarizeGroup(room);
        return Build(room, transcripts.Read(room.SessionId), hub.ActivityOf(room.Id)) with
        {
            Face = hub.FaceOf(room, transient: false),
            Steps = hub.StepProgress(room.Id),
            Status = room.Status,
            Workspace = room.Workspace,
            StickerPack = room.StickerPack,
        };
    }

    /// <summary>
    /// 단톡방 줄의 구성원 이름 · 표정 (알림 · 미니 창, 결정 "늘 보이기" · "이번 말에 답한 모두"): 그 상태인 구성원 — 쉬면 마지막 사용자 말 뒤에 답한 구성원.
    /// 표정은 기다리는 카드가 모두 질문이면 Question(데스크톱이 "질문"으로 보인다), 그 밖의 기다림은 Approval, 아니면 Idle.
    /// </summary>
    private (string? Member, Face Face) GroupWho(Room group, IReadOnlyList<GroupEntry> log, RoomActivity activity)
    {
        if (activity != RoomActivity.Idle)
        {
            var hits = group.Members.Where(m => m.RoomId.Length > 0 && hub.ActivityOf(m.RoomId) == activity).ToList();
            var face = Face.Idle;
            // 따로 읽는 사이 카드가 끝나 hits가 비면 빈 All이 참이 된다 — 그때는 Approval (/self-review 175acbd35a C5)
            if (activity == RoomActivity.Approval)
                face = hits.Count > 0 && hits.All(m => store.Get(m.RoomId) is { } child && hub.FaceOf(child, transient: false) == Face.Question)
                    ? Face.Question : Face.Approval;
            return (hits.Count == 0 ? null : string.Join(", ", hits.Select(m => m.Name)), face);
        }
        // 마지막 사용자 말은 뒤에서 찾고(목록 · 폴링마다 불린다 — C6), 실패 · 중지 표시 줄은 답으로 세지 않는다 (C2)
        var from = 0;
        for (var i = log.Count - 1; i >= 0; i--)
            if (log[i].Kind == GroupEntryKind.User) { from = i + 1; break; }
        var answered = log.Skip(from)
            .Where(e => e is { Kind: GroupEntryKind.Member, Failed: false, MemberId: not null }).Select(e => e.MemberId!).Distinct()
            .Select(id => group.Members.FirstOrDefault(m => m.Id == id)?.Name).OfType<string>().ToList();
        return (answered.Count == 0 ? null : string.Join(", ", answered), Face.Idle);
    }

    /// <summary>단톡방 한 줄: 구성원 중 하나라도 실행 · 답 기다림이면 그 상태, 미리보기 · 시각은 대화 줄 마지막 말.</summary>
    private RoomSummary SummarizeGroup(Room room)
    {
        IReadOnlyList<GroupEntry> log;
        try { log = groups?.Read(room.Id) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log = []; }
        var last = log.LastOrDefault();
        var who = last is null ? null
            : last.Kind == GroupEntryKind.User ? "나"
            : room.Members.FirstOrDefault(m => m.Id == last.MemberId)?.Name;
        var preview = last is null ? "[단톡방] " + string.Join(", ", room.Members.Select(m => m.Name))
            : Preview((who is null ? "" : who + ": ") + last.Text);
        var lastAt = new[] { last?.At, room.CreatedAt }.Max() ?? room.CreatedAt;
        var activity = GroupChat.ActivityOf(room, hub);
        var (member, face) = GroupWho(room, log, activity);
        return new RoomSummary(room.Id, room.Title ?? "단톡방", preview, lastAt, 0, activity, false, room.Level, room.Model)
        {
            Face = face,
            Member = member,
            Workspace = room.Workspace,
            StickerPack = room.StickerPack,
        };
    }

    /// <summary>
    /// 방 목록 묶음 (3차 d — 노션 "채팅방 세션 폴더 별 묶음"): 작업 폴더가 둘 이상이면 폴더별로, 묶음은 가장 최근 방 순 · 안은 받은 순 그대로.
    /// 하나뿐이면 묶지 않는다(null 이름 하나).
    /// </summary>
    public static IReadOnlyList<(string? Workspace, IReadOnlyList<RoomSummary> Rooms)> Groups(IReadOnlyList<RoomSummary> rooms)
    {
        var groups = rooms.GroupBy(r => r.Workspace, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Count <= 1) return [(null, rooms)];
        return [.. groups.Select(g => ((string?)g.Key, (IReadOnlyList<RoomSummary>)[.. g]))]; // 받은 목록이 최근 순이라 첫 방 순서 = 최근 순
    }

    /// <summary>검색 결과 상한 · 검색어 길이 상한.</summary>
    public const int SearchMax = 50, QueryMax = 100;

    /// <summary>휴지통 밖 모든 방에서 찾기 (제목 + 말 본문, 대소문자 무시). 최근 것부터 <see cref="SearchMax"/>개.</summary>
    public IReadOnlyList<SearchHit> Search(string query)
    {
        var all = store.All().Where(r => r.DeletedAt is null && r.GroupId is null).ToList();
        return Search(all.Where(r => !r.IsGroup).Select(r => (r, transcripts.Read(r.SessionId))), query,
            all.Where(r => r.IsGroup).Select(g => (g, GroupEntries(g.Id))));
    }

    private IReadOnlyList<GroupEntry> GroupEntries(string groupId)
    {
        try { return groups?.Read(groupId) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <param name="groupRooms">단톡방과 그 대화 줄 — 사용자 말 · 구성원 답을 찾는다 (안내 줄은 뺌, 결정 2026-10-29_단톡방-보류-성능-검색).</param>
    public static IReadOnlyList<SearchHit> Search(IEnumerable<(Room Room, ChatTranscript? Transcript)> rooms, string query,
        IEnumerable<(Room Group, IReadOnlyList<GroupEntry> Entries)>? groupRooms = null)
    {
        var q = query.Trim();
        if (q.Length == 0 || q.Length > QueryMax) return [];
        var hits = new List<SearchHit>();
        foreach (var (group, entries) in groupRooms ?? [])
        {
            var title = group.Title ?? "단톡방";
            if (group.Title is { } named && named.Contains(q, StringComparison.OrdinalIgnoreCase))
                hits.Add(new SearchHit(group.Id, title, null, null, entries.LastOrDefault()?.At ?? group.CreatedAt, named));
            foreach (var e in entries)
            {
                if (e.Kind == GroupEntryKind.Note || e.Failed) continue; // 안내 줄 · 중지 · 실패 답은 뺌 (1:1 방의 중지된 턴과 같이 — 0c7704170c C8)
                var at = e.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var name = e.Kind == GroupEntryKind.Member ? group.Members.FirstOrDefault(m => m.Id == e.MemberId)?.Name ?? "구성원" : null;
                hits.Add(new SearchHit(group.Id, title, e.Line, e.Kind == GroupEntryKind.User ? ChatSpeaker.User : ChatSpeaker.Conductor, e.At, Snippet(e.Text, at, q.Length))
                {
                    Name = name,
                });
            }
        }
        foreach (var (room, transcript) in rooms)
        {
            var title = TitleOf(room, transcript);
            // 붙인 이름 · 세션 이름만 제목으로 찾는다 — 이름이 없으면 제목이 첫 말이라 같은 말이 두 번 나온다
            if ((room.Title ?? transcript?.Title) is { } named && named.Contains(q, StringComparison.OrdinalIgnoreCase))
                hits.Add(new SearchHit(room.Id, title, null, null, room.LastRunAt ?? room.CreatedAt, named));
            if (transcript?.FormatError is not null) continue;
            foreach (var e in transcript?.Entries ?? [])
            {
                if (IsStoppedTurn(e)) continue;
                var at = e.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) hits.Add(new SearchHit(room.Id, title, e.Line, e.Speaker, e.Timestamp, Snippet(e.Text, at, q.Length)));
            }
            // 질문 카드 답 (4차 D1 보류 K1): 고른 답 · 질문 글도 찾는다. 답 말풍선은 줄 번호가 없어 그 다음 말(말풍선이 놓인 자리)로 데려간다
            foreach (var a in transcript?.Answers ?? [])
            {
                if (a.Declined is not null) continue;
                var text = string.Join("\n", a.Items.Select(i => $"{i.Question} → {i.Answer}"));
                var at = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var next = transcript!.Entries.FirstOrDefault(e => e.Line > a.Line);
                hits.Add(new SearchHit(room.Id, title, next?.Line, ChatSpeaker.User, a.Timestamp, Snippet(text, at, q.Length)));
            }
        }
        return [.. hits.OrderByDescending(h => h.At ?? DateTimeOffset.MinValue).Take(SearchMax)];
    }

    /// <summary>찾은 곳 앞뒤 글 조각 (한 줄로, 앞뒤 잘렸으면 …).</summary>
    public static string Snippet(string text, int at, int length, int around = 40)
    {
        var start = Math.Max(0, at - around);
        var end = Math.Min(text.Length, at + length + around);
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        if (end < text.Length && char.IsLowSurrogate(text[end])) end++;
        // 스티커 태그는 걷는다 — 회사 모드에서도 글자로 보이지 않게 (95e8b1c242 C8)
        var piece = Space().Replace(StickerTag().Replace(text[start..end], " "), " ").Trim();
        return (start > 0 ? "…" : "") + piece + (end < text.Length ? "…" : "");
    }

    /// <summary>
    /// 갈래 첫 턴 뒤의 읽음 위치: 이번 턴 사용자 말(기록의 마지막 사용자 말) 바로 앞 줄 — 이어받은 말은 읽음, 이번 턴부터 안 읽음
    /// (/self-review 95e8b1c242 C1). 사용자 말이 없으면 마지막 줄, 기록을 못 읽으면 null.
    /// </summary>
    public static int? ReadUpToBeforeLastUser(ChatTranscript? transcript)
    {
        if (transcript is not { FormatError: null } t) return null;
        var lastUser = t.Entries.LastOrDefault(e => e.Speaker == ChatSpeaker.User);
        return lastUser is null ? LastLine(t) : lastUser.Line - 1;
    }

    /// <summary>max자에서 자르되 서로게이트 쌍(이모지 등) 가운데를 가르지 않는다 (95e8b1c242 C10).</summary>
    public static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)];
    }

    /// <summary>방 이름 바꾸기 값 검사: 앞뒤 공백 걷고, 비우면 null(첫 말로 돌아감), 200자 넘으면 오류.</summary>
    public static (string? Title, string? Error) CleanTitle(string? title)
    {
        var t = title?.Trim();
        if (string.IsNullOrEmpty(t)) return (null, null);
        if (t.Length > 200) return (null, "제목이 너무 깁니다 (200자까지).");
        if (t.Any(char.IsControl)) return (null, "제목에 줄바꿈 · 제어 문자를 넣을 수 없습니다.");
        return (t, null);
    }

    /// <summary>한 번에 그리는 말 수.</summary>
    public const int PageSize = 50;

    /// <summary>
    /// 말풍선 한 쪽 고르기. after가 있으면 그 줄 뒤의 말 전부 (턴이 끝난 뒤 새 말만 붙이기). 아니면 before 줄 앞(없으면 끝)에서 size개 —
    /// 한 줄(assistant 메시지 하나)의 여러 말이 쪽 경계에서 갈라지지 않게 같은 줄은 함께 넣는다 (다음 쪽은 "그 줄 앞"으로 받으므로).
    /// </summary>
    public static MessagePage Page(IReadOnlyList<ChatEntry> entries, int? before, int? after, int size = PageSize)
    {
        if (after is { } a)
        {
            var fresh = entries.Where(e => e.Line > a).ToList();
            // 갈래 첫 턴처럼 새 말이 한 쪽보다 많으면 다 붙이지 않고 맨 끝 쪽으로 바꾼다 (95e8b1c242 C3)
            if (fresh.Count <= size) return new MessagePage(fresh, false);
            return Page(entries, null, null, size) with { Reset = true };
        }
        var end = before is { } b ? entries.Count(e => e.Line < b) : entries.Count; // 줄 번호는 늘어나기만 한다
        var start = Math.Max(0, end - size);
        while (start > 0 && start < end && entries[start - 1].Line == entries[start].Line) start--;
        return new MessagePage([.. entries.Skip(start).Take(end - start)], start > 0);
    }

    public static RoomSummary Build(Room room, ChatTranscript? transcript, RoomActivity activity)
    {
        var entries = transcript?.FormatError is null ? transcript?.Entries ?? [] : [];
        var last = entries.LastOrDefault();
        var lastAt = new[] { last?.Timestamp, room.LastRunAt, room.CreatedAt }.Max() ?? room.CreatedAt;
        var preview = last is null ? null
            : IsStoppedTurn(last) ? "중지된 턴"
            : last.Speaker == ChatSpeaker.User ? "나: " + Preview(Attachments.WithoutLines(last.Text)) : Preview(last.Text); // 첨부 경로는 빼고 (2-2 d)
        // ReadUpTo null(이 필드 전의 방, 켤 때 옮기므로 남지 않음)은 모두 읽음으로 — 잘못 세면 안 읽음이 쏟아지는 쪽보다 낫다.
        var unread = entries.Count(e => e.Speaker == ChatSpeaker.Conductor && !IsStoppedTurn(e) && e.Line > (room.ReadUpTo ?? int.MaxValue));
        return new RoomSummary(room.Id, TitleOf(room, transcript), preview, lastAt, unread, activity,
            activity == RoomActivity.Idle && room.LastError is not null, room.Level, room.Model);
    }

    /// <summary>도구 이름 짧게: MCP 도구 <c>mcp__서버__도구</c>는 도구 이름만 (칩).</summary>
    public static string ShortTool(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
        var cut = name.IndexOf("__", 5, StringComparison.Ordinal);
        return cut > 0 && cut + 2 < name.Length ? name[(cut + 2)..] : name;
    }

    /// <summary>
    /// 칩으로 묶기: 바로 이어진 같은 도구만 한 칩에 횟수 ("Read ×3"), 차례 그대로 — 진행 중 칩(chat.js addChip)과 같은 규칙이라
    /// 턴이 끝나 기록으로 바뀌어도 모양이 같다 (/self-review 83b453653b C9).
    /// </summary>
    /// <summary>칩을 몇 개까지 보일지 — 넘으면 앞을 "앞 N개"로 접는다 (화면 chat.js MaxChips와 같음).</summary>
    public const int MaxChips = 20;

    /// <summary>
    /// 끝난 턴 칩 (4차 C3 — 실행 중 칩과 같은 글): 이름 + 대상(ChatEntry.ToolTargets), 같은 글이 이어지면 ×N,
    /// <see cref="MaxChips"/>개를 넘으면 맨 앞에 ("앞 N개", 0). 실행 중 칩은 chat.js addChip이 같은 규칙으로 그린다.
    /// </summary>
    public static IReadOnlyList<(string Label, int Count)> TargetChips(IReadOnlyList<string> tools, IReadOnlyList<string?> targets)
    {
        var chips = new List<(string Label, int Count)>();
        for (var i = 0; i < tools.Count; i++)
        {
            var label = ShortTool(tools[i]) + (i < targets.Count && targets[i] is { } target ? " " + target : "");
            if (chips.Count > 0 && chips[^1].Label == label) chips[^1] = (label, chips[^1].Count + 1);
            else chips.Add((label, 1));
        }
        if (chips.Count <= MaxChips) return chips;
        var older = chips.Take(chips.Count - MaxChips).Sum(c => c.Count);
        return [($"앞 {older}개", 0), .. chips.Skip(chips.Count - MaxChips)];
    }

    public static IReadOnlyList<(string Name, int Count)> ToolChips(IEnumerable<string> tools)
    {
        var chips = new List<(string Name, int Count)>();
        foreach (var tool in tools.Select(ShortTool))
        {
            if (chips.Count > 0 && chips[^1].Name == tool) chips[^1] = (tool, chips[^1].Count + 1);
            else chips.Add((tool, 1));
        }
        return chips;
    }

    /// <summary>세션 기록의 마지막 말 줄 번호 (기록이 없거나 읽지 못하면 0).</summary>
    public static int LastLine(ChatTranscript? transcript) =>
        transcript?.FormatError is null ? transcript?.Entries.LastOrDefault()?.Line ?? 0 : 0;

    /// <summary>
    /// 읽음 표시로 받은 줄 번호로 새 ReadUpTo를 정한다: 세션 기록 마지막 줄을 넘지 않게 자르고(지휘자 검토 R3), 앞으로만 간다.
    /// 바뀌지 않으면 null — 저장 · 목록 알림을 건너뛴다 (/self-review 5225b1ffaf C8).
    /// </summary>
    public static int? NextReadUpTo(int? current, int line, int lastLine)
    {
        var clamped = Math.Min(line, lastLine);
        return clamped > (current ?? int.MinValue) ? clamped : null;
    }

    /// <summary>
    /// 이 필드 전에 만든 방(ReadUpTo null)을 마지막 줄까지 읽음으로 옮긴다 (켤 때 한 번, /self-review 5225b1ffaf C4). 옮긴 방 수.
    /// 방 색인이 깨졌으면 아무것도 하지 않는다 (쓰기 거절).
    /// </summary>
    public static int MarkLegacyRead(RoomStore store, ChatTranscripts transcripts)
    {
        if (store.LoadError is not null) return 0;
        var moved = 0;
        // 아직 갈라지지 않은 갈래 방은 건너뛴다 — 첫 턴이 끝날 때 읽음 위치를 정한다 (95e8b1c242 C1)
        foreach (var room in store.All().Where(r => r.ReadUpTo is null && r.ForkOf is null))
        {
            var last = LastLine(transcripts.Read(room.SessionId));
            store.Update(room.Id, r => r.ReadUpTo is null ? r with { ReadUpTo = last } : r);
            moved++;
        }
        return moved;
    }

    /// <summary>방 제목: 사용자가 붙인 이름 → 세션 이름(custom-title · ai-title) → 첫 사용자 말 한 줄 → "새 방".</summary>
    public static string TitleOf(Room room, ChatTranscript? transcript)
    {
        if (room.Title is { } title) return title;
        if (transcript?.Title is { } sessionTitle) return sessionTitle;
        var first = transcript?.Entries.FirstOrDefault(e => e.Speaker == ChatSpeaker.User)?.Text;
        var text = first is null ? null : Attachments.WithoutLines(first); // 첨부만 보낸 첫 말이면 "첨부 n개" (5b26cff7df C7)
        if (string.IsNullOrWhiteSpace(text)) return "새 방";
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    /// <summary>
    /// 중지한 턴 뒤 다음 말을 이으면 CLI가 답 없는 앞 턴에 넣는 자리표시 답 (조사 로그 2026-10-27 "No response requested." 관찰).
    /// 지휘자 말풍선 대신 안내 한 줄로 보이고, 안 읽은 수에 넣지 않는다.
    /// </summary>
    public static bool IsStoppedTurn(ChatEntry e) =>
        e.Speaker == ChatSpeaker.Conductor && e.Text.Trim() == "No response requested.";

    /// <summary>
    /// 미리보기 한 줄: 스티커 태그 · 코드 울타리 · 마크다운 기호 · 링크 주소를 걷고 공백을 하나로, <see cref="PreviewChars"/>자에서 자른다.
    /// 화면은 이 글을 textContent · Razor 인코딩으로만 넣는다.
    /// </summary>
    public static string Preview(string text)
    {
        var s = StickerTag().Replace(text, " ");
        // 위젯 HTML · 차트 JSON은 한 줄로 읽을 글이 아니다 (2-2 f 실제 확인: 목록에 <!DOCTYPE html>이 보였음)
        s = WidgetBlock().Replace(s, m => m.Groups["lang"].Value switch { "chart" => " [차트] ", "slides" => " [슬라이드] ", _ => " [위젯] " });
        s = Fence().Replace(s, " ");
        s = Link().Replace(s, "$1");
        s = LineMark().Replace(s, "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Space().Replace(s, " ").Trim();
        // 서로게이트 쌍(이모지 등) 가운데를 자르지 않는다 (/self-review 5225b1ffaf C9).
        return s.Length <= PreviewChars ? s : Cut(s, PreviewChars) + "…";
    }

    [GeneratedRegex(@"\[\[icon:[^\]\r\n]*\]\]")]
    private static partial Regex StickerTag();

    [GeneratedRegex(@"^\s*(```|~~~)[^\n]*$", RegexOptions.Multiline)]
    private static partial Regex Fence();

    // 화면(Markdig)이 위젯 · 차트 · 슬라이드로 그리는 모양과 맞춘다: ``` · ~~~ 세 개 이상, 언어 이름이 정확히 widget · chart · slides, 닫는 울타리는 같은 글자로 그 길이 이상,
    // 닫히지 않았으면 글 끝까지(중지된 턴), CRLF (803e5f13eb C6)
    [GeneratedRegex(@"^[ \t]*(?<f>(?<c>[`~])\k<c>{2,})[ \t]*(?<lang>widget|chart|slides)(?:[ \t][^\n]*)?\r?$.*?(?:^[ \t]*\k<f>\k<c>*[ \t]*\r?$|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex WidgetBlock();

    [GeneratedRegex(@"!?\[([^\]\n]*)\]\([^)\n]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^\s*(#{1,6}\s+|>\s?|[-*+]\s+|\d+\.\s+)", RegexOptions.Multiline)]
    private static partial Regex LineMark();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
