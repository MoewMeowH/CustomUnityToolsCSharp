using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>토스트 알림 (계획서 2026-10-28 알림-토스트): 글 · 방 인자 · 로고 주소 · 캐시 이름.</summary>
public sealed class ToastTests
{
    [Fact]
    public void For_OnePerRoom_UsesBalloonText()
    {
        var toasts = Toasts.For([
            new Notice(NoticeKind.Done, "0123456789ab", "방 하나", Preview: "앞 답"),
            new Notice(NoticeKind.Approval, "ba9876543210", "명령 방", Question: true),
            new Notice(NoticeKind.Failed, "0123456789ab", "방 하나", Preview: "새 답"), // 같은 방은 뒤 것만 (방마다 하나)
        ], withBody: true);
        Assert.Equal(2, toasts.Count);
        Assert.Equal(new ToastSpec("0123456789ab", "실패 · 방 하나", "새 답"), toasts.Single(t => t.RoomId == "0123456789ab"));
        Assert.Equal(new ToastSpec("ba9876543210", "질문", "명령 방"), toasts.Single(t => t.RoomId == "ba9876543210")); // 질문은 제목만
        Assert.Equal("답 끝", Toasts.For([new Notice(NoticeKind.Done, "0123456789ab", "방", Preview: "글")], withBody: false)[0].Title);
        Assert.Equal("(제목 없음)", Toasts.For([new Notice(NoticeKind.Done, "0123456789ab", "")], false)[0].Text);
    }

    [Theory]
    [InlineData("room=0123456789ab", "0123456789ab")]
    [InlineData("room=0123456789AB", null)]   // 방 ID 모양 (소문자 16진 12자)만
    [InlineData("room=../x", null)]
    [InlineData("other=1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RoomFrom_OnlyRoomIdShape(string? argument, string? expected) => Assert.Equal(expected, Toasts.RoomFrom(argument));

    [Theory]
    [InlineData("/profile-image/0123456789abcdef-01234567.webp", true)]
    [InlineData("/profile-image/0123456789abcdef-01234567.png", true)]
    [InlineData("/profile-image/0123456789abcdef-01234567.svg", false)]
    [InlineData("/profile-image/../profiles.json", false)]
    [InlineData("http://evil/profile-image/0123456789abcdef-01234567.png", false)] // 다른 호스트 주소는 받지 않는다
    [InlineData("/profile-image/0123456789abcdef-01234567.png\n", false)]
    [InlineData(null, false)]
    public void ProfileImagePath_OnlyAppNames(string? url, bool ok) => Assert.Equal(ok, Toasts.IsProfileImagePath(url));

    [Fact]
    public void CacheName_StablePngPerUrl()
    {
        var a = Toasts.CacheName("/profile-image/0123456789abcdef-01234567.webp");
        Assert.Matches(@"^[0-9a-f]{16}\.png$", a);
        Assert.Equal(a, Toasts.CacheName("/profile-image/0123456789abcdef-01234567.webp"));
        Assert.NotEqual(a, Toasts.CacheName("/profile-image/0123456789abcdef-76543210.webp"));
    }

    [Fact]
    public async Task SavePng_ConvertsImage_AndRejectsGarbage()
    {
        var dir = Directory.CreateTempSubdirectory("hbchat-toast-").FullName;
        try
        {
            byte[] png;
            using (var bmp = new System.Drawing.Bitmap(4, 4))
            using (var ms = new MemoryStream())
            {
                bmp.SetPixel(1, 1, System.Drawing.Color.Red);
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                png = ms.ToArray();
            }
            var path = Path.Combine(dir, "sub", "a.png");
            Assert.True(await Toasts.SavePngAsync(png, path)); // 폴더가 없어도 만든다
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(path)[..4]);
            Assert.False(await Toasts.SavePngAsync("<svg onload=x>"u8.ToArray(), Path.Combine(dir, "b.png")));
            Assert.False(File.Exists(Path.Combine(dir, "b.png")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"theme":"discord","conductorImage":"/profile-image/a.png"}""", "/profile-image/a.png")]
    [InlineData("""{"theme":"discord","conductorImage":null}""", null)]
    [InlineData("""{"theme":"discord"}""", null)]
    [InlineData("""{"conductorImage":1}""", null)]
    [InlineData("{ broken", null)]
    public void ConductorImage_ReadsStringOnly(string json, string? expected) => Assert.Equal(expected, Appearance.ConductorImage(json));
}
