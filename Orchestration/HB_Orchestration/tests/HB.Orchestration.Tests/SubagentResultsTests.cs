using System.Text;
using System.Text.Json;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>
/// 검토형 서브에이전트 결과 보관 · 서버 기록 연결 (계획서 2026-10-27_서브에이전트-결과-보존).
/// 합성 데이터 — 실제 서브에이전트 기록은 리뷰 대상 코드가 들어 있어 커밋하지 않는다.
/// </summary>
public sealed class SubagentResultsTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-orch-subagents-").FullName;
    private string Projects => Path.Combine(_dir, "projects");
    private string Archive => Path.Combine(_dir, "archive");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Line(object o) => JsonSerializer.Serialize(o);

    private static string Handback(string agentId, string useId, string text, string cwd = @"F:\AI\ToolCreate\subagent_in\2026-10-27_x", int second = 10) => Line(new
    {
        type = "assistant", sessionId = Session, agentId, cwd, version = "2.1.310", isSidechain = true, timestamp = $"2026-10-27T07:43:{second:00}.000Z",
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = useId, name = "SubagentHandback", input = new { message = text } } } },
    });

    private static string Prompt(string agentId) => Line(new
    {
        type = "user", sessionId = Session, agentId, cwd = @"F:\AI\ToolCreate\subagent_in\2026-10-27_x", timestamp = "2026-10-27T07:40:00.000Z",
        message = new { role = "user", content = "위임 메시지" },
    });

    /// <summary>projects/&lt;프로젝트&gt;/&lt;세션&gt;/subagents/agent-&lt;id&gt;.jsonl + .meta.json</summary>
    private string WriteAgent(string agentId, string? agentType, params string[] lines) => WriteAgentMeta(agentId,
        agentType is null ? null : Line(new { agentType, description = "Security-review change", finished = true }), lines);

    /// <summary>meta가 null이면 meta.json을 만들지 않는다.</summary>
    private string WriteAgentMeta(string agentId, string? meta, params string[] lines)
    {
        var folder = Path.Combine(Projects, "F--AI-ToolCreate", Session, "subagents");
        Directory.CreateDirectory(folder);
        var jsonl = Path.Combine(folder, $"agent-{agentId}.jsonl");
        File.WriteAllText(jsonl, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        if (meta is not null) File.WriteAllText(Path.Combine(folder, $"agent-{agentId}.meta.json"), meta, new UTF8Encoding(false));
        return jsonl;
    }

    private const string Review = "## 지적\n- C1 [major] Foo.cs:10 — PROJECT_DIR가 빌드 파라미터라 다른 폴더를 지울 수 있음 / 확신도 high\n\n## 읽은 파일\n- Foo.cs";

    // ── 보관 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sync_copies_reviewer_handback_with_meta_and_project()
    {
        WriteAgent("a1", "security-reviewer", Prompt("a1"), Handback("a1", "toolu_1", Review));

        var report = SubagentArchive.Sync(Projects, Archive);

        Assert.Equal(1, report.Added);
        Assert.Empty(report.Errors);
        var (results, bad) = SubagentArchive.ReadAll(Archive);
        Assert.Equal(0, bad);
        var r = Assert.Single(results);
        Assert.Equal("a1_toolu_1", r.Key);
        Assert.Equal("security-reviewer", r.AgentType);
        Assert.Equal(Review, r.Message);
        Assert.Equal(@"F:\AI\ToolCreate", r.ProjectDir);
        Assert.Equal("Security-review change", r.Description);
        Assert.Equal(Session, r.SessionId);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 7, 43, 10, TimeSpan.Zero), r.ReturnedAt);
    }

    [Fact]
    public void Archive_survives_source_deletion_and_is_not_rewritten()
    {
        var jsonl = WriteAgent("a1", "self-reviewer", Handback("a1", "toolu_1", Review));
        SubagentArchive.Sync(Projects, Archive);
        Directory.Delete(Path.GetDirectoryName(jsonl)!, recursive: true); // Claude Code가 세션 기록을 지움

        var again = SubagentArchive.Sync(Projects, Archive);

        Assert.Equal(0, again.Added);
        Assert.Equal(Review, Assert.Single(SubagentArchive.ReadAll(Archive).Results).Message);
    }

    [Fact]
    public void Sync_skips_non_reviewers()
    {
        WriteAgent("g1", "general-purpose", Handback("g1", "toolu_g", "일반 결과"));
        WriteAgent("w1", "web-searcher", Handback("w1", "toolu_w", "검색 결과"));

        var report = SubagentArchive.Sync(Projects, Archive);

        Assert.Equal((0, 0), (report.Added, report.Scanned));
        Assert.Empty(report.Errors);
        Assert.Empty(SubagentArchive.ReadAll(Archive).Results);
    }

    // 형식이 바뀐 것으로 보이면 오류로 내고 도장을 남기지 않는다 — 다음 Sync도 다시 보고 다시 알린다 (/self-review C2).
    [Fact]
    public void Missing_meta_is_an_error_every_time()
    {
        WriteAgent("x1", agentType: null, Handback("x1", "toolu_x", Review));

        Assert.Contains("meta.json이 없습니다", Assert.Single(SubagentArchive.Sync(Projects, Archive).Errors));
        Assert.Single(SubagentArchive.Sync(Projects, Archive).Errors);
    }

    [Fact]
    public void Meta_without_agentType_is_an_error()
    {
        WriteAgentMeta("x1", Line(new { kind = "self-reviewer", finished = true }), Handback("x1", "toolu_x", Review));

        Assert.Contains("agentType이 없습니다", Assert.Single(SubagentArchive.Sync(Projects, Archive).Errors));
    }

    [Fact]
    public void Finished_without_handback_is_an_error_and_reread_after_fix()
    {
        var jsonl = WriteAgent("a1", "self-reviewer", Prompt("a1")); // 끝났다는데 결과가 없음 (도구 이름이 바뀐 경우와 같다)

        var first = SubagentArchive.Sync(Projects, Archive);
        Assert.Contains("SubagentHandback", Assert.Single(first.Errors));
        Assert.Equal(0, first.NoResult);
        Assert.Equal(1, SubagentArchive.Sync(Projects, Archive).Scanned); // 도장이 없어 다시 읽는다

        File.AppendAllText(jsonl, Handback("a1", "toolu_1", Review) + "\n", new UTF8Encoding(false));
        Assert.Equal(1, SubagentArchive.Sync(Projects, Archive).Added);
    }

    [Fact]
    public void State_lives_outside_archive_listing_and_drops_deleted_sources()
    {
        var jsonl = WriteAgent("a1", "self-reviewer", Handback("a1", "toolu_1", Review));
        SubagentArchive.Sync(Projects, Archive);
        Assert.Equal(0, SubagentArchive.ReadAll(Archive).Unreadable);
        Assert.Contains("agent-a1.jsonl", File.ReadAllText(SubagentArchive.StatePath(Archive)));

        File.Delete(jsonl);
        SubagentArchive.Sync(Projects, Archive);

        Assert.DoesNotContain("agent-a1.jsonl", File.ReadAllText(SubagentArchive.StatePath(Archive)));
        Assert.Single(SubagentArchive.ReadAll(Archive).Results);
    }

    [Fact]
    public void Continued_agent_keeps_each_handback()
    {
        WriteAgent("a1", "self-reviewer", Handback("a1", "toolu_1", Review, second: 10), Handback("a1", "toolu_2", Review + "\n\n## 비고\n- 재검토", second: 50));

        SubagentArchive.Sync(Projects, Archive);

        Assert.Equal(["a1_toolu_2", "a1_toolu_1"], SubagentArchive.ReadAll(Archive).Results.Select(r => r.Key)); // 최근 순
    }

    [Fact]
    public void Unfinished_agent_is_reread_when_it_hands_back()
    {
        var jsonl = WriteAgentMeta("a1", Line(new { agentType = "self-reviewer", finished = false }), Prompt("a1"));
        var first = SubagentArchive.Sync(Projects, Archive);
        Assert.Equal(1, first.NoResult);
        Assert.Equal(0, first.Added);

        File.AppendAllText(jsonl, Handback("a1", "toolu_1", Review) + "\n", new UTF8Encoding(false));
        var second = SubagentArchive.Sync(Projects, Archive);

        Assert.Equal(1, second.Added);
    }

    [Fact]
    public void Unchanged_source_is_not_reread()
    {
        WriteAgent("a1", "self-reviewer", Handback("a1", "toolu_1", Review));
        Assert.Equal(1, SubagentArchive.Sync(Projects, Archive).Scanned);

        Assert.Equal(0, SubagentArchive.Sync(Projects, Archive).Scanned);
    }

    [Fact]
    public void Broken_lines_and_other_tools_are_ignored()
    {
        WriteAgent("a1", "perf-reviewer",
            "{not json",
            Line(new { type = "assistant", sessionId = Session, message = new { content = new object[] { new { type = "tool_use", id = "t", name = "Read", input = new { file_path = "x" } } } } }),
            Line(new { type = "assistant", sessionId = Session, message = new { content = new object[] { new { type = "tool_use", id = "t2", name = "SubagentHandback", input = new { other = 1 } } } } }),
            Handback("a1", "toolu_1", Review));

        var report = SubagentArchive.Sync(Projects, Archive);

        Assert.Equal(1, report.Added);
        Assert.Empty(report.Errors);
    }

    // ── 방식 비교 시험(2026-10-27)의 새 지적 ──────────────────────────────

    [Fact]
    public void Unreadable_string_value_does_not_stop_sync()
    {
        // 짝 없는 서로게이트 이스케이프: Parse는 통과하고 값 꺼내기에서 실패할 수 있다. 어느 쪽이든 Sync가 멈추면 안 된다.
        var broken = Handback("a1", "toolu_bad", "x").Replace("\"message\":\"x\"", "\"message\":\"\\ud800\"");
        WriteAgent("a1", "self-reviewer", broken, Handback("a1", "toolu_ok", Review, second: 20));
        WriteAgent("a2", "perf-reviewer", Handback("a2", "toolu_2", Review));

        var report = SubagentArchive.Sync(Projects, Archive);

        Assert.Contains(SubagentArchive.ReadAll(Archive).Results, r => r.Key == "a1_toolu_ok");
        Assert.Contains(SubagentArchive.ReadAll(Archive).Results, r => r.Key == "a2_toolu_2");
        Assert.True(report.Added >= 2);
    }

    [Fact]
    public void Meta_open_for_writing_by_claude_code_is_still_read()
    {
        var jsonl = WriteAgent("a1", "self-reviewer", Handback("a1", "toolu_1", Review));
        var meta = jsonl[..^".jsonl".Length] + ".meta.json";
        using var writer = new FileStream(meta, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var report = SubagentArchive.Sync(Projects, Archive);

        Assert.Empty(report.Errors);
        Assert.Equal(1, report.Added);
    }

    [Fact]
    public void Result_without_return_time_is_counted_not_hidden()
    {
        var noTime = Handback("a1", "toolu_1", Review).Replace("\"timestamp\":\"2026-10-27T07:43:10.000Z\",", "");
        WriteAgent("a1", "self-reviewer", noTime);
        var log = Path.Combine(_dir, "calls.jsonl");
        var options = Microsoft.Extensions.Options.Options.Create(new DashboardOptions { LogPath = log, ClaudeProjectsDir = Projects });
        var service = new SubagentResultsService(options,
            new ClaudeSessionsService(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeSessionsService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubagentResultsService>.Instance);

        Assert.Equal(1, service.UnrecordedRecent([]));
        Assert.Null(Assert.Single(service.Load([]).Views).Result.ReturnedAt);
    }

    [Fact]
    public void Missing_root_is_empty_not_error()
    {
        var report = SubagentArchive.Sync(Path.Combine(_dir, "nope"), Archive);
        Assert.Equal(0, report.Scanned);
        Assert.Empty(report.Errors);
    }

    [Theory]
    [InlineData(@"F:\AI\ToolCreate\subagent_in\2026-10-27_x", @"F:\AI\ToolCreate")]
    [InlineData(@"F:\AI\ToolCreate\SUBAGENT_IN\a", @"F:\AI\ToolCreate")]
    [InlineData(@"F:\AI\ToolCreate\", @"F:\AI\ToolCreate")]
    [InlineData(@"F:\AI\ToolCreate\subagent_in_old\a", @"F:\AI\ToolCreate\subagent_in_old\a")]
    public void ProjectDir_cuts_at_subagent_in(string cwd, string expected) =>
        Assert.Equal(expected, SubagentTranscriptReader.ProjectDirOf(cwd));

    [Fact]
    public void Key_is_file_name_safe() =>
        Assert.Equal("a1_------x", SubagentTranscriptReader.KeyFor("a1", @"..\..\x"));

    // ── 서버 기록 연결 ────────────────────────────────────────────────────

    private static readonly DateTimeOffset Returned = new(2026, 10, 27, 7, 43, 0, TimeSpan.Zero);

    private static SubagentResult Result(string message, string agent = "self-reviewer", DateTimeOffset? returned = null) => new()
    {
        Key = "k", AgentId = "a", AgentType = agent, Message = message, ReturnedAt = returned ?? Returned,
    };

    /// <summary>self_review 기록: Timestamp = 기록 시각 − 실행 시간 (VerdictRecorder와 같다). 기본은 넘기고 5분 뒤 기록, 실행 6분.</summary>
    private static CallRecord SelfReviewCall(string runId, string response, string agent = "self-reviewer", DateTimeOffset? recordedAt = null) => new()
    {
        Id = runId + "c", Timestamp = (recordedAt ?? Returned.AddMinutes(5)).AddMinutes(-6), ElapsedMs = 6 * 60_000,
        Tool = Agents.VerdictRecorder.SelfReviewTool, RunId = runId, Step = 1, Agent = agent,
        Model = "opus", Prompt = "의도", Response = response, Success = true,
    };

    private static CallRecord RunModeCall(string runId, int step, string ownReview, DateTimeOffset? at = null) => new()
    {
        Id = $"{runId}{step}", Timestamp = (at ?? Returned.AddMinutes(2)).AddMinutes(step), Tool = "review_main", RunId = runId, Step = step,
        Model = "m", Prompt = "p", Response = "r", Success = true, OwnReview = ownReview,
    };

    [Fact]
    public void Exact_via_selfReview_ignoring_crlf_and_trailing_spaces()
    {
        var link = SubagentLinker.Link([Result(Review)], [SelfReviewCall("run1", Review.Replace("\n", "  \r\n") + "\n\n")])[0].Link;
        Assert.Equal(SubagentLinkKind.Exact, link.Kind);
        Assert.Equal(("run1", "selfReview"), (link.RunId, link.Via));
    }

    [Fact]
    public void Exact_inside_concatenated_ownReview()
    {
        // 짧은 원문은 통째로 같아야 하므로(MinContainsLength) 실제 리뷰 길이로 만든다.
        var detail = "\n  상세: " + string.Concat(Enumerable.Repeat("재현 조건과 영향 범위를 설명하는 문장. ", 8));
        var self = Review.Replace("확신도 high", "확신도 high" + detail);
        var security = "## 지적\n- S1 [high] Guard.py:3 — 경로 검사를 우회할 수 있음 / 확신도 medium" + detail + "\n\n## 읽은 파일\n- Guard.py";
        Assert.True(self.Length >= SubagentLinker.MinContainsLength && security.Length >= SubagentLinker.MinContainsLength);
        var calls = new[] { RunModeCall("run2", 1, self + "\n" + security), RunModeCall("run2", 2, self + "\n" + security) };

        var views = SubagentLinker.Link([Result(self), Result(security, "security-reviewer")], calls);

        Assert.All(views, v => Assert.Equal((SubagentLinkKind.Exact, "run2", "ownReview"), (v.Link.Kind, v.Link.RunId, v.Link.Via)));
    }

    [Fact]
    public void Near_when_conductor_edited_a_little()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"- C{i} [minor] File{i}.cs:{i} — 지적 내용이 충분히 긴 줄 {i}").ToList();
        var original = "## 지적\n" + string.Join("\n", lines);
        var edited = "## 지적\n" + string.Join("\n", lines.Take(9)) + "\n- C10 [minor] 지휘자가 고쳐 쓴 줄입니다 아주 길게";

        var link = SubagentLinker.Link([Result(original)], [SelfReviewCall("run3", edited)])[0].Link;

        Assert.Equal(SubagentLinkKind.Near, link.Kind);
        Assert.Equal("run3", link.RunId);
        Assert.Equal(0.9, link.Similarity, 3);
    }

    [Fact]
    public void None_keeps_best_hint_below_threshold()
    {
        var other = "## 지적\n- C1 [major] Foo.cs:10 — PROJECT_DIR가 빌드 파라미터라 다른 폴더를 지울 수 있음 / 확신도 high\n- C2 [minor] 전혀 다른 지적이 들어 있는 긴 줄입니다";
        var mine = "## 지적\n- C1 [major] Foo.cs:10 — PROJECT_DIR가 빌드 파라미터라 다른 폴더를 지울 수 있음 / 확신도 high\n- S2 [high] 보안 리뷰어만 낸 지적이 들어 있는 긴 줄\n- S3 [low] 또 다른 보안 지적이 들어 있는 긴 줄입니다";

        var link = SubagentLinker.Link([Result(mine)], [SelfReviewCall("run4", other)])[0].Link;

        Assert.Equal(SubagentLinkKind.None, link.Kind);
        Assert.Equal("run4", link.RunId);
        Assert.Equal(1.0 / 3, link.Similarity, 3);
    }

    [Fact]
    public void None_without_any_record()
    {
        var link = SubagentLinker.Link([Result(Review)], [])[0].Link;
        Assert.Equal((SubagentLinkKind.None, null, 0.0), (link.Kind, link.RunId, link.Similarity));
    }

    // ── /self-review C1: 다른 기록에 잘못 붙지 않기 ────────────────────────

    [Fact]
    public void SelfReview_of_another_agent_is_not_a_candidate()
    {
        var link = SubagentLinker.Link([Result(Review, "security-reviewer")], [SelfReviewCall("run6", Review, agent: "self-reviewer")])[0].Link;
        Assert.Equal((SubagentLinkKind.None, null), (link.Kind, link.RunId));
    }

    [Theory]
    [InlineData(-60)]          // 넘기기 1시간 전에 이미 기록됨 = 앞 실행 (재검토 전 1차 기록)
    [InlineData(3 * 24 * 60)]  // 사흘 뒤 = 다른 작업
    public void Records_outside_time_window_are_not_candidates(int minutesAfterReturn)
    {
        var at = Returned.AddMinutes(minutesAfterReturn);
        var link = SubagentLinker.Link([Result(Review)], [SelfReviewCall("run7", Review, recordedAt: at), RunModeCall("run8", 1, Review, at)])[0].Link;
        Assert.Equal(SubagentLinkKind.None, link.Kind);
    }

    [Fact]
    public void Unknown_return_time_skips_time_window()
    {
        var link = SubagentLinker.Link([Result(Review) with { ReturnedAt = null }], [SelfReviewCall("run9", Review, recordedAt: Returned.AddDays(-30))])[0].Link;
        Assert.Equal(SubagentLinkKind.Exact, link.Kind);
    }

    [Fact]
    public void Same_read_files_do_not_make_reviews_similar()
    {
        var files = string.Join("\n", Enumerable.Range(1, 8).Select(i => $@"- F:\AI\ToolCreate\subagent_in\2026-10-27_x\Folder\File{i}.cs:1-200"));
        var security = $"## 지적\n- S1 [high] Guard.py:3 — 경로 검사를 우회할 수 있음 / 확신도 medium\n\n## 읽은 파일\n{files}";
        var self = $"## 지적\n- C1 [minor] Foo.cs:10 — 이름이 동작을 설명하지 못함 / 확신도 low\n\n## 읽은 파일\n{files}";

        var link = SubagentLinker.Link([Result(security, "security-reviewer")], [RunModeCall("run10", 1, self)])[0].Link;

        Assert.Equal(SubagentLinkKind.None, link.Kind);
        Assert.Equal(0, link.Similarity);
    }

    [Fact]
    public void Short_message_needs_whole_match_not_containment()
    {
        const string blocked = "검토를 끝내지 못했습니다.";
        var link = SubagentLinker.Link([Result(blocked)], [SelfReviewCall("run11", Review + "\n" + blocked)])[0].Link;
        Assert.NotEqual(SubagentLinkKind.Exact, link.Kind);

        Assert.Equal(SubagentLinkKind.Exact, SubagentLinker.Link([Result(blocked)], [SelfReviewCall("run12", blocked)])[0].Link.Kind);
    }

    // ── /self-review C3: 방금 넘긴 결과 ──────────────────────────────────

    [Theory]
    [InlineData(10, true)]
    [InlineData(120, false)]
    public void Recent_unrecorded_result_may_still_be_pending(int minutesAgo, bool pending)
    {
        var view = new SubagentView(Result(Review, returned: DateTimeOffset.Now.AddMinutes(-minutesAgo)), new SubagentLink(SubagentLinkKind.None, null, null, 0));
        Assert.Equal(pending, SubagentResultsService.MaybePending(view));
    }

    [Fact]
    public void Headings_alone_do_not_count_as_similar()
    {
        var link = SubagentLinker.Link([Result("## 지적\n- 없음\n\n## 읽은 파일\n- A.cs")], [SelfReviewCall("run5", "## 지적\n- 없음\n\n## 읽은 파일\n- B.cs")])[0].Link;
        Assert.Equal(SubagentLinkKind.None, link.Kind);
        Assert.Null(link.RunId);
    }
}
