namespace HB.Chat;

/// <summary>놀이방 캐릭터 하나: 펫 이름(폴더) · 보이는 이름.</summary>
public sealed record HousePet(string Id, string Name);

/// <summary>
/// 놀이방 캐릭터 = 스티커 폴더 아래 <c>Pets/&lt;이름&gt;/spritesheet.webp</c> (데스크톱 내장 펫과 같은 그림, 원본 대시보드 IconPack/Pets).
/// 내장 6종만 쓴다 (결정 "내장 6종만") — 이름은 이 목록에서만 받아 경로를 만든다. 그림이 없는 펫은 목록에서 빠진다.
/// 기본값: Claude 구성원 · 1:1 방 = 까마귀, Codex 구성원 = 해달 (결정 "기본은 동물"), 같은 엔진이 여럿이어도 같은 그림 (결정 "겹쳐도 같은 그림").
/// </summary>
public sealed class HousePetCatalog(Func<string?> stickerDir)
{
    /// <summary>1:1 방의 캐릭터 키 (<see cref="Room.HousePets"/>).</summary>
    public const string MainKey = "main";

    public const string SheetFile = "spritesheet.webp";
    public const string ClaudeDefault = "crow", CodexDefault = "sea_otter";

    /// <summary>내장 펫 (차례 = 선택 상자 차례).</summary>
    public static readonly IReadOnlyList<HousePet> Builtin =
    [
        new("crow", "까마귀"), new("sea_otter", "해달"), new("cloud", "구름 모찌"),
        new("claude_chan", "클로짱"), new("gpt_chan", "GPT짱"), new("gemini_chan", "제미짱"),
    ];

    /// <summary>그림이 있는 내장 펫.</summary>
    public IReadOnlyList<HousePet> List() => [.. Builtin.Where(p => SheetPath(p.Id) is not null)];

    /// <summary>그 펫의 시트 파일 (내장 이름 · 스티커 폴더 Pets 안 · 링크 아님 · 있음). 아니면 null.</summary>
    public string? SheetPath(string? pet)
    {
        if (Builtin.FirstOrDefault(p => p.Id == pet) is not { } known || Root() is not { } root) return null;
        try
        {
            var dir = new DirectoryInfo(Path.Combine(root, StickerPacks.PetsFolder, known.Id));
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.Parent?.Attributes.HasFlag(FileAttributes.ReparsePoint) == true) return null;
            var file = new FileInfo(Path.Combine(dir.FullName, SheetFile));
            return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? file.FullName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>엔진 기본 캐릭터 (null = Claude).</summary>
    public static string DefaultFor(string? engine) => engine == RoomEngines.Codex ? CodexDefault : ClaudeDefault;

    /// <summary>
    /// 그 방 그 키의 캐릭터: 고른 것이 있고 그림이 있으면 그것, 아니면 엔진 기본값, 그 그림도 없으면 그림이 있는 첫 펫.
    /// 그림이 있는 펫이 하나도 없으면 null (화면이 대신 모양을 그린다).
    /// </summary>
    public string? For(Room room, string key, string? engine)
    {
        if (room.HousePets?.GetValueOrDefault(key) is { } chosen && SheetPath(chosen) is not null) return chosen;
        var fallback = DefaultFor(engine);
        return SheetPath(fallback) is not null ? fallback : List().FirstOrDefault()?.Id;
    }

    private string? Root()
    {
        var dir = stickerDir();
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var full = Path.GetFullPath(dir.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
