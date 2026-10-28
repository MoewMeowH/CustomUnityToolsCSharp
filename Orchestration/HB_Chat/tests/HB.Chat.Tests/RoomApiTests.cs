using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>`/api/rooms` 줄 (계획서 2-3): 데스크톱 앱이 읽는 모양.</summary>
public sealed class RoomApiTests
{
    [Fact]
    public void Rows_CarryStateNames_PreviewOnlyWhenNotifyBodyOn()
    {
        var at = new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero);
        RoomSummary[] rooms = [
            new RoomSummary("0123456789ab", "방 하나", "본문 미리보기", at, 2, RoomActivity.Approval, false, PermissionLevel.Command, "haiku") { Face = Face.Approval },
            new RoomSummary("ba9876543210", "방 둘", null, at, 0, RoomActivity.Idle, true, PermissionLevel.Read, "opus"),
        ];
        var rows = RoomApi.Rows(rooms);
        Assert.Equal(new RoomRow("0123456789ab", "방 하나", "Approval", false, 2, "Approval", at), rows[0]);
        Assert.Equal(("Idle", true, "Idle"), (rows[1].Activity, rows[1].Failed, rows[1].Face));
        Assert.All(rows, r => Assert.Null(r.Preview)); // 기본(알림 본문 끔)은 본문을 싣지 않는다

        var withPreview = RoomApi.Rows(rooms, withPreview: true);
        Assert.Equal("본문 미리보기", withPreview[0].Preview);
        Assert.Null(withPreview[1].Preview);

        // 데스크톱(RoomStates.AnyRunning)이 읽는 모양 그대로 (26d478170f C11)
        var json = System.Text.Json.JsonSerializer.Serialize(rows, RoomApi.Json);
        Assert.Contains("\"activity\":\"Approval\"", json);
        Assert.Contains("\"id\":\"0123456789ab\"", json);
        // 한글은 \uXXXX로 나가므로 다시 읽어 본다 — 데스크톱 RoomStates가 "preview"로 읽는 이름
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(withPreview, RoomApi.Json));
        Assert.Equal("본문 미리보기", doc.RootElement[0].GetProperty("preview").GetString());
    }
}
