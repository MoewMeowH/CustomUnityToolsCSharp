using System.Text.Json;

namespace HB.Chat.Desktop;

/// <summary>`/api/rooms`의 방 한 줄 (데스크톱이 쓰는 칸만).</summary>
public sealed record RoomState(string Id, string Title, string Activity, bool Failed, int Unread)
{
    public bool IsIdle => Activity.Equals("Idle", StringComparison.OrdinalIgnoreCase);
    public bool IsApproval => Activity.Equals("Approval", StringComparison.OrdinalIgnoreCase);
    public bool IsBusy => Activity.Equals("Busy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>`/api/rooms` 응답 읽기 (계획서 2-3).</summary>
public static class RoomStates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>방 목록. 읽지 못하면 null (알림 · 트레이를 바꾸지 않는다).</summary>
    public static IReadOnlyList<RoomState>? Parse(string json)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<List<RoomState?>>(json, Json);
            // ID가 겹치면 첫 줄만 (UI 쪽 사전 만들기가 예외를 내지 않게 — 0f0b6007a4 C6)
            return rows is null ? null
                : [.. rows.Where(r => r is { Id.Length: > 0, Activity: not null }).Select(r => r! with { Title = r.Title ?? "" }).DistinctBy(r => r.Id)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 도는 턴(Busy · Approval)이 있는 방이 하나라도 있으면 true. 읽지 못하면 false (끄기를 막지 않는다).
    /// 알림용 Parse와 달리 id가 없는 줄도 활동만 본다 — 끌 때 확인은 묻는 쪽이 안전하다.
    /// </summary>
    public static bool AnyRunning(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.EnumerateArray().Any(room =>
                room.ValueKind == JsonValueKind.Object && room.TryGetProperty("activity", out var a) && a.ValueKind == JsonValueKind.String
                && !string.Equals(a.GetString(), "Idle", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public enum NoticeKind
{
    /// <summary>승인 카드가 기다린다.</summary>
    Approval,
    /// <summary>답이 끝났다.</summary>
    Done,
    /// <summary>실행이 실패로 끝났다.</summary>
    Failed,
}

public sealed record Notice(NoticeKind Kind, string RoomId, string Title);

/// <summary>
/// 알림 판정 (계획서 2-3 b): 앞 · 뒤 방 상태를 비교한다 — 승인 기다림이 되면 "승인 요청", 실행(Busy · Approval)이 쉼으로 끝나면 "답 끝" · "실패".
/// 처음 받은 목록(앞 상태 없음)으로는 알리지 않는다 (앱을 켰을 때 쏟아지지 않게). 사용자가 지금 보고 있는 방은 알리지 않는다.
/// </summary>
public static class Notices
{
    public static IReadOnlyList<Notice> Diff(IReadOnlyDictionary<string, RoomState>? before, IReadOnlyList<RoomState> after, string? viewingRoom)
    {
        if (before is null) return [];
        var notices = new List<Notice>();
        foreach (var room in after)
        {
            if (room.Id == viewingRoom || !before.TryGetValue(room.Id, out var was)) continue;
            if (room.IsApproval && !was.IsApproval) notices.Add(new Notice(NoticeKind.Approval, room.Id, room.Title));
            else if (room.IsIdle && !was.IsIdle) notices.Add(new Notice(room.Failed ? NoticeKind.Failed : NoticeKind.Done, room.Id, room.Title));
        }
        return notices;
    }

    /// <summary>풍선 한 개의 제목 · 글. 여럿이면 첫 줄은 개수, 글은 방 제목들 (본문은 싣지 않는다).</summary>
    public static (string Title, string Text) Balloon(IReadOnlyList<Notice> notices)
    {
        static string Label(NoticeKind k) => k switch
        {
            NoticeKind.Approval => "승인 요청",
            NoticeKind.Failed => "실패",
            _ => "답 끝",
        };
        if (notices.Count == 1) return (Label(notices[0].Kind), Cut(notices[0].Title, 200));
        var lines = notices.Take(5).Select(n => $"{Label(n.Kind)} · {Cut(n.Title, 40)}");
        return ($"알림 {notices.Count}개", string.Join("\n", lines) + (notices.Count > 5 ? "\n…" : ""));
    }

    /// <summary>풍선을 눌렀을 때 열 방: 승인 요청이 있으면 그 방, 아니면 첫 알림 (0f0b6007a4 C7).</summary>
    public static string? RoomToOpen(IReadOnlyList<Notice> notices) =>
        (notices.FirstOrDefault(n => n.Kind == NoticeKind.Approval) ?? notices.FirstOrDefault())?.RoomId;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// 아직 안 본 실패 (트레이 빨강): 실패 알림으로 더하고, 그 방을 보거나 · 그 방이 다시 성공(Failed가 아님)하거나 · 방이 없어지면 뺀다
/// (/self-review 0f0b6007a4 C3).
/// </summary>
public sealed class FailureMarks
{
    private readonly HashSet<string> _ids = [];

    public IReadOnlySet<string> Ids => _ids;

    public void Update(IReadOnlyList<Notice> notices, IReadOnlyList<RoomState> rooms, string? viewingRoom)
    {
        foreach (var n in notices)
        {
            if (n.Kind == NoticeKind.Failed) _ids.Add(n.RoomId);
            else if (n.Kind == NoticeKind.Done) _ids.Remove(n.RoomId);
        }
        _ids.IntersectWith(rooms.Where(r => r.Failed).Select(r => r.Id));
        if (viewingRoom is not null) _ids.Remove(viewingRoom);
    }
}

public enum TrayLevel { Idle, Failed, Busy, Approval }

/// <summary>트레이 아이콘 상태 (물을 것 3): 승인 기다림 > 실행 중 > 실패(아직 안 본) > 쉼.</summary>
public static class TrayStatus
{
    public static TrayLevel Level(IReadOnlyList<RoomState> rooms, IReadOnlySet<string> unseenFailures) =>
        rooms.Any(r => r.IsApproval) ? TrayLevel.Approval
        : rooms.Any(r => r.IsBusy) ? TrayLevel.Busy
        : rooms.Any(r => unseenFailures.Contains(r.Id)) ? TrayLevel.Failed
        : TrayLevel.Idle;

    public const string Disconnected = "HB Chat — 채팅 서버 연결 끊김";

    /// <summary>트레이 풍선 도움말 (63자 상한).</summary>
    public static string Tooltip(IReadOnlyList<RoomState> rooms)
    {
        var approval = rooms.Count(r => r.IsApproval);
        var busy = rooms.Count(r => r.IsBusy);
        var parts = new List<string>();
        if (approval > 0) parts.Add($"승인 기다림 {approval}");
        if (busy > 0) parts.Add($"실행 중 {busy}");
        var text = "HB Chat" + (parts.Count > 0 ? " — " + string.Join(" · ", parts) : " — 쉬는 중");
        return text.Length <= 63 ? text : text[..63];
    }
}
