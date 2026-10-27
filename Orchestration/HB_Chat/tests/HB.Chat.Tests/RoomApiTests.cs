using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>`/api/rooms` 줄 (계획서 2-3): 데스크톱 앱이 읽는 모양.</summary>
public sealed class RoomApiTests
{
    [Fact]
    public void Rows_CarryStateNamesWithoutPreview()
    {
        var at = new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero);
        var rows = RoomApi.Rows([
            new RoomSummary("0123456789ab", "방 하나", "본문 미리보기", at, 2, RoomActivity.Approval, false, PermissionLevel.Command, "haiku") { Face = Face.Approval },
            new RoomSummary("ba9876543210", "방 둘", null, at, 0, RoomActivity.Idle, true, PermissionLevel.Read, "opus"),
        ]);
        Assert.Equal(new RoomRow("0123456789ab", "방 하나", "Approval", false, 2, "Approval", at), rows[0]);
        Assert.Equal(("Idle", true, "Idle"), (rows[1].Activity, rows[1].Failed, rows[1].Face));
        Assert.DoesNotContain(typeof(RoomRow).GetProperties(), p => p.Name == "Preview"); // 알림에 본문을 띄우지 않는다

        // 데스크톱(RoomStates.AnyRunning)이 읽는 모양 그대로 (26d478170f C11)
        var json = System.Text.Json.JsonSerializer.Serialize(rows, RoomApi.Json);
        Assert.Contains("\"activity\":\"Approval\"", json);
        Assert.Contains("\"id\":\"0123456789ab\"", json);
    }
}
