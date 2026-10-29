using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>바탕화면 펫: 시트 규격 · 동작 · 따라갈 방 · 설정 · 자리 · 펫 찾기 · 내장 기본 펫 읽기.</summary>
public sealed class PetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-pet-" + Guid.NewGuid().ToString("N"));

    public PetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static RoomState R(string id, string activity, bool failed = false, string? face = null) => new(id, "방 " + id, activity, failed, 0, face);

    [Theory]
    [InlineData(1536, 2288, null, 11)] // CodePet 기본 펫 (v2, 192×208 칸)
    [InlineData(1536, 1872, null, 9)]  // v1
    [InlineData(800, 800, 2, 11)]      // 비율이 어느 것도 아니면 pet.json 판
    [InlineData(800, 800, null, 9)]
    public void Sheet_DetectsRowsByRatioThenVersion(int w, int h, int? version, int rows)
    {
        var sheet = PetSheet.Detect(w, h, version)!;
        Assert.Equal(rows, sheet.Rows);
        Assert.Equal(w / 8, sheet.CellWidth);
        Assert.Equal(new Rectangle(2 * sheet.CellWidth, 3 * sheet.CellHeight, sheet.CellWidth, sheet.CellHeight), sheet.Cell(3, 2));
        Assert.Null(PetSheet.Detect(0, 10, null));
    }

    [Fact]
    public void Actions_RowsFramesMatchCodePet_V2FrameCounts()
    {
        Assert.Equal(new PetActionInfo(0, 6, 3, false), PetActions.Info(PetAction.Idle, 9));
        Assert.Equal(new PetActionInfo(3, 4, 4, true), PetActions.Info(PetAction.Waving, 11));
        Assert.Equal(new PetActionInfo(5, 8, 4, true), PetActions.Info(PetAction.Failed, 9));
        Assert.Equal(8, PetActions.Info(PetAction.Running, 9).Frames);
        Assert.Equal(6, PetActions.Info(PetAction.Running, 11).Frames); // v2
        Assert.Equal(6, PetActions.Info(PetAction.Waiting, 11).Row);
    }

    [Fact]
    public void Mood_PicksUrgentRoom_SteadyAndBurst()
    {
        // 결정 "가장 급한 방 자동": 승인 → 실행 → 최근(목록 첫 줄)
        Assert.Equal("b", PetMood.Pick([R("a", "Busy"), R("b", "Approval"), R("c", "Idle")])!.Id);
        Assert.Equal("a", PetMood.Pick([R("c", "Idle"), R("a", "Busy")])!.Id);
        Assert.Equal("c", PetMood.Pick([R("c", "Idle"), R("d", "Idle")])!.Id);
        Assert.Null(PetMood.Pick([]));
        Assert.Equal(PetAction.Waiting, PetMood.Steady(R("a", "Approval", face: "Question")));
        Assert.Equal(PetAction.Running, PetMood.Steady(R("a", "Busy")));
        Assert.Equal(PetAction.Idle, PetMood.Steady(null));
        // 한 번 동작: 쉼 → 실행 점프, 실행 → 쉼 손 흔들기 · 실패면 실패, 다른 방 · 처음은 없음
        Assert.Equal(PetAction.Jumping, PetMood.Burst(R("a", "Idle"), R("a", "Busy")));
        Assert.Equal(PetAction.Waving, PetMood.Burst(R("a", "Approval"), R("a", "Idle")));
        Assert.Equal(PetAction.Failed, PetMood.Burst(R("a", "Busy"), R("a", "Idle", failed: true)));
        Assert.Null(PetMood.Burst(R("a", "Busy"), R("b", "Idle")));
        Assert.Null(PetMood.Burst(null, R("a", "Busy")));
        Assert.Null(PetMood.Burst(R("a", "Busy"), R("a", "Approval")));
    }

    [Fact]
    public void Bubble_PrefersApprovalAndCutsTitle()
    {
        Assert.Null(PetMood.Bubble([]));
        // 글씨 두 단계 (실제 확인 뒤): 상태 + 방 이름, 종류는 점 색
        Assert.Equal(new PetSaying(NoticeKind.Approval, "질문", "방 b"),
            PetMood.Bubble([new Notice(NoticeKind.Done, "a", "방 a"), new Notice(NoticeKind.Approval, "b", "방 b", Question: true)]));
        Assert.Equal(new PetSaying(NoticeKind.Failed, "실패", "방 a"), PetMood.Bubble([new Notice(NoticeKind.Failed, "a", "방 a")]));
        Assert.Equal("앞 답", PetMood.Bubble([new Notice(NoticeKind.Done, "a", "방 a", Preview: "  앞 답 ")])!.Preview); // 답 앞부분 (결정 "말풍선은 늘")
        Assert.EndsWith("…", PetMood.Bubble([new Notice(NoticeKind.Done, "a", new string('가', 50))])!.Room);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)] // 답 앞부분 (결정 "말풍선은 늘") — 두 줄까지
    public void BubblePaint_TransparentAroundRoundedBodyWithTail(bool dark, bool above, bool preview)
    {
        // 실제 확인 1 (A안): 투명 바탕 · 둥근 상자 · 펫 쪽 꼬리. HB_PET_DUMP 폴더가 있으면 그림을 남겨 눈으로 본다
        using var bold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var plain = new Font("Segoe UI", 9f);
        var saying = preview
            ? new PetSaying(NoticeKind.Done, "답 끝", "모델 선택창 확인", "설정 파일을 TOML로 바꾸는 설계는 가독성은 좋지만 런타임 파서가 하나 더 필요하고, 유니티 쪽은 JSON 도구가 이미 많아서 이득이 작다는 의견입니다.")
            : new PetSaying(NoticeKind.Approval, "승인 요청", "모델 선택창 확인 — A");
        var size = PetBubble.Measure(saying, bold, plain);
        using var image = PetBubble.Draw(saying, dark, above, size.Width / 2, size, bold, plain);
        if (Environment.GetEnvironmentVariable("HB_PET_DUMP") is { Length: > 0 } dump && Directory.Exists(dump))
            image.Save(Path.Combine(dump, $"bubble-{(dark ? "dark" : "light")}-{(above ? "above" : "below")}{(preview ? "-preview" : "")}.png"));
        Assert.Equal(0, image.GetPixel(0, 0).A);                        // 모서리 바깥은 투명
        Assert.Equal(255, image.GetPixel(size.Width / 2, above ? 20 : size.Height - 20).A); // 몸통은 불투명
        var tipY = above ? size.Height - 8 : 8;                           // 꼬리 끝 근처: 가운데는 칠해지고 옆은 비었다
        Assert.True(image.GetPixel(size.Width / 2, tipY).A > 200);
        Assert.True(image.GetPixel(size.Width / 2 - 30, tipY).A < 60); // 옆은 옅은 그림자뿐
    }

    [Fact]
    public void BubblePlace_AbovePetElseBelow_ClampedWithTailAtPet()
    {
        // 실제 확인 2: 끌기 중에도 펫 자리마다 다시 부른다 — 펫 위 가운데, 위로 넘치면 아래(꼬리 방향이 바뀜), 화면 안으로 자르고 꼬리는 펫 가운데
        var work = new Rectangle(0, 0, 1920, 1040);
        var size = new Size(200, 70);
        var mid = PetPlace.Bubble(new Rectangle(900, 500, 96, 104), size, work, 24);
        Assert.Equal((new Point(848, 430), true, 100), (mid.Location, mid.Above, mid.TailX));
        var top = PetPlace.Bubble(new Rectangle(900, 20, 96, 104), size, work, 24);
        Assert.Equal((124, false), (top.Location.Y, top.Above)); // 펫 아래
        var edge = PetPlace.Bubble(new Rectangle(1890, 500, 96, 104), size, work, 24);
        Assert.Equal(1720, edge.Location.X); // 오른쪽 끝에서 잘림
        Assert.Equal(176, edge.TailX);       // 펫 가운데(1938)가 밖이라 꼬리는 둥근 모서리 안쪽 끝
    }

    [Fact]
    public void Settings_DefaultsOnMissingOrBroken_RoundTrip()
    {
        var path = Path.Combine(_dir, "desktop-pet.json");
        Assert.Equal(new PetSettings(), PetSettings.Load(path)); // 결정 "기본 켬"
        Assert.True(PetSettings.Load(path).Visible);
        File.WriteAllText(path, "{ broken");
        Assert.Equal(new PetSettings(), PetSettings.Load(path));
        new PetSettings(false, 10, 20, PetSettings.Large, "codex:cat").Save(path);
        Assert.Equal(new PetSettings(false, 10, 20, PetSettings.Large, "codex:cat"), PetSettings.Load(path));
        File.WriteAllText(path, "{\"Size\":5000}");
        Assert.Equal(PetSettings.Medium, PetSettings.Load(path).Size); // 정해진 크기만
        Assert.Equal(new Size(96, 104), new PetSettings().WindowSize(sprite: true)); // 칸 비율 192:208
        Assert.Equal(new Size(96, 96), new PetSettings().WindowSize(sprite: false));
    }

    [Fact]
    public void Place_SavedOnScreenElseDefault_MiniBesidePet()
    {
        var screen = new Rectangle(0, 0, 1920, 1040);
        var size = new Size(96, 104);
        Assert.Equal(new Point(100, 200), PetPlace.Start(new PetSettings(X: 100, Y: 200), size, [screen], screen));
        var fallback = PetPlace.Start(new PetSettings(X: 5000, Y: 200), size, [screen], screen); // 모니터를 뗐다
        Assert.True(screen.Contains(new Rectangle(fallback, size)));

        var mini = new Size(380, 560);
        var right = PetPlace.MiniNear(new Rectangle(1800, 900, 96, 104), mini, screen); // 오른쪽 아래 펫 → 왼쪽에
        Assert.Equal(new Rectangle(1800 - 8 - 380, 1004 - 560, 380, 560), right);
        var left = PetPlace.MiniNear(new Rectangle(10, 900, 96, 104), mini, screen); // 왼쪽 끝 펫 → 오른쪽에
        Assert.Equal(10 + 96 + 8, left.X);
        Assert.True(screen.Contains(PetPlace.MiniNear(new Rectangle(900, 10, 96, 104), new Size(1900, 1000), screen))); // 어디도 안 맞으면 화면 안으로

        Assert.True(PetPlace.IsClick(new Point(10, 10), new Point(12, 13), new Size(4, 4)));
        Assert.False(PetPlace.IsClick(new Point(10, 10), new Point(20, 10), new Size(4, 4)));
    }

    [Fact]
    public void Sources_FindsBuiltinCodexAndDataPets_MetaOptional()
    {
        var builtin = Path.Combine(_dir, "builtin");
        var codex = Path.Combine(_dir, "codex");
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(builtin, "gpt_chan")); // 내장 펫 = exe 옆 Pets/<이름> (원본 IconPack/Pets)
        File.WriteAllText(Path.Combine(builtin, "gpt_chan", PetSources.SheetFile), "x");
        File.WriteAllText(Path.Combine(builtin, "gpt_chan", "pet.json"), "{\"displayName\":\"gpt_chan\",\"spriteVersionNumber\":2}");
        Directory.CreateDirectory(Path.Combine(codex, "cat"));
        File.WriteAllText(Path.Combine(codex, "cat", PetSources.SheetFile), "x");
        File.WriteAllText(Path.Combine(codex, "cat", "pet.json"), "{\"displayName\":\"고양이\",\"spriteVersionNumber\":2}");
        Directory.CreateDirectory(Path.Combine(codex, "nosheet")); // 시트 없는 폴더는 뺀다
        Directory.CreateDirectory(Path.Combine(data, "dog"));
        File.WriteAllText(Path.Combine(data, "dog", PetSources.SheetFile), "x");
        File.WriteAllText(Path.Combine(data, "dog", "pet.json"), "[1,2]"); // 깨진 메타는 폴더 이름

        var found = PetSources.Find(builtin, codex, data);
        Assert.Equal(["builtin:gpt_chan", "codex:cat", "data:dog"], found.Select(s => s.Key));
        Assert.Equal("gpt_chan (내장)", found[0].Label);
        Assert.Equal(("고양이 (Codex)", 2), (found[1].Label, found[1].Version));
        Assert.Equal(("dog", (int?)null), (found[2].Label, found[2].Version));
        Assert.Empty(PetSources.Find(Path.Combine(_dir, "none"), null, Path.Combine(_dir, "none2")));

        // 설정 키: 비었거나 옛 "builtin"이면 기본 내장 펫(gpt_chan), 고른 키 · 표정 그림은 그대로 (결정 "기본 펫 이름은 gpt_chan")
        Assert.Equal("builtin:gpt_chan", PetSources.Resolve(null, found));
        Assert.Equal("builtin:gpt_chan", PetSources.Resolve("builtin", found));
        Assert.Equal("codex:cat", PetSources.Resolve("codex:cat", found));
        Assert.Equal(PetSources.Faces, PetSources.Resolve(PetSources.Faces, found));
        Assert.Equal("builtin:gpt_chan", PetSources.Resolve(null, [])); // 목록이 비어도 키는 정해진다 (읽기에서 실패 → 표정 그림)
    }

    [Fact]
    public void Builtin_DefaultPetLoads_V2WithOccupiedIdleFrames()
    {
        // 내장 기본 펫 gpt_chan (CodePet MIT) — 빌드가 IconPack/Pets에서 출력 Pets/gpt_chan으로 복사한다. WIC WebP 디코더가 있는 Windows에서
        var path = Path.Combine(AppContext.BaseDirectory, "Pets", PetSources.DefaultPet, PetSources.SheetFile);
        Assert.True(File.Exists(path), path);
        using var sprite = PetSprite.Load(path, 2);
        Assert.Equal((1536, 2288, 11), (sprite.Sheet.Width, sprite.Sheet.Height, sprite.Sheet.Rows));
        Assert.NotEmpty(sprite.Frames(PetAction.Idle));
        Assert.All(sprite.Frames(PetAction.Running), c => Assert.InRange(c, 0, 5)); // v2 running 6프레임
        Assert.Equal(0, sprite.Image.GetPixel(0, 0).A); // 투명도가 살아 있다
    }

    [Fact]
    public void BrokenOrHugeSheet_ThrowsWhatLoadPetCatches()
    {
        // /self-review b280752387 C1 · 지휘자 리뷰 K1: 깨진 · 잘린 · 너무 큰 시트는 PetController.LoadPet이 잡는 예외로 — 놓치면 앱이 뜨지 않았다
        static bool Caught(Exception ex) => ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or FormatException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException;
        var builtin = Path.Combine(AppContext.BaseDirectory, "Pets", PetSources.DefaultPet, PetSources.SheetFile);
        var cut = Path.Combine(_dir, "cut.webp");
        File.WriteAllBytes(cut, File.ReadAllBytes(builtin)[..2000]);
        Assert.True(Caught(Record.Exception(() => PetSprite.Load(cut, null))!));
        var junk = Path.Combine(_dir, "junk.webp");
        File.WriteAllText(junk, "RIFF0000WEBPjunk");
        Assert.True(Caught(Record.Exception(() => PetSprite.Load(junk, null))!));
        var huge = Path.Combine(_dir, "huge.webp");
        using (var f = File.Create(huge)) f.SetLength(PetSprite.MaxFileBytes + 1); // 풀기 전에 파일 크기로 거절
        Assert.IsType<InvalidDataException>(Record.Exception(() => PetSprite.Load(huge, null)));
    }

    [Fact]
    public void Occupancy_SkipsEmptyCells()
    {
        var sheet = new PetSheet(16, 18, 9); // 칸 2×2, 9행
        var stride = 16 * 4;
        var pixels = new byte[stride * sheet.Height];
        pixels[(0 * stride) + (2 * 4) + 3] = 255; // 행 0 · 칸 1의 첫 점
        var occupied = PetSprite.Occupancy(pixels, stride, sheet);
        Assert.False(occupied[0, 0]);
        Assert.True(occupied[0, 1]);
    }
}
