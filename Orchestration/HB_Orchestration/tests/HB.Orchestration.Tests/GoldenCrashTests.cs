using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Tests;

/// <summary>골든 crash 사례 (계획서 2026-10-19_골든-crash사례): 사례 형식 · 2단계 채점 · 다단계 실행.</summary>
public sealed class GoldenCrashTests
{
    private const string CrashCase = """{ "id": "c1", "kind": "crash", "mode": "c" }""";

    private const string Expected = """
        [
          { "id": "T-CAUSE", "step": "triage", "summary": "전역 참조 넘침", "keywords": ["global reference", "전역 참조"] },
          { "id": "S-ISSUE", "step": "search", "summary": "공식 이슈", "keywords": ["15–20분"], "urls": ["issuetracker.unity.com/issues/8932"] },
          { "id": "S-DOC", "step": "search", "summary": "문서", "keywords": ["DumpReferenceTables"] }
        ]
        """;

    private sealed class Dir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hb-golden-crash-").FullName;

        public string Add(string id, string caseJson = CrashCase, string expected = Expected, string? traps = null, string input = "Android 크래시 로그")
        {
            var dir = Directory.CreateDirectory(Path.Combine(Root, id)).FullName;
            File.WriteAllText(Path.Combine(dir, "case.json"), caseJson);
            File.WriteAllText(Path.Combine(dir, "input.md"), input);
            File.WriteAllText(Path.Combine(dir, "expected.json"), expected);
            if (traps is not null) File.WriteAllText(Path.Combine(dir, "traps.json"), traps);
            return dir;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static GoldenCase Load(string? traps = null)
    {
        using var d = new Dir();
        var (c, error) = GoldenCaseLoader.Load(d.Add("c1", traps: traps));
        Assert.True(c is not null, error);
        return c!;
    }

    // ── 사례 형식 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""[ { "id": "A", "step": "guess", "keywords": ["k"] } ]""", "triage 또는 search")]
    [InlineData("""[ { "id": "A", "keywords": ["k"] } ]""", "triage 또는 search")]                       // 기대 항목은 step 필수
    [InlineData("""[ { "id": "A", "step": "triage", "urls": ["x"] } ]""", "keywords")]                    // 분류는 키워드로만
    [InlineData("""[ { "id": "A", "step": "search" } ]""", "urls나 keywords")]
    [InlineData("""[ { "id": "A", "step": "triage", "keywords": ["k"], "files": ["a.cs"] } ]""", "files·lines")]
    [InlineData("""[ { "id": "A", "step": "search", "urls": null } ]""", "null일 수 없습니다")]
    [InlineData("""[ { "id": "A", "step": "search", "urls": ["", "issues/1"] } ]""", "빈 조각")]
    public void Rejects_bad_crash_items(string expected, string message)
    {
        using var d = new Dir();
        var (c, error) = GoldenCaseLoader.Load(d.Add("c1", expected: expected));
        Assert.Null(c);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Crash_trap_may_omit_step()
    {
        var c = Load(traps: """[ { "id": "X", "keywords": ["메모리 부족"], "why": "OOM이 아니라 참조 테이블" } ]""");
        Assert.True(c.Info.IsCrash);
        Assert.Null(Assert.Single(c.Traps).Step);
    }

    // ── 채점 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Search_item_is_found_only_by_a_url_the_search_step_actually_saw()
    {
        var c = Load();
        var search = new WebSearchRecord { Calls = 4, Sources = ["https://issuetracker.unity.com/issues/8932?utm_source=x"], Citations = ["https://docs.unity.com/a"] };

        var s = GoldenCrashScorer.Score(c, "JNI global reference table overflow", "15–20분 뒤 크래시. DumpReferenceTables로 확인", search);

        Assert.Equal(["T-CAUSE", "S-ISSUE", "S-DOC"], s.Found.Select(p => p.ItemId));
        Assert.Equal(3, s.Found.Single(p => p.ItemId == "S-ISSUE").Strength);
        Assert.Contains("검색 결과에만 있던 URL", s.Found.Single(p => p.ItemId == "S-ISSUE").Basis); // 인용은 안 함
        Assert.Equal((4, 2), (s.SearchCalls, s.SourceCount));
        Assert.Empty(s.Missed);
    }

    [Fact]
    public void Cited_url_is_reported_separately_from_listed_one()
    {
        var c = Load();
        var search = new WebSearchRecord { Sources = ["https://issuetracker.unity.com/issues/8932"], Citations = ["https://issuetracker.unity.com/issues/8932"] };

        var s = GoldenCrashScorer.Score(c, null, null, search);

        Assert.Contains("인용·열람한 URL", s.Found.Single(p => p.ItemId == "S-ISSUE").Basis);
        Assert.Equal(1, s.SourceCount); // 같은 URL은 한 번
    }

    [Theory]
    [InlineData("https://issuetracker.unity.com/issues/8932", true)]
    [InlineData("https://issuetracker.unity.com/issues/8932/crash-when", true)]
    [InlineData("https://issuetracker.unity.com/issues/8932?utm_source=x", true)]
    [InlineData("https://issuetracker.unity.com/issues/89321", false)] // 번호 앞부분만 같은 다른 이슈
    [InlineData("https://issuetracker.unity.com/issues/8932x", false)]
    public void Url_fragment_needs_a_boundary_after_it(string url, bool match) =>
        Assert.Equal(match, GoldenCrashScorer.ContainsUrlFragment(url, "issuetracker.unity.com/issues/8932"));

    [Fact]
    public void Search_item_with_urls_matched_only_by_keyword_needs_checking()
    {
        var c = Load();

        var s = GoldenCrashScorer.Score(c, "원인 불명", "15–20분 뒤 크래시가 난다는 보고", new WebSearchRecord { Sources = ["https://example.com/other"] });

        Assert.DoesNotContain(s.Found, p => p.ItemId == "S-ISSUE");
        Assert.Contains("정답 URL 없음", Assert.Single(s.KeywordOnly).Basis);
        Assert.Equal(["T-CAUSE", "S-ISSUE", "S-DOC"], s.Missed); // 확인 필요도 놓침에 든다 (리뷰와 같게 찾음 + 놓침 = 기대 수)
    }

    [Fact]
    public void Url_seen_only_in_unverified_list_is_not_found()
    {
        var c = Load();
        var search = new WebSearchRecord { Unverified = ["https://issuetracker.unity.com/issues/8932"] };

        var s = GoldenCrashScorer.Score(c, null, "https://issuetracker.unity.com/issues/8932 참고", search);

        Assert.Contains("검색 결과에 없는 URL에만", Assert.Single(s.KeywordOnly).Basis);
        Assert.Single(s.Unverified);
    }

    [Theory]
    [InlineData("원인", "1. 메모리 부족 — 가능성 높음", GoldenItem.StepSearch)]     // step 없는 함정은 두 단계 모두 본다
    [InlineData("메모리 부족 (확인됨)", "다른 원인", GoldenItem.StepTriage)]
    [InlineData("메모리 부족은 아님 (낮음)", "메모리 부족 가능성은\n높음 — 다른 줄", null)] // 확신 표기와 같은 줄일 때만
    public void Trap_hits_only_when_stated_with_confidence(string triage, string search, string? step)
    {
        var c = Load(traps: """[ { "id": "X", "keywords": ["메모리 부족"] } ]""");

        var s = GoldenCrashScorer.Score(c, triage, search, null);

        Assert.Equal(step, s.TrapHits.SingleOrDefault()?.FindingId);
    }

    // ── 다단계 실행 ──────────────────────────────────────────────────────

    private const string Modes = """
        {
          "Roles": {
            "Tri": { "Prompt": "분류자" },
            "Sea": { "Prompt": "검색자", "UseGuidelines": false },
            "Rev": { "Prompt": "역할", "Output": "findings" }
          },
          "Modes": {
            "c": { "RequireOwnReview": true, "MaxRunsPerSession": 1,
                   "Steps": [ { "Role": "Tri", "Agent": "a1" }, { "Role": "Sea", "Agent": "a1", "Sees": "InputAndPrevious", "WebSearch": { "MaxCalls": 3 } } ] },
            "nosearch": { "Steps": [ { "Role": "Tri", "Agent": "a1" }, { "Role": "Sea", "Agent": "a1", "Sees": "InputAndPrevious" } ] },
            "one": { "Steps": [ { "Role": "Tri", "Agent": "a1" } ] },
            "con": { "Flow": "Concurrent", "Steps": [ { "Role": "Tri", "Agent": "a1" }, { "Role": "Sea", "Agent": "a1" } ] }
          }
        }
        """;

    /// <summary>웹검색은 제공사 Type이 openai-responses여야 계획을 통과한다 (요청은 가짜 클라이언트가 받는다).</summary>
    private static ModeRunnerHarness NewHarness() => new(Modes, providerType: OpenAIResponsesProvider.TypeName);

    /// <summary>검색 단계 응답: 검색 1회 · 결과 URL · 인용 URL을 MEAI 내용으로 (WebSearchExtractor가 호출 기록 WebSearch로 옮긴다).</summary>
    private static Func<CancellationToken, ChatResponse> SearchReply(string text, string resultUrl, string citedUrl) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new WebSearchToolCallContent("ws1") { Queries = ["JNI global reference overflow unity"] },
            new WebSearchToolResultContent("ws1") { Outputs = [new UriContent(new Uri(resultUrl), "text/html")] },
            new TextContent(text) { Annotations = [new CitationAnnotation { Url = new Uri(citedUrl) }] },
        ]))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static async Task<(int Code, string Out, string Err)> Cli(ModeRunnerHarness h, params string[] args) => await CliWithLog(h, h.LogPath, args);

    private static async Task<(int Code, string Out, string Err)> CliWithLog(ModeRunnerHarness h, string logPath, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await GoldenCli.RunAsync(["--golden", .. args], h.Options, () => h.Runner, logPath, o, e, CancellationToken.None);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Crash_case_runs_whole_mode_and_scores_search_from_the_call_record()
    {
        using var h = NewHarness();
        using var d = new Dir();
        d.Add("c1");
        h.Client.Script =
        [
            ScriptedChatClient.Reply("1. 의심: JNI 전역 참조 테이블 넘침"),
            SearchReply("DumpReferenceTables로 확인하라는 문서", "https://issuetracker.unity.com/issues/8932/crash-when-the-app-runs", "https://docs.unity.com/a"),
        ];

        var (code, output, err) = await Cli(h, "run", "--dir", d.Root, "--yes", "--label", "cr");

        Assert.True(code == 0, err);
        Assert.Contains("c1 (crash) · c: Tri@a1(m) [지침 붙음", output);
        Assert.Contains("→ Sea@a1(m) [지침 없음 (역할 UseGuidelines=false)]", output);
        Assert.Contains("[c1] 찾음 3/3", output);                                  // 분류 키워드 + 검색 결과 URL + 문서 키워드
        Assert.Contains("찾음 S-ISSUE [search] (검색 결과에만 있던 URL https://issuetracker.unity.com/issues/8932", output);
        Assert.Contains("[crash 합계] 분류 찾음 1/1 · 검색 찾음 2/2", output);
        Assert.DoesNotContain("줄만 맞은 짝", output);                              // 리뷰 사례가 없으면 리뷰 안내 없음
        Assert.EndsWith("_cr.jsonl", output.TrimEnd().Split('\n')[^1].Trim());      // 결과 경로는 맨 끝

        // 세션 한도 1회·ownReview 필수 모드여도 돌고, 두 단계 모두 golden으로 기록. 2단계는 1단계 결과를 받는다 (Sees).
        var records = h.Records();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(("golden", "c"), (r.Tool, r.Mode)));
        Assert.Equal("## 요청\nAndroid 크래시 로그", records[0].Prompt.ReplaceLineEndings("\n")); // run_mode처럼 서버가 감쌈
        Assert.Contains("JNI 전역 참조 테이블 넘침", records[1].Prompt);

        var line = File.ReadAllLines(Directory.GetFiles(Path.Combine(d.Root, "results"), "*_cr.jsonl").Single()).Single();
        Assert.Contains("\"kind\":\"crash\"", line);
        Assert.Contains("\"agent\":\"a1 → a1\"", line);
        Assert.Contains("\"searchCalls\":1", line);
        Assert.Contains("\"searchSources\":2", line);
    }

    [Theory]
    [InlineData("nosearch", "WebSearch")]   // 마지막 단계에 검색이 없음
    [InlineData("one", "2개 이상")]          // 단계 하나 (예: case.json에 mode를 빠뜨려 review)
    [InlineData("con", "Sequential 모드만")]
    public async Task Crash_case_rejects_mode_that_cannot_be_scored_before_calling(string mode, string message)
    {
        using var h = NewHarness();
        using var d = new Dir();
        d.Add("c1", caseJson: $$"""{ "id": "c1", "kind": "crash", "mode": "{{mode}}" }""");

        var (code, _, err) = await Cli(h, "run", "--dir", d.Root, "--yes");

        Assert.Equal(2, code);
        Assert.Contains(message, err);
        Assert.Equal(0, h.Client.Calls);
    }

    [Fact]
    public async Task Missing_search_record_is_a_scoring_failure_not_zero()
    {
        using var h = NewHarness();
        using var d = new Dir();
        d.Add("c1");
        h.Client.Script = [ScriptedChatClient.Reply("원인"), SearchReply("결과", "https://a.example/x", "https://a.example/y")];
        var elsewhere = Path.Combine(d.Root, "다른-기록.jsonl"); // 실제 기록과 다른 파일을 읽게 해 기록을 못 찾는 경우를 만든다

        var (code, output, err) = await CliWithLog(h, elsewhere, "run", "--dir", d.Root, "--yes", "--label", "m");

        Assert.Equal(1, code);
        Assert.Contains("[c1] 채점 실패: 검색 단계 호출 기록을 찾지 못함", err);
        Assert.DoesNotContain("[crash 합계]", output);
        Assert.Contains("\"error\":\"검색 단계 호출 기록을 찾지 못함\"", File.ReadAllText(Directory.GetFiles(Path.Combine(d.Root, "results"), "*_m.jsonl").Single()));
    }

    [Fact]
    public async Task Failed_step_saves_error_line_and_does_not_score()
    {
        using var h = NewHarness();
        using var d = new Dir();
        d.Add("c1");
        h.Client.Script = [ScriptedChatClient.Reply("")]; // 빈 응답 = 1단계 실패 → 2단계 안 감

        var (code, output, err) = await Cli(h, "run", "--dir", d.Root, "--yes", "--label", "f");

        Assert.Equal(1, code);
        Assert.Contains("[c1] 실행 실패", err);
        Assert.DoesNotContain("[crash 합계]", output);
        Assert.Equal(1, h.Client.Calls);
        Assert.Contains("\"error\":", File.ReadAllText(Directory.GetFiles(Path.Combine(d.Root, "results"), "*_f.jsonl").Single()));
    }

    [Fact]
    public async Task Score_command_rejects_crash_case()
    {
        using var h = NewHarness();
        using var d = new Dir();
        d.Add("c1");

        var (code, _, err) = await Cli(h, "score", "c1", "--from", "x", "--dir", d.Root);

        Assert.Equal(2, code);
        Assert.Contains("crash", err);
    }
}
