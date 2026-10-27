using System.Text.Json;

namespace HB.Chat.Desktop;

/// <summary>`/api/rooms` 응답 읽기 (계획서 2-3). b 단계의 알림 판정도 여기에 더한다.</summary>
public static class RoomStates
{
    /// <summary>도는 턴(Busy · Approval)이 있는 방이 하나라도 있으면 true. 읽지 못하면 false (끄기를 막지 않는다).</summary>
    public static bool AnyRunning(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var room in doc.RootElement.EnumerateArray())
            {
                if (room.TryGetProperty("activity", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { } s
                    && !s.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
