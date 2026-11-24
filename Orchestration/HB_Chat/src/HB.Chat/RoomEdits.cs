using System.Text.Json;

namespace HB.Chat;

/// <summary>방이 쓰기 도구로 부른 파일 하나: 전체 경로 · 마지막 시각 · 도구 이름.</summary>
public sealed record RoomEdit(string Path, DateTimeOffset At, string Tool);

/// <summary>
/// 방마다 쓰기 도구(Write · Edit · MultiEdit · NotebookEdit — 서브에이전트 포함)로 부른 파일 (곁칸 변경 탭의 출처 · 같은 저장소 경고).
/// 데이터 폴더 room-edits/&lt;방ID&gt;.json. "도구를 부른 파일"이라 거절 · 실패한 호출도 들어간다 — 화면은 git이 바뀌었다고 한 파일에만 붙인다.
/// Bash로 고친 파일은 알 수 없다. 방마다 최근 <see cref="Keep"/>개.
/// </summary>
public sealed class RoomEdits(Func<string> dataFolder)
{
    public const int Keep = 1000;

    /// <summary>같은 파일을 이 안에 다시 부르면 기록 파일을 다시 쓰지 않는다 (Edit를 잇달아 부를 때).</summary>
    public static readonly TimeSpan Rewrite = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, RoomEdit>> _rooms = new(StringComparer.Ordinal);

    private string FilePath(string roomId) => System.IO.Path.Combine(dataFolder(), "room-edits", roomId + ".json");

    /// <summary>기록을 더한다. 전체 경로가 아니거나 방 ID 모양이 아니면 버린다. 저장 실패는 무시한다 (실행을 막지 않게).</summary>
    public void Add(string roomId, string path, string tool, DateTimeOffset? now = null)
    {
        if (!RoomStore.IsRoomId(roomId) || !System.IO.Path.IsPathFullyQualified(path)) return;
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        var at = now ?? DateTimeOffset.Now;
        lock (_gate)
        {
            var map = Load(roomId);
            if (map.TryGetValue(full, out var was) && at - was.At < Rewrite) return;
            map[full] = new RoomEdit(full, at, tool);
            if (map.Count > Keep)
                foreach (var old in map.Values.OrderBy(e => e.At).Take(map.Count - Keep).ToList()) map.Remove(old.Path);
            try { AtomicFile.WriteAllText(FilePath(roomId), JsonSerializer.Serialize(map.Values, Json)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>그 방 기록 (새것부터).</summary>
    public IReadOnlyList<RoomEdit> For(string roomId)
    {
        if (!RoomStore.IsRoomId(roomId)) return [];
        lock (_gate) return [.. Load(roomId).Values.OrderByDescending(e => e.At)];
    }

    /// <summary>여러 방 기록을 경로별로: 경로 → (방 ID, 기록) 목록 (새것부터).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string RoomId, RoomEdit Edit)>> ByPath(IEnumerable<string> roomIds)
    {
        var all = new Dictionary<string, List<(string, RoomEdit)>>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var id in roomIds.Where(RoomStore.IsRoomId).Distinct(StringComparer.Ordinal))
                foreach (var edit in Load(id).Values)
                {
                    if (!all.TryGetValue(edit.Path, out var list)) all[edit.Path] = list = [];
                    list.Add((id, edit));
                }
        return all.ToDictionary(p => p.Key, p => (IReadOnlyList<(string, RoomEdit)>)[.. p.Value.OrderByDescending(e => e.Item2.At)], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>지운 방 기록을 버린다 (휴지통 비우기).</summary>
    public void Forget(string roomId)
    {
        if (!RoomStore.IsRoomId(roomId)) return;
        lock (_gate)
        {
            _rooms.Remove(roomId);
            try { File.Delete(FilePath(roomId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>_gate 안에서만. 한 번 읽은 방은 메모리에 둔다 (이 앱만 쓰는 파일). 없거나 깨졌으면 빈 기록.</summary>
    private Dictionary<string, RoomEdit> Load(string roomId)
    {
        if (_rooms.TryGetValue(roomId, out var map)) return map;
        map = new Dictionary<string, RoomEdit>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = FilePath(roomId);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<RoomEdit>>(File.ReadAllText(path), Json) is { } list)
                foreach (var e in list.Where(e => e?.Path is { Length: > 0 } && e.Tool is not null)) map[e.Path] = e;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        _rooms[roomId] = map;
        return map;
    }
}
