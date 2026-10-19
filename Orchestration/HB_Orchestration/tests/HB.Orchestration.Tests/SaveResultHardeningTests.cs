using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>save_result 쓰기 보강 (계획서 2026-10-19_save_result-쓰기-보강): 쓰기 전용 금지 목록 · 원자적 쓰기 · 닫히지 않은 펜스 표시.</summary>
public sealed class SaveResultHardeningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-orch-save-").FullName;
    private readonly string _log;

    public SaveResultHardeningTests() => _log = Path.Combine(_root, "_log", "calls.jsonl");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task Log(string response, string runId = "run000000b")
    {
        await CallLog.AppendAsync(new CallRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12], Timestamp = DateTimeOffset.Now,
            Tool = "review", RunId = runId, Mode = "review", Step = 1, Attempt = 1,
            Agent = "gpt_main_sol", Model = "m", Prompt = "p", ProjectDir = _root, Response = response, Success = true,
        }, _log);
    }

    private string Save(string path, int? codeBlock = null, bool overwrite = false) =>
        ResultSaver.Save("run000000b", 1, path, codeBlock, overwrite, ContextOptions.BuiltInDenyPatterns, _log,
            ContextOptions.BuiltInWriteDenyPatterns);

    private string[] TempFiles() => Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories);

    // ── 쓰기 전용 금지 목록 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/commands/x.md")]
    [InlineData("sub/.claude/agents/a.md")]
    [InlineData(".mcp.json")]
    [InlineData("CLAUDE.md")]
    [InlineData("pkg/CLAUDE.md")]
    [InlineData("CLAUDE.local.md")]
    [InlineData("REVIEW_GUIDELINES.md")]
    [InlineData("hb-modes.json")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitattributes")]
    [InlineData(".vscode/tasks.json")]
    [InlineData(".idea/workspace.xml")]
    [InlineData("src/A.csproj")]
    [InlineData("All.sln")]
    [InlineData("Directory.Build.props")]
    [InlineData("nuget.config")]
    [InlineData("Packages/manifest.json")]
    [InlineData("ProjectSettings/ProjectSettings.asset")]
    [InlineData("tools/run.ps1")]
    [InlineData("build.bat")]
    [InlineData("x.cmd")]
    [InlineData("scripts/a.sh")]
    [InlineData("helper.py")]
    [InlineData("lib/a.dll")]
    [InlineData("Assets/Player.cs")]
    [InlineData("Assets/Editor/Hook.cs")]
    [InlineData("Packages/com.x/Runtime/A.cs")]
    [InlineData("Assets/Game.asmdef")]
    [InlineData("Assets/Sub/Ref.asmref")]
    public async Task Write_only_patterns_are_rejected(string path)
    {
        await Log("내용");
        var result = Save(path);
        Assert.StartsWith($"[저장 불가] '{path}': 쓰기 금지 파일입니다 (WriteDenyPatterns '", result);
        Assert.False(File.Exists(Path.Combine(_root, path)));
    }

    // /cross-review 1b661e26fe 반영: 하위 폴더 Unity 프로젝트 · 빠진 실행·빌드·지침 파일
    [Theory]
    [InlineData("Client/Assets/Player.cs")]
    [InlineData("Client/Assets/Editor/Hook.cs")]
    [InlineData("Client/Packages/manifest.json")]
    [InlineData("Client/ProjectSettings/ProjectSettings.asset")]
    [InlineData("Client/Assets/csc.rsp")]
    [InlineData("web/.vscode/launch.json")]
    [InlineData("sub/.github/workflows/a.yml")]
    [InlineData(".husky/pre-commit")]
    [InlineData(".githooks/pre-push")]
    [InlineData(".pre-commit-config.yaml")]
    [InlineData("Directory.Packages.props")]
    [InlineData("build/common.targets")]
    [InlineData("global.json")]
    [InlineData("package.json")]
    [InlineData("AGENTS.md")]
    [InlineData("GEMINI.md")]
    [InlineData(".cursor/rules/a.mdc")]
    [InlineData("docs/payload.vbs")]
    [InlineData("review_tmp/payload.wsf")]
    [InlineData("docs/a.js")]
    [InlineData("payload.com")]
    [InlineData("x.scr")]
    [InlineData("tools/a.psd1")]
    [InlineData("setup.reg")]
    [InlineData("go.lnk")]
    public async Task Nested_and_additional_patterns_are_rejected(string path)
    {
        await Log("내용");
        Assert.StartsWith($"[저장 불가] '{path}': 쓰기 금지 파일입니다 (WriteDenyPatterns '", Save(path));
    }

    [Fact]
    public async Task Missing_write_deny_list_falls_back_to_built_in()
    {
        await Log("내용");
        // writeDenyPatterns를 넘기지 않은 호출 (예전 시그니처) — 조용히 열리지 않는다
        var result = ResultSaver.Save("run000000b", 1, ".claude/settings.json", null, false, ContextOptions.BuiltInDenyPatterns, _log);
        Assert.Contains("쓰기 금지 파일입니다", result);
    }

    [Fact]
    public async Task Existing_target_is_rejected_before_writing_temp()
    {
        await Log("새 내용");
        File.WriteAllText(Path.Combine(_root, "a.md"), "원래 내용");
        var watcher = new FileSystemWatcher(_root, "*.tmp") { EnableRaisingEvents = true };
        var created = false;
        watcher.Created += (_, _) => created = true;

        Assert.Contains("이미 있습니다", Save("a.md"));
        watcher.Dispose();
        Assert.False(created);
    }

    [Fact]
    public async Task Long_file_name_that_fits_is_saved()
    {
        await Log("내용");
        var name = "docs/" + new string('a', 240) + ".md"; // 구성 요소 243자 — 대상 이름에 덧붙이는 임시 이름이면 255를 넘었다
        Assert.StartsWith("저장:", Save(name));
        Assert.Empty(TempFiles());
    }

    [Theory]
    [InlineData("docs/review.md")]
    [InlineData("docs/preflight/2026-10-19_x/p1_A.md")]
    [InlineData("review_tmp/A.cs")]
    [InlineData("docs/snippet.cs")]
    [InlineData("Assets/notes.md")]
    [InlineData("claude.md.txt")]
    public async Task Ordinary_paths_are_still_allowed(string path)
    {
        await Log("내용");
        Assert.StartsWith("저장:", Save(path));
        Assert.Equal("내용", File.ReadAllText(Path.Combine(_root, path)));
    }

    [Fact]
    public void Write_only_patterns_do_not_apply_to_read()
    {
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "지침");
        var (full, error) = SafePath.Resolve("CLAUDE.md", _root, ContextOptions.BuiltInDenyPatterns, SafePath.Access.Read,
            ContextOptions.BuiltInWriteDenyPatterns);
        Assert.Null(error);
        Assert.NotNull(full);
    }

    [Fact]
    public async Task Secret_patterns_still_win_with_their_own_message()
    {
        await Log("내용");
        Assert.Contains("보호된 파일이라 쓸 수 없습니다 (DenyPatterns '.env')", Save(".env"));
    }

    // ── 원자적 쓰기 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Existing_file_without_overwrite_is_kept_and_reported()
    {
        await Log("새 내용");
        var target = Path.Combine(_root, "docs", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "원래 내용");

        Assert.Equal("[저장 불가] 'docs/a.md'이(가) 이미 있습니다. 덮어쓰려면 overwrite=true로 다시 호출하세요.", Save("docs/a.md"));
        Assert.Equal("원래 내용", File.ReadAllText(target));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Overwrite_replaces_content_and_leaves_no_temp()
    {
        await Log("새 내용");
        var target = Path.Combine(_root, "a.md");
        File.WriteAllText(target, "원래 내용");

        Assert.StartsWith("저장:", Save("a.md", overwrite: true));
        Assert.Equal("새 내용", File.ReadAllText(target));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Failed_overwrite_keeps_original_and_removes_temp()
    {
        await Log("새 내용");
        var target = Path.Combine(_root, "locked.md");
        File.WriteAllText(target, "원래 내용");

        string result;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None)) // 대상을 잠가 이동(교체)을 실패시킨다
            result = Save("locked.md", overwrite: true);

        Assert.StartsWith("[저장 불가] 'locked.md': 쓰기 실패 (", result);
        Assert.DoesNotContain("이미 있습니다", result);
        Assert.Equal("원래 내용", File.ReadAllText(target));
        Assert.Empty(TempFiles());
    }

    // ── 닫히지 않은 펜스 ─────────────────────────────────────────────────

    [Fact]
    public async Task Unclosed_fence_is_saved_with_note()
    {
        await Log("앞\n```csharp\nvar a = 1;\n```\n뒤\n```csharp\nvar b = 2;\n// 잘림");
        var result = Save("docs/b.cs.txt", codeBlock: 2);

        Assert.Contains(", 코드 블록 2/2 — 닫히지 않은 블록 (응답이 잘렸을 수 있음)", result);
        Assert.Equal("var b = 2;\n// 잘림", File.ReadAllText(Path.Combine(_root, "docs", "b.cs.txt")));
    }

    [Fact]
    public async Task Closed_fence_has_no_note()
    {
        await Log("```csharp\nvar a = 1;\n```\n");
        var result = Save("docs/a.txt", codeBlock: 1);
        Assert.Contains(", 코드 블록 1/1)", result);
        Assert.DoesNotContain("닫히지 않은", result);
    }

    [Fact]
    public async Task Missing_block_number_message_is_unchanged()
    {
        await Log("```\nx\n```\n");
        Assert.Equal("[저장 불가] 응답에 코드 블록이 1개라 3번째가 없습니다.", Save("docs/a.txt", codeBlock: 3));
    }
}
