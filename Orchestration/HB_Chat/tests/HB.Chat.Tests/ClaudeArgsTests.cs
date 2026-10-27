using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>권한 단계 → claude 인자 (계획서 4번 표 + 0번 실측 B · C).</summary>
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
            "-p | --output-format | stream-json | --include-partial-messages | --verbose | --model | opus | --session-id | " + Sid +
            " | --append-system-prompt | 규칙 | --tools |  | --strict-mcp-config | --permission-prompts | none",
            Joined(args));
        Assert.DoesNotContain("--mcp-config", args);
    }

    [Fact]
    public void Read_HasReadToolsAndOnlyTheOneMcpServer()
    {
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Read), Models);
        Assert.Contains("--tools | Read,Grep,Glob | --strict-mcp-config | --mcp-config | C:/data/mcp.json | --allowedTools | mcp__hb-orchestration", Joined(args));
        Assert.DoesNotContain("--permission-mode", args);
        Assert.Equal("none", args[args.ToList().IndexOf("--permission-prompts") + 1]);
    }

    [Fact]
    public void Edit_AddsEditToolsAndAcceptEdits_ButNoBash()
    {
        var args = ClaudeArgs.Build(Spec(PermissionLevel.Edit), Models);
        Assert.Contains("--tools | Read,Grep,Glob,Edit,Write | --permission-mode | acceptEdits", Joined(args));
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
