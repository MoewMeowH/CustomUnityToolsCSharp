using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Dashboard;

/// <summary>채팅 앱(HB_Chat) 방 하나: 앱의 방 ID와 제목. Link는 앱 주소가 설정돼 있을 때만.</summary>
public sealed record ChatAppRoom(string RoomId, string? Title, string? Link);

/// <summary>
/// 채팅 앱의 방 색인(rooms.json)을 읽어 세션 → 앱 방을 찾는다 (계획서 2026-10-27_대화형-채팅-앱 "대시보드와의 경계": 대시보드는 읽기만, 링크만).
/// 앱 세션도 보통 세션 기록이라 방 목록 · 채팅방에는 이미 나온다 — 여기서는 "채팅 앱 방" 표시와 앱으로 가는 링크만 더한다.
/// 파일 크기 · 수정 시각이 같으면 다시 읽지 않는다. 없거나 깨졌거나 모양이 다른 항목은 건너뛴다 (예외를 던지지 않는다).
/// </summary>
public sealed class ChatAppRooms(IOptions<DashboardOptions> options)
{
    private readonly Lock _gate = new();
    private (long Length, DateTime WriteUtc)? _stamp;
    private IReadOnlyDictionary<string, ChatAppRoom> _bySession = new Dictionary<string, ChatAppRoom>();

    public string Path =>
        string.IsNullOrWhiteSpace(options.Value.ChatAppRoomsPath)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HB_Chat", "rooms.json")
            : options.Value.ChatAppRoomsPath.Trim();

    /// <summary>그 세션이 채팅 앱의 방이면 그 방, 아니면 null.</summary>
    public ChatAppRoom? For(string sessionId) => Load().GetValueOrDefault(sessionId);

    private IReadOnlyDictionary<string, ChatAppRoom> Load()
    {
        lock (_gate)
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                _stamp = null;
                return _bySession = new Dictionary<string, ChatAppRoom>();
            }
            var stamp = (info.Length, info.LastWriteTimeUtc);
            if (_stamp == stamp) return _bySession;
            _stamp = stamp;
            return _bySession = Read(info.FullName, options.Value.ChatAppUrl);
        }
    }

    internal static IReadOnlyDictionary<string, ChatAppRoom> Read(string path, string? appUrl)
    {
        var map = new Dictionary<string, ChatAppRoom>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array) return map;
            var baseUrl = string.IsNullOrWhiteSpace(appUrl) ? null : appUrl.Trim().TrimEnd('/');
            foreach (var r in rooms.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var id = Str(r, "id");
                var session = Str(r, "sessionId");
                // 링크 · 화면에 들어가므로 앱이 만드는 모양만 받는다 (방 ID 12자 소문자 16진, 세션 ID uuid).
                if (id is not { Length: 12 } || !id.All(char.IsAsciiHexDigitLower) || !Guid.TryParseExact(session, "D", out _)) continue;
                map[session!] = new ChatAppRoom(id, Str(r, "title"), baseUrl is null ? null : $"{baseUrl}/Room/{id}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return map;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
