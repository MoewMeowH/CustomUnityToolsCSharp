using System.Drawing.Drawing2D;

namespace HB.Chat.Desktop;

/// <summary>
/// 바탕화면 펫 창: 테두리 없는 항상 위 레이어드 창(UpdateLayeredWindow) — 그림의 투명 부분이 그대로 투명하다.
/// 스프라이트 시트면 지금 동작 행의 프레임을 fps로 돌리고, 한 번 동작이 끝나면 계속 동작으로 돌아간다. 표정 그림이면 그 그림(gif면 프레임)을 그린다.
/// 누르면 Clicked, 끌면 옮기고 Moved, 오른쪽 단추는 ContextMenuStrip. 포커스는 가져가지 않는다 (WS_EX_NOACTIVATE).
/// </summary>
public sealed class PetForm : Form
{
    private const int WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    private readonly System.Windows.Forms.Timer _timer = new();
    private PetSprite? _sprite;
    private Image? _face;
    private string? _faceText;
    private PetAction _steady = PetAction.Idle;
    private PetAction? _once;
    private int _frame;
    private bool _dim;
    private Point _down, _grab;
    private bool _pressed, _dragging;

    /// <summary>클릭 (끌기가 아닌 왼쪽 단추 누르고 떼기).</summary>
    public event Action? Clicked;

    /// <summary>끌어서 옮긴 뒤 (새 위치를 저장하라고).</summary>
    public event Action? Moved;

    /// <summary>창 자리가 바뀔 때마다 (끌기 중 포함) — 말풍선이 따라오게 (2026-10-29 실제 확인 2).</summary>
    public event Action? Relocated;

    public PetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "HB Chat 펫";
        _timer.Tick += (_, _) => Advance();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    /// <summary>스프라이트 펫으로 (null이면 표정 그림 모드). 가진 시트는 부른 쪽이 정리한다.</summary>
    public void UseSprite(PetSprite? sprite)
    {
        _sprite = sprite;
        _frame = 0;
        _once = null;
        Restart();
    }

    /// <summary>표정 그림 모드의 그림 (null이면 text를 둥근 판에). gif는 ImageAnimator로 돌린다. 가진 그림은 부른 쪽이 정리한다.</summary>
    public void UseFace(Image? image, string text)
    {
        Animate(false);
        _face = image;
        _faceText = text;
        Animate(Visible);
        Render();
    }

    /// <summary>gif 표정 그림 돌리기 켜고 끄기 — 숨긴 동안은 멈춘다 (/self-review b280752387 C2: 숨겨도 프레임마다 그렸다).</summary>
    private void Animate(bool on)
    {
        if (_face is null || !ImageAnimator.CanAnimate(_face) || _animating == on) return;
        _animating = on;
        if (on) ImageAnimator.Animate(_face, OnFaceFrame);
        else ImageAnimator.StopAnimate(_face, OnFaceFrame);
    }

    private bool _animating;

    private void OnFaceFrame(object? sender, EventArgs e)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (Visible) Render(); });
    }

    /// <summary>계속 동작 (쉼 · 기다림 · 일하는 중). 한 번 동작이 도는 중이면 그것이 끝난 뒤에.</summary>
    public void SetSteady(PetAction action)
    {
        if (_steady == action) return;
        _steady = action;
        if (_once is null) { _frame = 0; Restart(); }
    }

    /// <summary>한 번 동작 (점프 · 손 흔들기 · 실패) — 끝나면 계속 동작으로.</summary>
    public void PlayOnce(PetAction action)
    {
        _once = action;
        _frame = 0;
        Restart();
    }

    /// <summary>서버와 끊김: 흐리게 (말풍선은 부른 쪽이 띄우지 않는다).</summary>
    public void SetDim(bool dim)
    {
        if (_dim == dim) return;
        _dim = dim;
        Render();
    }

    public void SetPetSize(Size size)
    {
        if (Size == size) return;
        Size = size;
        Render();
    }

    private PetAction Current => _once ?? _steady;

    private void Restart()
    {
        _timer.Stop();
        if (_sprite is not null && Visible)
        {
            _timer.Interval = Math.Max(50, 1000 / PetActions.Info(Current, _sprite.Sheet.Rows).Fps);
            _timer.Start();
        }
        Render();
    }

    private void Advance()
    {
        if (_sprite is null) return;
        var frames = _sprite.Frames(Current);
        if (_frame + 1 < frames.Count) _frame++;
        else if (_once is not null)
        {
            _once = null; // 한 번 동작이 끝남 — 계속 동작으로 (CodePet returnTo)
            _frame = 0;
            Restart();
            return;
        }
        else _frame = 0;
        Render();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        Animate(Visible);
        Restart(); // 숨기면 멈춘다 (위험: gif · 프레임 CPU)
    }

    /// <summary>지금 모습을 비트맵에 그려 레이어드 창으로 올린다.</summary>
    private void Render()
    {
        if (!IsHandleCreated || IsDisposed || Width <= 0 || Height <= 0) return;
        using var canvas = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var target = new Rectangle(0, 0, Width, Height);
            if (_sprite is not null)
            {
                var frames = _sprite.Frames(Current);
                var cell = _sprite.Sheet.Cell(PetActions.Info(Current, _sprite.Sheet.Rows).Row, frames[Math.Min(_frame, frames.Count - 1)]);
                g.DrawImage(_sprite.Image, target, cell, GraphicsUnit.Pixel);
            }
            else if (_face is not null)
            {
                ImageAnimator.UpdateFrames(_face); // 프레임 바꾸기 · 그리기 모두 UI 스레드
                g.DrawImage(_face, Fit(_face.Size, target));
            }
            else DrawPlate(g, target, _faceText ?? "");
        }
        if (_dim) Fade(canvas, 0.45f);
        Push(canvas);
    }

    private static Rectangle Fit(Size image, Rectangle box)
    {
        if (image.Width <= 0 || image.Height <= 0) return box;
        var scale = Math.Min(box.Width / (double)image.Width, box.Height / (double)image.Height);
        int w = (int)(image.Width * scale), h = (int)(image.Height * scale);
        return new(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    /// <summary>그림이 없을 때: 둥근 판에 표정 이름.</summary>
    private static void DrawPlate(Graphics g, Rectangle box, string text)
    {
        var r = Rectangle.Inflate(box, -4, -4);
        using var back = new SolidBrush(Color.FromArgb(230, 52, 56, 66));
        g.FillEllipse(back, r);
        using var font = new Font("Segoe UI", Math.Max(7f, box.Width / 9f), FontStyle.Bold, GraphicsUnit.Pixel);
        // GDI+ 글자 — GDI(TextRenderer)는 알파 비트맵에 글자 자리를 투명하게 남긴다 (말풍선과 같은 까닭)
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var white = new SolidBrush(Color.White);
        g.DrawString(text, font, white, r, format);
    }

    private static void Fade(Bitmap canvas, float alpha)
    {
        using var copy = (Bitmap)canvas.Clone();
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.Transparent);
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
        g.DrawImage(copy, new Rectangle(0, 0, canvas.Width, canvas.Height), 0, 0, canvas.Width, canvas.Height, GraphicsUnit.Pixel, attributes);
    }

    private void Push(Bitmap canvas) => Layered.Push(this, canvas);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Render(); // UpdateLayeredWindow가 위치도 같이 정한다
        Relocated?.Invoke();
    }

    // ---- 마우스: 누르고 떼기 = 클릭, 움직이면 끌기 ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _dragging = false;
        _down = Cursor.Position;
        _grab = new Point(_down.X - Left, _down.Y - Top);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        var now = Cursor.Position;
        if (!_dragging && PetPlace.IsClick(_down, now, SystemInformation.DragSize)) return;
        _dragging = true;
        Location = new Point(now.X - _grab.X, now.Y - _grab.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        if (_dragging) Moved?.Invoke();
        else Clicked?.Invoke();
        _dragging = false;
    }

    /// <summary>끄는 중 캡처를 잃으면(Alt+Tab · 다른 창) 누름을 풀고, 끌던 중이면 그 자리를 저장한다 (/self-review b280752387 C4).</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!_pressed || Capture) return;
        _pressed = false;
        if (_dragging) Moved?.Invoke();
        _dragging = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            Animate(false);
        }
        base.Dispose(disposing);
    }
}
