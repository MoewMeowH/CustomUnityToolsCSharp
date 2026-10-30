using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>권한 단계 → claude 인자 (+ 0번 실측 B · C).</summary>
public sealed class ClaudeArgsTests
{
    private const string Sid = "15eb061e-7153-4c14-83ba-083385ed6945";
    private static readonly string[] Models = ["opus", "haiku"];

    private static RunSpec Spec(PermissionLevel level, bool resume = false, string? mcp = "C:/data/mcp.json", string model = "opus") =>
        new(level, model, Sid, resume, mcp, "hb-orchestration", "규칙");

    private static string Joined(IReadOnlyList<string> args) => string.Join(" | ", args);

    [Fact]
    public void Chat_HasNoToolsAndNoMcp()
    {
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Chat, mcp: null), Models);
        Assert.Equal(
            "-p | --input-format | stream-json | --output-format | stream-json | --include-partial-messages | --verbose | --model | opus | --session-id | " + Sid +
            " | --append-system-prompt | 규칙 | --tools |  | --strict-mcp-config | --permission-prompt-tool | stdio",
            Joined(args));
        Assert.DoesNotContain("--mcp-config", args);
    }

    [Fact]
    public void Effort_AddedAfterModel_OnlyKnownLevels()
    {
        // Claude 앱 기능 B: 추론 강도 — 목록 값은 --effort, 없으면 넣지 않음, 모르는 값은 실행 전에 막음
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Read) with { Effort = "high" }, Models);
        Assert.Contains("--model | opus | --effort | high | --session-id", Joined(args));
        Assert.DoesNotContain("--effort", ClaudeArgs.Build(Spec(PermissionLevel.Read), Models));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Read) with { Effort = "ultra" }, Models));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Read) with { Effort = "high --dangerously" }, Models));
    }

    [Fact]
    public void Plan_PermissionModePlan_AndExitPlanModeTool_EditCommandOnly()
    {
        // Claude 앱 기능 C (2.1.312 실측): 계획 모드 + 승인 도구 ExitPlanMode. 편집은 acceptEdits 대신 plan
        var edit = Joined(ClaudeArgs.Build(Spec(PermissionLevel.Edit) with { Plan = true }, Models));
        Assert.Contains("TaskUpdate,WebSearch,WebFetch,Agent,ExitPlanMode | --permission-mode | plan", edit);
        Assert.DoesNotContain("acceptEdits", edit);
        var command = Joined(ClaudeArgs.Build(Spec(PermissionLevel.Command) with { Plan = true }, Models));
        Assert.Contains("TaskUpdate,WebSearch,WebFetch,ExitPlanMode | --permission-mode | plan", command);
        // 서브에이전트는 읽기 · 편집 방만 — 명령 방은 정의의 acceptEdits가 명령 카드를 건너뛰었다 (/self-review security 42f9d47dc4 C1)
        Assert.DoesNotContain(",Agent", Joined(ClaudeArgs.Build(Spec(PermissionLevel.Command), Models)));
        Assert.DoesNotContain(",Agent", command);
        Assert.Equal((true, true, false, false), (ClaudeArgs.AllowsAgents(PermissionLevel.Read), ClaudeArgs.AllowsAgents(PermissionLevel.Edit),
            ClaudeArgs.AllowsAgents(PermissionLevel.Command), ClaudeArgs.AllowsAgents(PermissionLevel.Chat)));
        Assert.DoesNotContain("ExitPlanMode", Joined(ClaudeArgs.Build(Spec(PermissionLevel.Edit), Models))); // 끄면 없음
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Read) with { Plan = true }, Models));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Chat, mcp: null) with { Plan = true }, Models));
        Assert.Equal(("acceptEdits", "default", (string?)null, (string?)null),
            (ClaudeArgs.PlanMode(PermissionLevel.Edit), ClaudeArgs.PlanMode(PermissionLevel.Command), ClaudeArgs.PlanMode(PermissionLevel.Read), ClaudeArgs.PlanMode(PermissionLevel.Chat)));
    }

    [Fact]
    public void PlanRequest_ParsesPlanText_AllowAnswerSetsMode()
    {
        var plan = ApprovalRequest.Parse("""{"tool_name":"ExitPlanMode","input":{"plan":"# 계획\n1. 고치기","planFilePath":"C:\\p.md"},"permission_suggestions":null}""")!;
        Assert.True(plan.IsPlan);
        Assert.Equal("# 계획\n1. 고치기", plan.Summary);
        Assert.Null(plan.Rule);
        var big = ApprovalRequest.Parse("{\"tool_name\":\"ExitPlanMode\",\"input\":{\"plan\":\"" + new string('x', ApprovalRequest.MaxSummaryChars + 5) + "\"}}")!;
        Assert.True(big.Truncated);
        Assert.False(big.DenyOnly); // 계획은 잘려도 허용 가능 (명령 실행이 아님)

        // 보이지 않는 키는 이름을 카드에, 허용 입력에서는 뺀다 (/self-review security d3dd55b237 C1)
        var withExtra = ApprovalRequest.Parse("""{"tool_name":"ExitPlanMode","input":{"plan":"p","planFilePath":"f","allowedPrompts":[{"tool":"Bash"}]}}""")!;
        Assert.Equal("allowedPrompts", withExtra.Extra);
        Assert.Equal("""{"plan":"p","planFilePath":"f"}""", ApprovalRequest.PlanInput("""{"plan":"p","planFilePath":"f","allowedPrompts":[{"tool":"Bash"}],"x":1}"""));
        Assert.Equal("{}", ApprovalRequest.PlanInput("not json"));

        using var doc = System.Text.Json.JsonDocument.Parse(PermissionDecision.AllowPlan("""{"plan":"p"}""", "acceptEdits"));
        var r = doc.RootElement;
        Assert.Equal("allow", r.GetProperty("behavior").GetString());
        Assert.Equal("p", r.GetProperty("updatedInput").GetProperty("plan").GetString());
        var perm = r.GetProperty("updatedPermissions")[0];
        Assert.Equal(("setMode", "acceptEdits", "session"), (perm.GetProperty("type").GetString(), perm.GetProperty("mode").GetString(), perm.GetProperty("destination").GetString()));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("  ", null, null)]
    [InlineData("max", "max", null)]
    [InlineData(" low ", "low", null)]
    [InlineData("HIGH", null, "알 수 없는 추론 강도입니다.")] // 대소문자도 CLI 값 그대로만
    [InlineData("auto", null, "알 수 없는 추론 강도입니다.")]
    public void RoomEffort_Clean(string? input, string? value, string? error) => Assert.Equal((value, error), RoomEffort.Clean(input));

    [Fact]
    public void Fork_FirstRunResumesSourceIntoThisSession()
    {
        // 갈래 방 첫 실행 (2-2 c, 0번 실측: fork-session + session-id → 그 ID로 갈라짐)
        const string source = "2ac92fd3-e819-408f-a834-c33490b7c038";
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Chat, mcp: null) with { ForkFrom = source }, Models);
        Assert.Contains("--resume | " + source + " | --fork-session | --session-id | " + Sid, Joined(args));
        Assert.Single(args, a => a == "--session-id");

        // 이어 쓰기(resume)면 갈래 인자를 쓰지 않는다
        var resumed = ClaudeArgs.Build(Spec(PermissionLevel.Chat, resume: true, mcp: null) with { ForkFrom = source }, Models);
        Assert.DoesNotContain("--fork-session", resumed);
        Assert.Contains("--resume | " + Sid, Joined(resumed));

        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Chat, mcp: null) with { ForkFrom = "not-a-uuid" }, Models));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Chat, mcp: null) with { ForkFrom = Sid }, Models));
    }

    [Fact]
    public void Read_HasReadToolsAndOnlyTheOneMcpServer()
    {
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Read), Models);
        // AskUserQuestion: 질문 카드 (3차 a — stdio 호스트에서만 도구 목록에 들어온다)
        Assert.Contains("--tools | Read,Grep,Glob,AskUserQuestion,TaskCreate,TaskGet,TaskList,TaskUpdate,WebSearch,WebFetch,Agent | --strict-mcp-config | --mcp-config | C:/data/mcp.json | --allowedTools | mcp__hb-orchestration", Joined(args));
        Assert.DoesNotContain("--permission-mode", args);
        Assert.Equal("stdio", args[args.ToList().IndexOf("--permission-prompt-tool") + 1]);
    }

    [Fact]
    public void AskRunMode_AllowsOtherServerToolsButNotRunMode()
    {
        // 외부 요청 전에 묻기: 서버 전체 대신 도구 넷만 미리 허용 — run_mode는 앱에 물어 카드가 뜬다
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Read) with { AskRunMode = true }, Models);
        Assert.Contains("--allowedTools | mcp__hb-orchestration__list_modes | mcp__hb-orchestration__save_result | mcp__hb-orchestration__record_verdicts | " +
                        "mcp__hb-orchestration__server_info", Joined(args));
        Assert.DoesNotContain("mcp__hb-orchestration", args);
        Assert.DoesNotContain(args, a => a.Contains("run_mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Edit_AddsEditToolsAndAcceptEdits_ButNoBash()
    {
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Edit), Models);
        Assert.Contains("--tools | Read,Grep,Glob,Edit,Write,AskUserQuestion,TaskCreate,TaskGet,TaskList,TaskUpdate,WebSearch,WebFetch,Agent | --permission-mode | acceptEdits", Joined(args));
        Assert.DoesNotContain(args, a => a.Contains("Bash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--strict-mcp-config", args);
    }

    [Fact]
    public void Edit_DeniesSettingsScriptsAndInstructionFiles()
    {
        // 편집 금지 경로는 --disallowedTools 바로 뒤에 Edit(…)으로 이어지고, 다음 인자는 다른 플래그다 (가변 인자가 끝나는 곳).
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Edit), Models).ToList();
        var at = args.IndexOf("--disallowedTools");
        Assert.True(at > 0);
        var rules = args.Skip(at + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Equal(ClaudeArgs.EditDenied.Select(p => $"Edit({p})"), rules);
        foreach (var must in new[] { "Edit(Orchestration/HB_Chat/**)", "Edit(**/appsettings*.json)", "Edit(**/CLAUDE.md)", "Edit(**/*.bat)", "Edit(Orchestration/Publish_Output/**)" })
            Assert.Contains(must, rules);

        // 읽기 · 대화 단계에는 편집 도구가 없어 금지 규칙도 없다.
        Assert.DoesNotContain("--disallowedTools", ClaudeArgs.Build(Spec(PermissionLevel.Read), Models));
    }

    [Fact]
    public void Resume_UsesResumeAndKeepsRoomRules()
    {
        // 방 규칙은 매 말 넣는다 — 안 넣으면 대화가 압축된 뒤 규칙이 빠진다 (/self-review 0a3b1d5155 C2).
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Chat, resume: true, mcp: null), Models);
        Assert.Contains("--resume | " + Sid, Joined(args));
        Assert.DoesNotContain("--session-id", args);
        Assert.Contains("--append-system-prompt | 규칙", Joined(args));
    }

    [Fact]
    public void RejectsModelOutsideList() =>
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Chat, model: "sonnet"), Models));

    [Fact]
    public void RejectsUnsafeModelEvenIfListed()
    {
        const string model = "opus --dangerously-skip-permissions";
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Chat, model: model), [.. Models, model]));
    }

    [Fact]
    public void RejectsNonUuidSession() =>
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Chat) with { SessionId = "../x" }, Models));

    [Fact]
    public void ReadAndEditNeedMcpConfig()
    {
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Read, mcp: null), Models));
        Assert.Throws<ArgumentException>(() => ClaudeArgs.Build(Spec(PermissionLevel.Edit, mcp: " "), Models));
    }

    [Fact]
    public void DangerousTextAnywhereThrows() =>
        Assert.Throws<InvalidOperationException>(() =>
            ClaudeArgs.Build(Spec(PermissionLevel.Read, mcp: "C:/bypass/mcp.json"), Models));
}
