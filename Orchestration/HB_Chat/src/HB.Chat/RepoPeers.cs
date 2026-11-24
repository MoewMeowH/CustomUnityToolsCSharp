namespace HB.Chat;

/// <summary>같은 git 저장소를 쓰는 다른 방: ID · 화면 이름(단톡방 구성원은 "단톡방 — 구성원") · 활동.</summary>
public sealed record RepoPeer(string RoomId, string Name, RoomActivity Activity)
{
    public bool Working => Activity != RoomActivity.Idle;
}

/// <summary>
/// 같은 저장소 여러 방 판정 (곁칸 변경 탭 출처 · 입력칸 위 띠 · 지휘자 말 끝 줄 — 계기: 다른 세션의 git rm · mv가 커밋에 섞인 일).
/// 저장소는 작업 폴더의 git 루트로 묶는다 (작업 폴더가 하위 폴더로 달라도 같은 저장소). 루트는 <see cref="GitChanges"/> 캐시를 쓴다.
/// </summary>
public static class RepoPeers
{
    /// <summary>말 끝 알림 줄의 머리 (화면 말풍선 · 미리보기에서는 <see cref="Attachments.Split"/>가 뺀다).</summary>
    public const string Marker = "[같은 저장소] ";

    /// <summary>
    /// 이 방과 같은 저장소 루트를 쓰는 다른 방들 (휴지통 · 단톡방 자체 방 제외, 단톡방 구성원 자식 방 포함). 루트를 모르는 방은 뺀다.
    /// rootOf: 작업 폴더 이름 → 저장소 루트 (모르면 null).
    /// </summary>
    public static IReadOnlyList<RepoPeer> Find(Room room, IEnumerable<Room> rooms, Func<string, string?> rootOf, Func<string, RoomActivity> activityOf,
        Func<string, Room?> get)
    {
        if (rootOf(room.Workspace) is not { } root) return [];
        var peers = new List<RepoPeer>();
        foreach (var other in rooms)
        {
            if (other.Id == room.Id || other.DeletedAt is not null || other.Members.Count > 0) continue; // 단톡방 자체는 돌지 않는다 — 구성원 자식 방으로 본다
            if (rootOf(other.Workspace) is not { } otherRoot || !string.Equals(otherRoot, root, StringComparison.OrdinalIgnoreCase)) continue;
            peers.Add(new RepoPeer(other.Id, NameOf(other, get), activityOf(other.Id)));
        }
        return peers;
    }

    /// <summary>
    /// 모든 방 작업 폴더의 저장소 루트를 구해(<see cref="GitChanges.RootAsync"/> — 작업 폴더마다 5분 캐시) 이 방의 루트와 같은 저장소 다른 방을 찾는다.
    /// workspacePath: 작업 폴더 이름 → 경로 (모르면 null).
    /// </summary>
    public static async Task<(string? Root, IReadOnlyList<RepoPeer> Peers)> FindAsync(Room room, IReadOnlyList<Room> rooms, GitChanges git,
        Func<string, string?> workspacePath, Func<string, RoomActivity> activityOf, Func<string, Room?> get, CancellationToken ct = default)
    {
        // 작업 폴더마다 함께 (차례로 하면 캐시가 빌 때 보내기가 작업 폴더 수만큼 늦어진다 — /self-review sub C3)
        var workspaces = rooms.Where(r => r.DeletedAt is null && r.Members.Count == 0).Select(r => r.Workspace).Append(room.Workspace)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = await Task.WhenAll(workspaces.Select(async ws => (ws, root: workspacePath(ws) is { } path ? await git.RootAsync(path, ct) : null)));
        var roots = found.ToDictionary(f => f.ws, f => f.root, StringComparer.OrdinalIgnoreCase);
        return (roots.GetValueOrDefault(room.Workspace), Find(room, rooms, ws => roots.GetValueOrDefault(ws), activityOf, get));
    }

    /// <summary>화면 이름: 단톡방 구성원 자식 방이면 "단톡방 제목 — 구성원 이름", 아니면 방 제목(없으면 "제목 없는 방").</summary>
    public static string NameOf(Room room, Func<string, Room?> get)
    {
        if (room.GroupId is { } groupId && get(groupId) is { } group)
        {
            var member = group.Members.FirstOrDefault(m => m.RoomId == room.Id)?.Name ?? "구성원";
            return $"{group.Title ?? "단톡방"} — {member}";
        }
        return room.Title ?? "제목 없는 방";
    }

    /// <summary>지휘자에게 보낼 말 끝 줄 — 작업 중인 다른 방이 있을 때만 (결정 "다른 방이 작업 중일 때만"). 없으면 null.</summary>
    public static string? Line(string root, IReadOnlyList<RepoPeer> peers)
    {
        var working = peers.Where(p => p.Working).ToList();
        if (working.Count == 0) return null;
        var names = string.Join(", ", working.Take(3).Select(p => $"'{p.Name}'")) + (working.Count > 3 ? $" 외 {working.Count - 3}개" : "");
        return $"{Marker}같은 git 저장소({root})에서 다른 방 {names}이(가) 작업 중입니다 — git add · commit 전에 git diff --cached로 이 방 변경만 올라갔는지 확인하세요.";
    }
}
