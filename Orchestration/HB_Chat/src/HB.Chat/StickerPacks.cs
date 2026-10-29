using System.Text.Json;
using System.Text.RegularExpressions;
using HB.Orchestration.ChatUi;

namespace HB.Chat;

/// <summary>스티커 팩 하나: 폴더 이름(ID) · 보이는 이름(pack.json의 name, 없으면 폴더 이름) · 그 폴더에 있는 그림 수.</summary>
public sealed record StickerPack(string Id, string Name, int Files);

/// <summary>
/// 스티커 팩 (4차 D2 — 노션 "이모티콘 사용 종류 변경": "이미지 경로 폴더 내 json 등이나 폴더 단위로 종류 나열 · 내부 이미지 이름형식은 동일").
/// 스티커 폴더(대시보드 설정 Dashboard:Stickers:Dir) 바로 아래 폴더 하나가 팩 하나 — 안의 파일 이름은 기본(폴더 바로 아래 파일)과 같다.
/// 팩에 빠진 그림은 기본 것으로 (FallbackDir). 링크(정션) 폴더 · 이름 모양이 아닌 폴더는 팩으로 보지 않는다.
/// 고르는 범위: 전체(설정 화면, ProfileStore) + 방별(곁칸 방 정보, Room.StickerPack) — 결정 "전체 + 방별". HB_Chat만 (대시보드는 기본 그대로).
/// </summary>
public sealed partial class StickerPacks(StickerCatalog stickers, ProfileStore profiles)
{
    public const int MaxPacks = 50;

    /// <summary>스티커 폴더 아래 팩들 (이름 차례). 폴더가 없으면 비어 있다.</summary>
    public IReadOnlyList<StickerPack> List()
    {
        if (BaseDir() is not { } root) return [];
        var packs = new List<StickerPack>();
        try
        {
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories().Take(MaxPacks * 2))
            {
                if (!IsPackName(dir.Name) || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                // 그림 수는 실제 찾기와 같은 규칙으로 — 팩 폴더에서 찾힌 태그만 (확장자 · 하위 경로 · 폴더 밖 검사 같음, ef2c83416c C7)
                var catalog = Catalog(dir.Name, dir.FullName);
                var files = stickers.Options.Files.Keys.Count(t => catalog.FileFor(t) is { } f && IsUnder(f.Path, dir.FullName));
                packs.Add(new StickerPack(dir.Name, DisplayName(dir.FullName) ?? dir.Name, files));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return [.. packs.OrderBy(p => p.Name, StringComparer.CurrentCulture).Take(MaxPacks)];
    }

    /// <summary>있는 팩인지 (이름 모양 · 폴더 · 링크 아님).</summary>
    public bool Exists(string? pack) => PackDir(pack) is not null;

    /// <summary>방에 쓸 팩: 방에 고른 것 → 전체 설정 → 기본(null). 없어진 팩은 기본으로.</summary>
    public string? Effective(Room? room) => Effective(room?.StickerPack);

    /// <summary>방에 고른 팩 → 전체 설정 → 기본(null). 고른 팩 폴더가 사라졌으면 다음 것으로.</summary>
    public string? Effective(string? roomPack) =>
        Exists(roomPack) ? roomPack : profiles.StickerPack() is { } global && Exists(global) ? global : null;

    /// <summary>
    /// 그 팩의 그림 찾기 — 팩 폴더 먼저, 없으면 기본 폴더. 그림 주소는 /sticker-pack/{팩}/{태그}. 팩이 없거나(null · 사라짐) 모르면 기본 그대로.
    /// </summary>
    public StickerCatalog For(string? pack) =>
        pack is not null && PackDir(pack) is { } dir ? Catalog(pack, dir) : stickers;

    public StickerCatalog ForRoom(Room? room) => For(Effective(room));

    /// <summary>
    /// 기본 그림 수 (설정 목록 중 기본 폴더에서 찾히는 태그 — 팩 줄의 "그림 n/전체"의 전체, ef2c83416c C7).
    /// </summary>
    public int Total() => stickers.Options.Files.Keys.Count(t => stickers.FileFor(t) is not null);

    /// <summary>팩을 안 골랐을 때 보이는 이름: 기본 팩 설정(Dashboard:Stickers:DefaultPack)이 있으면 그 이름.</summary>
    public string DefaultName() =>
        stickers.Options.DefaultPack?.Trim() is { Length: > 0 } pack ? $"기본 ({List().FirstOrDefault(p => p.Id == pack)?.Name ?? pack})" : "기본 (스티커 폴더 바로 아래)";

    /// <summary>팩 폴더는 만들 때 한 번만 확인한다 — Options를 읽을 때마다 폴더를 다시 보지 않게 (ef2c83416c C2).</summary>
    private StickerCatalog Catalog(string pack, string dir)
    {
        var url = "/sticker-pack/" + Uri.EscapeDataString(pack) + "/";
        return new StickerCatalog(() =>
        {
            var o = stickers.Options;
            // 팩 → 스티커 폴더 → 기본 팩 차례 (기본 팩은 스티커 폴더 = FallbackDir 아래)
            return new StickerOptions { Dir = dir, FallbackDir = o.Dir, DefaultPack = o.DefaultPack, SizePx = o.SizePx, Files = o.Files, Aliases = o.Aliases, Auto = o.Auto, UrlBase = url };
        });
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private string? BaseDir()
    {
        var dir = stickers.Options.Dir;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var full = Path.GetFullPath(dir.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>팩 폴더 (스티커 폴더 바로 아래, 이름 모양, 링크 아님). 아니면 null.</summary>
    private string? PackDir(string? pack)
    {
        if (!IsPackName(pack) || BaseDir() is not { } root) return null;
        var dir = new DirectoryInfo(Path.Combine(root, pack!));
        try
        {
            return dir.Exists && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && string.Equals(dir.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? dir.FullName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>pack.json의 name (40자, 제어 문자 없음). 없거나 읽지 못하면 null.</summary>
    private static string? DisplayName(string dir)
    {
        var file = Path.Combine(dir, "pack.json");
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 4096) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                   && n.GetString()!.Trim() is { Length: > 0 and <= 40 } name && !name.Any(char.IsControl) ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// 팩 이름 모양: 글자 · 숫자 · - _ . 공백 40자까지, 점 · 공백으로 시작하거나 끝나지 않음 (경로 · 주소에 들어간다 — Windows는 끝 점 · 공백을 지운다).
    /// <see cref="PetsFolder"/>는 팩이 아니다 (데스크톱 내장 펫 폴더 — 결정).
    /// </summary>
    public static bool IsPackName(string? name) =>
        name is not null && PackName().IsMatch(name) && !string.Equals(name, PetsFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>스티커 폴더 아래 데스크톱 펫 폴더 이름 (IconPack/Pets/&lt;펫&gt;/spritesheet.webp).</summary>
    public const string PetsFolder = "Pets";

    [GeneratedRegex(@"^[\p{L}\p{N}_\-][\p{L}\p{N}_\-. ]{0,39}(?<![. ])\z")]
    private static partial Regex PackName();
}
