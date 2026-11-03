using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>펫 걷기 (설정 "움직이기") · 바라보기 16방향 (v2 시트 9 · 10행) — 창 없이 순수 로직.</summary>
public sealed class PetMotionTests
{
    // ---- 바라보기 (CodePet sprite-layout.js와 같은 셈) ----

    [Theory]
    [InlineData(0, -10, 0)]   // 위
    [InlineData(10, -10, 2)]  // 오른쪽 위 (45°)
    [InlineData(10, 0, 4)]    // 오른쪽
    [InlineData(0, 10, 8)]    // 아래 (화면 y는 아래가 +)
    [InlineData(-10, 0, 12)]  // 왼쪽
    [InlineData(-10, -10, 14)]
    [InlineData(-1, -100, 0)] // 거의 위 — 0번으로 반올림 (15.x가 아니라)
    public void Direction_ClockwiseFromUp(double dx, double dy, int expected) => Assert.Equal(expected, PetLook.Direction(dx, dy));

    [Fact]
    public void Direction_ZeroOrNotFinite_IsNull()
    {
        Assert.Null(PetLook.Direction(0, 0));
        Assert.Null(PetLook.Direction(double.NaN, 1));
    }

    [Theory]
    [InlineData(0, 9, 0)]
    [InlineData(7, 9, 7)]
    [InlineData(8, 10, 0)]
    [InlineData(15, 10, 7)]
    [InlineData(16, 9, 0)]  // 둘레
    [InlineData(-1, 10, 7)]
    public void Cell_RowNinePlus(int direction, int row, int column) => Assert.Equal((row, column), PetLook.Cell(direction));

    [Fact]
    public void Playable_EmptyCellFallsToNearestDrawn()
    {
        static bool All(int r, int c) => true;
        Assert.Equal(5, PetLook.Playable(5, All));
        bool OnlyTwelve(int r, int c) => (r, c) == PetLook.Cell(12);
        Assert.Equal(12, PetLook.Playable(15, OnlyTwelve)); // 0 · 14 → 1 · 13 → 2 · 12
        bool TwoAndSix(int r, int c) => (r, c) == PetLook.Cell(2) || (r, c) == PetLook.Cell(6);
        Assert.Equal(6, PetLook.Playable(4, TwoAndSix)); // 같은 거리면 시계 방향 먼저
        Assert.Null(PetLook.Playable(3, (_, _) => false)); // v1 시트 · 빈 행
    }

    [Fact]
    public void At_OnlyOutsidePetWithinRange()
    {
        var pet = new Rectangle(1000, 500, 96, 104); // 가운데 (1048, 552)
        static bool All(int r, int c) => true;
        Assert.Null(PetLook.At(new Point(1050, 550), pet, All));                       // 펫 위
        Assert.Equal(4, PetLook.At(new Point(1048 + 200, 552), pet, All));             // 오른쪽 200px
        Assert.Equal(0, PetLook.At(new Point(1048, 552 - PetLook.Range), pet, All));   // 딱 범위
        Assert.Null(PetLook.At(new Point(1048, 552 - PetLook.Range - 1), pet, All));   // 범위 밖
    }

    // ---- 걷기 ----

    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        public void Add(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly Rectangle Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Target_InsideWorkArea_FarEnough()
    {
        var random = new Random(7);
        var pet = new Rectangle(900, 900, 96, 104);
        for (var i = 0; i < 500; i++)
        {
            var x = PetMotion.Target(pet, Work, random)!.Value;
            Assert.InRange(x, Work.Left, Work.Right - pet.Width);
            Assert.True(Math.Abs(x - pet.Left) >= PetMotion.MinStep, $"{x}");
        }
        Assert.Null(PetMotion.Target(pet, new Rectangle(880, 0, 200, 1040), random)); // 좁아서 120px 갈 곳이 없음
        Assert.Null(PetMotion.Target(pet, new Rectangle(0, 0, 50, 1040), random));    // 펫보다 좁음
    }

    [Fact]
    public void Walk_RestsThenWalksToTarget_ThenRestsAgain()
    {
        var clock = new Clock();
        var motion = new PetMotion(() => clock.Now, new Random(1));
        var pet = new Rectangle(900, 900, 100, 108);
        Assert.Equal(default, motion.Tick(true, pet, Work)); // 쉼 시작
        clock.Add(PetMotion.RestMin.TotalSeconds - 0.1);
        Assert.Equal(default, motion.Tick(true, pet, Work)); // 아직 8초 안
        Assert.False(motion.Walking);

        clock.Add(PetMotion.RestMax.TotalSeconds); // 쉼 끝 — 걷기 시작 (이 틱은 자리만 잡음)
        motion.Tick(true, pet, Work);
        Assert.True(motion.Walking);
        var facing = motion.Facing;
        Assert.True(facing is PetAction.RunRight or PetAction.RunLeft);

        // 0.05초 틱: 너비 100 × 0.6 = 60px/s → 3px씩
        clock.Add(0.05);
        var step = motion.Tick(true, pet, Work);
        Assert.Equal(pet.Left + (facing == PetAction.RunRight ? 3 : -3), step.X);
        Assert.False(step.Ended);

        // 도착할 때까지 (멀어도 1820px / 60px/s ≈ 31초) — 부른 쪽처럼 낸 자리로 옮기면서
        var last = step;
        for (var i = 0; i < 2000 && motion.Walking; i++)
        {
            clock.Add(0.05);
            if (last.X is { } x) pet.X = x;
            last = motion.Tick(true, pet, Work);
        }
        Assert.True(last.Ended);
        Assert.False(motion.Walking);
        Assert.Null(motion.Facing);
        Assert.Equal(default, motion.Tick(true, pet with { X = last.X!.Value }, Work)); // 다시 쉼
    }

    [Fact]
    public void Walk_StopsAtOnceWhenNotAllowed_AndRestartsRest()
    {
        var clock = new Clock();
        var motion = new PetMotion(() => clock.Now, new Random(2));
        var pet = new Rectangle(900, 900, 96, 104);
        motion.Tick(true, pet, Work);
        clock.Add(PetMotion.RestMax.TotalSeconds + 1);
        motion.Tick(true, pet, Work);
        Assert.True(motion.Walking);
        Assert.Equal(new PetStep(null, true), motion.Tick(false, pet, Work)); // 실행 · 승인 · 누름 → 바로 멈춤, 자리 저장
        Assert.False(motion.Walking);
        Assert.Equal(new PetStep(null, false), motion.Tick(false, pet, Work)); // 걷지 않았으면 저장할 것 없음
        motion.Tick(true, pet, Work);
        clock.Add(PetMotion.RestMin.TotalSeconds - 1);
        motion.Tick(true, pet, Work);
        Assert.False(motion.Walking); // 쉼 시간이 처음부터
    }

    /// <summary>걷는 중 바깥에서 창이 옮겨지면(크기 바꾸기 · 화면 구성 변경) 걷던 자리로 되돌아가지 않고 멈춘다 (088647d699 C1).</summary>
    [Fact]
    public void Walk_StopsWhenMovedFromOutside()
    {
        var clock = new Clock();
        var motion = new PetMotion(() => clock.Now, new Random(5));
        var pet = new Rectangle(900, 900, 100, 108);
        motion.Tick(true, pet, Work);
        clock.Add(PetMotion.RestMax.TotalSeconds + 1);
        motion.Tick(true, pet, Work);
        clock.Add(0.05);
        var step = motion.Tick(true, pet, Work);
        pet.X = step.X!.Value;
        clock.Add(0.05);
        Assert.False(motion.Tick(true, pet, Work).Ended); // 낸 자리 그대로면 계속 걷는다
        Assert.True(motion.Walking);
        clock.Add(0.05);
        Assert.Equal(new PetStep(null, true), motion.Tick(true, pet with { X = 1500 }, Work)); // 옮겨짐 → 멈추고 그 자리 저장
        Assert.False(motion.Walking);
    }

    [Fact]
    public void Walk_LongPauseDoesNotJump()
    {
        var clock = new Clock();
        var motion = new PetMotion(() => clock.Now, new Random(3));
        var pet = new Rectangle(900, 900, 100, 108);
        motion.Tick(true, pet, Work);
        clock.Add(PetMotion.RestMax.TotalSeconds + 1);
        motion.Tick(true, pet, Work);
        clock.Add(10); // 창이 10초 멈췄다 풀림
        var step = motion.Tick(true, pet, Work);
        Assert.InRange(Math.Abs(step.X!.Value - pet.Left), 1, 6); // 0.1초어치(6px)까지만
    }

    [Fact]
    public void Walk_TargetClampedWhenScreenShrinks()
    {
        var clock = new Clock();
        var motion = new PetMotion(() => clock.Now, new Random(4));
        var pet = new Rectangle(100, 900, 100, 108);
        motion.Tick(true, pet, Work);
        clock.Add(PetMotion.RestMax.TotalSeconds + 1);
        motion.Tick(true, pet, Work);
        var small = new Rectangle(0, 0, 400, 1040); // 해상도가 줄었다
        PetStep last = default;
        for (var i = 0; i < 2000 && motion.Walking; i++)
        {
            clock.Add(0.05);
            if (last.X is { } x) pet.X = x;
            last = motion.Tick(true, pet, small);
            if (last.X is { } nx) Assert.InRange(nx, small.Left, small.Right - pet.Width);
        }
        Assert.True(last.Ended);
    }

    [Fact]
    public void Settings_MoveDefaultsOff_AndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hbchat-petmove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "desktop-pet.json");
            File.WriteAllText(path, """{"Visible":true,"X":10,"Y":20,"Size":96,"Pet":"builtin:crow"}"""); // 옛 파일 (Move 없음)
            Assert.False(PetSettings.Load(path).Move);
            (PetSettings.Load(path) with { Move = true }).Save(path);
            Assert.True(PetSettings.Load(path).Move);
            Assert.Equal("builtin:crow", PetSettings.Load(path).Pet);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>내장 펫(빌드가 IconPack/Pets에서 출력 Pets/로 복사)은 걷기 두 행과 바라보기 16칸이 다 그려져 있다 — 걷기 · 바라보기가 실제로 돈다.</summary>
    [Fact]
    public void BuiltinPets_HaveRunRowsAndAllLookCells()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Pets");
        var sources = PetSources.Find(root, null, Path.Combine(root, "__none__"));
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            using var sprite = PetSprite.Load(source.Path, source.Version);
            Assert.Equal(PetSheet.V2Rows, sprite.Sheet.Rows);
            Assert.True(sprite.Has(PetAction.RunRight), source.Key);
            Assert.True(sprite.Has(PetAction.RunLeft), source.Key);
            for (var d = 0; d < PetLook.Directions; d++)
                Assert.Equal(d, PetLook.Playable(d, sprite.Occupied));
        }
    }

    /// <summary>끌기를 놓은 자리를 화면 안으로 (사용자 "2번 고치자"): 넘긴 쪽을 밀어 넣고, 화면 사이 옮기기는 그대로.</summary>
    [Fact]
    public void DragEnd_PushedInsideScreen()
    {
        Rectangle left = new(0, 0, 1920, 1040), right = new(1920, 0, 2560, 1400); // 두 화면 (작업 표시줄 빼고)
        var size = new Size(96, 104);
        Point At(int x, int y) => PetPlace.Inside(new Rectangle(new Point(x, y), size), [left, right]);
        Assert.Equal(new Point(500, 500), At(500, 500));               // 안이면 그대로
        Assert.Equal(new Point(0, 500), At(-60, 500));                 // 왼쪽 밖
        Assert.Equal(new Point(500, 1040 - 104), At(500, 1000));       // 작업 표시줄 위로
        Assert.Equal(new Point(0, 0), At(-500, -500));                 // 완전히 밖 — 가까운 화면 안
        Assert.Equal(new Point(4480 - 96, 300), At(5000, 300));        // 오른쪽 화면 밖 → 그 화면 안
        Assert.Equal(new Point(1920, 1200), At(1900, 1200));           // 두 화면에 걸침 — 더 많이 걸친(오른쪽, 왼쪽은 그 높이 없음) 쪽
        Assert.Equal(new Point(2000, 100), At(2000, 100));             // 다른 화면으로 옮기기는 그대로
        Assert.Equal(new Point(10, 10), PetPlace.Inside(new Rectangle(10, 10, 96, 104), []));
    }

    /// <summary>화면 설정이 바뀐 메시지면 펫 · 말풍선이 그림을 다시 올린다 (사진 앱을 열고 닫은 뒤 그림만 사라진 일, 2026-11-03).</summary>
    [Fact]
    public void DisplayMessages_TriggerRevive()
    {
        Assert.Equal("WM_DISPLAYCHANGE", Layered.DisplayMessage(0x007E));
        Assert.Equal("WM_DPICHANGED", Layered.DisplayMessage(0x02E0));
        Assert.Equal("WM_DWMCOMPOSITIONCHANGED", Layered.DisplayMessage(0x031E));
        Assert.Null(Layered.DisplayMessage(0x0200)); // 마우스 이동 등은 아님
    }

    [Fact]
    public void RunRows_AreRowsOneAndTwo()
    {
        Assert.Equal(new PetActionInfo(1, 8, 5, false), PetActions.Info(PetAction.RunRight, PetSheet.V2Rows));
        Assert.Equal(new PetActionInfo(2, 8, 5, false), PetActions.Info(PetAction.RunLeft, PetSheet.V1Rows));
    }
}
