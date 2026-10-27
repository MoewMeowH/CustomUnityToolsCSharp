using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>리플레이 골든 사례 만들기 · 읽기 (계획서 2026-10-27_리플레이-골든).</summary>
public sealed class ReplayGoldenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-replay-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Cwd => Path.Combine(_dir, "proj", "subagent_in", "2026-10-27_x");

    private static string ReadResult(string path, string content, int start, int num, int total) => JsonSerializer.Serialize(new
    {
        type = "user",
        message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t", content = "1\t…" } } },
        toolUseResult = new { type = "text", file = new { filePath = path, content, numLines = num, startLine = start, totalLines = total } },
    });

    private string Transcript(params string[] reads)
    {
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = $"대상 폴더: {Cwd}\\\n파일: {Cwd}\\src\\a.cs (전체)" } }),
        };
        lines.AddRange(reads);
        var path = Path.Combine(_dir, "agent-x.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Transcript_restores_whole_reads_and_lists_partial_ones()
    {
        var path = Transcript(
            ReadResult(Path.Combine(Cwd, "src", "a.cs"), "class A\n{\n}\n", 1, 3, 3),
            ReadResult(Path.Combine(Cwd, "b.cs"), "x\n", 10, 5, 400),                 // 일부만
            ReadResult(Path.Combine(_dir, "outside.cs"), "y\n", 1, 1, 1),            // 폴더 밖
            ReadResult(Path.Combine(Cwd, "c.md"), "# a\n", 5, 1, 9),
            ReadResult(Path.Combine(Cwd, "c.md"), "# a\n## b\n", 1, 9, 9),           // 뒤에 통째로 읽음 → 복원 가능
            "{ 깨진 줄");

        var t = ReplayCaseBuilder.ReadTranscript(path, Cwd);

        Assert.StartsWith("대상 폴더: ", t.Delegation);
        Assert.Equal(["c.md", "src/a.cs"], t.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("class A\n{\n}\n", t.Files["src/a.cs"]);
        Assert.Equal(["b.cs"], t.Partial);
        Assert.Contains(t.Outside, p => p.EndsWith("outside.cs"));                               // 폴더 밖은 따로 (C5)
        Assert.Equal(Path.GetFullPath(Cwd), t.Base);

        // cwd가 프로젝트 루트인 기록: 기준 폴더는 읽은 경로의 subagent_in/<실행>
        var fromRoot = ReplayCaseBuilder.ReadTranscript(path, Path.Combine(_dir, "proj"));
        Assert.Equal(Path.GetFullPath(Cwd), fromRoot.Base);
        Assert.Equal(["c.md", "src/a.cs"], fromRoot.Files.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Split_reads_that_cover_the_whole_file_are_stitched()
    {
        Assert.Equal("a\nb\nc\nd\n", ReplayCaseBuilder.Stitch([("c\nd", 3, 2, 4), ("a\nb\n", 1, 2, 4)]));
        Assert.Null(ReplayCaseBuilder.Stitch([("a\nb", 1, 2, 4)]));                               // 3~4줄을 못 읽음
        Assert.Equal("x\r\ny", ReplayCaseBuilder.Stitch([("x", 2, 1, 2), ("x\r\ny", 1, 2, 2)])); // 통째로 읽은 원문이 있으면 그대로
    }

    private const string Review = """
        ## 지적
        - C1 [major] src/a.cs:2 — `Foo.Bar()`가 null을 돌려줄 때 처리 없음 / 검사 추가 / 확신도 high
        - C2 [minor] src/a.cs:3 — 이름이 모호함
        - C3 [nit] src/a.cs:1 — 주석
        ## 자료 부족
        - 없음
        ## 읽은 파일
        - subagent_in/2026-10-27_x/src/a.cs:1-3
        """;

    // HB_ORCH_VERDICTS_PATH가 있어도 실제 판정 파일을 건드리지 않게 경로를 직접 준다 (sub_self 2f9f8ff874 C8)
    private (ReplayCaseBuilder.Candidate Candidate, string VerdictPath) Setup(string transcript, string key = "k")
    {
        var verdictPath = Path.Combine(_dir, "verdicts.jsonl");
        VerdictRecord V(string key, string verdict, string summary, int[] steps, string? file = null, string? reason = null) => new()
        {
            Id = key, Timestamp = DateTimeOffset.Now, RunId = "run0000001", FindingKey = key, Summary = summary, Verdict = verdict,
            Steps = steps, File = file, Reason = reason,
        };
        VerdictLog.Append([
            V("C1", Verdicts.Adopted, "null 처리 누락", [1]),
            V("C2", Verdicts.Rejected, "이름은 규칙대로", [1], reason: "이미 정한 이름"),
            V("C3", Verdicts.Deferred, "주석", [1], reason: "나중에"),
            V("K1", Verdicts.Adopted, "`Baz` 해제 누락", [], file: "src/a.cs:3"),
            V("K2", Verdicts.Adopted, "문서 표현 다듬기", []),                                      // 위치 · 코드 이름 없음 → 뺌
        ], verdictPath);
        var result = new SubagentResult
        {
            Key = key, AgentId = "x", AgentType = "self-reviewer", Message = Review, Cwd = Cwd, SourcePath = transcript,
            ProjectDir = Path.Combine(_dir, "proj"), ReturnedAt = DateTimeOffset.Now, Description = "Self-review x",
        };
        return (new ReplayCaseBuilder.Candidate("run0000001", result, "sub_self", null), verdictPath);
    }

    [Fact]
    public void Build_writes_a_loadable_case_with_key_from_verdicts()
    {
        var (candidate, verdictPath) = Setup(Transcript(ReadResult(Path.Combine(Cwd, "src", "a.cs"), "class A\n{\n}", 1, 3, 3)));
        var golden = Path.Combine(_dir, "GoldenSet");

        var (folder, skip, notes) = ReplayCaseBuilder.Build(candidate, golden, verdictPath);

        Assert.Null(skip);
        Assert.Equal("class A\n{\n}", File.ReadAllText(Path.Combine(folder!, "files", "src", "a.cs")));
        var input = File.ReadAllText(Path.Combine(folder!, "input.md"));
        Assert.Equal("대상 폴더: ./\n파일: src\\a.cs (전체)", input);                            // 절대 경로를 작업 폴더 기준으로
        Assert.Contains(notes, n => n.StartsWith("K2:"));

        var (c, error) = GoldenCaseLoader.Load(folder!);
        Assert.Null(error);
        Assert.True(c!.Info.IsReplay);
        Assert.Equal("sub_self", c.Info.Mode);
        Assert.Equal([new ContextRange("src/a.cs", 1, 3)], c.Context);
        Assert.Equal(["C1", "K1"], c.Expected.Select(e => e.Id));
        var c1 = c.Expected[0];
        Assert.Equal(["src/a.cs"], c1.Files);
        Assert.Equal([2], c1.Lines);
        Assert.Equal(["Foo.Bar"], c1.Required);                                                 // 지적 문장의 코드 이름 → 필수 키워드
        Assert.Equal("self-review", c1.Origin);
        Assert.Equal("conductor", c.Expected[1].Origin);                                         // 지휘자 추가 채택 (리뷰어가 놓친 것)
        Assert.Equal(["Baz"], c.Expected[1].Required);
        Assert.Equal([3], c.Expected[1].Lines);
        var trap = Assert.Single(c.Traps);
        Assert.Equal(("C2", "이미 정한 이름"), (trap.Id, trap.Why));

        Assert.Equal((null, "이미 있음"), (ReplayCaseBuilder.Build(candidate, golden, verdictPath).Folder, ReplayCaseBuilder.Build(candidate, golden, verdictPath).Skip));
    }

    [Fact]
    public void Build_skips_when_a_file_was_read_only_partly()
    {
        var (candidate, verdictPath) = Setup(Transcript(ReadResult(Path.Combine(Cwd, "src", "a.cs"), "x", 2, 1, 3)));

        var (folder, skip, _) = ReplayCaseBuilder.Build(candidate, Path.Combine(_dir, "GoldenSet"), verdictPath);

        Assert.Null(folder);
        Assert.StartsWith("통째로 읽지 않은 파일: src/a.cs", skip);
        Assert.False(Directory.Exists(Path.Combine(_dir, "GoldenSet", "replay-run0000001")));
    }

    [Fact]
    public void Identifiers_pick_code_names_even_with_korean_particles()
    {
        Assert.Equal(["ReadText", "recentQuota", "snake_case", "0xFF"],
            ReplayCaseBuilder.Identifiers("`ReadText()`가 ModeRunner.cs의 recentQuota와 snake_case를 0xFF로 센다"));
        Assert.Equal(["IndexModel.OnGet"], ReplayCaseBuilder.Identifiers("IndexModel.OnGet에서 a b c"));
        Assert.Empty(ReplayCaseBuilder.Identifiers("이름이 모호하다 · README.md 문구"));
    }

    [Theory]
    [InlineData("0줄", "", 0)]
    [InlineData("끝 줄바꿈", "a\nb\n", 2)]
    [InlineData("끝 줄바꿈 없음", "a\nb", 2)]
    public void Line_count(string _, string text, int lines) => Assert.Equal(lines, ReplayCaseBuilder.CountLines(text));

    [Fact]
    public void Replay_case_needs_context_inside_files()
    {
        var dir = Path.Combine(_dir, "GoldenSet", "replay-bad");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "case.json"), """{ "id": "replay-bad", "kind": "replay", "mode": "sub_self" }""");
        File.WriteAllText(Path.Combine(dir, "input.md"), "의도");
        File.WriteAllText(Path.Combine(dir, "expected.json"), """[{ "id": "C1", "files": ["a.cs"], "lines": [1] }]""");
        Assert.Contains("context.json", GoldenCaseLoader.Load(dir).Error);

        File.WriteAllText(Path.Combine(dir, "context.json"), """[{ "path": "../case.json", "startLine": 1, "endLine": 1 }]""");
        Assert.Contains("files/ 안에 없습니다", GoldenCaseLoader.Load(dir).Error);

        File.WriteAllText(Path.Combine(dir, "files", "a.cs"), "x\n");
        File.WriteAllText(Path.Combine(dir, "context.json"), """[{ "path": "a.cs", "startLine": 1, "endLine": 1 }]""");
        Assert.Null(GoldenCaseLoader.Load(dir).Error);
    }

    [Theory]
    [InlineData("replay-build --agents a=b", "replay-build는")]
    [InlineData("run --exclude MoJak", "--exclude는 replay-build에서만")]
    public void Replay_build_arguments_are_checked(string args, string expected)
    {
        var (parsed, error) = GoldenCli.Parse(["--golden", .. args.Split(' ')]);
        Assert.Null(parsed);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Replay_build_defaults_to_all_and_keeps_excludes()
    {
        var (parsed, error) = GoldenCli.Parse(["--golden", "replay-build", "--exclude", "MoJak", "--exclude", "Other", "--yes"]);
        Assert.Null(error);
        Assert.Equal(("all", true), (parsed!.Which, parsed.Yes));
        Assert.Equal(["MoJak", "Other"], parsed.Exclude!);
    }

    // ── sub_self 2f9f8ff874 반영 ─────────────────────────────────────────

    private static string Handback(string id) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", name = SubagentTranscriptReader.HandbackToolName, id, input = new { message = "결과" } } } },
    });

    [Fact]
    public void Continued_results_are_skipped_and_reads_after_the_handback_are_ignored()   // C1
    {
        var a = Path.Combine(Cwd, "src", "a.cs");
        var path = Transcript(ReadResult(a, "v1\n", 1, 1, 1), Handback("h1"), ReadResult(Path.Combine(Cwd, "late.cs"), "x", 5, 1, 9), Handback("h2"));

        var first = ReplayCaseBuilder.ReadTranscript(path, Cwd, "h1");
        Assert.False(first.Continued);
        Assert.Equal(["src/a.cs"], first.Files.Keys);                                          // 넘긴 뒤의 Read(late.cs)는 안 섞임
        Assert.Empty(first.Partial);
        Assert.True(ReplayCaseBuilder.ReadTranscript(path, Cwd, "h2").Continued);

        var (candidate, verdictPath) = Setup(path, key: "x_h2");
        Assert.StartsWith("이어 부른 결과", ReplayCaseBuilder.Build(candidate, Path.Combine(_dir, "GoldenSet"), verdictPath).Skip);
    }

    [Fact]
    public void Stitch_rejects_mixed_versions_and_missing_line_info()   // C1 · C6
    {
        Assert.Null(ReplayCaseBuilder.Stitch([("a\nb", 1, 2, 4), ("c\nd\ne", 3, 3, 5)]));        // 전체 줄 수가 다름 = 판본이 섞임
        Assert.Null(ReplayCaseBuilder.Stitch([("a", 1, 0, -1)]));                                 // 줄 정보 없음
        Assert.Equal("", ReplayCaseBuilder.Stitch([("", 1, 0, 0)]));                              // 빈 파일은 통째로 읽음
    }

    [Fact]
    public void Outside_reads_and_excluded_words_have_their_own_reasons()   // C5
    {
        var (candidate, verdictPath) = Setup(Transcript(ReadResult(Path.Combine(Cwd, "src", "a.cs"), "x\n", 1, 1, 1), ReadResult(Path.Combine(_dir, "CLAUDE.md"), "y\n", 1, 1, 1)));
        Assert.StartsWith("위임 폴더 밖 파일을 읽음: CLAUDE.md", ReplayCaseBuilder.Build(candidate, Path.Combine(_dir, "G1"), verdictPath).Skip);

        var (inside, verdictPath2) = Setup(Transcript(ReadResult(Path.Combine(Cwd, "src", "a.cs"), "x\n", 1, 1, 1)));
        Assert.StartsWith("제외 글자 '2026-10-27_X'", ReplayCaseBuilder.Build(inside, Path.Combine(_dir, "G2"), verdictPath2, ["2026-10-27_X"]).Skip);   // 위임 폴더 경로
    }

    [Fact]
    public void Delegation_paths_keep_neighbor_folders()   // C11
    {
        var cwd = @"F:\P\subagent_in\run_x";
        Assert.Equal("폴더: ./\n이웃: F:\\P\\subagent_in\\run_x2\\a.cs\n파일: a.cs",
            ReplayCaseBuilder.RelativizeDelegation($"폴더: {cwd}\\\n이웃: {cwd}2\\a.cs\n파일: {cwd}\\a.cs", cwd));
    }

    [Fact]
    public void Identifiers_skip_common_words_and_namespace_paths()   // C12
    {
        Assert.Equal(["Foo.Bar"], ReplayCaseBuilder.Identifiers("`null`과 `===`, e.g. HB.Orchestration.Agents의 `Foo.Bar` `return`"));
    }

    [Fact]
    public void Head_locations_read_every_file_and_range()   // 시범 replay-65843a0873: 다른 파일 줄로 짚어 놓침
    {
        const string review = "## 지적\n- C1 [major] HB_ScreenRecorder.cs:237, HB_ScreenRecorderWindow.cs:82-87 — 문제\n" +
                              "- C3 [minor] hook_common.py:70-81 (scan) · 129-140 (tokenize) · share_guard.py:303 — 문제\n";
        Assert.Equal(["HB_ScreenRecorder.cs:237-", "HB_ScreenRecorderWindow.cs:82-87"],
            ReplayCaseBuilder.HeadLocations(review, "C1").Select(l => $"{l.File}:{l.From}-{l.To}"));
        Assert.Equal(["hook_common.py:70-81", "hook_common.py:129-140", "share_guard.py:303-"],
            ReplayCaseBuilder.HeadLocations(review, "C3").Select(l => $"{l.File}:{l.From}-{l.To}"));
        Assert.Empty(ReplayCaseBuilder.HeadLocations(review, "C9"));
    }

    [Fact]
    public void Result_lines_mask_the_user_name()   // GoldenSet 별도 저장소 push (H2 개인 값)
    {
        const string json = """{"path":"C:\\Users\\alice\\AppData\\x","other":"C:/Users/alice/y","keep":"C:\\Users\\alicex\\z","raw":"C:\Users\alice\.claude"}""";
        Assert.Equal("""{"path":"C:\\Users\\<user>\\AppData\\x","other":"C:/Users/<user>/y","keep":"C:\\Users\\alicex\\z","raw":"C:\Users\<user>\.claude"}""",
            GoldenCli.MaskUserName(json, "alice"));
    }

    [Fact]
    public void Replay_input_hash_covers_restored_files()   // C4
    {
        var (candidate, verdictPath) = Setup(Transcript(ReadResult(Path.Combine(Cwd, "src", "a.cs"), "class A\n", 1, 1, 1)));
        var (folder, _, _) = ReplayCaseBuilder.Build(candidate, Path.Combine(_dir, "GoldenSet"), verdictPath);
        var before = GoldenCli.InputHash(GoldenCaseLoader.Load(folder!).Case!);

        File.WriteAllText(Path.Combine(folder!, "files", "src", "a.cs"), "class B\n");

        Assert.NotEqual(before, GoldenCli.InputHash(GoldenCaseLoader.Load(folder!).Case!));
    }
}
