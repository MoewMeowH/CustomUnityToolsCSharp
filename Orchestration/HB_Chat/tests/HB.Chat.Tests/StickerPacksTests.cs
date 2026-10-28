using HB.Chat;
using HB.Orchestration.ChatUi;

namespace HB.Chat.Tests;

/// <summary>스티커 팩 (4차 D2): 폴더 하나 = 팩 하나, pack.json 이름, 팩 → 기본 대신 찾기, 고른 팩(방 → 전체 → 기본).</summary>
public sealed class StickerPacksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-packs-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly StickerCatalog _stickers;
    private readonly ProfileStore _profiles;

    public StickerPacksTests()
    {
        _root = Path.Combine(_dir, "stickers");
        Directory.CreateDirectory(Path.Combine(_root, "cat"));
        Directory.CreateDirectory(Path.Combine(_root, "dog"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
        File.WriteAllBytes(Path.Combine(_root, "01-idle.png"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "06-done.png"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "cat", "06-done.png"), [2]);
        File.WriteAllText(Path.Combine(_root, "cat", "pack.json"), """{"name":"고양이"}""");
        var options = new StickerOptions
        {
            Dir = _root,
            Files = new(StringComparer.Ordinal) { ["대기"] = "01-idle.png", ["긍정"] = "06-done.png" },
        };
        _stickers = new StickerCatalog(() => options);
        _profiles = new ProfileStore(Path.Combine(_dir, "data"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void List_FoldersAsPacks_WithNameAndCount()
    {
        // 화면 차례는 이름(문화권 정렬)이라 비교는 ID 차례로 (ef2c83416c C1)
        var packs = new StickerPacks(_stickers, _profiles);
        Assert.Equal([new StickerPack("cat", "고양이", 1), new StickerPack("dog", "dog", 0)], packs.List().OrderBy(p => p.Id, StringComparer.Ordinal)); // .hidden은 이름 모양이 아님
        Assert.Equal(2, packs.Total()); // 기본 폴더에서 찾히는 그림 수
    }

    [Fact]
    public void For_PackFirst_ThenDefault_AndUrl()
    {
        var packs = new StickerPacks(_stickers, _profiles);
        var cat = packs.For("cat");
        Assert.Equal(Path.Combine(_root, "cat", "06-done.png"), cat.FileFor("긍정")!.Value.Path);
        Assert.Equal(Path.Combine(_root, "01-idle.png"), cat.FileFor("대기")!.Value.Path); // 팩에 없음 → 기본
        Assert.Equal("/sticker-pack/cat/", cat.Options.UrlBase);
        Assert.Same(_stickers, packs.For(null));
        Assert.Same(_stickers, packs.For("없는팩"));
        Assert.Same(_stickers, packs.For(".."));
    }

    [Fact]
    public void Effective_RoomThenGlobalThenDefault()
    {
        var packs = new StickerPacks(_stickers, _profiles);
        var room = new Room { Id = "0123456789ab", SessionId = Guid.NewGuid().ToString(), Workspace = "ws", Level = PermissionLevel.Chat, Model = "opus", CreatedAt = DateTimeOffset.Now };
        Assert.Null(packs.Effective(room));
        Assert.Null(_profiles.SetStickerPack("dog"));
        Assert.Equal("dog", packs.Effective(room));
        Assert.Equal("cat", packs.Effective(room with { StickerPack = "cat" }));
        Assert.Equal("dog", new ProfileStore(Path.Combine(_dir, "data")).StickerPack()); // 저장됨
        Assert.Equal("dog", packs.Effective(room with { StickerPack = "사라진팩" })); // 방 팩 폴더가 없으면 전체 설정으로
        Assert.NotNull(_profiles.SetStickerPack("../x"));
        Directory.Delete(Path.Combine(_root, "dog"));
        Assert.Null(packs.Effective(room)); // 전체 팩도 없으면 기본
    }

    [Theory]
    [InlineData("cat", true)]
    [InlineData("고양이 팩", true)]
    [InlineData("v1.2", true)]
    [InlineData("..", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/b", false)]
    [InlineData("끝점.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPackName(string? name, bool ok) => Assert.Equal(ok, StickerPacks.IsPackName(name));
}
