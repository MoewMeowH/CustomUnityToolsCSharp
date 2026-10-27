namespace HB.Chat;

/// <summary>
/// 휴지통 (계획서 2-2 c). 방은 DeletedAt을 적어 목록 · 검색 · 보내기에서 빼고, 30일이 지나면 방 색인과 그 방 첨부 폴더를 지운다.
/// 세션 기록(~/.claude/projects)은 지우지 않는다 — 사용자 결정 2026-10-28 "추천대로" (대시보드 기록 · 비용 근거가 남는다).
/// </summary>
public static class Trash
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    /// <summary>완전히 지울 때까지 남은 날 (올림, 0 이상).</summary>
    public static int DaysLeft(Room room, DateTimeOffset now) =>
        room.DeletedAt is { } at ? Math.Max(0, (int)Math.Ceiling((at + KeepFor - now).TotalDays)) : 0;

    /// <summary>
    /// 30일이 지난 방을 지운다: 방 색인에서 빼고 데이터 폴더의 attachments/&lt;방 ID&gt; 폴더를 지운다. 지운 방 ID 목록.
    /// 방 ID는 12자 16진만 경로에 쓴다 (색인이 손으로 고쳐졌어도 다른 폴더를 지우지 않게). 방 색인이 깨졌으면 아무것도 하지 않는다.
    /// </summary>
    public static IReadOnlyList<string> Purge(RoomStore store, string dataFolder, DateTimeOffset now)
    {
        if (store.LoadError is not null) return [];
        var expired = store.RemoveWhere(r => r.DeletedAt is { } at && now - at >= KeepFor);
        foreach (var id in expired.Where(RoomStore.IsRoomId))
        {
            var dir = Path.Combine(dataFolder, "attachments", id);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 다음 지우기 때 남은 폴더는 그대로 — 색인에서는 빠졌다
        }
        return expired;
    }
}
