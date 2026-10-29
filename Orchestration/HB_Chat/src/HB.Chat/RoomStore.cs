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

    /// <summary>
    /// 갈래 방(계획서 2-2 c 방 복제): 이어받을 원래 세션 ID. 첫 실행을 <c>--resume 원래 --fork-session --session-id 이 방</c>으로 하고,
    /// 성공하면 비운다 (2-2 0번 실측: fork-session에 session-id를 주면 그 ID로 갈라진다).
    /// </summary>
    public string? ForkOf { get; init; }

    /// <summary>갈래를 만들 때 원래 세션 기록의 크기. 첫 말 전에 원래 방이 더 진행됐는지 방 칸에 알리는 기준 (/self-review 95e8b1c242 C2).</summary>
    public long? ForkSourceSize { get; init; }

    /// <summary>마지막 실행이 끝날 때의 컨텍스트 크기 · 창 (토큰, 계획서 2-2 b 컨텍스트 표시). 첫 턴 전에는 없다.</summary>
    public long? ContextTokens { get; init; }
    public int? ContextWindow { get; init; }

    /// <summary>마지막 턴의 지휘자 상태 한 줄 (CLI post_turn_summary, 3차 b). 방 머리 · 목록 · 미니 창에 보인다.</summary>
    public RoomStatus? Status { get; init; }

    /// <summary>이 방만 쓸 스티커 팩 (4차 D2, 폴더 이름). null이면 전체 설정을 따른다 (StickerPacks.Effective).</summary>
    public string? StickerPack { get; init; }

    /// <summary>추론 강도 (claude --effort, <see cref="RoomEffort.Levels"/> 중 하나). null이면 인자를 넣지 않는다 — CLI 기본.</summary>
    public string? Effort { get; init; }

    /// <summary>"계획 먼저" (Claude 앱 기능 C): 다음 실행을 계획 모드로. 계획을 승인하면 앱이 끈다. 편집 · 명령 단계에서만 쓰인다.</summary>
    public bool PlanNext { get; init; }

    /// <summary>
    /// 이 방에 더 붙일 작업 폴더 .mcp.json의 MCP 서버 이름 (계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결 — Notion E). 읽기 이상 단계에서만 붙는다.
    /// 기본 서버(hb-orchestration)는 여기 넣지 않는다 — 늘 붙고, 끄려면 <see cref="McpOff"/>.
    /// </summary>
    public IReadOnlyList<string> McpExtra { get; init; } = [];

    /// <summary>이 방에서 끈 기본 연결: 기본 MCP 서버 이름(hb-orchestration) · 앱 MCP(<see cref="TerminalMcp.Server"/>). 다음 실행부터 안 붙는다.</summary>
    public IReadOnlyList<string> McpOff { get; init; } = [];

    /// <summary>연결한 Unity 프로젝트 이름 (설정에 등록한 것). 명령 방에서만 그 실행에 `unity mcp`가 붙는다 (결정 "명령 방만").</summary>
    public string? UnityProject { get; init; }

    /// <summary>
    /// 마지막 실행 init의 MCP 서버 상태 · MCP 도구 이름 · 그 시각 — 연결 탭이 앱을 다시 켠 뒤에도 보이게 (사용자 확인 2026-10-29 "첫 실행 때도 안 보임").
    /// 실행 시작(init)마다 바꾼다.
    /// </summary>
    public IReadOnlyList<McpServerState> McpStatus { get; init; } = [];
    public IReadOnlyList<string> McpTools { get; init; } = [];
    public DateTimeOffset? McpStatusAt { get; init; }

    /// <summary>
    /// 지휘자가 마지막으로 받은 방 규칙의 지문 (<see cref="RoomRules.Hash"/>). 이어 쓰는 세션은 CLI가 처음 시스템 프롬프트를 계속 쓰므로,
    /// 지금 규칙과 다르면 다음 말 앞에 새 규칙을 붙인다 (계획서 2026-10-29_HB_Chat-2-2-작은-한계-정리). null이면 이 칸 전에 만든 방 — 한 번 받는다.
    /// </summary>
    public string? RulesHash { get; init; }

    /// <summary>
    /// 갈래 방이 이어받은 방들의 ID (조상 먼저 · 원래 방 마지막). 그 방들의 첨부 폴더도 읽게 한다 — 이어받은 대화의 [첨부] 줄은 원래 방 폴더를 가리킨다
    /// (계획서 2026-10-29_HB_Chat-2-2-작은-한계-정리, 2-2 리뷰 C6). <see cref="ForkOf"/>는 첫 실행 뒤 지워지므로 따로 둔다.
    /// </summary>
    public IReadOnlyList<string> AttachFrom { get; init; } = [];
}

/// <summary>MCP 서버 하나의 실행 상태 (init mcp_servers 한 항목: connected · failed · needs-auth …).</summary>
public sealed record McpServerState(string Name, string Status);

/// <summary>추론 강도 (Claude 앱 기능 B): claude 2.1.312 --effort가 받는 값. 화면 select · 저장 · 인자 검사가 같이 쓴다.</summary>
public static class RoomEffort
{
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>빈 값은 null(기본), 목록 값은 그대로, 그 밖은 오류.</summary>
    public static (string? Value, string? Error) Clean(string? effort) =>
        string.IsNullOrWhiteSpace(effort) ? (null, null)
        : Levels.Contains(effort.Trim(), StringComparer.Ordinal) ? (effort.Trim(), null)
        : (null, "알 수 없는 추론 강도입니다.");

    /// <summary>화면 이름.</summary>
    public static string Name(string? effort) => effort switch
    {
        null => "기본",
        "low" => "low (빠름)",
        "max" => "max (가장 깊게)",
        _ => effort,
    };
}

/// <summary>지휘자 상태 한 줄: 종류(blocked · completed · review_ready …) · 상태 글 · 사용자가 할 일.</summary>
public sealed record RoomStatus(string? Category, string? Detail, string? NeedsAction);

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
                _rooms = [.. (JsonSerializer.Deserialize<RoomFile>(File.ReadAllText(path), Json)?.Rooms ?? []).Where(r => r is not null).Select(Clean)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 파일의 목록 칸이 null이면(손으로 고침 · 다른 판이 씀) 빈 목록으로 — JSON의 null이 기본값 []를 덮으면 그 칸을 쓰는 곳마다 NullReference가 났다
    /// (/self-review security 4c300badae C2).
    /// </summary>
    private static Room Clean(Room r) => r with
    {
        AllowRules = r.AllowRules ?? [], McpExtra = r.McpExtra ?? [], McpOff = r.McpOff ?? [], McpStatus = r.McpStatus ?? [],
        McpTools = r.McpTools ?? [], AttachFrom = r.AttachFrom ?? [],
    };

    public IReadOnlyList<Room> All()
    {
        lock (_lock) return [.. _rooms];
    }

    public Room? Get(string id)
    {
        lock (_lock) return _rooms.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>
    /// 새 방. forkOf가 있으면 그 세션의 대화를 이어받는 갈래 방 — 읽음 위치는 null(이어받은 말은 읽은 것으로, 켤 때 마지막 줄로 옮겨짐).
    /// </summary>
    public Room Create(string workspace, PermissionLevel level, string model, string? title, string? forkOf = null, long? forkSourceSize = null,
        string? effort = null)
    {
        var room = new Room
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            SessionId = Guid.NewGuid().ToString("D"),
            Workspace = workspace,
            Level = level,
            Model = model,
            Effort = effort,
            CreatedAt = DateTimeOffset.Now,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            ReadUpTo = forkOf is null ? 0 : null,
            ForkOf = forkOf,
            ForkSourceSize = forkOf is null ? null : forkSourceSize,
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

    /// <summary>
    /// 조건에 맞는 방을 색인에서 뺀다 (휴지통 30일 지남 · 휴지통 즉시 비우기 — Trash). 뺀 방 ID 목록. 조건은 잠금 안에서 본다 — 같은 순간의 되살리기와 겹쳐도
    /// 되살린 방은 지우지 않는다 (/self-review 95e8b1c242 C7). 저장에 실패하면 메모리도 되돌린다.
    /// </summary>
    public IReadOnlyList<string> RemoveWhere(Func<Room, bool> remove)
    {
        lock (_lock)
        {
            ThrowIfBroken();
            var before = _rooms;
            var removed = _rooms.Where(remove).Select(r => r.Id).ToList();
            if (removed.Count == 0) return [];
            _rooms = [.. _rooms.Where(r => !removed.Contains(r.Id))];
            try
            {
                Save();
            }
            catch
            {
                _rooms = before;
                throw;
            }
            return removed;
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
