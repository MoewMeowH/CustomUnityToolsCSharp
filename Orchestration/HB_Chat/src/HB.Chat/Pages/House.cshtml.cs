using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HB.Chat.Pages;

/// <summary>놀이방 창의 캐릭터 하나: 키(구성원 ID · 1:1은 main) · 이름 · 펫(그림이 있는 펫이 없으면 null).</summary>
public sealed record HouseCharacter(string Key, string Name, string? Pet);

/// <summary>
/// 놀이방 창 (/House/{방}): 방 작업을 2.5D 집 안 펫 캐릭터로 보는 따로 창. 움직임은 화면(wwwroot/house.js)이 방 SSE(/events/{방})를 읽어 만든다 —
/// 서버는 캐릭터 · 펫 목록을 싣고, 캐릭터 바꾸기만 저장한다 (Room.HousePets).
/// </summary>
public sealed class HouseModel(RoomStore store, HousePetCatalog pets, ProfileView profiles) : PageModel
{
    public Room Room { get; private set; } = null!;

    /// <summary>single · group · playground (화면이 쉴 때 하는 일을 고른다).</summary>
    public string Kind => Room.Playground is not null ? "playground" : Room.IsGroup ? "group" : "single";

    public IReadOnlyList<HouseCharacter> Characters { get; private set; } = [];

    public IReadOnlyList<HousePet> Pets { get; private set; } = [];

    public IActionResult OnGet(string id)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        Room = room;
        Characters = CharactersOf(room);
        Pets = pets.List();
        return Page();
    }

    /// <summary>캐릭터 바꾸기: 그 방의 캐릭터 키 · 그림이 있는 내장 펫만.</summary>
    public IActionResult OnPostPet(string id, string? key, string? pet)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (key is null || !CharactersOf(room).Any(c => c.Key == key)) return BadRequest("없는 캐릭터입니다.");
        if (pets.SheetPath(pet) is null) return BadRequest("없는 펫입니다.");
        store.Update(id, r => r with { HousePets = new Dictionary<string, string>(r.HousePets ?? new Dictionary<string, string>()) { [key] = pet! } });
        return new JsonResult(new { ok = true });
    }

    private IReadOnlyList<HouseCharacter> CharactersOf(Room room) => room.IsGroup
        ? [.. room.Members.Select(m => new HouseCharacter(m.Id, m.Name, pets.For(room, m.Id, store.Get(m.RoomId)?.Engine ?? m.Engine)))]
        : [new HouseCharacter(HousePetCatalog.MainKey, profiles.ConductorName, pets.For(room, HousePetCatalog.MainKey, room.Engine))];
}
