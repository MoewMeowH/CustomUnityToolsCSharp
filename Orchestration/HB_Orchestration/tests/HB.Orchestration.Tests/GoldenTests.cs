using System.Text;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>골든 세트 (계획서 2026-10-19_골든세트-회귀평가): 채점 규칙 · 사례 형식 · CLI.</summary>
public sealed class GoldenTests
{
    // ── 채점 ─────────────────────────────────────────────────────────────

    private static GoldenItem Item(string id, string? file = "SafePath.cs", int[]? lines = null, params string[] keywords) =>
        new() { Id = id, Summary = $"{id} 요약", Files = file is null ? [] : [file], Lines = lines ?? [], Keywords = keywords };

    private static GoldenFinding F(string id, string? file, int? line, string problem, string? detail = null) =>
        new(id, "major", file, line, problem, detail);

    private static GoldenCase Case(IReadOnlyList<GoldenItem> expected, IReadOnlyList<GoldenItem>? traps = null) =>
        new(new GoldenCaseInfo { Id = "c", Kind = "seeded" }, "", "입력", expected, traps ?? []);

    [Fact]
    public void Same_file_near_line_with_keyword_is_strongest_and_ignores_path_prefix()
    {
        var c = Case([Item("E1", "SafePath.cs", [36, 40], "대체 데이터 스트림")]);
        var s = GoldenScorer.Score(c, [F("1-F1", "@orch/src/HB.Orchestration.Agents/SafePath.cs", 45, "':' 대체 데이터 스트림 우회")]);

        var pair = Assert.Single(s.Found);
        Assert.Equal(("E1", "1-F1", 3), (pair.ItemId, pair.FindingId, pair.Strength));
        Assert.Contains("±5", pair.Basis);
        Assert.Empty(s.Missed);
    }

    [Fact]
    public void Line_outside_tolerance_is_not_near()
    {
        var c = Case([Item("E1", "SafePath.cs", [36, 40])]);
        var s = GoldenScorer.Score(c, [F("1-F1", "SafePath.cs", 46, "다른 이야기")]);
        Assert.Empty(s.Found);
        Assert.Equal(["1-F1"], s.Unmatched);
    }

    [Fact]
    public void Keyword_matches_without_file_but_not_in_another_file()
    {
        var c = Case([Item("E1", "SafePath.cs", [10, 10], "장치 이름")]);

        Assert.Equal(2, Assert.Single(GoldenScorer.Score(c, [F("a", null, null, "Windows 장치 이름 통과")]).Found).Strength);
        Assert.Empty(GoldenScorer.Score(c, [F("b", "ResultSaver.cs", 10, "장치 이름")]).Found);
    }

    [Theory]
    [InlineData("threads와 loads", "ADS", false)]   // 영문 키워드는 단어 안에서 맞지 않는다
    [InlineData("NTFS ADS 우회", "ADS", true)]
    [InlineData("(ADS)로", "ADS", true)]
    [InlineData("Assets/csc.rsp에", "rsp", true)]
    [InlineData("::$DATA", "$DATA", true)]          // 키워드 앞이 기호면 그쪽은 경계를 안 본다
    [InlineData("대체데이터스트림", "데이터", true)] // 한국어는 부분 문자열
    [InlineData("ArgumentOutOfRangeException 발생", "ArgumentOutOfRange", true)] // 오른쪽은 보지 않는다 (식별자 앞부분)
    public void Ascii_keywords_match_on_word_boundaries(string text, string keyword, bool expected) =>
        Assert.Equal(expected, GoldenScorer.ContainsKeyword(text, keyword));

    [Fact]
    public void Keyword_is_case_insensitive_and_reads_detail()
    {
        var c = Case([Item("E1", null, null, "ADS")]);
        Assert.Single(GoldenScorer.Score(c, [F("a", "X.cs", 1, "우회", detail: "NTFS ads 로 읽힘")]).Found);
    }

    [Fact]
    public void Line_only_match_is_weak_and_stronger_candidate_wins()
    {
        var c = Case([Item("E1", "SafePath.cs", [36, 40], "대소문자")]);
        var s = GoldenScorer.Score(c, [F("weak", "SafePath.cs", 38, "다른 문제"), F("strong", "SafePath.cs", 39, "대소문자 무시 비교")]);

        var pair = Assert.Single(s.Found);
        Assert.Equal("strong", pair.FindingId);
        Assert.Equal(["weak"], s.Unmatched); // 한 기대 지적에 짝은 하나
    }

    [Fact]
    public void Matching_maximizes_pairs_when_greedy_would_lose_one()
    {
        // E1은 a(강도 3)·b(강도 2) 모두 되고 E2는 a(강도 2)만 된다. 탐욕이면 a→E1로 E2를 잃는다.
        var c = Case([Item("E1", "A.cs", [10, 10], "경로", "다른"), Item("E2", null, null, "루트")]);
        var s = GoldenScorer.Score(c, [F("a", "A.cs", 10, "경로 루트"), F("b", "A.cs", 30, "다른 것")]);

        Assert.Equal([("E1", "b"), ("E2", "a")], s.Found.Select(p => (p.ItemId, p.FindingId)));
        Assert.Empty(s.Missed);
    }

    [Fact]
    public void Free_finding_is_used_before_taking_another_items_pair()
    {
        // rec-ffd7 sol 실행: null 지적이 두 기대 지적의 키워드에 걸리고, 충돌 지적은 null 쪽 줄 범위 안이었다 → 서로 바뀌어 짝지어졌음.
        var c = Case([Item("null", "C.cs", [120, 128], "값이 null"), Item("case", "C.cs", [103, 107], "\"name\"", "대소문자 충돌")]);
        var s = GoldenScorer.Score(c, [
            F("1-F1", "C.cs", null, "Name 값이 null인 배열", detail: "{\"name\": null}"),
            F("1-F2", "C.cs", 127, "Name 키의 대소문자 충돌")]);

        Assert.Equal([("null", "1-F1"), ("case", "1-F2")], s.Found.Select(p => (p.ItemId, p.FindingId)));
    }

    [Fact]
    public void Line_only_pair_is_shown_but_not_counted()
    {
        var c = Case([Item("ADS", "SafePath.cs", [39, 48], "대체 데이터 스트림")]);
        var s = GoldenScorer.Score(c, [F("1-F4", "SafePath.cs", 35, "macOS 대소문자 구분")]);

        Assert.Empty(s.Found);
        Assert.Equal(["ADS"], s.Missed);
        Assert.Equal(("ADS", "1-F4", 1), (s.Weak[0].ItemId, s.Weak[0].FindingId, s.Weak[0].Strength));
        Assert.Empty(s.Unmatched);
    }

    [Fact]
    public void Trap_takes_a_finding_before_a_line_only_expected_pair()
    {
        // rec-1133: N-ADS(32-53)의 줄 범위 안에 있는 junction 지적은 함정 R2로 가야 한다.
        var c = Case([Item("N-ADS", "SafePath.cs", [32, 53], "대체 데이터 스트림")], traps: [Item("R2", "SafePath.cs", [45, 48], "junction")]);
        var s = GoldenScorer.Score(c, [F("P2", "SafePath.cs", 45, "루트 자체가 junction")]);

        Assert.Equal(("R2", "P2"), (Assert.Single(s.TrapHits).ItemId, s.TrapHits[0].FindingId));
        Assert.Empty(s.Weak);
        Assert.Equal(["N-ADS"], s.Missed);
    }

    [Fact]
    public void Line_only_trap_is_not_a_hit()
    {
        var c = Case([Item("E1", null, null, "x")], traps: [Item("T1", "A.cs", [10, 10], "junction")]);
        var s = GoldenScorer.Score(c, [F("f", "A.cs", 10, "다른 이야기")]);
        Assert.Empty(s.TrapHits);
        Assert.Equal(["f"], s.Unmatched);
    }

    [Fact]
    public void Line_only_candidate_does_not_take_a_finding_another_item_matches_by_keyword()
    {
        var c = Case([Item("A", "S.cs", [10, 20]), Item("B", null, null, "스트림")]);
        var s = GoldenScorer.Score(c, [F("x", "S.cs", 15, "NTFS 스트림")]);

        Assert.Equal(("B", "x"), (Assert.Single(s.Found).ItemId, s.Found[0].FindingId));
        Assert.Empty(s.Weak);
    }

    [Fact]
    public void One_finding_pairs_with_only_one_expected()
    {
        var c = Case([Item("E1", null, null, "경로"), Item("E2", null, null, "경로")]);
        var s = GoldenScorer.Score(c, [F("a", null, null, "경로 문제")]);
        Assert.Equal("E1", Assert.Single(s.Found).ItemId);
        Assert.Equal(["E2"], s.Missed);
    }

    [Fact]
    public void Traps_only_see_findings_left_after_expected_and_can_take_several()
    {
        var c = Case([Item("E1", null, null, "드라이브 루트")], traps: [Item("T1", null, null, "junction")]);
        var s = GoldenScorer.Score(c, [
            F("a", null, null, "드라이브 루트 junction"),   // 기대 지적에 먼저 쓰인다
            F("b", null, null, "루트 자체가 junction"),
            F("c", null, null, "또 junction"),
            F("d", null, null, "새 지적")]);

        Assert.Equal("a", Assert.Single(s.Found).FindingId);
        Assert.Equal(["b", "c"], s.TrapHits.Select(t => t.FindingId));
        Assert.Equal(["d"], s.Unmatched);
    }

    [Fact]
    public void No_findings_means_everything_missed()
    {
        var s = GoldenScorer.Score(Case([Item("E1", null, null, "x")]), []);
        Assert.Equal(0, s.Recall);
        Assert.Equal(["E1"], s.Missed);
    }

    [Theory]
    [InlineData("review_tmp/2-3/SafePath.cs", "SafePath.cs")]
    [InlineData(@"src\A\SafePath.cs:38", "SafePath.cs")]
    [InlineData("SafePath.cs:38-40", "SafePath.cs")]
    [InlineData("", null)]
    public void FileName_drops_folders_and_line(string path, string? expected) =>
        Assert.Equal(expected, GoldenScorer.FileName(path));

    // ── 지적 읽기 ─────────────────────────────────────────────────────────

    private const string SelfReview = """
        ## 지적
        - C1 [major] subagent_in/x/SafePath.cs:41-44 — ':' 대체 데이터 스트림으로 금지 패턴을 우회 / 거절 / 확신도(high)
          상세: .env::$DATA 로 주 스트림이 열린다
        - C2 [minor] ResultSaver.cs — 메시지가 모호 / 고침 / 확신도(low)
        - C3 [nit] (위치 없음) — 이름 / 바꿈 / 확신도(low)
        ## 자료 부족
        - C9 [major] X.cs:1 — 이 절은 읽지 않는다
        """;

    [Fact]
    public void Reads_self_reviewer_findings_only_in_findings_section()
    {
        var (findings, error) = GoldenScorer.ReadFindings(SelfReview);

        Assert.Null(error);
        Assert.Equal(["C1", "C2", "C3"], findings.Select(f => f.Id));
        Assert.Equal(("SafePath.cs", 41), (GoldenScorer.FileName(findings[0].File), findings[0].Line));
        Assert.Equal(".env::$DATA 로 주 스트림이 열린다", findings[0].Detail);
        Assert.Equal(("ResultSaver.cs", (int?)null), (findings[1].File, findings[1].Line));
        Assert.Null(findings[2].File);
    }

    [Fact]
    public void Self_review_problem_excludes_suggestion_and_handles_odd_lines_and_duplicate_ids()
    {
        var (findings, error) = GoldenScorer.ReadFindings("""
            ## 지적
            - C1 [major] A.cs:99999999999 — 문제 본문 / 제안에만 있는 키워드 / 확신도(high)
            - C1 [minor] B.cs:3 — 두 번째 C1 / 제안 / 확신도(low)
            """);

        Assert.Null(error);
        Assert.Equal(["C1", "C1#2"], findings.Select(f => f.Id));
        Assert.Equal("문제 본문", findings[0].Problem);
        Assert.Null(findings[0].Line); // int 범위 밖은 줄 없음
        Assert.Empty(GoldenScorer.Score(Case([Item("E", null, null, "제안에만")]), findings).Found);
    }

    [Fact]
    public void Reads_old_review_lines_without_ids()
    {
        var (findings, error) = GoldenScorer.ReadFindings("""
            - [major] ResultSaver.cs:61, SafePath.cs:45-53 — 예외가 새어 나간다 / 감싼다 / 확신도(high)
            - [minor] SafePath.cs:31,38 — 드라이브 루트 / 고친다 / 확신도(high)
            - [minor] Orchestration/HB_Orchestration/src/HB.Orchestration.Agents/OrchestrationOptions.cs:227 — 목록 / 더함 / 확신도(high)
            총 3건
            """);

        Assert.Null(error);
        Assert.Equal(["P1", "P2", "P3"], findings.Select(f => f.Id));
        Assert.Equal(("ResultSaver.cs", (int?)61), (findings[0].File, findings[0].Line));
        Assert.Equal(("SafePath.cs", (int?)31), (findings[1].File, findings[1].Line));
        Assert.Equal(("OrchestrationOptions.cs", (int?)227), (GoldenScorer.FileName(findings[2].File), findings[2].Line));
    }

    [Fact]
    public void Reads_structured_json_and_rejects_unknown_text()
    {
        var json = """{"findings":[{"severity":"major","file":"A.cs","line":3,"problem":"p","detail":null,"suggestion":null,"confidence":"high"}],"needsInfo":[]}""";
        var (findings, error) = GoldenScorer.ReadFindings(json);
        Assert.Null(error);
        Assert.Equal(("1-F1", "A.cs", (int?)3), (findings[0].Id, findings[0].File, findings[0].Line));

        // DeepSeek 앞부분 깨짐도 JSON으로 읽는다 (2026-10-20 8647dee8c2 · 4c125d11ce)
        Assert.Single(GoldenScorer.ReadFindings("hink>" + json).Findings);
        Assert.Single(GoldenScorer.ReadFindings(json[1..]).Findings);

        Assert.NotNull(GoldenScorer.ReadFindings("자유 텍스트 리뷰").Error);
        var none = GoldenScorer.ReadFindings("## 지적\n문제 없음\n");
        Assert.Null(none.Error);
        Assert.Empty(none.Findings);
    }

    // ── 사례 형식 ─────────────────────────────────────────────────────────

    private sealed class GoldenDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hb-golden-").FullName;

        public string Add(string id, string? caseJson = null, string? input = "## 요청\n봐 줘", string? expected = null, string? traps = null)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Root, id)).FullName;
            File.WriteAllText(Path.Combine(dir, "case.json"), caseJson ?? $$"""{ "id": "{{id}}", "kind": "seeded", "mode": "r" }""");
            if (input is not null) File.WriteAllText(Path.Combine(dir, "input.md"), input);
            File.WriteAllText(Path.Combine(dir, "expected.json"), expected ??
                """[ { "id": "E1", "summary": "콜론", "files": ["SafePath.cs"], "lines": [40, 42], "keywords": ["대체 데이터 스트림"] } ]""");
            if (traps is not null) File.WriteAllText(Path.Combine(dir, "traps.json"), traps);
            return dir;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Loads_all_cases_skipping_results_and_underscore_folders()
    {
        using var d = new GoldenDir();
        d.Add("b");
        d.Add("a", traps: """[ { "id": "T1", "summary": "링크", "keywords": ["junction"], "why": "설계" } ]""");
        Directory.CreateDirectory(Path.Combine(d.Root, "results"));
        Directory.CreateDirectory(Path.Combine(d.Root, "_보관"));

        var (cases, errors) = GoldenCaseLoader.LoadMany(d.Root, "all");

        Assert.Empty(errors);
        Assert.Equal(["a", "b"], cases.Select(c => c.Info.Id));
        Assert.Equal("설계", Assert.Single(cases[0].Traps).Why);
        Assert.Equal((40, 42), (cases[0].Expected[0].From, cases[0].Expected[0].To));
    }

    [Theory]
    [InlineData("""{ "id": "other", "kind": "seeded" }""", null, "폴더 이름")]
    [InlineData("""{ "id": "x", "kind": "guess" }""", null, "kind")]
    [InlineData(null, """[ { "id": "E1", "summary": "s" } ]""", "짝을 지을 수 없습니다")]
    [InlineData(null, """[ { "id": "E1", "summary": "s", "lines": [5] } ]""", "files가 없습니다")]
    [InlineData(null, """[ { "id": "E1", "summary": "s", "files": ["A.cs"], "lines": [9, 3] } ]""", "[시작, 끝]")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"] }, { "id": "e1", "keywords": ["k"] } ]""", "두 번")]
    [InlineData(null, "[]", "기대 지적이 없습니다")]
    [InlineData(null, "{ 깨짐", "JSON")]
    [InlineData(null, "[ null ]", "null입니다")]
    [InlineData(null, """[ { "id": "E1", "keywords": null, "lines": [1], "files": ["A.cs"] } ]""", "null일 수 없습니다")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "files": null } ]""", "null일 수 없습니다")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "required": null } ]""", "null일 수 없습니다")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "locations": [null] } ]""", "null일 수 없습니다")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "locations": [ { "from": 10 } ] } ]""", "locations는")]       // 파일 없는 위치는 채점에 안 쓰임
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "locations": [ { "file": "A.cs", "from": 0 } ] } ]""", "locations는")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "locations": [ { "file": "A.cs", "from": 10, "to": 5 } ] } ]""", "locations는")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "locations": [ { "file": "A.cs" } ] } ]""", "locations는")]
    [InlineData(null, """[ { "id": "E1", "keywords": ["k"], "required": [""] } ]""", "빈 말")]                        // 필수가 있는 척 → 찾음 불가
    [InlineData(null, """[ { "id": "E1", "keywords": [" NUL"] } ]""", "앞뒤 공백")]                                   // 공백이 경계 검사를 없앰
    [InlineData("""{ "id": "x", "kind": "seeded", "mode": null }""", null, "mode가 비어")]
    public void Rejects_bad_case_files(string? caseJson, string? expected, string message)
    {
        using var d = new GoldenDir();
        var dir = d.Add("x", caseJson, expected: expected);
        var (c, error) = GoldenCaseLoader.Load(dir);
        Assert.Null(c);
        Assert.Contains(message, error);
    }

    [Theory]
    [InlineData("""[ { "id": "E1", "summary": "s", "required": ["k"] } ]""")]
    [InlineData("""[ { "id": "E1", "summary": "s", "locations": [ { "file": "A.cs", "from": 3 } ] } ]""")]
    public void Required_only_or_locations_only_items_load(string expected)
    {
        using var d = new GoldenDir();
        var (c, error) = GoldenCaseLoader.Load(d.Add("x", null, expected: expected));
        Assert.Null(error);
        Assert.NotNull(c);
    }

    [Fact]
    public void Set_key_hash_covers_traps_and_ignores_case_order()
    {
        using var d = new GoldenDir();
        d.Add("b");
        var aDir = d.Add("a", traps: """[ { "id": "T1", "keywords": ["junction"] } ]""");
        var (cases, _) = GoldenCaseLoader.LoadMany(d.Root, "all");
        var before = GoldenCaseLoader.SetKeyHash(cases);
        Assert.Equal(16, before.Length);
        Assert.Equal(before, GoldenCaseLoader.SetKeyHash(Enumerable.Reverse(cases)));

        File.WriteAllText(Path.Combine(aDir, "traps.json"), """[ { "id": "T1", "keywords": ["junction*"] } ]""");
        var (changed, _) = GoldenCaseLoader.LoadMany(d.Root, "all");
        Assert.NotEqual(before, GoldenCaseLoader.SetKeyHash(changed)); // 함정만 바꿔도 묶음 해시가 바뀐다
    }

    [Theory]
    [InlineData("../x")]
    [InlineData(@"a\b")]
    [InlineData("C:x")]
    [InlineData("..")]
    public void Case_name_cannot_leave_the_golden_folder(string which)
    {
        using var d = new GoldenDir();
        var (cases, errors) = GoldenCaseLoader.LoadMany(d.Root, which);
        Assert.Empty(cases);
        Assert.Contains("폴더 이름 하나", Assert.Single(errors));
    }

    [Fact]
    public void Key_hash_changes_when_expected_changes()
    {
        using var d = new GoldenDir();
        var dir = d.Add("a");
        var before = GoldenCaseLoader.Load(dir).Case!.KeySha256;
        File.WriteAllText(Path.Combine(dir, "expected.json"), """[ { "id": "E1", "summary": "s", "keywords": ["다른"] } ]""");
        Assert.NotEqual(before, GoldenCaseLoader.Load(dir).Case!.KeySha256);
    }

    [Fact]
    public void Missing_input_is_rejected()
    {
        using var d = new GoldenDir();
        Assert.Contains("input.md", GoldenCaseLoader.Load(d.Add("x", input: null)).Error);
    }

    // ── CLI ──────────────────────────────────────────────────────────────

    private const string Modes = """
        {
          "Roles": { "Rev": { "Prompt": "역할", "Output": "findings" }, "Free": { "Prompt": "역할" } },
          "Modes": {
            "r":    { "RequireOwnReview": true, "MaxRunsPerSession": 1, "Steps": [ { "Role": "Rev", "Agent": "a1" } ] },
            "free": { "Steps": [ { "Role": "Free", "Agent": "a1" } ] },
            "two":  { "Steps": [ { "Role": "Rev", "Agent": "a1" }, { "Role": "Rev", "Agent": "a1" } ] }
          }
        }
        """;

    private const string Answer = """{"findings":[{"severity":"major","file":"review_tmp/SafePath.cs","line":41,"problem":"대체 데이터 스트림 우회","detail":null,"suggestion":null,"confidence":"high"},{"severity":"minor","file":"B.cs","line":1,"problem":"새것","detail":null,"suggestion":null,"confidence":"low"}],"needsInfo":[]}""";

    private static async Task<(int Code, string Out, string Err)> Cli(ModeRunnerHarness h, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await GoldenCli.RunAsync(["--golden", .. args], h.Options, () => h.Runner, h.LogPath, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Run_without_yes_shows_plan_and_calls_nothing()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");

        var (code, output, _) = await Cli(h, "run", "--dir", d.Root);

        Assert.Equal(0, code);
        Assert.Contains("a (seeded) · r: Rev@a1(m)", output);
        Assert.Contains("--yes", output);
        Assert.Equal(0, h.Client.Calls);
        Assert.False(Directory.Exists(Path.Combine(d.Root, "results")));
    }

    [Fact]
    public async Task Plan_shows_which_guidelines_file_is_attached()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");

        var (code, output, _) = await Cli(h, "run", "--dir", d.Root);

        Assert.Equal(0, code);
        Assert.Contains($"지침 붙음: {Path.GetFullPath(h.GuidelinesPath)}", output);
    }

    /// <summary>지침 파일을 못 찾은 로더. 기본 로더는 환경 변수·현재 폴더까지 찾아 PC 환경에 따라 결과가 달라지므로 바꿔 끼운다.</summary>
    private static readonly Func<OrchestrationOptions, GuidelinesLoader.Result> NoFile = _ => new GuidelinesLoader.Result("", null, null, null);

    private static GuidelinesLoader.Result FileWith(string text) =>
        new(GuidelinesLoader.Header + text, @"C:\proj\REVIEW_GUIDELINES.md", Encoding.UTF8.GetBytes(text), BlobStore.Sha256Hex(Encoding.UTF8.GetBytes(text)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_guidelines_shows_plan_then_refuses_without_calls(bool yes)
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Runner.LoadGuidelines = NoFile;
        using var d = new GoldenDir();
        d.Add("a");
        h.Client.Script = [ScriptedChatClient.Reply(Answer)];

        var (code, output, err) = await Cli(h, yes ? ["run", "--dir", d.Root, "--yes"] : ["run", "--dir", d.Root]);

        Assert.Equal(2, code);
        Assert.Contains("[골든 실행 계획]", output);                       // 미리보기·실행 모두 계획은 먼저 보임
        Assert.Contains($"지침 없음 ({GuidelinesProvenance.ReasonNoFile})", output);
        Assert.Contains("[리뷰 지침 없음]", err);
        Assert.Contains(Environment.CurrentDirectory, err);                // 어디서 찾았는지
        Assert.Contains("--no-guidelines", err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Specified_guidelines_path_missing_refuses_instead_of_falling_back()
    {
        // 2026-10-22: 지정 경로 오타면 예전 서버는 현재 폴더 REVIEW_GUIDELINES.md로 넘어갔다 — 이제 서버가 오류, 골든은 미리보기에서 거절.
        using var h = new ModeRunnerHarness(Modes, guidelines: null!);  // Options.GuidelinesPath는 있지만 파일은 없음 (실제 GuidelinesLoader)
        using var d = new GoldenDir();
        d.Add("a");
        h.Client.Script = [ScriptedChatClient.Reply(Answer)];

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(2, code);
        Assert.Contains("[리뷰 지침 경로 없음]", err);
        Assert.Contains(h.GuidelinesPath, err);
        Assert.Equal(0, h.Client.Calls);

        var (flagCode, _, _) = await Cli(h, "run", "--dir", d.Root, "--no-guidelines", "--yes", "--label", "ng");
        Assert.Equal(0, flagCode); // 일부러 지침 없이 비교하는 경우는 막지 않음
    }

    [Fact]
    public async Task No_guidelines_flag_strips_guidelines_even_when_the_file_exists()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        h.Client.Script = [ScriptedChatClient.Reply(Answer)];

        var (code, output, err) = await Cli(h, "run", "--dir", d.Root, "--no-guidelines", "--yes", "--label", "ng");

        Assert.True(code == 0, err);
        Assert.Contains($"지침 없음 ({GuidelinesProvenance.ReasonGoldenSkipped})", output);
        Assert.DoesNotContain("한글 규칙", ScriptedChatClient.SystemText(h.Client.Received[0])); // 파일이 있어도 안 붙음
        Assert.Equal(GuidelinesProvenance.ReasonGoldenSkipped, h.Records()[0].Provenance!.Guidelines!.Reason);

        var line = File.ReadAllLines(Directory.GetFiles(Path.Combine(d.Root, "results"), "*_ng.jsonl").Single()).Single();
        Assert.Contains("\"guidelinesAttached\":false", line);
        Assert.Contains($"\"guidelinesReason\":\"{GuidelinesProvenance.ReasonGoldenSkipped}\"", line);
    }

    [Fact]
    public async Task Result_line_records_attached_guidelines_hash()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        h.Client.Script = [ScriptedChatClient.Reply(Answer)];

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes", "--label", "g");

        Assert.True(code == 0, err);
        var line = File.ReadAllLines(Directory.GetFiles(Path.Combine(d.Root, "results"), "*_g.jsonl").Single()).Single();
        Assert.Contains("\"guidelinesAttached\":true", line);
        Assert.Contains($"\"guidelinesSha256\":\"{BlobStore.Sha256Hex(File.ReadAllBytes(h.GuidelinesPath))}\"", line);
    }

    [Fact]
    public async Task Guidelines_changed_after_the_check_stops_before_calling()
    {
        using var h = new ModeRunnerHarness(Modes);
        var loads = 0;
        // 확인 2번(사례 a·b) + a 실행 1번은 같은 파일, b 실행 직전에 내용이 바뀜
        h.Runner.LoadGuidelines = _ => ++loads <= 3 ? FileWith("- 처음 규칙\n") : FileWith("- 바뀐 규칙\n");
        using var d = new GoldenDir();
        d.Add("a");
        d.Add("b");
        h.Client.Script = [ScriptedChatClient.Reply(Answer), ScriptedChatClient.Reply(Answer)];

        var (code, output, err) = await Cli(h, "run", "all", "--dir", d.Root, "--yes", "--label", "chg");

        Assert.Equal(1, code);
        Assert.Contains("[a] 찾음", output);
        Assert.Contains($"[b] 실행 실패: {ModeRunner.GuidelinesChanged}", err);
        Assert.Equal(1, h.Client.Calls); // b는 호출하지 않음
    }

    [Theory]
    [InlineData("\n## 첨부 코드\n코드\n\n## 요청\n봐 줘", "\n## 첨부 코드\n코드\n\n## 요청\n")] // 첨부가 앞인 형식
    [InlineData("## 요청\n목적\n\n## 첨부 코드\n코드\n```", "## 요청\n목적\n\n## 첨부 코드\n코드\n")] // 요청이 앞인 예전 기록
    [InlineData("한 줄\n\n", null)]                                                            // 끝 빈 줄은 마지막 줄로 안 셈
    [InlineData("한 줄", null)]
    public void Repeat_cache_prefix_is_everything_before_the_last_line(string prompt, string? expected) =>
        Assert.Equal(expected, ModeRunner.RepeatCachePrefix(prompt));

    [Fact]
    public async Task Golden_run_puts_cache_point_before_last_line_when_provider_uses_context_cache()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Options.Providers["fake"].PromptCache = PromptCacheMode.Context;
        using var d = new GoldenDir();
        var attachment = "\n## 첨부 코드 (서버가 디스크에서 읽음, 줄 번호 포함)\n\n### a.cs:1-1\n" + new string('x', StepCall.MinContextCacheChars) + "\n";
        d.Add("a", input: attachment + "\n## 요청\n봐 줘");
        h.Client.Script = [ScriptedChatClient.Reply(Answer)];

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.True(code == 0, err);
        var texts = h.Client.Received.Single().Messages.Single(m => m.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Contents.OfType<Microsoft.Extensions.AI.TextContent>().ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(attachment + "\n## 요청\n", texts[0].Text);
        Assert.Equal("봐 줘", texts[1].Text);
        Assert.Equal(attachment + "\n## 요청\n봐 줘", texts[0].Text + texts[1].Text); // 이어 붙이면 원문 그대로
    }

    [Fact]
    public async Task Score_rejects_no_guidelines_flag()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");

        var (code, _, err) = await Cli(h, "score", "a", "--from", "x", "--no-guidelines", "--dir", d.Root);

        Assert.Equal(2, code);
        Assert.Contains("--no-guidelines", err);
    }

    [Fact]
    public async Task Run_with_yes_scores_records_golden_tool_and_ignores_session_limit()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        d.Add("b");
        h.Client.Script = [ScriptedChatClient.Reply(Answer), ScriptedChatClient.Reply(Answer)];

        var (code, output, err) = await Cli(h, "run", "all", "--dir", d.Root, "--yes", "--label", "t");

        Assert.True(code == 0, err);
        Assert.Contains("[a] 찾음 1/1", output);
        Assert.Contains("[합계] 찾음 2/2", output);
        Assert.Contains("짝 없음 1-F2", output);

        // ownReview 필수·실행 한도 1회인 모드여도 두 사례 모두 돈다 (세션 한도와 무관)
        var records = h.Records();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(("golden", "r"), (r.Tool, r.Mode)));
        Assert.Equal("## 요청\n봐 줘", records[0].Prompt); // 원문 그대로 (요청 머리말을 다시 붙이지 않음)
        Assert.Equal("golden a", records[0].Reason);

        var lines = File.ReadAllLines(Path.Combine(d.Root, "results", $"{DateTime.Now:yyyy-MM-dd}_t.jsonl"));
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, doc.RootElement.GetProperty("found").GetInt32());
        Assert.Equal(records[0].RunId, doc.RootElement.GetProperty("runId").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("inputSha256").GetString()!.Length);
        Assert.True(doc.RootElement.TryGetProperty("provenance", out _));
    }

    [Fact]
    public async Task Bad_case_rejects_everything_before_any_call()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        d.Add("b", expected: "[]");

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(2, code);
        Assert.Contains("[사례 형식 오류] b:", err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Theory]
    [InlineData("two", "단계가 하나인 모드만")]
    [InlineData("nope", "알 수 없는 모드")]
    public async Task Unsupported_mode_is_rejected_before_any_call(string mode, string message)
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--mode", mode, "--yes");

        Assert.Equal(2, code);
        Assert.Contains(message, err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Unknown_agent_override_is_rejected_before_any_call()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--agents", "a1=zz", "--yes");

        Assert.Equal(2, code);
        Assert.Contains("zz", err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Role_without_structured_output_is_rejected_before_any_call()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a", caseJson: """{ "id": "a", "kind": "seeded", "mode": "free" }""");

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(2, code);
        Assert.Contains("구조화 출력(Output)이 없어", err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Unparseable_answer_is_a_failed_case_not_zero_found()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        h.Client.Script = [ScriptedChatClient.Reply("자유 텍스트")];

        var (code, output, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(1, code);
        Assert.Contains("구조화 출력이 없어 채점하지 못했습니다", err);
        Assert.DoesNotContain("찾음 0/1", output);
        Assert.Contains("구조화 출력 없음", File.ReadAllText(Path.Combine(d.Root, "results", $"{DateTime.Now:yyyy-MM-dd}_a1.jsonl")));
    }

    [Fact]
    public async Task Input_over_mode_limit_is_rejected_before_any_call()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a", input: new string('x', 50));
        h.Options.Budget.MaxInputChars = 10;

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(2, code);
        Assert.Contains("입력이 너무 깁니다", err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Cancel_keeps_results_of_finished_cases()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        d.Add("b");
        using var cts = new CancellationTokenSource();
        // 첫 사례 응답 뒤 취소: 두 번째 사례는 돌지 않고, 첫 사례 결과는 파일에 남는다.
        h.Client.Script = [_ => { cts.Cancel(); return ScriptedChatClient.Reply(Answer)(CancellationToken.None); }, ScriptedChatClient.Reply(Answer)];

        var o = new StringWriter();
        var e = new StringWriter();
        var code = await GoldenCli.RunAsync(["--golden", "run", "--dir", d.Root, "--yes", "--label", "c"], h.Options, () => h.Runner, h.LogPath, o, e, cts.Token);

        Assert.Equal(1, code);
        Assert.Contains("[취소]", e.ToString());
        Assert.Equal(1, h.Client.Calls);
        var line = Assert.Single(File.ReadAllLines(Path.Combine(d.Root, "results", $"{DateTime.Now:yyyy-MM-dd}_c.jsonl")));
        Assert.Contains("\"caseId\":\"a\"", line);
        Assert.Contains("\"keySha256\"", line);
    }

    [Fact]
    public async Task Score_reads_self_reviewer_file_without_calling_agents()
    {
        using var h = new ModeRunnerHarness(Modes);
        using var d = new GoldenDir();
        d.Add("a");
        var from = Path.Combine(d.Root, "self.md");
        File.WriteAllText(from, SelfReview);

        var (code, output, err) = await Cli(h, "score", "a", "--from", from, "--dir", d.Root, "--label", "self");

        Assert.True(code == 0, err);
        Assert.Contains("[a] 찾음 1/1", output);
        Assert.Equal(0, h.Client.Calls);
        var line = Assert.Single(File.ReadAllLines(Path.Combine(d.Root, "results", $"{DateTime.Now:yyyy-MM-dd}_self.jsonl")));
        Assert.Contains("\"source\":\"file:self.md\"", line);
    }

    [Theory]
    [InlineData("score", "a")]                       // --from 없음
    [InlineData("score", "a", "--from", "x", "--yes")]
    [InlineData("run", "--mode")]                    // 값 없음
    [InlineData("run", "--bogus")]
    [InlineData("run", "a", "b")]
    [InlineData("run", "--label", "a/b")]
    [InlineData("list")]
    public async Task Usage_errors_exit_2(params string[] args)
    {
        using var h = new ModeRunnerHarness(Modes);
        var (code, _, err) = await Cli(h, args);
        Assert.Equal(2, code);
        Assert.Contains("사용법", err);
    }

    [Fact]
    public async Task Missing_folder_setting_is_explained()
    {
        using var h = new ModeRunnerHarness(Modes);
        var (code, _, err) = await Cli(h, "run");
        Assert.Equal(2, code);
        Assert.Contains("Golden:Folder", err);
    }
}
