using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HB.Chat;

/// <summary>
/// 방 하나 = 앱이 만든 Claude 세션 하나 (계획서 3번). 말 본문은 여기 두지 않는다 — 세션 기록이 저장소다.
/// KnownSize: 앱이 마지막 실행 뒤 본 세션 기록 크기. 다음 말 때 다르면 이 앱 밖에서 이어 썼다는 뜻이라 경고한다.
/// TotalCostUsd: 마지막 result의 total_cost_usd (resume한 세션은 앞 실행까지 합친 클라이언트 추정).
/// </summary>
public sealed record Room
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Workspace { get; init; }
    public required PermissionLevel Level { get; init; }
    public required string Model { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastError { get; init; }
    public double? TotalCostUsd { get; init; }
    public long? KnownSize { get; init; }

    /// <summary>
    /// 실행을 시작하면 적고, 결과를 남길 때 지운다. 다음 실행 때 남아 있으면 앞 실행이 앱 종료(충돌 · 강제 종료)로 끊긴 것 —
    /// 그 실행이 쓴 세션 기록 때문에 크기가 달라도 "앱 밖에서 바뀜"이 아니다 (Job Object 실제 확인에서 발견).
    /// </summary>
    public DateTimeOffset? RunStartedAt { get; init; }

    /// <summary>명령 단계의 "이 방에서 항상 허용" 규칙 (예: <c>Bash(git status)</c>). 다음 실행부터 --allowedTools로 들어간다.</summary>
    public IReadOnlyList<string> AllowRules { get; init; } = [];

    /// <summary>
    /// 사용자가 본 마지막 말의 세션 기록 줄 번호 (<see cref="HB.Orchestration.Shared.ChatEntry.Line"/>). 안 읽은 수 = 이 줄 뒤의 지휘자 말 수 (계획서 2-2 a).
    /// 새 방은 0(아무것도 안 읽음). null은 이 필드 전에 만든 방뿐이고, 켤 때 마지막 줄까지 읽음으로 옮긴다 (<see cref="RoomList.MarkLegacyRead"/>,
    /// /self-review 5225b1ffaf C4 — 안 그러면 업그레이드 직후 기존 방이 전부 안 읽음으로 뜬다).
    /// </summary>
    public int? ReadUpTo { get; init; }

    /// <summary>휴지통에 넣은 시각 (계획서 2-2 c). 있으면 목록에서 빠지고 말을 보내지 않는다.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>
/// 방 색인 rooms.json. 쓸 때마다 임시 파일 → 바꾸기로 원자적으로 쓴다.
/// 파일을 읽지 못하면(깨짐) 덮어쓰지 않는다: LoadError를 두고 모든 쓰기를 거절한다 (사람이 고치거나 옮긴 뒤 다시 켠다).
/// </summary>
public sealed class RoomStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();
    private readonly string _path;
    private List<Room> _rooms = [];

    public string? LoadError { get; }

    public RoomStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _rooms = JsonSerializer.Deserialize<RoomFile>(File.ReadAllText(path), Json)?.Rooms ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public IReadOnlyList<Room> All()
    {
        lock (_lock) return [.. _rooms];
    }

    public Room? Get(string id)
    {
        lock (_lock) return _rooms.FirstOrDefault(r => r.Id == id);
    }

    public Room Create(string workspace, PermissionLevel level, string model, string? title)
    {
        var room = new Room
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            SessionId = Guid.NewGuid().ToString("D"),
            Workspace = workspace,
            Level = level,
            Model = model,
            CreatedAt = DateTimeOffset.Now,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            ReadUpTo = 0,
        };
        lock (_lock)
        {
            ThrowIfBroken();
            _rooms.Add(room);
            try
            {
                Save();
            }
            catch
            {
                _rooms.RemoveAt(_rooms.Count - 1); // 저장하지 못한 방은 목록에도 남기지 않는다 (0a3b1d5155 C6).
                throw;
            }
        }
        return room;
    }

    /// <summary>방을 고쳐 쓴다. 없는 방이면 null.</summary>
    public Room? Update(string id, Func<Room, Room> change)
    {
        lock (_lock)
        {
            ThrowIfBroken();
            var i = _rooms.FindIndex(r => r.Id == id);
            if (i < 0) return null;
            var before = _rooms[i];
            _rooms[i] = change(before) with { Id = id };
            try
            {
                Save();
            }
            catch
            {
                _rooms[i] = before; // 메모리와 디스크가 어긋나지 않게 (0a3b1d5155 C6).
                throw;
            }
            return _rooms[i];
        }
    }

    private void ThrowIfBroken()
    {
        if (LoadError is not null) throw new InvalidOperationException($"방 색인을 읽지 못해 쓰지 않습니다 ({_path}): {LoadError}");
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new RoomFile { Rooms = _rooms }, Json), new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class RoomFile
    {
        public int Version { get; set; } = 1;
        public List<Room> Rooms { get; set; } = [];
    }

    /// <summary>방 ID 모양 (12자 소문자 16진). 주소로 받은 값을 거를 때.</summary>
    public static bool IsRoomId(string? id) => id is { Length: 12 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
