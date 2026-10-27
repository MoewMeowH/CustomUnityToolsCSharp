using System.Text.Json;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>승인 요청 읽기 · 규칙 모양 · 훅 응답 · 명령 단계 인자 (2차 계획서 2-1, 0번 실측 A의 본문 모양).</summary>
public sealed class ApprovalTests
{
    /// <summary>0번 실측 A에서 받은 본문에서 경로 · ID만 줄인 것.</summary>
    private const string HookBody = """
        {"session_id":"37cbb203-6dfe-45d4-ae04-3991504ed663","cwd":"C:\\ws","permission_mode":"default","hook_event_name":"PermissionRequest",
         "tool_name":"Bash","tool_input":{"command":"python -c \"print('probe-ok')\"","description":"Run python probe print command"},
         "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"python -c \"print('probe-ok')\""}],"behavior":"allow","destination":"localSettings"}]}
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
            {"tool_name":"Write","tool_input":{"file_path":"C:\\ws\\.claude\\x.json","content":"{}"},
             "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Write","ruleContent":"//C:/ws/.claude/**"}],"behavior":"allow"}]}
            """);
        Assert.NotNull(r);
        Assert.Equal("Write", r.Tool);
        Assert.Equal("C:\\ws\\.claude\\x.json", r.Summary);
        Assert.Null(r.Rule); // 명령 도구가 아닌 규칙은 "항상 허용"으로 받지 않는다
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"tool_input":{}}""")]
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
        var body = $$"""{"tool_name":"Bash","tool_input":{"command":"{{command}}"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo"}],"behavior":"allow"}]}""";
        var r = ApprovalRequest.Parse(body)!;
        Assert.True(r.Truncated);
        Assert.Null(r.Rule); // 다 보지 못한 명령은 항상 허용도 없다
        Assert.Equal(ApprovalRequest.MaxSummaryChars + 1, r.Summary.Length);

        Assert.False(ApprovalRequest.Parse(HookBody)!.Truncated);
    }

    [Theory]
    [InlineData("http://localhost:5290", "http://127.0.0.1:5290")]
    [InlineData("http://0.0.0.0:5290", "http://127.0.0.1:5290")]
    [InlineData("http://[::]:5290", "http://127.0.0.1:5290")]
    [InlineData("http://+:5290", "http://127.0.0.1:5290")]
    [InlineData("http://*:5290", "http://127.0.0.1:5290")]
    [InlineData("http://127.0.0.1:61234", "http://127.0.0.1:61234")]
    public void LoopbackUrl(string address, string expected) => Assert.Equal(expected, ApprovalEndpoint.LoopbackUrl(address));

    [Fact]
    public void HookJson_AllowAndDeny()
    {
        using var allow = JsonDocument.Parse(new ApprovalAnswer(true, null).ToHookJson());
        var d = allow.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PermissionRequest", d.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", d.GetProperty("decision").GetProperty("behavior").GetString());

        using var deny = JsonDocument.Parse(new ApprovalAnswer(false, "안 돼요").ToHookJson());
        var dd = deny.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
        Assert.Equal("deny", dd.GetProperty("behavior").GetString());
        Assert.Equal("안 돼요", dd.GetProperty("message").GetString());
    }

    private static RunSpec CommandSpec(IReadOnlyList<string>? rules = null, string? url = "http://127.0.0.1:5290/approve?room=0123456789ab&token=abc") =>
        new(PermissionLevel.Command, "opus", "15eb061e-7153-4c14-83ba-083385ed6945", false, "C:/data/mcp.json", "hb-orchestration", "규칙",
            url, 600, rules);

    [Fact]
    public void CommandArgs_AddCommandToolsHookAndRules()
    {
        var args = ClaudeArgs.Build(CommandSpec(["Bash(git status)", "Bash(*)"]), ["opus"]).ToList();
        Assert.Equal("Read,Grep,Glob,Edit,Write,Bash,PowerShell", args[args.IndexOf("--tools") + 1]);
        // acceptEdits는 cp · mv 같은 파일 명령을 묻지 않고 허용해 카드를 지나치게 하므로 쓰지 않는다 (36e4e20e04 C1).
        Assert.DoesNotContain("--permission-mode", args);
        Assert.Contains("Edit(**/CLAUDE.md)", args); // 편집 금지는 그대로

        var allowed = args.Skip(args.IndexOf("--allowedTools") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Equal(["mcp__hb-orchestration", ClaudeArgs.CommandEditAllow, "Bash(git status)"], allowed); // 작업 폴더 안 편집 + 규칙, 넓은 규칙은 걸러짐

        using var settings = JsonDocument.Parse(args[args.IndexOf("--settings") + 1]);
        var matcher = settings.RootElement.GetProperty("hooks").GetProperty("PermissionRequest")[0];
        Assert.Equal("Bash|PowerShell", matcher.GetProperty("matcher").GetString()); // 명령만 카드로 (36e4e20e04 C2)
        var hook = matcher.GetProperty("hooks")[0];
        Assert.Equal("http", hook.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:5290/approve?room=0123456789ab&token=abc", hook.GetProperty("url").GetString());
        Assert.Equal(600, hook.GetProperty("timeout").GetInt32());
        Assert.Equal("none", args[args.IndexOf("--permission-prompts") + 1]);
    }

    [Fact]
    public void CommandArgs_NeedHookUrl() =>
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(CommandSpec(url: null), ["opus"]));

    [Fact]
    public void OtherLevels_HaveNoCommandToolsOrHook()
    {
        foreach (var level in new[] { PermissionLevel.Chat, PermissionLevel.Read, PermissionLevel.Edit })
        {
            var spec = CommandSpec(["Bash(git status)"]) with { Level = level };
            var args = ClaudeArgs.Build(spec, ["opus"]);
            Assert.DoesNotContain("--settings", args);
            Assert.DoesNotContain(args, a => a.Contains("Bash", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("POST", "/approve", true)]
    [InlineData("POST", "/APPROVE", true)]
    [InlineData("POST", "/Room/0123456789ab", false)]
    [InlineData("PUT", "/approve", false)]
    public void SameOrigin_ExemptsOnlyApprovalPost(string method, string path, bool ok) =>
        Assert.Equal(ok, SameOriginGuard.IsAllowed(method, null, null, "http", "127.0.0.1:5290", path));
}
