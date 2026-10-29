namespace HB.Chat;

/// <summary>
/// 휴지통 (계획서 2-2 c). 방은 DeletedAt을 적어 목록 · 검색 · 보내기에서 빼고, 30일이 지나면(또는 휴지통 화면에서 바로) 방 색인과 그 방 첨부 폴더를 지운다.
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
    public static IReadOnlyList<string> Purge(RoomStore store, string dataFolder, DateTimeOffset now) =>
        Remove(store, dataFolder, r => r.DeletedAt is { } at && now - at >= KeepFor);

    /// <summary>
    /// 휴지통 즉시 비우기 (4차 C1 — 사용자 희망 "휴지통 즉시 비우기"): 준 방들 중 휴지통에 있는 방만 지운다 — 조건은 잠금 안에서 보므로
    /// 같은 순간 되살린 방은 남는다. "비우기"도 화면에 보인 방 ID만 받는다 — 확인한 범위 밖(그사이 다른 탭이 넣은 방)을 지우지 않게
    /// (/self-review bd52c6f0d1 C3). 세션 기록은 남김 (4차 결정 C1 "남김").
    /// </summary>
    public static IReadOnlyList<string> DeleteNow(RoomStore store, string dataFolder, IReadOnlyCollection<string> ids) =>
        Remove(store, dataFolder, r => r.DeletedAt is not null && ids.Contains(r.Id));

    private static IReadOnlyList<string> Remove(RoomStore store, string dataFolder, Func<Room, bool> which)
    {
        if (store.LoadError is not null) return [];
        var removed = store.RemoveWhere(which).ToList();
        // 단톡방을 지우면 구성원 자식 방도 (계획서 2026-10-29_HB_Chat-단톡방) — 자식 방만 따로 지우지는 않는다(휴지통 목록에 없음)
        if (removed.Count > 0) removed.AddRange(store.RemoveWhere(r => r.GroupId is { } g && removed.Contains(g)));
        foreach (var id in removed.Where(RoomStore.IsRoomId))
        {
            // 첨부 · 지휘자 브라우저 프로필(쿠키 · 로그인) · 스크린샷 (/self-review security 8b11177bd5 C6). 브라우저는 휴지통에 넣을 때 닫았다
            foreach (var dir in new[] { Path.Combine(dataFolder, "attachments", id), Path.Combine(dataFolder, "browser", id), Path.Combine(dataFolder, "browser-shots", id) })
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 다음 지우기 때 남은 폴더는 그대로 — 색인에서는 빠졌다
            }
            // 실행 도중 앱이 죽어 남은 방별 MCP 설정 파일 (계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결, 지휘자 검토 K7)
            McpConfigFile.DeleteRoom(McpConfigFile.RoomPath(dataFolder, id));
            new GroupLog(() => dataFolder).Delete(id); // 단톡방 대화 줄 (없으면 아무것도 안 함)
        }
        return removed;
    }
}
