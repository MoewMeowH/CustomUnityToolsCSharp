using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

/// <summary>채점기 판 2 (계획서 2026-10-22_채점기-구조-개선): 필수 키워드 · 양쪽 경계 · 위치 목록 · 최적 배정 · 중복 분류.</summary>
public sealed class GoldenScorerV2Tests
{
    private static GoldenItem Item(string id, string? file = "A.cs", int[]? lines = null, string[]? keywords = null, string[]? required = null,
        GoldenLocation[]? locations = null) =>
        new()
        {
            Id = id, Summary = $"{id} 요약", Files = file is null ? [] : [file], Lines = lines ?? [],
            Keywords = keywords ?? [], Required = required ?? [], Locations = locations ?? [],
        };

    private static GoldenFinding F(string id, string? file, int? line, string problem) => new(id, "major", file, line, problem, null);

    private static GoldenCase Case(params GoldenItem[] expected) =>
        new(new GoldenCaseInfo { Id = "c", Kind = "seeded" }, "", "입력", expected, []);

    // ── W5 양쪽 경계 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("NUL 같은 예약 장치", "NUL", true)]
    [InlineData("null을 넘기면", "NUL", false)]                          // 예전 규칙(왼쪽만)에서는 맞았다 — N-DEVICE 틀린 짝
    [InlineData("ArgumentOutOfRangeException 발생", "ArgumentOutOfRange", false)]
    [InlineData("ArgumentOutOfRangeException 발생", "ArgumentOutOfRange*", true)] // '*' = 앞부분 일치
    [InlineData("Assets/csc.rsp에", "rsp", true)]
    [InlineData("::$DATA", "$DATA", true)]
    [InlineData("loads 실패", "ADS", false)]
    [InlineData("대체데이터스트림", "데이터", true)]                      // 한국어는 부분 문자열
    [InlineData("255자를 넘으면", "255자", true)]
    [InlineData("12550번 줄", "255", false)]
    public void Terms_match_on_both_word_boundaries(string text, string keyword, bool expected) =>
        Assert.Equal(expected, GoldenScorer.ContainsTerm(text, keyword));

    // ── W1 필수 키워드 · 보조 ───────────────────────────────────────────

    [Fact]
    public void Required_keyword_decides_found_and_auxiliary_alone_stays_weak()
    {
        var item = Item("E1", "A.cs", [10, 12], keywords: ["재사용"], required: ["같은 이름 백업"]);

        Assert.Equal(3, GoldenScorer.Match(item, F("a", "A.cs", 11, "같은 이름 백업 폴더"))!.Value.Strength);
        Assert.Equal(2, GoldenScorer.Match(item, F("b", null, null, "같은 이름 백업 폴더"))!.Value.Strength);
        Assert.Equal(1, GoldenScorer.Match(item, F("c", "A.cs", 11, "폴더를 재사용"))!.Value.Strength);   // 위치 + 보조 = 강도 1
        Assert.Equal(1, GoldenScorer.Match(item, F("d", null, null, "폴더를 재사용"))!.Value.Strength);   // 보조만 = 강도 1
        Assert.Null(GoldenScorer.Match(item, F("e", "B.cs", 11, "같은 이름 백업")));                      // 다른 파일이면 키워드 짝 아님

        var s = GoldenScorer.Score(Case(item), [F("d", null, null, "폴더를 재사용")]);
        Assert.Empty(s.Found);
        Assert.Single(s.Weak);
    }

    [Fact]
    public void Without_required_keywords_act_as_required_like_before()
    {
        var item = Item("E1", "A.cs", [10, 12], keywords: ["재사용"]);
        Assert.Equal(2, GoldenScorer.Match(item, F("a", null, null, "폴더를 재사용"))!.Value.Strength);
    }

    // ── W4 위치 목록 ─────────────────────────────────────────────────────

    [Fact]
    public void Any_location_counts_as_near_and_old_format_still_reads()
    {
        var item = Item("E1", file: null, required: ["루트에만"], locations:
        [
            new GoldenLocation { File = "change.diff", From = 59, To = 68 },
            new GoldenLocation { File = "Options.cs", From = 225, To = 232 },
        ]);

        Assert.Equal(3, GoldenScorer.Match(item, F("a", "Options.cs", 230, "패턴이 루트에만 걸림"))!.Value.Strength);
        Assert.Equal(3, GoldenScorer.Match(item, F("b", "change.diff", 64, "패턴이 루트에만 걸림"))!.Value.Strength);
        Assert.Equal(2, GoldenScorer.Match(item, F("c", "change.diff", 150, "패턴이 루트에만 걸림"))!.Value.Strength);
        Assert.Null(GoldenScorer.Match(item, F("d", "Other.cs", 64, "패턴이 루트에만 걸림")));

        var old = Item("E2", "A.cs", [10, 20]);
        var loc = Assert.Single(old.AllLocations);
        Assert.Equal(("A.cs", 10, 20), (loc.File, loc.From, loc.To));
    }

    // ── W2 최적 배정 ─────────────────────────────────────────────────────

    [Fact]
    public void Assignment_maximizes_found_count_then_strength()
    {
        // A는 a(3)·b(2), B는 a(2)만. 찾음 2개가 되려면 A-b · B-a.
        var A = Item("A", "A.cs", [10, 10], required: ["알파"]);
        var B = Item("B", "A.cs", [50, 50], required: ["베타"]);
        var s = GoldenScorer.Score(Case(A, B),
        [
            F("a", "A.cs", 10, "알파 베타"),
            F("b", null, null, "알파"),
        ]);

        Assert.Equal(2, s.Found.Count);
        Assert.Equal("b", s.Found.Single(p => p.ItemId == "A").FindingId);
        Assert.Equal("a", s.Found.Single(p => p.ItemId == "B").FindingId);
    }

    [Fact]
    public void Same_strength_prefers_earlier_finding_and_stronger_pair_wins()
    {
        var A = Item("A", "A.cs", [10, 10], required: ["알파"]);
        var s = GoldenScorer.Score(Case(A), [F("x", null, null, "알파"), F("y", "A.cs", 10, "알파")]);
        Assert.Equal("y", Assert.Single(s.Found).FindingId); // 강도 3이 2보다 먼저

        var t = GoldenScorer.Score(Case(A), [F("x", null, null, "알파"), F("y", null, null, "알파")]);
        Assert.Equal("x", Assert.Single(t.Found).FindingId); // 같은 강도면 앞 지적
    }

    [Fact]
    public void Max_weight_assignment_handles_more_rows_than_columns()
    {
        var w = new long[3, 1];
        w[0, 0] = 5; w[1, 0] = 9; w[2, 0] = 0;
        var r = GoldenScorer.MaxWeightAssignment(w);
        Assert.Equal(0, r[1]);
        Assert.Null(r[0]);
    }

    // ── W7 중복 ───────────────────────────────────────────────────────────

    [Fact]
    public void Second_finding_on_found_item_is_duplicate_not_unmatched()
    {
        var A = Item("A", "A.cs", [10, 12], required: ["알파"]);
        var s = GoldenScorer.Score(Case(A),
        [
            F("a", "A.cs", 10, "알파 결함"),
            F("b", "A.cs", 12, "알파 결함 다시"),   // 필수 + 위치 → 중복
            F("c", null, null, "알파 결함 또"),     // 필수만(위치 없음) → 중복 아님, 짝 없음
        ]);

        Assert.Equal("a", Assert.Single(s.Found).FindingId);
        Assert.Equal("b", Assert.Single(s.Duplicates).FindingId);
        Assert.Equal(["c"], s.Unmatched);
    }
}
