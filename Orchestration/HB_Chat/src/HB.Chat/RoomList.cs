using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>
/// 방 목록 한 줄 (계획서 2-2 a): 제목 · 마지막 말 미리보기 · 마지막 활동 시각 · 안 읽은 지휘자 말 수 · 실행 상태.
/// Failed는 쉬는 중이고 마지막 실행이 실패한 방 (실행 중이면 실행 상태가 먼저).
/// </summary>
public sealed record RoomSummary(
    string Id, string Title, string? Preview, DateTimeOffset LastAt, int Unread, RoomActivity Activity, bool Failed,
    PermissionLevel Level, string Model);

/// <summary>왼쪽 방 목록 칸의 모델: 목록과 지금 연 방.</summary>
public sealed record SidebarView(IReadOnlyList<RoomSummary> Rooms, string? CurrentId);

/// <summary>
/// 방 목록 만들기. 세션 기록은 <see cref="ChatTranscripts"/>의 이어 읽기 캐시로 읽는다 (붙은 줄만).
/// 휴지통에 있는 방은 넣지 않는다.
/// </summary>
public sealed partial class RoomList(RoomStore store, RoomHub hub, ChatTranscripts transcripts)
{
    /// <summary>미리보기 글자 수 상한.</summary>
    public const int PreviewChars = 80;

    /// <summary>최근 활동 순.</summary>
    public IReadOnlyList<RoomSummary> All() =>
        [.. store.All().Where(r => r.DeletedAt is null).Select(Summarize).OrderByDescending(s => s.LastAt)];

    public RoomSummary Summarize(Room room) =>
        Build(room, transcripts.Read(room.SessionId), hub.ActivityOf(room.Id));

    public static RoomSummary Build(Room room, ChatTranscript? transcript, RoomActivity activity)
    {
        var entries = transcript?.FormatError is null ? transcript?.Entries ?? [] : [];
        var last = entries.LastOrDefault();
        var lastAt = new[] { last?.Timestamp, room.LastRunAt, room.CreatedAt }.Max() ?? room.CreatedAt;
        var preview = last is null ? null
            : IsStoppedTurn(last) ? "중지된 턴"
            : (last.Speaker == ChatSpeaker.User ? "나: " : "") + Preview(last.Text);
        // ReadUpTo null(이 필드 전의 방, 켤 때 옮기므로 남지 않음)은 모두 읽음으로 — 잘못 세면 안 읽음이 쏟아지는 쪽보다 낫다.
        var unread = entries.Count(e => e.Speaker == ChatSpeaker.Conductor && !IsStoppedTurn(e) && e.Line > (room.ReadUpTo ?? int.MaxValue));
        return new RoomSummary(room.Id, TitleOf(room, transcript), preview, lastAt, unread, activity,
            activity == RoomActivity.Idle && room.LastError is not null, room.Level, room.Model);
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
        foreach (var room in store.All().Where(r => r.ReadUpTo is null))
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
        var text = transcript?.Entries.FirstOrDefault(e => e.Speaker == ChatSpeaker.User)?.Text;
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
        s = Fence().Replace(s, " ");
        s = Link().Replace(s, "$1");
        s = LineMark().Replace(s, "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Space().Replace(s, " ").Trim();
        if (s.Length <= PreviewChars) return s;
        // 서로게이트 쌍(이모지 등) 가운데를 자르지 않는다 (/self-review 5225b1ffaf C9).
        var cut = char.IsHighSurrogate(s[PreviewChars - 1]) ? PreviewChars - 1 : PreviewChars;
        return s[..cut] + "…";
    }

    [GeneratedRegex(@"\[\[icon:[^\]\r\n]*\]\]")]
    private static partial Regex StickerTag();

    [GeneratedRegex(@"^\s*(```|~~~)[^\n]*$", RegexOptions.Multiline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"!?\[([^\]\n]*)\]\([^)\n]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^\s*(#{1,6}\s+|>\s?|[-*+]\s+|\d+\.\s+)", RegexOptions.Multiline)]
    private static partial Regex LineMark();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
