using HB.Chat;

namespace HB.Chat.Tests;

public class SlashCommandsTests
{
    [Theory]
    [InlineData("/clear", SlashKind.NewSession, "clear", null)]
    [InlineData("  /reset  ", SlashKind.NewSession, "reset", null)]
    [InlineData("/new", SlashKind.NewSession, "new", null)]
    [InlineData("/clear 이건 무시", SlashKind.NewSession, "clear", "이건 무시")]
    [InlineData("/clear\n다음 줄", SlashKind.NewSession, "clear", "다음 줄")]
    [InlineData("/model", SlashKind.Model, "model", null)]
    [InlineData("/model sonnet", SlashKind.Model, "model", "sonnet")]
    [InlineData("/effort  low ", SlashKind.Effort, "effort", "low")]
    public void Classify_TakesTheFiveCommands(string text, SlashKind kind, string name, string? arg)
    {
        var cmd = SlashCommands.Classify(text);
        Assert.NotNull(cmd);
        Assert.Equal(kind, cmd!.Kind);
        Assert.Equal(name, cmd.Name);
        Assert.Equal(arg, cmd.Arg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("clear")]
    [InlineData("/compact")]
    [InlineData("/clearx")]
    [InlineData("/models")]
    [InlineData("/CLEAR")]
    [InlineData("보통 말 /clear")]
    [InlineData("/c/Users/me/file.txt")]
    public void Classify_LeavesOtherTextToTheCli(string? text) => Assert.Null(SlashCommands.Classify(text));

    [Fact]
    public void ResolveModel_MatchesListIgnoringCase()
    {
        string[] list = ["opus", "sonnet", "haiku"];
        Assert.Equal("sonnet", SlashCommands.ResolveModel("Sonnet", list));
        Assert.Equal("opus", SlashCommands.ResolveModel(" opus ", list));
        Assert.Null(SlashCommands.ResolveModel("claude-opus-5-5", list));
        Assert.Null(SlashCommands.ResolveModel("sonnet extra", list));
        Assert.Null(SlashCommands.ResolveModel(null, list));
    }

    [Fact]
    public void CleanEffort_DefaultClearsAndUnknownFails()
    {
        Assert.Equal((null, null), SlashCommands.CleanEffort("default"));
        Assert.Equal(("high", null), SlashCommands.CleanEffort("HIGH"));
        Assert.NotNull(SlashCommands.CleanEffort("turbo").Error);
    }

    [Fact]
    public void Help_ShowsCurrentAndChoices()
    {
        var model = SlashCommands.ModelHelp("opus", ["opus", "sonnet"], "gpt");
        Assert.Contains("'gpt'", model);
        Assert.Contains("지금 모델: opus", model);
        Assert.Contains("opus, sonnet", model);
        var effort = SlashCommands.EffortHelp(null);
        Assert.Contains("지금 강도: 기본", effort);
        Assert.Contains("default", effort);
    }

    [Fact]
    public void CarryOver_KeepsConnectionsNotConversation()
    {
        var from = new Room
        {
            Id = "aaaaaaaaaaaa", SessionId = Guid.NewGuid().ToString("D"), Workspace = "ws", Level = PermissionLevel.Command, Model = "opus",
            CreatedAt = DateTimeOffset.Now, Title = "옛 방", StickerPack = "crow_con", McpExtra = ["notion-todo"], McpOff = ["hb-chat"],
            UnityProject = "Game", AskRunMode = true, ReadUpTo = 40,
        };
        var fresh = new Room
        {
            Id = "bbbbbbbbbbbb", SessionId = Guid.NewGuid().ToString("D"), Workspace = "ws", Level = PermissionLevel.Command, Model = "opus",
            CreatedAt = DateTimeOffset.Now, ReadUpTo = 0,
        };
        var carried = SlashCommands.CarryOver(fresh, from);
        Assert.Equal(fresh.SessionId, carried.SessionId);
        Assert.Null(carried.Title);
        Assert.Equal(0, carried.ReadUpTo);
        Assert.Equal("crow_con", carried.StickerPack);
        Assert.Equal(["notion-todo"], carried.McpExtra);
        Assert.Equal(["hb-chat"], carried.McpOff);
        Assert.Equal("Game", carried.UnityProject);
        Assert.True(carried.AskRunMode);
    }
}
