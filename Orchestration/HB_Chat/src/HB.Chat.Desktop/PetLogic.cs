using System.Text.Json;

namespace HB.Chat.Desktop;

/// <summary>
/// 스프라이트 시트 규격 (CodePet · Codex 펫 형식, 계획서 2026-10-29_바탕화면-펫 결정): 가로 8칸, v1은 9행 · v2는 11행, 칸 비율 가로 192 : 세로 208.
/// pet.json의 spriteVersionNumber가 없으면 그림 비율로 가른다 (CodePet sprite-layout.js detectSpriteRows와 같은 0.08행 오차).
/// </summary>
public sealed record PetSheet(int Width, int Height, int Rows)
{
    public const int Columns = 8, V1Rows = 9, V2Rows = 11;
    public const double CellAspect = 208.0 / 192.0;

    public int CellWidth => Width / Columns;
    public int CellHeight => Height / Rows;

    public Rectangle Cell(int row, int column) => new(column * CellWidth, row * CellHeight, CellWidth, CellHeight);

    /// <summary>그림 크기로 규격을 정한다. 비율이 v1 · v2 어느 것도 아니면 version(2면 v2, 그 밖 v1). 크기가 0이면 null.</summary>
    public static PetSheet? Detect(int width, int height, int? version)
    {
        if (width <= 0 || height <= 0) return null;
        var rows = height / (width / (double)Columns * CellAspect);
        var detected = new[] { V1Rows, V2Rows }.FirstOrDefault(r => Math.Abs(rows - r) <= 0.08);
        return new PetSheet(width, height, detected != 0 ? detected : version == 2 ? V2Rows : V1Rows);
    }
}

/// <summary>펫 동작 = 시트 한 행 (CodePet renderer.js PET_STATES의 제자리 동작만 — 달리기 · 바라보기는 다음 단계).</summary>
public enum PetAction { Idle, Waving, Jumping, Failed, Waiting, Running, Review }

/// <param name="Once">한 번 돌고 <see cref="PetActions.After"/>로 돌아가는 동작.</param>
public readonly record struct PetActionInfo(int Row, int Frames, int Fps, bool Once);

public static class PetActions
{
    /// <summary>행 · 프레임 · fps (v1 값). v2는 waiting · running · review가 6프레임 (CodePet V2_STATE_FRAMES).</summary>
    public static PetActionInfo Info(PetAction action, int rows)
    {
        var v2 = rows == PetSheet.V2Rows;
        return action switch
        {
            PetAction.Waving => new(3, 4, 4, true),
            PetAction.Jumping => new(4, 5, 5, true),
            PetAction.Failed => new(5, 8, 4, true),
            PetAction.Waiting => new(6, v2 ? 6 : 8, 3, false),
            PetAction.Running => new(7, v2 ? 6 : 8, 4, false),
            PetAction.Review => new(8, v2 ? 6 : 8, 3, false),
            _ => new(0, 6, 3, false),
        };
    }
}

/// <summary>
/// 펫이 따라갈 방 · 동작 (결정 "가장 급한 방 자동"): 승인 · 질문 기다림 → 실행 중 → 가장 최근 방(목록 첫 줄 — 서버가 최근 활동 순으로 준다).
/// </summary>
public static class PetMood
{
    public static RoomState? Pick(IReadOnlyList<RoomState> rooms) =>
        rooms.FirstOrDefault(r => r.IsApproval) ?? rooms.FirstOrDefault(r => r.IsBusy) ?? rooms.FirstOrDefault();

    /// <summary>계속 도는 동작: 승인 · 질문 → 기다림, 실행 중 → 일하는 중, 그 밖 · 방 없음 → 쉼.</summary>
    public static PetAction Steady(RoomState? room) =>
        room is null ? PetAction.Idle : room.IsApproval ? PetAction.Waiting : room.IsBusy ? PetAction.Running : PetAction.Idle;

    /// <summary>
    /// 한 번 동작 (계획서 결정 표): 같은 방이 쉼 → 실행이면 점프, 실행 · 기다림 → 쉼이면 실패면 실패 동작 · 아니면 손 흔들기. 그 밖 · 처음 목록은 null.
    /// 서버 목록은 끝난 턴의 잠깐 표정(끝남 · 실패)을 싣지 않아 앞 · 뒤 상태로 정한다.
    /// </summary>
    public static PetAction? Burst(RoomState? before, RoomState? after)
    {
        if (before is null || after is null || before.Id != after.Id) return null;
        if (before.IsIdle && !after.IsIdle) return PetAction.Jumping;
        if (!before.IsIdle && after.IsIdle) return after.Failed ? PetAction.Failed : PetAction.Waving;
        return null;
    }

    /// <summary>
    /// 말풍선 글 (결정 "짧게 띄움", 실제 확인 뒤 "글씨 두 단계"): 알림 중 승인 · 질문을 먼저, 없으면 첫 알림 — 상태(굵게) + 방 이름(흐리게). 알림이 없으면 null.
    /// </summary>
    public static PetSaying? Bubble(IReadOnlyList<Notice> notices)
    {
        if ((notices.FirstOrDefault(n => n.Kind == NoticeKind.Approval) ?? notices.FirstOrDefault()) is not { } n) return null;
        var status = n.Kind switch
        {
            NoticeKind.Approval => n.Question ? "질문" : "승인 요청",
            NoticeKind.Failed => "실패",
            _ => "답 끝",
        };
        return new(n.Kind, status, n.Title.Length > 40 ? n.Title[..40] + "…" : n.Title, string.IsNullOrWhiteSpace(n.Preview) ? null : n.Preview.Trim());
    }
}

/// <summary>
/// 말풍선 한 번: 알림 종류(점 색) · 상태 글(굵게) · 방 이름(흐리게, 빈 글이면 한 줄) · 답 앞부분(답 끝 · 실패에만, 결정 "말풍선은 늘" — 두 줄까지).
/// </summary>
public sealed record PetSaying(NoticeKind Kind, string Status, string Room, string? Preview = null);

/// <summary>말풍선 자리: 창 왼쪽 위 · 펫 위에 떴는지(꼬리가 아래를 가리킴) · 꼬리 끝의 가로 위치(말풍선 안 x).</summary>
public readonly record struct BubblePlace(Point Location, bool Above, int TailX);

/// <summary>
/// 펫 설정 (데이터 폴더 desktop-pet.json): 보이기 · 위치 · 크기(가로 px) · 고른 펫. 읽지 못하면 기본값 (결정 "기본 켬").
/// Pet: <see cref="PetSources.Faces"/>(방 표정 그림), "builtin", "codex:이름", "data:이름". null이면 기본 펫.
/// </summary>
public sealed record PetSettings(bool Visible = true, int? X = null, int? Y = null, int Size = PetSettings.Medium, string? Pet = null)
{
    public const int Small = 72, Medium = 96, Large = 128;

    public static PetSettings Load(string path)
    {
        try
        {
            var loaded = File.Exists(path) ? JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(path)) : null;
            return loaded is null ? new() : loaded with { Size = loaded.Size is Small or Medium or Large ? loaded.Size : Medium };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 못 남겨도 다음에 기본 위치일 뿐
    }

    /// <summary>스프라이트 펫의 창 크기 (칸 비율을 지킨다). 표정 그림은 정사각형.</summary>
    public Size WindowSize(bool sprite) => sprite ? new(Size, (int)Math.Round(Size * PetSheet.CellAspect)) : new(Size, Size);
}

/// <summary>펫 창 · 미니 창 자리.</summary>
public static class PetPlace
{
    /// <summary>
    /// 펫 위치: 저장한 자리가 보이는 화면 안이면 그대로, 아니면 기본 화면 오른쪽 아래(미니 창 기본 자리 위).
    /// </summary>
    public static Point Start(PetSettings settings, Size size, IReadOnlyList<Rectangle> screens, Rectangle primary)
    {
        if (settings is { X: { } x, Y: { } y } && screens.Any(s => s.Contains(new Rectangle(x, y, size.Width, size.Height))))
            return new(x, y);
        return new(primary.Right - size.Width - 24, primary.Bottom - size.Height - MiniForm.StartSize.Height - 40);
    }

    /// <summary>
    /// 펫을 눌렀을 때 미니 창 자리 (결정 "펫 옆에"): 펫 왼쪽 → 오른쪽 → 위 → 아래 중 그 화면 안에 다 들어가는 첫 자리, 없으면 화면 안으로 민다.
    /// </summary>
    public static Rectangle MiniNear(Rectangle pet, Size mini, Rectangle work)
    {
        const int gap = 8;
        Rectangle[] tries =
        [
            new(pet.Left - gap - mini.Width, pet.Bottom - mini.Height, mini.Width, mini.Height),
            new(pet.Right + gap, pet.Bottom - mini.Height, mini.Width, mini.Height),
            new(pet.Right - mini.Width, pet.Top - gap - mini.Height, mini.Width, mini.Height),
            new(pet.Right - mini.Width, pet.Bottom + gap, mini.Width, mini.Height),
        ];
        foreach (var r in tries)
            if (work.Contains(r)) return r;
        var first = tries[0];
        return new(Math.Clamp(first.X, work.Left, Math.Max(work.Left, work.Right - mini.Width)),
            Math.Clamp(first.Y, work.Top, Math.Max(work.Top, work.Bottom - mini.Height)), mini.Width, mini.Height);
    }

    /// <summary>
    /// 말풍선 자리 (실제 확인 뒤 — 끌기 중에도 펫이 움직일 때마다 다시 부른다): 펫 위 가운데, 화면 위로 넘치면 펫 아래.
    /// 가로는 그 화면 안으로 자르고, 꼬리는 펫 가운데를 가리키되 둥근 모서리(margin) 안에 둔다.
    /// </summary>
    /// <param name="size">말풍선 창 크기 (꼬리 · 그림자 자리 포함).</param>
    public static BubblePlace Bubble(Rectangle pet, Size size, Rectangle work, int margin)
    {
        var x = Math.Clamp(pet.Left + (pet.Width - size.Width) / 2, work.Left, Math.Max(work.Left, work.Right - size.Width));
        var above = pet.Top - size.Height >= work.Top;
        var y = above ? pet.Top - size.Height : Math.Min(pet.Bottom, Math.Max(work.Top, work.Bottom - size.Height));
        var tail = Math.Clamp(pet.Left + pet.Width / 2 - x, margin, Math.Max(margin, size.Width - margin));
        return new(new Point(x, y), above, tail);
    }

    /// <summary>누른 뒤 이만큼 안에서 떼면 클릭, 넘게 움직이면 끌기.</summary>
    public static bool IsClick(Point down, Point up, Size dragSize) =>
        Math.Abs(up.X - down.X) <= dragSize.Width && Math.Abs(up.Y - down.Y) <= dragSize.Height;
}

/// <summary>고를 수 있는 펫 하나 (Path는 spritesheet.webp).</summary>
public sealed record PetSource(string Key, string Label, string Path, int? Version);

/// <summary>
/// 펫 찾기 (결정): 내장 펫(exe 옆 Pets/&lt;이름&gt; — 원본 대시보드 IconPack/Pets) · Codex가 깐 ~/.codex/pets/* · 데이터 폴더 pets/*.
/// 폴더마다 spritesheet.webp가 있어야 하고, pet.json(displayName · spriteVersionNumber)은 없어도 된다 (CodePet listAvailablePets와 같음).
/// </summary>
public static class PetSources
{
    /// <summary>방 표정 그림(스티커 팩)을 쓰는 펫 키.</summary>
    public const string Faces = "faces";
    /// <summary>내장 펫 키 앞부분 ("builtin:이름"). 이것만 있는 옛 설정 키는 기본 내장 펫으로 읽는다.</summary>
    public const string Builtin = "builtin";
    /// <summary>기본 펫 폴더 이름 (사용자 결정 "기본 펫 이름은 gpt_chan").</summary>
    public const string DefaultPet = "gpt_chan";
    public const string SheetFile = "spritesheet.webp";

    public static IReadOnlyList<PetSource> Find(string builtinRoot, string? codexPetsDir, string dataPetsDir)
    {
        var list = new List<PetSource>();
        list.AddRange(Folder(builtinRoot, Builtin, " (내장)"));
        list.AddRange(Folder(codexPetsDir, "codex", " (Codex)"));
        list.AddRange(Folder(dataPetsDir, "data", ""));
        return list;
    }

    /// <summary>
    /// 설정 키를 실제로 읽을 펫 키로: 비었거나 옛 "builtin"이면 기본 내장 펫(gpt_chan, 없으면 첫 내장 펫), 그 밖은 그대로. 표정 그림 키도 그대로.
    /// </summary>
    public static string Resolve(string? key, IReadOnlyList<PetSource> sources)
    {
        if (key is { Length: > 0 } && key != Builtin) return key;
        var builtin = sources.Where(s => s.Key.StartsWith(Builtin + ":", StringComparison.Ordinal)).ToList();
        return (builtin.FirstOrDefault(s => s.Key == $"{Builtin}:{DefaultPet}") ?? builtin.FirstOrDefault())?.Key ?? $"{Builtin}:{DefaultPet}";
    }

    private static IEnumerable<PetSource> Folder(string? root, string prefix, string suffix)
    {
        string[] dirs;
        try { dirs = root is not null && Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dirs = []; }
        foreach (var dir in dirs.Order(StringComparer.OrdinalIgnoreCase))
        {
            var sheet = Path.Combine(dir, SheetFile);
            if (!File.Exists(sheet)) continue;
            var name = Path.GetFileName(dir);
            var (label, version) = Meta(Path.Combine(dir, "pet.json"));
            yield return new($"{prefix}:{name}", (label ?? name) + suffix, sheet, version);
        }
    }

    /// <summary>pet.json의 displayName · spriteVersionNumber. 없거나 깨졌으면 (null, null).</summary>
    internal static (string? Label, int? Version) Meta(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            var label = root.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } s
                ? (s.Length > 40 ? s[..40] : s) : null;
            int? version = root.TryGetProperty("spriteVersionNumber", out var v) && v.TryGetInt32(out var n) ? n : null;
            return (label, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }
}
