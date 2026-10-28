using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HB.Chat.Desktop;

/// <summary>
/// 맨 위 줄에서 마우스가 닿은 곳 (5차 b). Caption은 빈 곳 · 앱 이름 · 상태 글 — 끌면 창 이동.
/// Status는 상태 글이 잘렸을 때만 — 그때는 끌기 대신 마우스를 올려 전체 글을 본다.
/// </summary>
public enum TitleHit { Caption, Tab, Minimize, Maximize, Close, Status }

/// <summary>맨 위 줄 배치 결과: 앱 이름 · 탭 · 상태 글 · 창 단추 셋.</summary>
public sealed record TitleParts(Rectangle Name, IReadOnlyList<Rectangle> Tabs, Rectangle Status, Rectangle Minimize, Rectangle Maximize, Rectangle Close);

/// <summary>
/// 맨 위 줄 배치 · 맞닿기 판정 (5차 b). 화면 없는 로직 — 글 폭은 부르는 쪽이 재서 준다.
/// 왼쪽부터 앱 이름 · 탭, 가운데 상태 글(남는 폭), 오른쪽 끝에 최소화 · 최대화 · 닫기.
/// </summary>
public static class TitleLayout
{
    /// <summary>논리 크기(96 DPI): 줄 높이 · 창 단추 폭 · 바깥 여백 · 탭 안 여백 · 탭 사이.</summary>
    public const int Height = 36, ButtonWidth = 46, Pad = 12, TabPad = 10, TabGap = 2, TabInset = 6;

    public static TitleParts Arrange(int width, int height, float scale, int nameWidth, IReadOnlyList<int> tabTextWidths)
    {
        int S(int v) => (int)Math.Round(v * scale);
        var button = S(ButtonWidth);
        var close = new Rectangle(width - button, 0, button, height);
        var max = new Rectangle(close.Left - button, 0, button, height);
        var min = new Rectangle(max.Left - button, 0, button, height);

        var name = new Rectangle(S(Pad), 0, nameWidth, height);
        var x = name.Right + S(Pad);
        var tabs = new List<Rectangle>(tabTextWidths.Count);
        foreach (var w in tabTextWidths)
        {
            var tab = new Rectangle(x, S(TabInset), w + 2 * S(TabPad), height - 2 * S(TabInset));
            tabs.Add(tab);
            x = tab.Right + S(TabGap);
        }
        var statusLeft = x + S(Pad);
        var status = new Rectangle(statusLeft, 0, Math.Max(0, min.Left - S(Pad) - statusLeft), height);
        return new TitleParts(name, tabs, status, min, max, close);
    }

    /// <summary>
    /// 이름 · 탭 · 창 단추가 겹치지 않는 가장 좁은 폭 (상태 글 칸 0). 창 최소 폭을 이것 이상으로 둔다 —
    /// 고배율 모니터에서 창 단추가 탭 위에 그려지고 탭을 누를 수 없던 것 (/self-review 993ef3b338 C1).
    /// </summary>
    public static int RequiredWidth(float scale, int nameWidth, IReadOnlyList<int> tabTextWidths)
    {
        var parts = Arrange(0, Height, scale, nameWidth, tabTextWidths);
        var right = parts.Tabs.Count > 0 ? parts.Tabs[^1].Right : parts.Name.Right;
        return right + (int)Math.Round(Pad * scale) + 3 * parts.Close.Width;
    }

    /// <summary>점이 닿은 곳. 탭이면 tab에 몇 번째인지. 상태 글은 잘렸을 때(statusClipped)만 Status, 그 밖의 빈 곳은 모두 Caption.</summary>
    public static TitleHit Hit(TitleParts parts, Point p, out int tab, bool statusClipped = false)
    {
        tab = -1;
        if (statusClipped && parts.Status.Contains(p)) return TitleHit.Status;
        if (parts.Close.Contains(p)) return TitleHit.Close;
        if (parts.Maximize.Contains(p)) return TitleHit.Maximize;
        if (parts.Minimize.Contains(p)) return TitleHit.Minimize;
        for (var i = 0; i < parts.Tabs.Count; i++)
        {
            if (!parts.Tabs[i].Contains(p)) continue;
            tab = i;
            return TitleHit.Tab;
        }
        return TitleHit.Caption;
    }

    // WM_NCHITTEST 결과 값
    public const int HtTransparent = -1, HtClient = 1, HtCaption = 2, HtTop = 12, HtTopLeft = 13, HtTopRight = 14;

    /// <summary>
    /// 큰 창의 WM_NCHITTEST (창 안쪽 좌표): 제목 줄을 지웠으니 맨 위 band 픽셀은 위쪽 크기 조절(양 끝은 모서리), 맨 위 줄의 빈 곳은 제목 줄.
    /// 최대화면 크기 조절이 없다. 그 밖은 Windows가 정한 값(client) 그대로.
    /// </summary>
    public static int FrameHit(Point client, int clientWidth, int band, bool maximized, bool onCaption)
    {
        if (!maximized && client.Y >= 0 && client.Y < band)
        {
            if (client.X < band * 2) return HtTopLeft;
            if (client.X >= clientWidth - band * 2) return HtTopRight;
            return HtTop;
        }
        return onCaption ? HtCaption : HtClient;
    }
}

/// <summary>창 틀 Win32 (5차 b): 제목 영역 지우기 · 어두운 틀 · 크기 조절 테두리 두께.</summary>
internal static class WindowFrame
{
    public const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084;

    /// <summary>Windows 10 20H1+ · 11: 제목 줄 · 테두리를 어두운 모드로 (DWMWA_USE_IMMERSIVE_DARK_MODE). 안 되는 판에서는 조용히 그대로.</summary>
    public static void SetDark(IntPtr hwnd, bool dark)
    {
        var on = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
    }

    /// <summary>크기 조절 테두리 두께(픽셀) — 최대화 때 화면 밖으로 나가는 폭 · 위쪽 크기 조절 띠.</summary>
    public static int ResizeBorder(int dpi) =>
        GetSystemMetricsForDpi(SmCySizeFrame, (uint)dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, (uint)dpi);

    /// <summary>
    /// WM_NCCALCSIZE(wParam ≠ 0): Windows가 계산한 안쪽 영역에서 위쪽만 창 맨 위로 되돌린다 — 제목 줄만 없어지고
    /// 좌 · 우 · 아래 크기 조절 테두리와 그림자는 Windows 것 그대로. 최대화면 화면 밖으로 나간 테두리만큼 내린다.
    /// </summary>
    public static void CalcSize(ref Message m, Action<Message> defaultProc, int dpi)
    {
        var before = Marshal.PtrToStructure<NcCalcSizeParams>(m.LParam);
        defaultProc(m);
        var after = Marshal.PtrToStructure<NcCalcSizeParams>(m.LParam);
        after.Rect0.Top = before.Rect0.Top + (IsZoomed(m.HWnd) ? ResizeBorder(dpi) : 0);
        Marshal.StructureToPtr(after, m.LParam, false);
        m.Result = IntPtr.Zero;
    }

    /// <summary>틀 계산을 다시 하게 한다 (핸들을 만든 뒤 한 번 — 처음 그린 틀에 제목 줄이 남지 않게).</summary>
    public static void Refresh(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

    private const int DwmUseImmersiveDarkMode = 20, SmCySizeFrame = 33, SmCxPaddedBorder = 92;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NcCalcSizeParams
    {
        public Rect Rect0, Rect1, Rect2;
        public IntPtr Pos;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// 큰 창 맨 위 줄 (5차 b): Windows 제목 줄 대신 앱이 그린다 — 앱 이름 · 탭(채팅 · 대시보드 · 설정) · 상태 글 · 최소화 · 최대화 · 닫기.
/// 빈 곳 · 앱 이름 · 상태 글은 WM_NCHITTEST에 HTTRANSPARENT를 돌려 창(MainForm)이 제목 줄(HTCAPTION)로 받는다 —
/// 끌기 · 두 번 눌러 최대화 · 화면 끝 끌어 붙이기 · 오른쪽 단추 창 메뉴는 Windows가 한다. 맨 위 band 픽셀도 창에 넘겨 위쪽 크기 조절.
/// </summary>
public sealed class TitleBar : Control
{
    private readonly string _name;
    private readonly string[] _tabs;
    private readonly bool[] _tabEnabled;
    private readonly string?[] _tabTips;
    private readonly ToolTip _tip = new();
    private Font _font = null!, _bold = null!; // 생성자의 ApplyDpi가 만든다 (993ef3b338 C7)
    private int _nameWidth, _statusWidth = -1;
    private int[] _tabWidths = [];
    private TitleParts? _parts;
    private (TitleHit Kind, int Tab)? _hover, _pressed;
    private string? _tipText;
    private FrameColors _colors = Appearance.Colors(Appearance.IsDark(Appearance.Default, false));
    private string _status = "";
    private int _checked;
    private bool _maximized;

    /// <summary>탭을 누름 (몇 번째).</summary>
    public event Action<int>? TabClicked;

    public event Action? MinimizeClicked, MaximizeClicked, CloseClicked;

    /// <summary>필요한 폭이 바뀜 (배율 변경) — 창이 최소 폭을 다시 정한다.</summary>
    public event Action? RequiredWidthChanged;

    /// <summary>이름 · 탭 · 창 단추가 겹치지 않는 가장 좁은 폭 (장치 픽셀).</summary>
    public int RequiredWidth => TitleLayout.RequiredWidth(DpiScale, _nameWidth, _tabWidths);

    public TitleBar(string name, IReadOnlyList<(string Text, string? Tip)> tabs)
    {
        _name = name;
        _tabs = [.. tabs.Select(t => t.Text)];
        _tabTips = [.. tabs.Select(t => t.Tip)];
        _tabEnabled = [.. tabs.Select(_ => true)];
        Dock = DockStyle.Top;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // 누를 때 화면(WebView2)의 입력 초점을 뺏지 않게
        ApplyDpi();
    }

    /// <summary>위쪽 크기 조절 띠(픽셀). 최대화면 0 — 창이 정해 준다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ResizeBand { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FrameColors Colors
    {
        get => _colors;
        set { _colors = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; _statusWidth = -1; Invalidate(); }
    }

    /// <summary>눌린 모양의 탭 (-1 = 없음).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CheckedTab
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Maximized
    {
        get => _maximized;
        set { if (_maximized == value) return; _maximized = value; Invalidate(); }
    }

    public void SetTabEnabled(int tab, bool enabled)
    {
        if (_tabEnabled[tab] == enabled) return;
        _tabEnabled[tab] = enabled;
        Invalidate();
    }

    private float DpiScale => DeviceDpi / 96f;

    private void ApplyDpi()
    {
        var px = LogicalToDeviceUnits(12); // 9pt
        var font = new Font("Segoe UI", px, GraphicsUnit.Pixel);
        var bold = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
        _font?.Dispose();
        _bold?.Dispose();
        (_font, _bold) = (font, bold);
        _nameWidth = LogicalToDeviceUnits(14) + TextRenderer.MeasureText(_name, _bold, Size.Empty, Measure).Width;
        _tabWidths = [.. _tabs.Select(t => TextRenderer.MeasureText(t, _font, Size.Empty, Measure).Width)];
        _statusWidth = -1;
        Height = LogicalToDeviceUnits(TitleLayout.Height);
        _parts = null;
        Invalidate();
        RequiredWidthChanged?.Invoke();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpi();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpi(); // 모니터 DPI는 핸들이 생긴 뒤에야 맞다
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _parts = null;
    }

    private const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private TitleParts Parts => _parts ??= TitleLayout.Arrange(Width, Height, DpiScale, _nameWidth, _tabWidths);

    /// <summary>상태 글이 칸보다 길어 잘렸는지 — 그때만 그 칸에 마우스를 올려 전체 글을 본다 (지휘자 검토 R3).</summary>
    private bool StatusClipped
    {
        get
        {
            if (_status.Length == 0) return false;
            if (_statusWidth < 0) _statusWidth = TextRenderer.MeasureText(_status, _font, Size.Empty, Measure).Width;
            return _statusWidth > Parts.Status.Width;
        }
    }

    /// <summary>a 쪽으로 t만큼 섞은 색.</summary>
    private static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(b.R + (a.R - b.R) * t), (int)(b.G + (a.G - b.G) * t), (int)(b.B + (a.B - b.B) * t));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var c = _colors;
        g.Clear(c.Back);
        var p = Parts;

        // 앱 이름 + 강조색 점
        var dot = LogicalToDeviceUnits(8);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var accent = new SolidBrush(c.Accent)) g.FillEllipse(accent, p.Name.Left, (Height - dot) / 2f, dot, dot);
        g.SmoothingMode = SmoothingMode.None;
        var nameText = p.Name with { X = p.Name.X + LogicalToDeviceUnits(14), Width = p.Name.Width - LogicalToDeviceUnits(14) };
        TextRenderer.DrawText(g, _name, _bold, nameText, c.Text, Measure | TextFormatFlags.VerticalCenter);

        // 탭: 눌린 탭 · 마우스 올린 탭은 둥근 바탕
        for (var i = 0; i < _tabs.Length; i++)
        {
            var r = p.Tabs[i];
            var back = i == _checked ? c.Selected : _hover is (TitleHit.Tab, var h) && h == i && _tabEnabled[i] ? c.Hover : c.Back;
            if (back != c.Back) FillRound(g, back, r, LogicalToDeviceUnits(4));
            // 막힌 탭은 보통 탭(흐린 글)보다 더 흐리게 — 바탕 쪽으로 반 섞음 (지휘자 검토 R4)
            var fore = !_tabEnabled[i] ? Mix(c.Muted, c.Back, 0.5f) : i == _checked ? c.Text : c.Muted;
            TextRenderer.DrawText(g, _tabs[i], _font, r, fore, Measure | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (p.Status.Width > 0 && _status.Length > 0)
            TextRenderer.DrawText(g, _status, _font, p.Status, c.Muted, Measure | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        DrawButton(g, TitleHit.Minimize, p.Minimize);
        DrawButton(g, TitleHit.Maximize, p.Maximize);
        DrawButton(g, TitleHit.Close, p.Close);
    }

    private static void FillRound(Graphics g, Color color, Rectangle r, int radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>창 단추: 마우스를 올리면 바탕(닫기는 Windows처럼 빨강), 그림은 선으로 (아이콘 글꼴이 없는 판에서도 같게).</summary>
    private void DrawButton(Graphics g, TitleHit kind, Rectangle r)
    {
        var hot = _hover is { } h && h.Kind == kind;
        var down = hot && _pressed is { } pr && pr.Kind == kind;
        var fore = _colors.Text;
        if (kind == TitleHit.Close && hot)
        {
            using var red = new SolidBrush(down ? Color.FromArgb(0x94, 0x1a, 0x10) : Color.FromArgb(0xc4, 0x2b, 0x1c));
            g.FillRectangle(red, r);
            fore = Color.White;
        }
        else if (hot)
        {
            using var back = new SolidBrush(down ? _colors.Selected : _colors.Hover);
            g.FillRectangle(back, r);
        }

        var s = LogicalToDeviceUnits(10);
        var x = r.Left + (r.Width - s) / 2;
        var y = r.Top + (r.Height - s) / 2;
        using var pen = new Pen(fore, Math.Max(1, LogicalToDeviceUnits(1)));
        switch (kind)
        {
            case TitleHit.Minimize:
                g.DrawLine(pen, x, y + s / 2, x + s, y + s / 2);
                break;
            case TitleHit.Maximize when _maximized:
                var o = LogicalToDeviceUnits(2);
                g.DrawRectangle(pen, x, y + o, s - o, s - o);
                g.DrawLine(pen, x + o, y, x + s, y);
                g.DrawLine(pen, x + s, y, x + s, y + s - o);
                break;
            case TitleHit.Maximize:
                g.DrawRectangle(pen, x, y, s, s);
                break;
            case TitleHit.Close:
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(pen, x, y, x + s, y + s);
                g.DrawLine(pen, x + s, y, x, y + s);
                g.SmoothingMode = SmoothingMode.None;
                break;
        }
    }

    private (TitleHit Kind, int Tab)? HitAt(Point p)
    {
        var kind = TitleLayout.Hit(Parts, p, out var tab, StatusClipped);
        return kind == TitleHit.Caption ? null : (kind, tab);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Invalidate();
        var tip = hit switch
        {
            (TitleHit.Tab, var i) => _tabTips[i],
            (TitleHit.Minimize, _) => "최소화",
            (TitleHit.Maximize, _) => _maximized ? "이전 크기로" : "최대화",
            (TitleHit.Close, _) => "닫기 — 트레이에서 계속 돌아갑니다",
            (TitleHit.Status, _) => _status,
            _ => null,
        };
        if (tip == _tipText) return;
        _tipText = tip;
        // 붙은 단추 사이로 옮기면 MouseLeave가 없어 이미 사라진 도움말이 다시 안 뜬다 — 닫고 새 글로 (993ef3b338 C6)
        _tip.Hide(this);
        _tip.SetToolTip(this, tip);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _pressed = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = HitAt(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = null;
        Invalidate();
        if (e.Button != MouseButtons.Left || pressed is not { } hit || HitAt(e.Location) != hit) return;
        switch (hit.Kind)
        {
            case TitleHit.Tab when _tabEnabled[hit.Tab]:
                TabClicked?.Invoke(hit.Tab);
                break;
            case TitleHit.Minimize:
                MinimizeClicked?.Invoke();
                break;
            case TitleHit.Maximize:
                MaximizeClicked?.Invoke();
                break;
            case TitleHit.Close:
                CloseClicked?.Invoke();
                break;
        }
    }

    /// <summary>빈 곳 · 위쪽 크기 조절 띠는 창에 넘긴다 (창이 HTCAPTION · HTTOP으로 받는다). 잘린 상태 글 칸은 넘기지 않는다 (툴팁).</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WindowFrame.WmNcHitTest)
        {
            var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            var p = PointToClient(screen);
            if (p.Y < ResizeBand || HitAt(p) is null)
            {
                m.Result = TitleLayout.HtTransparent;
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _font.Dispose();
            _bold.Dispose();
        }
        base.Dispose(disposing);
    }
}
