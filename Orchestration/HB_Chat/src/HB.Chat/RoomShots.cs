using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// 방 기록 스크린샷 파일 (데이터 폴더 browser-shots/&lt;방&gt;/) — 지휘자 브라우저 스크린샷과 Unity capture_game_view 그림이 같이 쓴다
/// (계획서 2026-10-29_HB_Chat-방별-MCP-Unity-연결 결정 3). 폴더 이름은 먼저 만든 브라우저 것 그대로 (이미 남은 그림 · 휴지통 정리와 맞게).
/// 기록 줄(<see cref="RoomNotes"/>)의 요약 칸에 파일 이름을 적고, 화면은 ?handler=Shot으로 받는다.
/// </summary>
public static partial class RoomShots
{
    /// <summary>방마다 남기는 그림 수 상한 — 넘으면 오래된 것부터 지운다.</summary>
    public const int MaxPerRoom = 200;

    /// <summary>그림 하나 상한 (바이트) — 더 크면 남기지 않는다.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    public static string Folder(string dataFolder, string roomId) => Path.Combine(dataFolder, "browser-shots", roomId);

    /// <summary>그림을 남기고 이름을 준다. 같은 밀리초면 뒤에 번호. ext는 png · jpg.</summary>
    public static string Save(string dataFolder, string roomId, byte[] image, string ext = "png")
    {
        if (!RoomStore.IsRoomId(roomId)) throw new InvalidOperationException("방 ID 모양이 아닙니다.");
        if (ext is not ("png" or "jpg")) throw new ArgumentException($"그림 형식이 아닙니다: {ext}");
        var dir = Folder(dataFolder, roomId);
        Directory.CreateDirectory(dir);
        var stem = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        var name = $"{stem}.{ext}";
        // 새 파일로만 연다 — 브라우저 · Unity 그림이 같은 밀리초에 써도 서로 덮지 않게 (/self-review 0bcf01451b C4)
        for (var i = 1; ; i++)
        {
            try
            {
                using var file = new FileStream(Path.Combine(dir, name), FileMode.CreateNew, FileAccess.Write);
                file.Write(image);
                break;
            }
            catch (IOException) when (i < 1000 && File.Exists(Path.Combine(dir, name)))
            {
                name = $"{stem}-{i}.{ext}";
            }
        }
        foreach (var old in Directory.GetFiles(dir).Where(f => Name().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal).SkipLast(MaxPerRoom))
        {
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return name;
    }

    /// <summary>그림 파일 경로 — 이름 모양이 맞고 그 방 폴더에 있을 때만.</summary>
    public static string? PathOf(string dataFolder, string roomId, string? name)
    {
        if (!RoomStore.IsRoomId(roomId) || name is null || !Name().IsMatch(name)) return null;
        var path = Path.Combine(Folder(dataFolder, roomId), name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>응답 형식 (이름 끝으로).</summary>
    public static string ContentType(string name) => name.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : "image/png";

    /// <summary>MCP 그림 형식 → 확장자. 모르는 형식은 null (남기지 않는다).</summary>
    public static string? Ext(string? mediaType) => mediaType switch
    {
        "image/png" => "png",
        "image/jpeg" or "image/jpg" => "jpg",
        _ => null,
    };

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}(-\d{1,4})?\.(png|jpg)$")]
    private static partial Regex Name();
}
