using System.Text.Json;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>승인 요청 읽기 · 규칙 모양 · 호스트 답 · 명령 단계 인자 (2차 2-1, 3차 a — stdio 호스트의 control_request 모양).</summary>
public sealed class ApprovalTests
{
    /// <summary>3차 0번 실측에서 받은 can_use_tool 요청(request 객체)에서 ID만 줄인 것.</summary>
    private const string HookBody = """
        {"subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash",
         "input":{"command":"python -c \"print('probe-ok')\"","description":"Run python probe print command"},"description":"Run python probe print command",
         "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"python -c \"print('probe-ok')\""}],"behavior":"allow","destination":"localSettings"}],
         "decision_reason":"This command requires approval","decision_reason_type":"other","tool_use_id":"toolu_01"}
        """;

    [Fact]
    public void Parse_ReadsToolCommandDescriptionAndNarrowRule()
    {
        var r = ApprovalRequest.Parse(HookBody);
        Assert.NotNull(r);
        Assert.Equal("Bash", r.Tool);
        Assert.Equal("python -c \"print('probe-ok')\"", r.Summary);
        Assert.Equal("Run python probe print command", r.Description);
        Assert.Equal("Bash(python -c \"print('probe-ok')\")", r.Rule);
    }

    [Fact]
    public void Parse_OtherToolsShowPathAndHaveNoRule()
    {
        var r = ApprovalRequest.Parse("""
            {"tool_name":"Write","input":{"file_path":"C:\\ws\\.claude\\x.json","content":"{}"},
             "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Write","ruleContent":"//C:/ws/.claude/**"}],"behavior":"allow"}]}
            """);
        Assert.NotNull(r);
        Assert.Equal("Write", r.Tool);
        Assert.Equal("C:\\ws\\.claude\\x.json", r.Summary);
        Assert.Null(r.Rule); // 명령 도구가 아닌 규칙은 "항상 허용"으로 받지 않는다
    }

    /// <summary>
    /// 찾기 · 목록 도구는 "패턴 — 경로"로 요약 (사용자 보고 2026-10-30: 입력 JSON이 백슬래시 두 번으로 기록에 남았다). 그 밖의 입력은 추가 입력으로.
    /// 명령 카드는 path · pattern이 있어도 요약은 명령 그대로, 그 키는 추가 입력에 보인다 (본 것 = 허용되는 것).
    /// </summary>
    [Fact]
    public void Parse_SearchToolsShowPatternAndPath()
    {
        var grep = ApprovalRequest.Parse("""{"tool_name":"Grep","input":{"pattern":"localhost","path":"C:\\Windows\\System32\\drivers\\etc","-i":true}}""")!;
        Assert.Equal(@"localhost — C:\Windows\System32\drivers\etc", grep.Summary);
        Assert.Equal("-i=true", grep.Extra);
        Assert.Equal("*.ini", ApprovalRequest.Parse("""{"tool_name":"Glob","input":{"pattern":"*.ini"}}""")!.Summary);
        Assert.Equal(@"C:\Windows", ApprovalRequest.Parse("""{"tool_name":"LS","input":{"path":"C:\\Windows"}}""")!.Summary);
        var bash = ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"ls","path":"C:\\x"}}""")!;
        Assert.Equal("ls", bash.Summary);
        Assert.Contains(@"path=C:\x", bash.Extra);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"input":{}}""")]
    public void Parse_RejectsWrongShape(string body) => Assert.Null(ApprovalRequest.Parse(body));

    [Theory]
    [InlineData("Bash(git status)", true)]
    [InlineData("PowerShell(Get-ChildItem)", true)]
    [InlineData("Bash(python -c \"print(1+1)\")", true)] // 괄호 짝이 맞는 정확한 명령 (실제 확인에서 하나로 동작)
    [InlineData("Bash(npm run test:*)", false)]           // 접두 와일드카드 — 정확한 명령만 받는다
    [InlineData("Bash(python:*)", false)]
    [InlineData("Bash(c*)", false)]
    [InlineData("Bash(*)", false)]
    [InlineData("Bash()", false)]
    [InlineData("Bash(x) Bash(y)", false)]                // 괄호 짝이 안 맞음 = 값이 둘로 읽힐 수 있는 모양
    [InlineData("Bash(echo ))", false)]
    [InlineData("Write(**)", false)]                      // 명령 도구만
    [InlineData("Bash(a\nb)", false)]
    [InlineData("Bash(echo --dangerously-skip-permissions)", false)] // 저장하면 인자 검사에 걸려 방이 막힌다
    [InlineData(null, false)]
    public void RuleShape(string? rule, bool ok) => Assert.Equal(ok, ApprovalRules.IsAcceptable(rule));

    [Fact]
    public void Parse_LongCommand_IsTruncatedWithoutRule()
    {
        var command = new string('a', ApprovalRequest.MaxSummaryChars + 5);
        var body = $$"""{"tool_name":"Bash","input":{"command":"{{command}}"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo"}],"behavior":"allow"}]}""";
        var r = ApprovalRequest.Parse(body)!;
        Assert.True(r.Truncated);
        Assert.Null(r.Rule); // 다 보지 못한 명령은 항상 허용도 없다
        Assert.Equal(ApprovalRequest.MaxSummaryChars + 1, r.Summary.Length);

        Assert.False(ApprovalRequest.Parse(HookBody)!.Truncated);
    }

    [Fact]
    public void Parse_ShowsExtraInputAndFlagsRiskyNames()
    {
        // 명령 · 설명 밖의 입력은 Extra로 (허용하면 같이 실행된다 — /self-review 5e747ff851 C1)
        var plain = ApprovalRequest.Parse(HookBody)!;
        Assert.Null(plain.Extra);
        Assert.False(plain.DenyOnly);

        var bg = ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"x","description":"d","timeout":5000,"run_in_background":true},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"x"}],"behavior":"allow"}]}""")!;
        Assert.Equal("timeout=5000, run_in_background=true", bg.Extra);
        Assert.False(bg.DenyOnly);
        Assert.Equal("Bash(x)", bg.Rule);

        // 위험한 이름은 거절만 · 규칙 없음. false면 위험하지 않다.
        var risky = ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"x","dangerouslyDisableSandbox":true},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"x"}],"behavior":"allow"}]}""")!;
        Assert.True(risky.Risky);
        Assert.True(risky.DenyOnly);
        Assert.Null(risky.Rule);
        Assert.False(ApprovalRequest.Parse("""{"tool_name":"Bash","input":{"command":"x","dangerouslyDisableSandbox":false}}""")!.Risky);

        // 긴 추가 입력은 잘리고 거절만
        var longValue = new string('v', ApprovalRequest.MaxExtraValueChars + 1);
        Assert.True(ApprovalRequest.Parse($$$"""{"tool_name":"Bash","input":{"command":"x","env":"{{{longValue}}}"}}""")!.Truncated);
    }

    [Fact]
    public void Log_CutsLongDecision()
    {
        // 질문 카드의 답(기타 글)도 300자까지만 남긴다 (/self-review 5e747ff851 C2)
        var dir = Path.Combine(Path.GetTempPath(), "hbchat-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ApprovalLog(() => dir).Write("0123456789ab", new ApprovalRequest("AskUserQuestion", "q", null, null), "답: " + new string('a', 1000));
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "approvals.jsonl")));
            Assert.Equal(301, doc.RootElement.GetProperty("decision").GetString()!.Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DecisionJson_AllowKeepsInputAndDenyCarriesMessage()
    {
        // 허용: updatedInput = 받은 입력 그대로 (3차 0번 실측 — SDK canUseTool과 같은 모양)
        using var allow = JsonDocument.Parse(new ApprovalAnswer(true, null).ToDecisionJson("""{"command":"git status","description":"d"}"""));
        Assert.Equal("allow", allow.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("git status", allow.RootElement.GetProperty("updatedInput").GetProperty("command").GetString());

        using var deny = JsonDocument.Parse(new ApprovalAnswer(false, "안 돼요").ToDecisionJson("{}"));
        Assert.Equal("deny", deny.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("안 돼요", deny.RootElement.GetProperty("message").GetString());
        Assert.False(deny.RootElement.TryGetProperty("updatedInput", out _));
    }

    private static RunSpec CommandSpec(IReadOnlyList<string>? rules = null) =>
        new(PermissionLevel.Command, "opus", "15eb061e-7153-4c14-83ba-083385ed6945", false, "C:/data/mcp.json", "hb-orchestration", "규칙", rules);

    [Fact]
    public void CommandArgs_AddCommandToolsStdioHostAndRules()
    {
        var args = ClaudeArgs.Build(CommandSpec(["Bash(git status)", "Bash(*)"]), ["opus"]).ToList();
        Assert.Equal("Read,Grep,Glob,Edit,Write,Bash,PowerShell,AskUserQuestion,TaskCreate,TaskGet,TaskList,TaskUpdate,WebSearch,WebFetch", args[args.IndexOf("--tools") + 1]);
        // acceptEdits는 cp · mv 같은 파일 명령을 묻지 않고 허용해 카드를 지나치게 하므로 쓰지 않는다 (36e4e20e04 C1).
        Assert.DoesNotContain("--permission-mode", args);
        Assert.Contains("Edit(**/CLAUDE.md)", args); // 편집 금지는 그대로

        var allowed = args.Skip(args.IndexOf("--allowedTools") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Equal(["mcp__hb-orchestration", ClaudeArgs.CommandEditAllow, "Bash(git status)"], allowed); // 작업 폴더 안 편집 + 규칙, 넓은 규칙은 걸러짐

        // 승인은 stdio 호스트 한 곳 — HTTP 훅을 붙이지 않는다 (훅의 거절을 호스트의 허용이 이긴다, 3차 0번 실측)
        Assert.Equal("stdio", args[args.IndexOf("--permission-prompt-tool") + 1]);
        Assert.DoesNotContain("--settings", args);
        Assert.DoesNotContain("--permission-prompts", args);
    }

    [Fact]
    public void OtherLevels_HaveNoCommandToolsOrHook()
    {
        foreach (var level in new[] { PermissionLevel.Chat, PermissionLevel.Read, PermissionLevel.Edit })
        {
            var spec = CommandSpec(["Bash(git status)"]) with { Level = level };
            var args = ClaudeArgs.Build(spec, ["opus"]);
            Assert.DoesNotContain("--settings", args);
            Assert.DoesNotContain(args, a => a.Contains("Bash", StringComparison.Ordinal));
            Assert.Equal("stdio", args[args.ToList().IndexOf("--permission-prompt-tool") + 1]); // 거절도 호스트가 한다
        }
    }

    [Fact]
    public void SameOrigin_HasNoApprovalExemptionAnymore()
    {
        // 2-1의 /approve 예외는 3차 a에서 없앴다 — Origin · Referer 없는 POST는 모두 거절
        Assert.False(SameOriginGuard.IsAllowed("POST", null, null, "http", "127.0.0.1:5290"));
        Assert.True(SameOriginGuard.IsAllowed("POST", "http://127.0.0.1:5290", null, "http", "127.0.0.1:5290"));
    }
}
