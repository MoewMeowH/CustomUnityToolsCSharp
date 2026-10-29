using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HB.Chat.Desktop;

/// <summary>
/// 펫 말풍선 (계획서 2026-10-29_바탕화면-펫 결정 "짧게 띄움", 실제 확인 뒤 A안): 펫 본체처럼 레이어드 창에 직접 그린다 —
/// 둥근 상자(반경 10, 채팅 앱 .panel과 같게) + 펫을 가리키는 꼬리 + 부드러운 그림자 + 투명 바탕, 나타날 때 · 사라질 때 짧게 흐려진다.
/// 색은 채팅 앱 테마 값(--panel · --line · --text · --muted · --accent, 밝음 · 어두움)과 같게, 글은 상태(굵게) + 방 이름(흐리게) 두 단계.
/// 펫이 움직이면(끌기 중 포함) <see cref="Follow"/>로 다시 자리를 잡고, 위 · 아래가 바뀌면 꼬리 방향도 바꾼다. 포커스는 가져가지 않는다.
/// </summary>
public sealed class PetBubble : Form
{
    private const int Radius = 10, Tail = 8, Shadow = 6, PadX = 12, PadY = 8, MaxText = 260, FadeMs = 140;

    private readonly System.Windows.Forms.Timer _hide = new(), _fade = new() { Interval = 15 };
    private readonly Font _bold = new("Segoe UI", 9.5f, FontStyle.Bold), _plain = new("Segoe UI", 9f);
    private PetSaying? _saying;
    private bool _dark, _above = true;
    private int _tailX;
    private Rectangle _pet;
    private double _alpha;
    private int _fadeDir;
    private DateTime _fadeFrom;

    public PetBubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "HB Chat 펫 말풍선";
        _hide.Tick += (_, _) => { _hide.Stop(); FadeOut(); };
        _fade.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; // 레이어드 · 도구 창 · 포커스 안 가져감
            return cp;
        }
    }

    /// <summary>채팅 앱 테마 색 (Pages/Shared/_Layout.cshtml :root 값).</summary>
    private static (Color Panel, Color Line, Color Text, Color Muted, Color Accent, Color Ok, Color Err) Palette(bool dark) => dark
        ? (Color.FromArgb(0x2b, 0x2d, 0x31), Color.FromArgb(0x3f, 0x41, 0x47), Color.FromArgb(0xdb, 0xde, 0xe1), Color.FromArgb(0x94, 0x9b, 0xa4),
            Color.FromArgb(0x58, 0x65, 0xf2), Color.FromArgb(0x23, 0xa5, 0x5a), Color.FromArgb(0xf2, 0x3f, 0x43))
        : (Color.White, Color.FromArgb(0xe3, 0xe6, 0xec), Color.FromArgb(0x1d, 0x23, 0x30), Color.FromArgb(0x6b, 0x73, 0x85),
            Color.FromArgb(0x3b, 0x6e, 0xf5), Color.FromArgb(0x1f, 0x9d, 0x55), Color.FromArgb(0xd6, 0x45, 0x45));

    /// <summary>펫 위(자리가 없으면 아래)에 보이고 몇 초 뒤 흐려지며 숨는다.</summary>
    public void Say(PetSaying saying, Rectangle pet, bool dark, int milliseconds = 4000)
    {
        _saying = saying;
        _dark = dark;
        Size = Measure(saying);
        Follow(pet);
        if (!Visible || _fadeDir < 0) FadeIn();
        _hide.Stop();
        _hide.Interval = milliseconds;
        _hide.Start();
    }

    /// <summary>펫 자리가 바뀜 (끌기 중 포함): 보이는 중이면 자리 · 꼬리를 다시 잡는다.</summary>
    public void Follow(Rectangle pet)
    {
        _pet = pet;
        if (_saying is null) return;
        var place = PetPlace.Bubble(pet, Size, Screen.FromRectangle(pet).WorkingArea, Shadow + Radius + Tail);
        _above = place.Above;
        _tailX = place.TailX;
        Location = place.Location;
        if (Visible) Render();
    }

    /// <summary>바로 숨긴다 (펫 숨기기).</summary>
    public void Quiet()
    {
        _hide.Stop();
        _fade.Stop();
        _fadeDir = 0;
        Hide();
    }

    private Size Measure(PetSaying saying) => Measure(saying, _bold, _plain);

    internal static Size Measure(PetSaying saying, Font bold, Font plain)
    {
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        var status = g.MeasureString(saying.Status, bold, MaxText, format).ToSize();
        var room = saying.Room.Length == 0 ? Size.Empty : g.MeasureString(saying.Room, plain, MaxText, format).ToSize();
        var preview = PreviewHeight(saying, plain);
        var width = Math.Max(status.Width + 14, room.Width) + 2;
        if (preview > 0) width = MaxText; // 답 앞부분은 두 줄까지 감싸므로 넓게
        var text = new Size(Math.Min(MaxText, width), bold.Height + 2 + (room.IsEmpty ? 0 : plain.Height + 2) + preview);
        return new Size(text.Width + PadX * 2 + Shadow * 2, text.Height + PadY * 2 + Shadow * 2 + Tail);
    }

    /// <summary>답 앞부분 칸 높이 (두 줄까지, 줄 사이 위 여백 4). 없으면 0.</summary>
    private static int PreviewHeight(PetSaying saying, Font plain)
    {
        if (string.IsNullOrWhiteSpace(saying.Preview)) return 0;
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        var lines = g.MeasureString(saying.Preview, plain, MaxText, format).Height > plain.Height * 1.5f ? 2 : 1;
        return 4 + lines * (plain.Height + 1);
    }

    // ---- 흐려지기 ----

    private void FadeIn()
    {
        if (!Visible) { _alpha = 0; Show(); }
        StartFade(+1);
    }

    private void FadeOut() => StartFade(-1);

    private void StartFade(int direction)
    {
        _fadeDir = direction;
        _fadeFrom = DateTime.UtcNow - TimeSpan.FromMilliseconds((direction > 0 ? _alpha : 1 - _alpha) * FadeMs);
        _fade.Start();
        Step();
    }

    private void Step()
    {
        var t = Math.Clamp((DateTime.UtcNow - _fadeFrom).TotalMilliseconds / FadeMs, 0, 1);
        _alpha = _fadeDir > 0 ? t : 1 - t;
        if (t >= 1)
        {
            _fade.Stop();
            if (_fadeDir < 0) { _fadeDir = 0; Hide(); return; }
            _fadeDir = 0;
        }
        Render();
    }

    // ---- 그리기 ----

    private void Render()
    {
        if (!IsHandleCreated || _saying is not { } saying || Width <= 0 || Height <= 0) return;
        using var canvas = Draw(saying, _dark, _above, _tailX, Size, _bold, _plain);
        Layered.Push(this, canvas, (byte)Math.Round(_alpha * 255));
    }

    /// <summary>말풍선 그림 (창 크기 그대로, 투명 바탕). 시험이 모양을 볼 수 있게 창과 떼어 둔다.</summary>
    internal static Bitmap Draw(PetSaying saying, bool dark, bool above, int tailX, Size size, Font bold, Font plain)
    {
        var (panel, line, text, muted, accent, ok, err) = Palette(dark);
        var canvas = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // 투명 바탕에는 ClearType이 번진다
        var body = new Rectangle(Shadow, Shadow + (above ? 0 : Tail), size.Width - Shadow * 2, size.Height - Shadow * 2 - Tail);
        // 그림자: 넓은 것부터 옅게 몇 겹 (아래로 1px)
        for (var i = Shadow; i >= 1; i--)
        {
            using var shadow = Shape(Rectangle.Inflate(body with { Y = body.Y + 1 }, i, i), Radius + i, above, tailX);
            using var brush = new SolidBrush(Color.FromArgb(dark ? 14 : 9, 0, 0, 0));
            g.FillPath(brush, shadow);
        }
        using (var shape = Shape(body, Radius, above, tailX))
        {
            using var fill = new SolidBrush(panel);
            using var pen = new Pen(line, 1f);
            g.FillPath(fill, shape);
            g.DrawPath(pen, shape);
        }
        var dot = saying.Kind switch { NoticeKind.Failed => err, NoticeKind.Done => ok, _ => accent };
        var top = body.Top + PadY;
        using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, body.Left + PadX, top + 5, 8, 8);
        // GDI+ 글자 — GDI(TextRenderer)는 알파 비트맵에 글자 자리 알파를 0으로 남겨 레이어드 창에서 글자가 비친다
        using var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        var statusBox = new RectangleF(body.Left + PadX + 14, top, body.Width - PadX * 2 - 14, bold.Height + 2);
        using (var brush = new SolidBrush(text)) g.DrawString(saying.Status, bold, brush, statusBox, format);
        var y = statusBox.Bottom;
        if (saying.Room.Length > 0)
        {
            using var brush = new SolidBrush(muted);
            g.DrawString(saying.Room, plain, brush, new RectangleF(body.Left + PadX, y, body.Width - PadX * 2, plain.Height + 2), format);
            y += plain.Height + 2;
        }
        // 답 앞부분 (결정 "말풍선은 늘"): 글 색, 두 줄까지 감싸고 넘치면 …
        if (PreviewHeight(saying, plain) is > 0 and var height)
        {
            using var wrap = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };
            using var brush = new SolidBrush(text);
            g.DrawString(saying.Preview, plain, brush, new RectangleF(body.Left + PadX, y + 4, body.Width - PadX * 2, height - 4), wrap);
        }
        return canvas;
    }

    /// <summary>둥근 상자 + 펫 쪽 꼬리를 한 모양으로 (선이 꼬리 둘레를 이어서 돈다).</summary>
    private static GraphicsPath Shape(Rectangle r, int radius, bool above, int tailX)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        tailX = Math.Clamp(tailX, r.Left + radius + Tail, r.Right - radius - Tail);
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        if (!above) { path.AddLine(tailX - Tail, r.Top, tailX, r.Top - Tail); path.AddLine(tailX, r.Top - Tail, tailX + Tail, r.Top); }
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        if (above) { path.AddLine(tailX + Tail, r.Bottom, tailX, r.Bottom + Tail); path.AddLine(tailX, r.Bottom + Tail, tailX - Tail, r.Bottom); }
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) FadeOut(); // 눌러서 바로 닫기
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hide.Dispose();
            _fade.Dispose();
            _bold.Dispose();
            _plain.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>레이어드 창 그리기 (UpdateLayeredWindow, 픽셀 알파 + 창 전체 알파) — 펫 본체 · 말풍선이 같이 쓴다.</summary>
internal static class Layered
{
    private const int UlwAlpha = 2;
    private const byte AcSrcOver = 0, AcSrcAlpha = 1;

    public static void Push(Form form, Bitmap canvas, byte alpha = 255)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var hbitmap = canvas.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, hbitmap);
        try
        {
            var size = new NativeSize(canvas.Width, canvas.Height);
            var source = new NativePoint(0, 0);
            var top = new NativePoint(form.Left, form.Top);
            var blend = new BlendFunction { BlendOp = AcSrcOver, SourceConstantAlpha = alpha, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(form.Handle, screen, ref top, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(hbitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int Cx = cx, Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize, IntPtr hdcSrc,
        ref NativePoint pprSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
