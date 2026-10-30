using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>없앤 대화 · 편집 단계와 항상 허용 규칙이 든 옛 색인을 그대로 연다.</summary>
public sealed class PermissionLevelMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-level-" + Guid.NewGuid().ToString("N")[..8]);

    public PermissionLevelMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("Chat", PermissionLevel.Read)]
    [InlineData("Edit", PermissionLevel.Read)]
    [InlineData("Read", PermissionLevel.Read)]
    [InlineData("Command", PermissionLevel.Command)]
    [InlineData("App", PermissionLevel.App)]
    public void OldIndex_LoadsWithLoweredLevel_AndNextSaveDropsOldValues(string stored, PermissionLevel expected)
    {
        var path = Path.Combine(_dir, "rooms.json");
        var id = new RoomStore(path).Create("ws", PermissionLevel.Read, "opus", null).Id;
        var json = File.ReadAllText(path);
        Assert.Contains("\"level\": \"Read\"", json);
        File.WriteAllText(path, json.Replace("\"level\": \"Read\"", $"\"level\": \"{stored}\", \"allowRules\": [\"Bash(x)\"]"));

        var store = new RoomStore(path);
        Assert.Null(store.LoadError);
        Assert.Equal(expected, store.Get(id)!.Level);

        store.Update(id, r => r with { Title = "저장" });
        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("allowRules", saved);
        Assert.DoesNotContain("\"Chat\"", saved);
        Assert.DoesNotContain("\"Edit\"", saved);
    }

    [Fact]
    public void UnknownLevel_IsLoadError_NotOverwritten()
    {
        var path = Path.Combine(_dir, "rooms.json");
        new RoomStore(path).Create("ws", PermissionLevel.Read, "opus", null);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"level\": \"Read\"", "\"level\": \"Nope\""));
        Assert.NotNull(new RoomStore(path).LoadError);
    }
}
