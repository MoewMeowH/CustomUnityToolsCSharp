using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HB.Chat.Desktop;

/// <summary>
/// 트레이 상태 아이콘 (물을 것 3: 상태 색 점 — 회사에서도 무난): 쉼 회색 · 실행 파랑 · 승인 주황 · 실패 빨강.
/// 그림 파일 없이 그린다. Icon.FromHandle은 핸들을 가지지 않으므로 GetHicon 핸들을 들고 있다가 끝날 때 DestroyIcon으로 돌려준다.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayLevel, (Icon Icon, IntPtr Handle)> _icons = [];

    public TrayIcons()
    {
        _icons[TrayLevel.Idle] = Draw(Color.FromArgb(0x8A, 0x8F, 0x98));
        _icons[TrayLevel.Busy] = Draw(Color.FromArgb(0x3B, 0x82, 0xF6));
        _icons[TrayLevel.Approval] = Draw(Color.FromArgb(0xF5, 0x9E, 0x0B));
        _icons[TrayLevel.Failed] = Draw(Color.FromArgb(0xEF, 0x44, 0x44));
    }

    public Icon this[TrayLevel level] => _icons[level].Icon;

    /// <summary>트레이가 그리는 작은 아이콘 크기(배율 반영)로 그린다 — 큰 그림을 셸이 줄이면 흰 테두리가 뭉개진다 (0f0b6007a4 C9).</summary>
    private static (Icon, IntPtr) Draw(Color color)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var pad = size / 10f + 1;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var edge = new Pen(Color.White, Math.Max(1.5f, size / 12f));
            g.FillEllipse(fill, pad, pad, size - 2 * pad, size - 2 * pad);
            g.DrawEllipse(edge, pad, pad, size - 2 * pad, size - 2 * pad);
        }
        var handle = bmp.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    public void Dispose()
    {
        foreach (var (icon, handle) in _icons.Values)
        {
            icon.Dispose();
            DestroyIcon(handle);
        }
        _icons.Clear();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
