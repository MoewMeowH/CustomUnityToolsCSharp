using HB.Orchestration.Dashboard;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>채팅 앱(HB_Chat) 방 색인 읽기 — 대시보드는 표시 · 링크만 (계획서 2026-10-27_대화형-채팅-앱 "대시보드와의 경계").</summary>
public sealed class ChatAppRoomsTests : IDisposable
{
    private const string Sid = "69a95e9f-612a-4105-ac02-19e7ccc2c419";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-chatapp-" + Guid.NewGuid().ToString("N"));

    public ChatAppRoomsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ChatAppRooms Rooms(string path, string? url = "http://localhost:5290/") =>
        new(Options.Create(new DashboardOptions { ChatAppRoomsPath = path, ChatAppUrl = url }));

    [Fact]
    public void FindsAppRoom_AndBuildsLink()
    {
        var path = Path.Combine(_dir, "rooms.json");
        File.WriteAllText(path, $$"""
            {"version":1,"rooms":[
              {"id":"8e49a93acf4c","sessionId":"{{Sid}}","title":"실제 확인"},
              {"id":"NOT-HEX-ID!!","sessionId":"b647f6a5-4e90-494b-bd27-7c5e7a186f0a"},
              {"id":"aaaaaaaaaaaa","sessionId":"../../etc"},
              "junk"]}
            """);
        var rooms = Rooms(path);
        var room = rooms.For(Sid.ToUpperInvariant()); // 세션 ID는 대소문자 구분 없이
        Assert.NotNull(room);
        Assert.Equal("8e49a93acf4c", room.RoomId);
        Assert.Equal("실제 확인", room.Title);
        Assert.Equal("http://localhost:5290/Room/8e49a93acf4c", room.Link);
        Assert.Null(rooms.For("b647f6a5-4e90-494b-bd27-7c5e7a186f0a")); // 방 ID 모양이 아니면 건너뜀
        Assert.Null(rooms.For("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void NoUrl_NoLink()
    {
        var path = Path.Combine(_dir, "rooms.json");
        File.WriteAllText(path, $$"""{"rooms":[{"id":"8e49a93acf4c","sessionId":"{{Sid}}"}]}""");
        var room = Rooms(path, url: "").For(Sid);
        Assert.NotNull(room);
        Assert.Null(room.Link);
    }

    [Fact]
    public void MissingOrBrokenFile_IsEmpty_AndChangesAreRead()
    {
        var path = Path.Combine(_dir, "rooms.json");
        var rooms = Rooms(path);
        Assert.Null(rooms.For(Sid)); // 파일 없음

        File.WriteAllText(path, "{ broken");
        Assert.Null(rooms.For(Sid)); // 깨짐 — 예외 없이 비어 있음

        File.WriteAllText(path, $$"""{"rooms":[{"id":"8e49a93acf4c","sessionId":"{{Sid}}"}]}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); // 크기가 우연히 같아도 시각으로 다시 읽음
        Assert.NotNull(rooms.For(Sid));
    }
}
