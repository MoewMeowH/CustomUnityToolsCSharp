using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>놀이방 캐릭터: 내장 펫만 · 스티커 폴더 Pets 안만 · 기본값(Claude 까마귀 · Codex 해달) · 고른 펫이 사라지면 기본값.</summary>
public sealed class HousePetCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-house-" + Guid.NewGuid().ToString("N"));
    private readonly string _pets;
    private readonly HousePetCatalog _catalog;

    public HousePetCatalogTests()
    {
        _pets = Path.Combine(_dir, "stickers", "Pets");
        foreach (var pet in new[] { "crow", "sea_otter", "cloud", "my_pet" })
        {
            Directory.CreateDirectory(Path.Combine(_pets, pet));
            File.WriteAllBytes(Path.Combine(_pets, pet, HousePetCatalog.SheetFile), [1]);
        }
        Directory.CreateDirectory(Path.Combine(_pets, "gpt_chan")); // 시트 없음
        File.WriteAllBytes(Path.Combine(_dir, "stickers", "secret.webp"), [9]);
        _catalog = new HousePetCatalog(() => Path.Combine(_dir, "stickers"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Room NewRoom(IReadOnlyDictionary<string, string>? pets = null) => new()
    {
        Id = "0123456789ab", SessionId = Guid.NewGuid().ToString(), Workspace = "ws", Level = PermissionLevel.Read, Model = "opus",
        CreatedAt = DateTimeOffset.Now, HousePets = pets,
    };

    [Fact]
    public void List_BuiltinWithSheetOnly_InBuiltinOrder()
    {
        // 내장이 아닌 my_pet · 시트 없는 gpt_chan은 빠진다
        Assert.Equal(["crow", "sea_otter", "cloud"], _catalog.List().Select(p => p.Id));
        Assert.Equal("까마귀", _catalog.List()[0].Name);
    }

    [Theory]
    [InlineData("my_pet")]
    [InlineData("gpt_chan")]
    [InlineData("../secret")]
    [InlineData("..")]
    [InlineData("CROW")]
    [InlineData("")]
    [InlineData(null)]
    public void SheetPath_RefusesUnknownOrOutside(string? pet) => Assert.Null(_catalog.SheetPath(pet));

    [Fact]
    public void SheetPath_InsidePetsFolder()
    {
        var path = _catalog.SheetPath("crow");
        Assert.Equal(Path.Combine(_pets, "crow", HousePetCatalog.SheetFile), path);
    }

    [Fact]
    public void SheetPath_NoStickerFolder_Null()
    {
        Assert.Null(new HousePetCatalog(() => null).SheetPath("crow"));
        Assert.Null(new HousePetCatalog(() => Path.Combine(_dir, "nope")).SheetPath("crow"));
        Assert.Empty(new HousePetCatalog(() => "  ").List());
    }

    [Fact]
    public void For_DefaultsByEngine_SameEngineSamePet()
    {
        var room = NewRoom();
        Assert.Equal("crow", _catalog.For(room, "a", null));
        Assert.Equal("crow", _catalog.For(room, "b", null)); // 결정 "겹쳐도 같은 그림"
        Assert.Equal("sea_otter", _catalog.For(room, "c", RoomEngines.Codex));
        Assert.Equal("crow", _catalog.For(room, HousePetCatalog.MainKey, null));
    }

    [Fact]
    public void For_ChosenPet_FallsBackWhenGone()
    {
        var room = NewRoom(new Dictionary<string, string> { ["a"] = "cloud", ["b"] = "gpt_chan", ["c"] = "my_pet" });
        Assert.Equal("cloud", _catalog.For(room, "a", null));
        Assert.Equal("crow", _catalog.For(room, "b", null)); // 시트 없음
        Assert.Equal("sea_otter", _catalog.For(room, "c", RoomEngines.Codex)); // 내장 아님
    }

    [Fact]
    public void For_DefaultWithoutSheet_FirstPetWithSheet_ElseNull()
    {
        Directory.Delete(Path.Combine(_pets, "crow"), recursive: true);
        Assert.Equal("sea_otter", _catalog.For(NewRoom(), "a", null)); // 까마귀 그림 없음 → 그림이 있는 첫 펫
        Assert.Null(new HousePetCatalog(() => null).For(NewRoom(), "a", null));
    }

    private (RoomStore Store, HB.Chat.Pages.HouseModel Page) NewPage()
    {
        Directory.CreateDirectory(_dir);
        var store = new RoomStore(Path.Combine(_dir, "rooms.json"));
        return (store, new HB.Chat.Pages.HouseModel(store, _catalog, new ProfileView(new ProfileStore(Path.Combine(_dir, "data")))));
    }

    [Fact]
    public void Page_Get_UnknownRoom_NotFound_SingleRoomHasMainCharacter()
    {
        var (store, page) = NewPage();
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(page.OnGet("0123456789ab"));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(page.OnGet("../x"));
        var room = store.Create("ws", PermissionLevel.Read, "opus", null);
        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(page.OnGet(room.Id));
        Assert.Equal("single", page.Kind);
        var only = Assert.Single(page.Characters);
        Assert.Equal((HousePetCatalog.MainKey, "crow"), (only.Key, only.Pet));
    }

    [Fact]
    public void Page_PostPet_ChecksRoomKeyAndPet_ThenSaves()
    {
        var (store, page) = NewPage();
        var group = store.Create("ws", PermissionLevel.Read, "opus", "g",
            shape: r => r with { Members = [new GroupMember("m1", "A", "", ""), new GroupMember("m2", "B", "", "") { Engine = RoomEngines.Codex }] });
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(page.OnPostPet("0123456789ab", "m1", "cloud"));
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(page.OnPostPet(group.Id, "nobody", "cloud"));
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(page.OnPostPet(group.Id, HousePetCatalog.MainKey, "cloud")); // 단톡방에 main 없음
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(page.OnPostPet(group.Id, "m1", "my_pet"));    // 내장 아님
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(page.OnPostPet(group.Id, "m1", "gpt_chan"));  // 그림 없음
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(page.OnPostPet(group.Id, "m1", null));
        Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(page.OnPostPet(group.Id, "m1", "cloud"));
        page.OnGet(group.Id);
        Assert.Equal([("m1", "cloud"), ("m2", "sea_otter")], page.Characters.Select(c => (c.Key, c.Pet)));
    }

    [Fact]
    public void Room_HousePets_RoundTripsThroughStore()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "rooms.json");
        var store = new RoomStore(file);
        var room = store.Create("ws", PermissionLevel.Read, "opus", null);
        store.Update(room.Id, r => r with { HousePets = new Dictionary<string, string> { [HousePetCatalog.MainKey] = "cloud" } });
        var again = new RoomStore(file).Get(room.Id);
        Assert.Equal("cloud", again?.HousePets?[HousePetCatalog.MainKey]);
    }
}
