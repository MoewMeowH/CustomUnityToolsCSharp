namespace HB.Chat.Desktop;

/// <summary>미니 창이 보여 줄 방 (물을 것 1): 고정한 방이 있으면 그 방, 아니면 승인 기다림 방 → 없으면 가장 최근 활동 방.</summary>
public static class MiniRoom
{
    /// <param name="rooms">`/api/rooms` 차례(최근 활동 순)</param>
    /// <param name="pinned">사용자가 고정한 방 (없어졌으면 무시)</param>
    public static string? Pick(IReadOnlyList<RoomState> rooms, string? pinned)
    {
        if (pinned is not null && rooms.Any(r => r.Id == pinned)) return pinned;
        return (rooms.FirstOrDefault(r => r.IsApproval) ?? rooms.FirstOrDefault())?.Id;
    }

    /// <summary>
    /// 지금 방에서 바꿀 방, 바꾸지 않으면 null. 신호마다 "가장 최근 방"을 다시 고르면 두 방이 번갈아 움직일 때 미니 창이 오가며
    /// 다시 읽으므로(지휘자 검토 R1) 바꾸는 때를 좁힌다: 고정한 방 · 승인 기다림이 새로 생김 · 지금 방이 없음 ·
    /// 지금 방은 쉬는데 가장 최근 방이 도는 중.
    /// </summary>
    public static string? Next(IReadOnlyList<RoomState> rooms, string? pinned, string? current)
    {
        string? To(string? id) => id == current ? null : id;
        if (pinned is not null && rooms.Any(r => r.Id == pinned)) return To(pinned);
        var now = rooms.FirstOrDefault(r => r.Id == current);
        if (rooms.FirstOrDefault(r => r.IsApproval) is { } approval && now is not { IsApproval: true }) return To(approval.Id);
        if (now is null) return To(rooms.FirstOrDefault()?.Id);
        return now.IsIdle && rooms.FirstOrDefault() is { IsIdle: false } recent ? To(recent.Id) : null;
    }

    /// <summary>고정한 방 정리: 목록에 없으면 풀지만, 빈 목록(서버를 다시 켜는 중 등)이면 그대로 둔다 (/self-review bde22e2224 C7).</summary>
    public static string? KeepPin(IReadOnlyList<RoomState> rooms, string? pinned) =>
        pinned is null || rooms.Count == 0 || rooms.Any(r => r.Id == pinned) ? pinned : null;
}

/// <summary>전역 단축키 (물을 것 2, 설정 Hotkey — 기본 Ctrl+Alt+Space). RegisterHotKey의 수정 키 · 가상 키.</summary>
public readonly record struct Hotkey(uint Modifiers, Keys Key)
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8, NoRepeat = 0x4000;

    /// <summary>"Ctrl+Alt+Space" 같은 글. 수정 키가 하나도 없거나 키가 없으면 null (아무 키나 가로채지 않게). 비우면 단축키 없음.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        Keys? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Control; break;
                case "alt": mods |= Alt; break;
                case "shift": mods |= Shift; break;
                case "win": mods |= Win; break;
                default:
                    // 숫자만 쓴 글은 Enum.TryParse가 값으로 읽고("1" → LButton), 쉼표는 플래그를 합친다("A,B") — 키 이름 하나만 받는다
                    if (key is not null || raw.All(char.IsDigit) || raw.Contains(',') || !Enum.TryParse<Keys>(raw, ignoreCase: true, out var k)
                        || k is Keys.None || (k & Keys.Modifiers) != 0 || !Enum.IsDefined(k)
                        || k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
                            or Keys.LButton or Keys.RButton or Keys.MButton or Keys.XButton1 or Keys.XButton2)
                        return null;
                    key = k;
                    break;
            }
        }
        return mods != 0 && key is { } final ? new Hotkey(mods, final) : null;
    }
}
