namespace HB.Chat.Desktop;

/// <summary>
/// 바탕화면 펫 묶음: 설정(desktop-pet.json) · 펫 고르기 · 시트 읽기 · 표정 그림 받기 · 오른쪽 메뉴 · 말풍선.
/// 큰 창(MainForm)이 방 목록이 올 때마다 <see cref="Update"/>, 끊기면 <see cref="Disconnected"/>를 부른다. UI 스레드에서만 쓴다.
/// </summary>
public sealed class PetController : IDisposable
{
    private readonly PetForm _form = new();
    private readonly PetBubble _bubble = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _petMenu = new("펫");
    private readonly ToolStripMenuItem _sizeMenu = new("크기");
    private readonly string _settingsPath, _builtinDir, _dataPetsDir;
    private readonly string? _codexPetsDir;
    private readonly Uri _chatUrl;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Image?> _faces = new(StringComparer.Ordinal);
    private PetSettings _settings;
    private PetSprite? _sprite;
    private string? _loadedPet, _shownPet;
    private bool _disposed;
    private readonly HashSet<string> _fetching = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _failed = new(StringComparer.Ordinal);
    private RoomState? _room;
    private string? _faceUrl;
    private bool _dark;

    /// <summary>펫을 눌렀다 — 따라가는 방(없으면 null)과 펫 자리.</summary>
    public event Action<string?, Rectangle>? Clicked;

    /// <summary>"펫 보이기"가 바뀜 (트레이 메뉴 체크를 맞추라고).</summary>
    public event Action<bool>? VisibleChanged;

    /// <summary>오른쪽 메뉴 "미니 창".</summary>
    public event Action? MiniRequested;

    public PetController(string dataFolder, Uri chatUrl, HttpClient http, Action<string> log)
    {
        _settingsPath = Path.Combine(dataFolder, "desktop-pet.json");
        _dataPetsDir = Path.Combine(dataFolder, "pets");
        _builtinDir = Path.Combine(AppContext.BaseDirectory, "Pets"); // 내장 펫 폴더들 (원본 대시보드 IconPack/Pets — csproj가 복사)
        _codexPetsDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? Path.Combine(home, ".codex", "pets") : null;
        _chatUrl = chatUrl;
        _http = http;
        _log = log;
        _settings = PetSettings.Load(_settingsPath);

        _form.Clicked += () => Clicked?.Invoke(_room?.Id, _form.Bounds);
        _form.Moved += () => Save(_settings with { X = _form.Left, Y = _form.Top });
        _form.Relocated += () => _bubble.Follow(_form.Bounds); // 끌기 중에도 말풍선이 따라온다 (실제 확인 2026-10-29)
        _menu.Items.Add("미니 창", null, (_, _) => MiniRequested?.Invoke());
        _menu.Items.Add(_petMenu);
        _menu.Items.Add(_sizeMenu);
        foreach (var (label, size) in new[] { ("작게", PetSettings.Small), ("보통", PetSettings.Medium), ("크게", PetSettings.Large) })
            _sizeMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetSize(size)) { Tag = size });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("펫 숨기기", null, (_, _) => SetVisible(false));
        _menu.Opening += (_, _) => FillMenus();
        _form.ContextMenuStrip = _menu;
    }

    public bool Visible => _settings.Visible;

    /// <summary>지금 화면에 있어 말풍선을 띄울 수 있다 — 이때는 Windows 알림을 띄우지 않는다.</summary>
    public bool Showing => _settings.Visible && _form.Visible;

    /// <summary>처음 보이기 (큰 창이 뜬 뒤). 설정이 꺼져 있으면 숨긴 채.</summary>
    public void Start(bool dark)
    {
        _dark = dark;
        LoadPet();
        Place();
        if (_settings.Visible) _form.Show();
    }

    public void SetDark(bool dark) => _dark = dark;

    public void SetVisible(bool visible)
    {
        if (_settings.Visible == visible) return;
        Save(_settings with { Visible = visible });
        if (visible) { Place(); _form.Show(); }
        else { _form.Hide(); _bubble.Quiet(); }
        VisibleChanged?.Invoke(visible);
    }

    /// <summary>방 목록이 왔다 (before = 앞 목록, 처음이면 null). 따라갈 방 · 동작 · 표정 그림 · 말풍선을 맞춘다.</summary>
    public void Update(IReadOnlyDictionary<string, RoomState>? before, IReadOnlyList<RoomState> rooms, IReadOnlyList<Notice> notices)
    {
        _form.SetDim(false);
        var room = PetMood.Pick(rooms);
        // 한 번 동작은 따라가던 방 기준 — 그 방의 뒤 상태로 (끝난 방이 목록에서 다른 방에 밀려도 손을 흔든다)
        var watched = _room is not null && rooms.FirstOrDefault(r => r.Id == _room.Id) is { } same ? same : null;
        var burst = PetMood.Burst(before is not null && _room is not null && before.TryGetValue(_room.Id, out var was) ? was : null, watched);
        _room = room;
        _form.SetSteady(PetMood.Steady(room));
        if (burst is { } once) _form.PlayOnce(once);
        if (_sprite is null) ShowFace(room);
        if (Showing && PetMood.Bubble(notices) is { } saying) _bubble.Say(saying, _form.Bounds, _dark);
    }

    /// <summary>채팅 서버와 끊김: 쉬는 흐린 모습.</summary>
    public void Disconnected()
    {
        _room = null;
        _form.SetSteady(PetAction.Idle);
        _form.SetDim(true);
    }

    // ---- 펫 고르기 · 읽기 ----

    private IReadOnlyList<PetSource> Sources() => PetSources.Find(_builtinDir, _codexPetsDir, _dataPetsDir);

    /// <summary>고른 펫을 읽는다. 없어졌거나 못 읽으면 기본 펫, 그것도 안 되면 표정 그림 (이유는 로그).</summary>
    private void LoadPet()
    {
        var sources = Sources();
        var key = PetSources.Resolve(_settings.Pet, sources);
        if (key == _loadedPet) return;
        _loadedPet = key;
        var old = _sprite;
        _sprite = null;
        _shownPet = PetSources.Faces;
        if (key != PetSources.Faces)
        {
            var fallback = PetSources.Resolve(null, sources); // 고른 펫을 못 읽으면 기본 내장 펫 (gpt_chan)
            foreach (var source in new[] { sources.FirstOrDefault(s => s.Key == key), sources.FirstOrDefault(s => s.Key == fallback) }.OfType<PetSource>().Distinct())
            {
                try
                {
                    _sprite = PetSprite.Load(source.Path, source.Version);
                    _shownPet = source.Key; // 메뉴 체크는 실제로 보이는 펫 (기본 펫으로 넘어간 경우 — 지휘자 리뷰 K4)
                    break;
                }
                // FormatException: WPF가 형식 오류를 FileFormatException으로 감싸 던지는 경우 (/self-review b280752387 C1 — 이 PC의 깨진 webp는 COMException).
                // 놓치면 MainForm 생성자(Start)에서 나 앱이 뜨지 않았다
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or FormatException
                                               or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    _log($"펫 {source.Key}를 읽지 못함: {ex.GetType().Name}: {ex.Message}"); // WebP 디코더가 없는 PC 등
                }
            }
        }
        _form.UseSprite(_sprite);
        old?.Dispose();
        if (_sprite is null) ShowFace(_room);
        _form.SetPetSize(_settings.WindowSize(_sprite is not null));
    }

    /// <summary>표정 그림 모드: 따라가는 방 줄의 faceUrl을 받아 그린다 (주소마다 한 번 받아 둔다). 없으면 표정 이름 판.</summary>
    /// <remarks>
    /// 같은 주소는 한 번만 받고(받는 중이면 기다리지 않고 판 글자), 실패한 주소는 1분 뒤에 다시 받는다. 캐시가 차면 지금 그림만 남기고 Dispose한다.
    /// 컨트롤러가 정리된 뒤 끝난 받기는 버린다 (/self-review b280752387 C3 · 지휘자 리뷰 K2 · K5).
    /// </remarks>
    private async void ShowFace(RoomState? room)
    {
        var url = room?.FaceUrl;
        var label = room is null ? "HB" : room.IsApproval ? "승인?" : room.IsBusy ? "생각 중" : "쉼";
        var same = url == _faceUrl;
        _faceUrl = url;
        if (url is null || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal))
        {
            _form.UseFace(null, label);
            return;
        }
        if (_faces.TryGetValue(url, out var cached))
        {
            if (!same) _form.UseFace(cached, label);
            return;
        }
        if (_fetching.Contains(url) || _failed.TryGetValue(url, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1))
        {
            _form.UseFace(null, label); // 받는 중 · 막 실패함 — 판 글자는 지금 상태로
            return;
        }
        _fetching.Add(url);
        Image? image = null;
        try
        {
            var bytes = await _http.GetByteArrayAsync(new Uri(_chatUrl, url));
            if (bytes.Length <= 8 * 1024 * 1024) image = Image.FromStream(new MemoryStream(bytes)); // png · gif (스티커 팩) — 스트림은 그림과 같이 산다
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            _log($"표정 그림을 받지 못함: {ex.GetType().Name}");
        }
        finally { _fetching.Remove(url); }
        if (_disposed)
        {
            image?.Dispose();
            return;
        }
        if (image is null)
        {
            _failed[url] = DateTime.UtcNow;
            if (_faceUrl == url && _sprite is null) _form.UseFace(null, label);
            return;
        }
        _failed.Remove(url);
        if (_faces.Count >= 64) // 팩을 자주 바꿔도 커지지 않게 — 지금 보이는 그림 말고는 Dispose
        {
            var keep = _faceUrl is { } now && _faces.TryGetValue(now, out var showing) ? showing : null;
            foreach (var (_, old) in _faces) if (!ReferenceEquals(old, keep)) old?.Dispose();
            _faces.Clear();
            if (keep is not null) _faces[_faceUrl!] = keep;
        }
        _faces[url] = image;
        if (_faceUrl == url && _sprite is null) _form.UseFace(image, label);
    }

    // ---- 메뉴 · 설정 ----

    private void FillMenus()
    {
        _petMenu.DropDownItems.Clear();
        var current = _shownPet;
        foreach (var source in Sources())
            _petMenu.DropDownItems.Add(new ToolStripMenuItem(source.Label, null, (_, _) => SetPet(source.Key)) { Checked = source.Key == current });
        _petMenu.DropDownItems.Add(new ToolStripMenuItem("표정 그림 (방 스티커 팩)", null, (_, _) => SetPet(PetSources.Faces)) { Checked = current == PetSources.Faces });
        _petMenu.DropDownItems.Add(new ToolStripSeparator());
        _petMenu.DropDownItems.Add("펫 폴더 열기", null, (_, _) => OpenPetsFolder());
        foreach (ToolStripMenuItem item in _sizeMenu.DropDownItems) item.Checked = item.Tag is int size && size == _settings.Size;
    }

    private void SetPet(string key)
    {
        Save(_settings with { Pet = key });
        _faceUrl = null;
        LoadPet();
        Place();
    }

    private void SetSize(int size)
    {
        Save(_settings with { Size = size });
        _form.SetPetSize(_settings.WindowSize(_sprite is not null));
        Place();
    }

    /// <summary>데이터 폴더 pets/를 연다 (없으면 만든다) — 시트 폴더(spritesheet.webp + pet.json)를 넣는 곳.</summary>
    private void OpenPetsFolder()
    {
        try
        {
            Directory.CreateDirectory(_dataPetsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_dataPetsDir}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log($"펫 폴더를 열지 못함: {ex.Message}");
        }
    }

    /// <summary>저장한 자리(보이는 화면 안이면)나 기본 자리로.</summary>
    private void Place()
    {
        var size = _settings.WindowSize(_sprite is not null);
        var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        _form.Location = PetPlace.Start(_settings, size, [.. Screen.AllScreens.Select(s => s.WorkingArea)], primary);
        _form.SetPetSize(size);
    }

    private void Save(PetSettings settings)
    {
        _settings = settings;
        settings.Save(_settingsPath);
    }

    public void Dispose()
    {
        _disposed = true;
        _bubble.Dispose();
        _form.Dispose();
        _menu.Dispose();
        _sprite?.Dispose();
        foreach (var image in _faces.Values) image?.Dispose();
    }
}
