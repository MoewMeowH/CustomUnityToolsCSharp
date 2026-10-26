using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>모델별 프롬프트 덧붙임 prompts/&lt;역할&gt;/&lt;에이전트&gt;.md (계획서 2026-10-23_모델별-프롬프트-덧붙임).</summary>
public sealed class PromptAppendTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string AppendPath(string role = "R", string agent = "a1") => Path.Combine(_h.Root, "prompts", role, agent + ".md");

    private void WriteAppend(string text, string role = "R", string agent = "a1")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppendPath(role, agent))!);
        File.WriteAllText(AppendPath(role, agent), text);
    }

    [Fact]
    public async Task No_file_leaves_system_prompt_byte_identical()
    {
        await _h.Run("seq");
        var before = ScriptedChatClient.SystemText(_h.Client.Received[0]);
        Directory.CreateDirectory(Path.Combine(_h.Root, "prompts", "R")); // 폴더만 있고 파일은 없음
        await _h.Run("seq");

        Assert.Equal(before, ScriptedChatClient.SystemText(_h.Client.Received[1]));
        Assert.DoesNotContain(PromptAppendLoader.Header.Trim(), before);
        Assert.All(_h.Records(), r => Assert.Null(r.Provenance!.Append));
    }

    [Fact]
    public async Task File_is_appended_last_and_recorded_with_blob()
    {
        WriteAppend("- 모델 버릇 고침\n");
        await _h.Run("seq");

        var sent = ScriptedChatClient.SystemText(_h.Client.Received[0]);
        Assert.EndsWith(PromptAppendLoader.Header + "- 모델 버릇 고침\n", sent);
        Assert.True(sent.IndexOf("한글 규칙", StringComparison.Ordinal) < sent.IndexOf("모델 버릇", StringComparison.Ordinal)); // 지침 뒤

        var p = _h.Records()[0].Provenance!;
        var raw = File.ReadAllBytes(AppendPath());
        Assert.Equal((Path.GetFullPath(AppendPath()), BlobStore.Sha256Hex(raw), (long?)raw.Length), (p.Append!.Path, p.Append.Sha256, p.Append.Bytes));
        Assert.Equal(raw, File.ReadAllBytes(BlobStore.PathFor(_h.DefaultBlobFolder, p.Append.Sha256!)));
    }

    [Fact]
    public async Task Only_matching_role_and_agent_get_it()
    {
        WriteAppend("R 전용", role: "R");
        await _h.Run("two"); // R@a1 → NoG@a1

        var sent = _h.Client.Received.Select(ScriptedChatClient.SystemText).ToList();
        Assert.Contains("R 전용", sent[0]);
        Assert.DoesNotContain("R 전용", sent[1]);
        Assert.NotNull(_h.Records()[0].Provenance!.Append);
        Assert.Null(_h.Records()[1].Provenance!.Append);
    }

    [Fact]
    public async Task Read_error_refuses_the_run()
    {
        WriteAppend("x");
        using (new FileStream(AppendPath(), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _h.Run("seq");
            Assert.StartsWith("[모델별 덧붙임 읽기 실패]", result);
        }
        Assert.Empty(_h.Client.Received);
    }

    [Theory]
    [InlineData("..", "a1")]
    [InlineData("R", "../x")]
    [InlineData("R/S", "a1")]
    [InlineData("R", "a:1")]
    [InlineData("한글 역할", "a1")]
    public void Names_that_are_not_plain_get_no_append_and_do_not_escape(string role, string agent)
    {
        WriteAppend("밖으로 나가면 안 됨", role: "x"); // prompts/x/a1.md — "../x" 등으로 닿지 않아야 한다
        File.WriteAllText(Path.Combine(_h.Root, "x.md"), "루트의 파일");
        Assert.Null(PromptAppendLoader.Load(PromptAppendLoader.RootFor(_h.ModesPath), role, agent));
    }

    [Fact]
    public async Task Role_name_with_other_letters_still_runs()
    {
        // 역할 이름에는 글자 제한이 없다 — 덧붙임 경로로 못 쓸 뿐 실행은 된다 (/self-review f3ecfed5ff C1)
        using var g = new ModeRunnerHarness("""{ "Roles": { "역할.v2": { "Prompt": "역할" } }, "Modes": { "m": { "Steps": [ { "Role": "역할.v2", "Agent": "a1" } ] } } }""");
        Assert.DoesNotContain("[", (await g.Run("m"))[..1]);
        Assert.Single(g.Client.Received);
    }

    [Fact]
    public async Task Blank_file_is_no_append()
    {
        WriteAppend("  \n\t\n");
        await _h.Run("seq");
        Assert.DoesNotContain(PromptAppendLoader.Header.Trim(), ScriptedChatClient.SystemText(_h.Client.Received[0]));
        Assert.Null(_h.Records()[0].Provenance!.Append);
    }

    [Fact]
    public async Task Debate_append_comes_after_debate_rules()
    {
        WriteAppend("- 맨 끝 줄\n");
        await _h.Run("deb");

        var sent = _h.Client.Received.Select(ScriptedChatClient.SystemText).ToList();
        Assert.All(sent, s => Assert.EndsWith(PromptAppendLoader.Header + "- 맨 끝 줄\n", s));
        var debater = sent.First(s => s.Contains("## 토론 규칙"));
        Assert.True(debater.IndexOf("## 토론 규칙", StringComparison.Ordinal) < debater.IndexOf("## 모델별 덧붙임", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Swapped_agent_name_is_used()
    {
        _h.Options.Agents.Add(new AgentOptions { Name = "a2", Provider = "fake", Model = "m2", MaxAttempts = 1 });
        File.SetLastWriteTimeUtc(_h.SettingsPath, DateTime.UtcNow.AddSeconds(5)); // 설정 다시 읽기
        WriteAppend("a1 전용", agent: "a1");
        WriteAppend("a2 전용", agent: "a2");

        await _h.Runner.RunAsync("seq", "요청", null, ["a1=a2"], null, null, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

        var sent = ScriptedChatClient.SystemText(_h.Client.Received[0]);
        Assert.Contains("a2 전용", sent);
        Assert.DoesNotContain("a1 전용", sent);
        Assert.EndsWith(Path.Combine("prompts", "R", "a2.md"), _h.Records()[0].Provenance!.Append!.Path);
    }

    [Fact]
    public void List_shows_role_at_agent_pairs_and_list_modes_line()
    {
        Assert.Empty(PromptAppendLoader.List(PromptAppendLoader.RootFor(_h.ModesPath)));
        Assert.Contains("모델별 덧붙임: 없음 — 설정 Orchestration:PromptsPath가 비어 있고", _h.Runner.Describe()); // 원본을 못 읽고 있음을 보인다
        Directory.CreateDirectory(Path.Combine(_h.Root, "prompts", "R"));
        Assert.DoesNotContain("모델별 덧붙임", _h.Runner.Describe()); // 폴더는 있고 본문 있는 파일이 없으면 출력이 이전과 같다

        WriteAppend("x", "R", "a1");
        WriteAppend("y", "NoG", "deepseek_main_flash");
        File.WriteAllText(Path.Combine(_h.Root, "prompts", "R", "notes.txt"), "md 아님");

        Assert.Equal(["NoG@deepseek_main_flash", "R@a1"], PromptAppendLoader.List(PromptAppendLoader.RootFor(_h.ModesPath)));
        Assert.Contains("모델별 덧붙임(modes.json 옆): NoG@deepseek_main_flash, R@a1", _h.Runner.Describe());
    }

    [Fact]
    public async Task Golden_precheck_refuses_when_append_changed()
    {
        // 골든 리뷰 사례는 구조화 출력 역할이어야 한다 (내장 Reviewer)
        using var g = new ModeRunnerHarness("""{ "Modes": { "rv": { "Steps": [ { "Role": "Reviewer", "Agent": "a1" } ] } } }""");
        var (step, error) = g.Runner.PreviewFixedPrompt("rv", null, 10);
        Assert.Null(error);
        Assert.Null(step!.Value.AppendSha256);

        var path = Path.Combine(g.Root, "prompts", "Reviewer", "a1.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "실행 전 확인 뒤에 생김");
        var (_, _, runError) = await g.Runner.RunFixedPromptAsync("rv", "입력", null, "golden", "r", CancellationToken.None,
            expectedGuidelines: step.Value.Guidelines, expectedAppendSha256: step.Value.AppendSha256);
        Assert.StartsWith(ModeRunner.GuidelinesChanged, runError);
        Assert.Contains("모델별 덧붙임", runError);
        Assert.Empty(g.Client.Received);

        // 실행 전 확인에 덧붙임이 있었으면 같은 해시로 호출까지 간다 — --no-guidelines여도 덧붙임은 붙는다 (결정 2)
        var (again, _) = g.Runner.PreviewFixedPrompt("rv", null, 10, withoutGuidelines: true);
        Assert.NotNull(again!.Value.AppendSha256);
        var (_, _, okError) = await g.Runner.RunFixedPromptAsync("rv", "입력", null, "golden", "r", CancellationToken.None,
            withoutGuidelines: true, expectedGuidelines: again.Value.Guidelines, expectedAppendSha256: again.Value.AppendSha256);
        Assert.Null(okError);
        var sent = ScriptedChatClient.SystemText(Assert.Single(g.Client.Received));
        Assert.Contains("실행 전 확인 뒤에 생김", sent);
        Assert.DoesNotContain("한글 규칙", sent); // 지침은 빠지고 덧붙임은 남음
    }

    [Fact]
    public async Task Comment_only_default_file_leaves_system_prompt_byte_identical()
    {
        // 기본 파일은 서식 안내 주석뿐 (계획서 2026-10-27_실수목록-분리 결정 4)
        await _h.Run("seq");
        var before = ScriptedChatClient.SystemText(_h.Client.Received[0]);
        WriteAppend("<!--\n서식: - 실수 / 대신 / 날짜·출처\n-->\n\n");
        await _h.Run("seq");

        Assert.Equal(before, ScriptedChatClient.SystemText(_h.Client.Received[1]));
        Assert.Null(_h.Records()[1].Provenance!.Append);
        Assert.Empty(PromptAppendLoader.List(PromptAppendLoader.RootFor(_h.ModesPath)));
    }

    [Fact]
    public async Task Comments_are_stripped_and_raw_file_is_recorded()
    {
        WriteAppend("<!-- 안내 -->\n- 실수 하나\n<!-- 끝 주석 -->");
        await _h.Run("seq");

        var sent = ScriptedChatClient.SystemText(_h.Client.Received[0]);
        Assert.EndsWith(PromptAppendLoader.Header + "- 실수 하나\n", sent);
        Assert.DoesNotContain("안내", sent);
        Assert.Equal(BlobStore.Sha256Hex(File.ReadAllBytes(AppendPath())), _h.Records()[0].Provenance!.Append!.Sha256);
    }

    [Theory]
    [InlineData("<!-- 바깥 <!-- 안쪽 --> 남은 안내 -->\n- 실수\n")]
    [InlineData("<!-- 닫지 않음\n- 실수\n")]
    public async Task Unpaired_comment_refuses_the_run(string text)
    {
        WriteAppend(text);
        Assert.StartsWith("[모델별 덧붙임 읽기 실패]", await _h.Run("seq"));
        Assert.Empty(_h.Client.Received);
        Assert.Equal(["R@a1"], PromptAppendLoader.List(PromptAppendLoader.RootFor(_h.ModesPath))); // 거절되는 파일은 보이게 남긴다
    }

    [Fact]
    public async Task Configured_prompts_path_is_used_instead_of_modes_folder()
    {
        var other = Path.Combine(_h.Root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(other, "R"));
        File.WriteAllText(Path.Combine(other, "R", "a1.md"), "설정 폴더");
        WriteAppend("modes 옆");
        _h.Options.PromptsPath = other;
        File.SetLastWriteTimeUtc(_h.SettingsPath, DateTime.UtcNow.AddSeconds(5)); // 설정 다시 읽기

        await _h.Run("seq");
        var sent = ScriptedChatClient.SystemText(_h.Client.Received[0]);
        Assert.Contains("설정 폴더", sent);
        Assert.DoesNotContain("modes 옆", sent);
        Assert.Contains("모델별 덧붙임(설정 PromptsPath): R@a1", _h.Runner.Describe());
    }

    [Fact]
    public async Task Configured_prompts_path_missing_refuses_the_run()
    {
        WriteAppend("modes 옆");
        _h.Options.PromptsPath = Path.Combine(_h.Root, "없는 폴더");
        File.SetLastWriteTimeUtc(_h.SettingsPath, DateTime.UtcNow.AddSeconds(5));

        Assert.StartsWith("[모델별 덧붙임 폴더 오류]", await _h.Run("seq"));
        Assert.Empty(_h.Client.Received);
        Assert.Contains("모델별 덧붙임: 폴더 오류", _h.Runner.Describe());
    }

    [Fact]
    public void Relative_prompts_path_is_based_on_server_folder()
    {
        var server = Path.Combine(_h.Root, "server");
        Directory.CreateDirectory(Path.Combine(_h.Root, "src", "prompts"));
        var (root, error) = PromptAppendLoader.ResolveRoot(Path.Combine("..", "src", "prompts"), server, _h.ModesPath);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(Path.Combine(_h.Root, "src", "prompts")), root);
    }

    [Fact]
    public void Save_result_cannot_write_into_prompts()
    {
        var root = _h.Root;
        var (_, error) = SafePath.Resolve("Publish_Output/prompts/Reviewer/a1.md", root, ContextOptions.BuiltInDenyPatterns, SafePath.Access.Write);
        Assert.Contains("prompts", error);
    }
}
