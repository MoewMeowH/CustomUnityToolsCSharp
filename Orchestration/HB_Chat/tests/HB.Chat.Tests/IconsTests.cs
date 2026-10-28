using System.Text.RegularExpressions;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>화면 아이콘 (4차 D3): 모양 · 꾸밈 표시, 화면(cshtml · chat.js)이 쓰는 이름이 모두 모음에 있는지.</summary>
public sealed partial class IconsTests
{
    [Fact]
    public void Svg_IsDecorative_AndUnknownIsEmpty()
    {
        var svg = Icons.Svg("trash").Value!;
        Assert.StartsWith("<svg class=\"icon icon-trash\"", svg);
        Assert.Contains("aria-hidden=\"true\"", svg);
        Assert.Equal("", Icons.Svg("없는이름").Value);
        Assert.Contains("\"reply\":", Icons.Json());
    }

    [Fact]
    public void EveryIconNameUsedByPages_Exists()
    {
        // 저장소의 HB.Chat 폴더 (테스트 출력 폴더에서 위로)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "HB.Chat"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var app = Path.Combine(dir!.FullName, "src", "HB.Chat");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(app, "Pages"), "*.cshtml", SearchOption.AllDirectories))
            foreach (Match m in RazorIcon().Matches(File.ReadAllText(file))) used.Add(m.Groups[1].Value);
        foreach (Match m in ScriptIcon().Matches(File.ReadAllText(Path.Combine(app, "wwwroot", "chat.js")))) used.Add(m.Groups[1].Value);

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(Icons.Names));
    }

    [GeneratedRegex(@"Icons\.Svg\(""([a-z-]+)""\)")]
    private static partial Regex RazorIcon();

    [GeneratedRegex(@"\bicon\(""([a-z-]+)""\)")]
    private static partial Regex ScriptIcon();
}
