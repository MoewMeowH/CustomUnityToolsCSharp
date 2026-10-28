using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>입력칸 '/' 명령 · 스킬 목록 (Claude 앱 기능 A): init 읽기 · 파일 설명 · 합치기 · 이름 모양.</summary>
public sealed class CommandCatalogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hbchat-cmd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string rel, string text)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Init_ReadsCommandsSkillsTerminalAndPlugins()
    {
        var ev = Assert.IsType<InitEvent>(StreamParser.Parse(
            """{"type":"system","subtype":"init","session_id":"s","slash_commands":["ping","echo",1,"compact"],"skills":["echo"],"terminal_slash_commands":["color"],"plugins":[{"name":"unity","path":"C:\\p\\unity"},{"name":"x"}]}"""));
        Assert.Equal(["ping", "echo", "compact"], ev.SlashCommands);
        Assert.Equal(["echo"], ev.Skills);
        Assert.Equal(["color"], ev.TerminalCommands);
        Assert.Equal([("unity", @"C:\p\unity")], ev.Plugins); // 경로 없는 줄은 뺀다
        Assert.Empty(Assert.IsType<InitEvent>(StreamParser.Parse("""{"type":"system","subtype":"init"}""")).SlashCommands);
        Assert.Equal(["Explore", "general-purpose"], Assert.IsType<InitEvent>(StreamParser.Parse("""{"type":"system","subtype":"init","agents":["Explore","general-purpose"]}""")).Agents);
    }

    [Fact]
    public void TaskEvents_ParseStartProgressDone()
    {
        // Claude 앱 기능 D (2.1.312 실측 모양)
        Assert.Equal(new TaskEvent("started", "t1", "writer", "Write out.txt", null, null),
            StreamParser.Parse("""{"type":"system","subtype":"task_started","task_id":"t1","description":"Write out.txt","subagent_type":"writer","is_backgrounded":true}"""));
        Assert.Equal(new TaskEvent("progress", "t1", "general-purpose", "Reading gp.txt", null, 3),
            StreamParser.Parse("""{"type":"system","subtype":"task_progress","task_id":"t1","description":"Reading gp.txt","subagent_type":"general-purpose","usage":{"tool_uses":3}}"""));
        Assert.Equal(new TaskEvent("done", "t1", null, null, "failed", null),
            StreamParser.Parse("""{"type":"system","subtype":"task_notification","task_id":"t1","status":"failed"}"""));
        Assert.IsType<OtherEvent>(StreamParser.Parse("""{"type":"system","subtype":"task_started"}""")); // task_id 없음
    }

    [Fact]
    public void TaskEvents_BackgroundBash_KindAndOutputFile()
    {
        // 백그라운드 명령 (2026-10-29 실측 모양): task_started의 task_type, task_notification의 output_file
        var started = Assert.IsType<TaskEvent>(StreamParser.Parse("""{"type":"system","subtype":"task_started","task_id":"b1","description":"Serve","is_backgrounded":true,"task_type":"local_bash"}"""));
        Assert.Equal(TaskEvent.BashKind, started.Kind);
        Assert.Null(started.Agent);
        var done = Assert.IsType<TaskEvent>(StreamParser.Parse("""{"type":"system","subtype":"task_notification","task_id":"b1","status":"completed","output_file":"C:\\t\\b1.output"}"""));
        Assert.Equal("done", done.Phase);
        Assert.Equal("C:\\t\\b1.output", done.OutputFile);
    }

    [Fact]
    public void TaskEvents_LongTextCapped()
    {
        // /self-review security 42f9d47dc4 C3: 에이전트 정의 · 지휘자가 정하는 글은 상한까지만 화면 · 기록으로 간다
        var line = $$"""{"type":"system","subtype":"task_started","task_id":"t1","description":"{{new string('d', 5000)}}","subagent_type":"{{new string('a', 500)}}","status":"{{new string('s', 500)}}"}""";
        var t = Assert.IsType<TaskEvent>(StreamParser.Parse(line));
        Assert.Equal(StreamParser.StatusChars + 1, t.Description!.Length);
        Assert.Equal(81, t.Agent!.Length);
        Assert.Equal(41, t.Status!.Length);
    }

    [Fact]
    public void Frontmatter_DescriptionHintName()
    {
        Assert.Equal(("x", "한 줄 설명", "<숫자>"), CommandCatalog.Frontmatter("---\nname: x\ndescription: \"한 줄 설명\"\nargument-hint: <숫자>\n---\n본문"));
        Assert.Equal((null, "여러 줄 이어 붙임", null), CommandCatalog.Frontmatter("---\r\ndescription: >\r\n  여러 줄\r\n  이어 붙임\r\n---\r\n"));
        Assert.Equal((null, null, null), CommandCatalog.Frontmatter("앞머리 없음\ndescription: x"));
        Assert.Equal(CommandCatalog.MaxDescription + 1, CommandCatalog.Frontmatter("---\ndescription: " + new string('가', 300) + "\n---")!.Description!.Length);
    }

    [Fact]
    public void FilesOnly_WhenNoInit_WorkspaceWinsAndSubfoldersNamespace()
    {
        var ws = Path.Combine(_dir, "ws");
        var user = Path.Combine(_dir, "user");
        Write("ws/.claude/commands/ping.md", "---\ndescription: 작업 폴더 핑\nargument-hint: <n>\n---\nPONG");
        Write("ws/.claude/commands/tools/lint.md", "---\ndescription: 린트\n---\n");
        Write("ws/.claude/skills/echo/SKILL.md", "---\nname: echo\ndescription: 에코 스킬\n---\n");
        Write("user/commands/ping.md", "---\ndescription: 사용자 핑 (가려짐)\n---\n");
        Write("user/skills/synced/abc/docx/SKILL.md", "---\nname: docx\ndescription: 워드\n---\n"); // 관리형처럼 깊은 폴더
        Write("ws/.claude/commands/big.md", "---\ndescription: 큼\n---\n" + new string('x', CommandCatalog.MaxBytes + 1));

        var list = CommandCatalog.Build(null, ws, user);
        Assert.Equal(new CommandInfo("ping", "작업 폴더 핑", "<n>", "command"), list.Single(c => c.Name == "ping"));
        Assert.Equal("린트", list.Single(c => c.Name == "tools:lint").Description);
        Assert.Equal("skill", list.Single(c => c.Name == "echo").Kind);
        Assert.Equal("워드", list.Single(c => c.Name == "docx").Description);
        Assert.DoesNotContain(list, c => c.Name == "big"); // 큰 파일은 건너뜀
        Assert.Equal("command", list[0].Kind); // 명령 먼저
    }

    [Fact]
    public void WithInit_InitIsTheList_FilesGiveDescriptions()
    {
        var ws = Path.Combine(_dir, "ws");
        var user = Path.Combine(_dir, "user");
        Write("ws/.claude/commands/ping.md", "---\ndescription: 핑\n---\n");
        Write("ws/.claude/commands/stale.md", "---\ndescription: init에 없음\n---\n");
        Write("user/skills/synced/abc/docx/SKILL.md", "---\nname: docx\ndescription: 워드\n---\n");
        var plugin = Path.Combine(_dir, "plugins", "unity");
        Write("plugins/unity/skills/ui/SKILL.md", "---\nname: ui\ndescription: 유니티 UI\n---\n");
        var init = new InitEvent("s", "2.1.312", [])
        {
            SlashCommands = ["ping", "anthropic-skills:docx", "unity:ui", "compact", "color", "mystery", "__remote-workflow", "bad name"],
            Skills = ["anthropic-skills:docx", "unity:ui"],
            TerminalCommands = ["color"],
            Plugins = [("unity", plugin), ("builtin-thing", "builtin")],
        };
        var list = CommandCatalog.Build(init, ws, user);
        Assert.Equal(["ping", "anthropic-skills:docx", "unity:ui", "compact", "mystery", "color"], list.Select(c => c.Name));
        Assert.DoesNotContain(list, c => c.Name == "stale"); // init이 정답 목록
        Assert.Equal("워드", list.Single(c => c.Name == "anthropic-skills:docx").Description); // ':' 뒤 이름으로 설명 찾기
        Assert.Equal("유니티 UI", list.Single(c => c.Name == "unity:ui").Description);         // 플러그인 경로
        Assert.Equal(new CommandInfo("compact", "대화를 요약해 컨텍스트를 줄임", null, "builtin"), list.Single(c => c.Name == "compact"));
        Assert.Equal("Claude 기본 명령", list.Single(c => c.Name == "mystery").Description);
        var color = list.Single(c => c.Name == "color");
        Assert.Equal("terminal", color.Kind);
        Assert.Contains("터미널 전용", color.Description);
    }

    [Fact]
    public void WithInit_PluginNamesDoNotBorrowOtherDescriptions_SkillKindWins()
    {
        // db1b130ff2 C2: 플러그인 경로를 못 읽어도 사용자 명령 review.md의 설명 · 종류가 "myplug:review"에 붙지 않는다
        var ws = Path.Combine(_dir, "ws");
        var user = Path.Combine(_dir, "user");
        Write("user/commands/review.md", "---\ndescription: 내 리뷰 명령\n---\n");
        Write("user/commands/lint.md", "---\ndescription: 내 린트\n---\n");
        var init = new InitEvent("s", "v", [])
        {
            SlashCommands = ["myplug:review", "other:lint", "review"],
            Skills = ["myplug:review", "review"],
            Plugins = [("myplug", Path.Combine(_dir, "missing-plugin"))],
        };
        var list = CommandCatalog.Build(init, ws, user);
        Assert.Equal(new CommandInfo("myplug:review", null, null, "skill"), list.Single(c => c.Name == "myplug:review"));
        Assert.Equal("내 린트", list.Single(c => c.Name == "other:lint").Description); // 플러그인이 아닌 접두어는 끝부분 일치
        var review = list.Single(c => c.Name == "review");
        Assert.Equal(("skill", "내 리뷰 명령"), (review.Kind, review.Description)); // init 스킬 소속이 종류를 정한다
    }

    [Fact]
    public void Frontmatter_PlainMultilineValue()
    {
        // 지휘자 검토 R3: 키 뒤가 비고 들여쓴 줄이 이어지면 이어 붙인다
        Assert.Equal("첫 줄 둘째 줄", CommandCatalog.Frontmatter("---\ndescription:\n  첫 줄\n  둘째 줄\nname: x\n---\n").Description);
    }

    [Fact]
    public void FilesOnly_BudgetPerLocation_AndSortIsOrdinal()
    {
        // C3: 앞 위치(작업 폴더 명령)에 문서가 상한보다 많아도 뒤 위치(사용자 스킬)는 읽힌다. C8: 이름 순은 Ordinal
        var ws = Path.Combine(_dir, "ws");
        var user = Path.Combine(_dir, "user");
        for (var i = 0; i < CommandCatalog.MaxFiles + 5; i++) Write($"ws/.claude/commands/doc{i:D3}.md", "x");
        Write("user/skills/late/SKILL.md", "---\nname: late\ndescription: 뒤 위치\n---\n");
        var list = CommandCatalog.Build(null, ws, user);
        Assert.Equal("뒤 위치", list.Single(c => c.Name == "late").Description);
        Assert.Equal(CommandCatalog.MaxFiles, list.Count(c => c.Kind == "command"));
        var names = list.Where(c => c.Kind == "command").Select(c => c.Name).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Theory]
    [InlineData("release.notes", true)] // db1b130ff2 C5
    [InlineData("ping", true)]
    [InlineData("unity:ui-uitk", true)]
    [InlineData("a_b-c", true)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    [InlineData("<b>", false)]
    [InlineData("", false)]
    public void Name_Shape(string name, bool ok) => Assert.Equal(ok, CommandCatalog.IsName(name));
}
