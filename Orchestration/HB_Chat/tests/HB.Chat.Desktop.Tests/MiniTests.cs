using System.Drawing;
using HB.Chat.Desktop;

namespace HB.Chat.Desktop.Tests;

/// <summary>2-3 c: 미니 창이 보일 방 · 단축키 읽기 · 미니 창 위치.</summary>
public sealed class MiniTests
{
    private static RoomState R(string id, string activity) => new(id, "방 " + id, activity, false, 0);

    [Fact]
    public void Pick_PinnedThenApprovalThenMostRecent()
    {
        RoomState[] rooms = [R("recent", "Idle"), R("busy", "Busy"), R("ask", "Approval")]; // /api/rooms는 최근 활동 순
        Assert.Equal("ask", MiniRoom.Pick(rooms, null));
        Assert.Equal("busy", MiniRoom.Pick(rooms, "busy"));
        Assert.Equal("ask", MiniRoom.Pick(rooms, "gone"));                     // 고정한 방이 없어짐
        Assert.Equal("recent", MiniRoom.Pick([R("recent", "Idle"), R("busy", "Busy")], null));
        Assert.Null(MiniRoom.Pick([], null));
    }

    [Fact]
    public void Next_SwitchesOnlyWhenItShould()
    {
        RoomState[] busyBoth = [R("b", "Busy"), R("a", "Busy")];
        Assert.Null(MiniRoom.Next(busyBoth, null, "a"));                                  // 지금 방이 도는 중 — 오가지 않음 (R1)
        Assert.Equal("b", MiniRoom.Next([R("b", "Busy"), R("a", "Idle")], null, "a"));    // 지금 방은 쉬고 다른 방이 돎
        Assert.Null(MiniRoom.Next([R("b", "Idle"), R("a", "Idle")], null, "a"));          // 둘 다 쉼 — 그대로
        Assert.Equal("c", MiniRoom.Next([R("a", "Busy"), R("c", "Approval")], null, "a")); // 승인 기다림이 먼저
        Assert.Null(MiniRoom.Next([R("c", "Approval"), R("d", "Approval")], null, "d"));  // 이미 승인 방을 보는 중
        Assert.Equal("x", MiniRoom.Next([R("x", "Idle")], null, "gone"));                 // 지금 방이 없어짐
        Assert.Equal("x", MiniRoom.Next([R("x", "Idle")], null, null));                   // 처음
        Assert.Equal("p", MiniRoom.Next([R("c", "Approval"), R("p", "Idle")], "p", "c")); // 고정이 승인보다 먼저
        Assert.Null(MiniRoom.Next([], null, "a"));
    }

    [Fact]
    public void KeepPin_DropsOnlyWhenListLacksIt()
    {
        Assert.Equal("p", MiniRoom.KeepPin([R("p", "Idle")], "p"));
        Assert.Null(MiniRoom.KeepPin([R("x", "Idle")], "p"));
        Assert.Equal("p", MiniRoom.KeepPin([], "p")); // 빈 목록(다시 켜는 중)이면 유지 (bde22e2224 C7)
        Assert.Null(MiniRoom.KeepPin([R("x", "Idle")], null));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", Hotkey.Control | Hotkey.Alt, Keys.Space)]
    [InlineData(" ctrl + shift + h ", Hotkey.Control | Hotkey.Shift, Keys.H)]
    [InlineData("Win+Alt+F9", Hotkey.Win | Hotkey.Alt, Keys.F9)]
    [InlineData("Control+Alt+D1", Hotkey.Control | Hotkey.Alt, Keys.D1)]
    public void Hotkey_Parses(string text, uint mods, Keys key) => Assert.Equal(new Hotkey(mods, key), Hotkey.Parse(text));

    [Theory]
    [InlineData("Space")]            // 수정 키 없음 — 아무 키나 가로채지 않는다
    [InlineData("Ctrl+Alt")]         // 키 없음
    [InlineData("Ctrl+A+B")]         // 키 둘
    [InlineData("Ctrl+1")]           // 숫자만 쓰면 Enum이 값으로 읽는다
    [InlineData("Ctrl+Shift+ShiftKey")]
    [InlineData("Ctrl+Nope")]
    [InlineData("Ctrl+A,B")]         // 쉼표는 플래그를 합친다 (bde22e2224 C8)
    [InlineData("Ctrl+LButton")]     // 마우스 단추
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Hotkey_RejectsBad(string? text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Placement_MiniMinimumIsSmallerThanMain()
    {
        Rectangle[] screens = [new(0, 0, 1920, 1040)];
        var mini = new WindowPlacement(1500, 460, 380, 560, false);
        Assert.Null(mini.FitTo(screens));                                    // 큰 창 최소 크기로는 너무 작음
        Assert.NotNull(mini.FitTo(screens, MiniForm.MinSizeMini));
    }
}
