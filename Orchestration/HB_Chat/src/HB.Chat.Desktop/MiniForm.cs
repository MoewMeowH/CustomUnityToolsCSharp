using Microsoft.Web.WebView2.WinForms;

namespace HB.Chat.Desktop;

/// <summary>
/// 미니 창 (계획서 2-3 c, 시안 C): 화면 구석 항상 위 작은 창. 같은 방 화면을 `?view=mini` 작은 배치로 띄운다 —
/// 표정 · 상태 글 · 말풍선 · 승인 카드 · 입력칸이 그대로 동작한다 (서버 로직 없음). WebView2 규칙은 큰 창과 같다(MainForm이 설정).
/// 위 줄: 방 고르기("자동" = 승인 기다림 방 → 가장 최근 활동 방, 아니면 고정) · 큰 창에서 열기. 닫기(X)는 숨김.
/// </summary>
public sealed class MiniForm : Form
{
    public static readonly Size StartSize = new(380, 560), MinSizeMini = new(300, 320);

    private readonly Uri _chatUrl;
    private readonly string _placementPath;
    private readonly ComboBox _rooms = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _open = new() { Text = "큰 창", AutoSize = true, Dock = DockStyle.Right };
    private readonly Panel _top;
    private IReadOnlyList<RoomState> _last = [];
    private string _itemsKey = "";
    private bool _appClosing, _dark;

    /// <summary>펫을 눌러 연 방 — 숨길 때까지 자동 전환하지 않는다. 숨기거나 직접 고르면 null.</summary>
    private string? _held;

    /// <summary>고정한 방 (null = 자동).</summary>
    public string? Pinned { get; private set; }

    /// <summary>지금 띄운 방 — 미니 화면의 실제 주소를 따라간다 (SourceChanged).</summary>
    public string? Current { get; private set; }

    public WebView2 View { get; } = new() { Dock = DockStyle.Fill };

    /// <summary>"큰 창에서 열기" — 지금 방을 큰 창으로.</summary>
    public event Action<string?>? OpenInMain;

    public MiniForm(Uri chatUrl, string placementPath)
    {
        _chatUrl = chatUrl;
        _placementPath = placementPath;
        Text = "HB Chat 미니";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost = true;
        ShowInTaskbar = false;
        MinimumSize = MinSizeMini;
        Size = StartSize;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        if (WindowPlacement.Load(_placementPath)?.FitTo(Screen.AllScreens.Select(s => s.WorkingArea), MinSizeMini) is { } p) Bounds = p.Bounds;

        var top = _top = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 3, 4, 3) };
        top.Controls.Add(_rooms);
        top.Controls.Add(_open);
        _open.Click += (_, _) => OpenInMain?.Invoke(Current);
        // 사용자가 고른 때만 (화살표 · 휠로 움직일 때마다 고정 · 다시 읽지 않게 — /self-review bde22e2224 C2)
        _rooms.SelectionChangeCommitted += (_, _) =>
        {
            if (_rooms.SelectedItem is not RoomItem item) return;
            Pinned = item.Id; // null이면 자동
            _held = null; // 직접 고르면 펫으로 연 방 붙잡기도 푼다
            if ((item.Id ?? MiniRoom.Next(_last, null, null)) is { } id && id != Current) ShowRoom(id);
        };
        // 쓰는 중이라 미룬 자동 전환은 미니 창에서 나갈 때 다시 본다 (bde22e2224 C4)
        Deactivate += (_, _) => Follow();
        Controls.Add(View);
        Controls.Add(top);
    }

    private sealed record RoomItem(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>WebView2 설정이 끝난 뒤 (큰 창이 부름): 미니 화면이 다른 주소로 가면 따라간다.</summary>
    public void Attach() => View.CoreWebView2.SourceChanged += (_, _) => OnSource(View.CoreWebView2.Source);

    /// <summary>
    /// 미니 화면 주소가 바뀜: 방 주소면 그 방을 지금 방으로, ?view=mini가 빠졌으면(오류 때 다시 열기 등) 붙여 다시 연다.
    /// 방이 아닌 채팅 주소(목록 등)로 갔으면 지금 방으로 되돌린다 (bde22e2224 C1).
    /// </summary>
    private void OnSource(string? source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var u) || !NavigationPolicy.SameOrigin(u, _chatUrl)) return;
        if (MainForm.RoomIdOf(source, _chatUrl) is { } id)
        {
            Current = id;
            if (!u.Query.Contains("view=mini", StringComparison.OrdinalIgnoreCase)) ShowRoom(id);
        }
        else if (Current is { } back) ShowRoom(back);
    }

    /// <summary>방 목록이 바뀌었을 때 (UI 스레드). 목록이 실제로 바뀌었을 때만 고르기 칸을 다시 채운다 (열어 둔 칸이 닫히지 않게 — C2).</summary>
    public void UpdateRooms(IReadOnlyList<RoomState> rooms)
    {
        _last = rooms;
        Pinned = MiniRoom.KeepPin(rooms, Pinned);
        var items = new List<RoomItem> { new(null, "자동 (승인 · 답 기다림 · 최근 방)") };
        items.AddRange(rooms.Select(r => new RoomItem(r.Id, Label(r))));
        var key = string.Join("\n", items.Select(i => i.Id + "\t" + i.Label)) + "\n" + Pinned;
        // 펼쳐 둔 동안은 다시 채우지 않는다 — 닫힌 뒤 다음 목록 갱신에 맞춘다 (/self-review 175acbd35a C4)
        if (key != _itemsKey && !_rooms.DroppedDown)
        {
            _itemsKey = key;
            _rooms.BeginUpdate();
            _rooms.Items.Clear();
            _rooms.Items.AddRange([.. items]);
            _rooms.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Id == Pinned));
            _rooms.EndUpdate();
        }
        Follow();
    }

    /// <summary>자동 · 고정 규칙으로 방을 바꾼다. 미니 창에서 쓰는 중(앞 창)이면 바꾸지 않는다 — 처음 띄울 때는 바꾼다.</summary>
    private void Follow()
    {
        if (Current is not null && ActiveForm == this) return;
        if (_held is not null && Visible) return; // 펫을 눌러 연 방은 숨길 때까지 그대로 (/self-review b280752387 C5)
        if (MiniRoom.Next(_last, Pinned, Current) is { } next) ShowRoom(next);
    }

    private static string Label(RoomState r)
    {
        var mark = r.IsQuestion ? "● 질문 · " : r.IsApproval ? "● 승인 · " : r.IsBusy ? "… " : "";
        var title = r.Title.Length > 40 ? r.Title[..40] + "…" : r.Title;
        // 단톡방 구성원 (2026-10-29_단톡방-차례-알림) — 승인 · 질문을 기다리는 구성원만: 실행 · 답 끝 이름은 답마다 바뀌어 고르기 칸을 자꾸 다시 채웠다 (C4)
        if (r.IsApproval && !string.IsNullOrWhiteSpace(r.Member)) title += $" ({(r.Member.Length > 30 ? r.Member[..30] + "…" : r.Member)})";
        return mark + (title.Length > 0 ? title : r.Id);
    }

    public void ShowRoom(string id)
    {
        if (!MainForm.IsRoomId(id) || View.CoreWebView2 is null) return;
        Current = id;
        View.CoreWebView2.Navigate(new Uri(_chatUrl, $"/Room/{id}?view=mini").AbsoluteUri);
    }

    /// <summary>
    /// 화면 테마 (5차 b, 물을 것 2): 테두리는 Windows 것 그대로 두고 제목 줄만 어둡게(DWM) + 위 줄(방 고르기 · 큰 창) 색. 큰 창이 부른다.
    /// </summary>
    public void ApplyTheme(FrameColors colors, bool dark)
    {
        _dark = dark;
        if (IsHandleCreated)
        {
            WindowFrame.SetDark(Handle, dark);
            WindowFrame.Refresh(Handle); // 떠 있는 동안 바꾸면 제목 줄이 다음 활성화까지 옛 색일 수 있다 — 틀을 다시 그리게 (993ef3b338 C3)
        }
        BackColor = View.DefaultBackgroundColor = colors.Page;
        _top.BackColor = colors.Back;
        _rooms.FlatStyle = _open.FlatStyle = FlatStyle.Flat;
        _rooms.BackColor = colors.Hover;
        _rooms.ForeColor = _open.ForeColor = colors.Text;
        _open.BackColor = colors.Selected;
        _open.FlatAppearance.BorderColor = colors.Selected;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowFrame.SetDark(Handle, _dark);
    }

    public void Toggle()
    {
        if (Visible && WindowState != FormWindowState.Minimized) Hide();
        else
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    /// <summary>
    /// 펫을 눌렀을 때 (계획서 2026-10-29_바탕화면-펫): 보이면 숨기고, 아니면 펫 옆(그 화면 안)에 roomId 방으로 연다. 크기는 지금 크기 그대로.
    /// </summary>
    public void ToggleNear(Rectangle pet, string? roomId)
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Hide();
            return;
        }
        WindowState = FormWindowState.Normal;
        Bounds = PetPlace.MiniNear(pet, Size, Screen.FromRectangle(pet).WorkingArea);
        _held = roomId;
        if (roomId is not null && roomId != Current) ShowRoom(roomId);
        Show();
        Activate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _held = null; // 숨기면 펫으로 연 방 붙잡기를 푼다 — 다음부터 다시 자동
    }

    /// <summary>닫기(X)는 숨김. 앱이 끝날 때(<see cref="CloseForExit"/>)만 진짜 닫힌다.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, false).Save(_placementPath);
        if (e.CloseReason == CloseReason.UserClosing && !_appClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>앱이 끝날 때 큰 창이 부른다 — 위치를 남기고 진짜 닫는다.</summary>
    public void CloseForExit()
    {
        _appClosing = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) View.Dispose();
        base.Dispose(disposing);
    }
}
