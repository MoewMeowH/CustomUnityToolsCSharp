using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>데이터 폴더 쓰기가 Claude 앱(MSIX) 패키지 캐시로 옮겨지는지 (4차 A).</summary>
public sealed class RedirectedFolderTests
{
    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\HB_Chat\.p", @"\\?\C:\Users\u\AppData\Local\Packages\Claude_x\LocalCache\Local\HB_Chat\.p", true)]
    [InlineData(@"C:\Users\u\AppData\Local\HB_Chat\.p", @"\\?\C:\Users\u\AppData\Local\HB_Chat\.p", false)]
    [InlineData(@"C:\Users\u\AppData\Local\HB_Chat\.p", @"\\?\D:\real\HB_Chat\.p", false)] // 링크 · subst로 다른 곳 — 패키지 캐시가 아니면 그대로
    [InlineData(@"C:\Users\u\AppData\Local\Packages\A\LocalCache\Local\X\.p", @"\\?\C:\Users\u\AppData\Local\Packages\A\LocalCache\Local\X\.p", false)] // 처음부터 그 폴더를 줌
    public void IsRedirected_OnlyIntoPackageCache(string expected, string final, bool redirected) =>
        Assert.Equal(redirected, RedirectedFolder.IsRedirected(expected, final));

    [Fact]
    public void Probe_PlainFolder_IsNull_AndLeavesNothing()
    {
        // 테스트 출력 폴더(F: 등, AppData 밖)는 옮겨지지 않는다
        var dir = Path.Combine(AppContext.BaseDirectory, "redirect-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(RedirectedFolder.Probe(dir));
            Assert.Empty(Directory.GetFileSystemEntries(dir)); // 시험 파일은 닫으면 지워짐
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Message_NamesBothFolders() =>
        Assert.Contains(@"C:\a → C:\b", RedirectedFolder.Message(@"C:\a", @"C:\b"));
}
