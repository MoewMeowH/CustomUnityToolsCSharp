using HB.Chat;
using HB.Orchestration.ChatUi;

namespace HB.Chat.Tests;

/// <summary>스티커 방 규칙: 태그 목록 · 쓸 때를 설정(Aliases · Hints)에서 읽는다.</summary>
public sealed class StickerRulesTests
{
    private static StickerOptions Options() => new()
    {
        Files = new(StringComparer.Ordinal) { ["해냈다"] = "06-done.png", ["ㄹㅇㅋㅋ"] = "28-lol.png" },
        Aliases = new(StringComparer.Ordinal) { ["완료"] = "해냈다", ["폭소"] = "ㄹㅇㅋㅋ", ["없는그림"] = "없음" },
        Hints = new(StringComparer.Ordinal) { ["완료"] = "실제 작업이 끝났을 때만", ["폭소"] = " ", ["없는그림"] = "보이면 안 됨" },
    };

    [Fact]
    public void ListsAliasesThatReachFiles_WithHints()
    {
        var rules = StickerRules.For(Options());
        Assert.Contains("[[icon:태그]]", rules);
        Assert.Contains("태그: 완료 · 폭소.", rules);
        Assert.Contains("쓸 때: 완료 = 실제 작업이 끝났을 때만.", rules); // 빈 Hint는 빠짐
        Assert.DoesNotContain("없는그림", rules); // Files로 이어지지 않는 태그 · 그 Hint는 빠짐
        Assert.DoesNotContain("보이면 안 됨", rules);
    }

    [Fact]
    public void NoHints_OmitsHintSentence()
    {
        var o = Options();
        o.Hints.Clear();
        var rules = StickerRules.For(o);
        Assert.Contains("태그: 완료 · 폭소.", rules);
        Assert.DoesNotContain("쓸 때:", rules);
    }

    [Fact]
    public void Empty_WhenNoSettingsOrNoTags()
    {
        Assert.Equal("", StickerRules.For(null));
        Assert.Equal("", StickerRules.For(new StickerOptions()));
        Assert.Equal("", StickerRules.For(new StickerOptions { Aliases = new(StringComparer.Ordinal) { ["완료"] = "해냈다" } }));
    }

    [Fact]
    public void ChangingSettings_ChangesRulesHash()
    {
        var o = Options();
        var before = RoomRules.Hash(StickerRules.For(o));
        o.Hints["폭소"] = "진짜 웃긴 상황";
        Assert.NotEqual(before, RoomRules.Hash(StickerRules.For(o)));
    }
}
