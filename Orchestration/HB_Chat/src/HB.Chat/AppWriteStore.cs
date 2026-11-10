using System.Text.Json;

namespace HB.Chat;

/// <summary>
/// 앱 단계 방의 작업 폴더 밖 쓰기 설정 (데이터 폴더 app-writes.json): 묻기(기본) · 그냥 허용, 늘 허용한 폴더 목록 (앱 전체 — 결정 "앱 전체").
/// 묻기면 앱 단계 실행이 ask 규칙 <c>Edit(//**)</c>로 모든 Write · Edit를 앱에 묻고, 앱이 <see cref="WorkspaceStore.AppWrite"/>로 가른다.
/// 원자적으로 쓰고(WorkspaceStore와 같은 규칙), 깨졌으면 덮지 않고 모든 쓰기를 거절하며 묻기로 둔다(닫힌 쪽).
/// 폴더는 더할 때 · 켤 때 읽을 때 같은 검사(작업 폴더와 같은 검사: 드라이브 루트 · 시스템 · 홈 · 보호 폴더를 품거나 그 안 · 링크)를 거친다.
/// </summary>
public sealed class AppWriteStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly Func<string, string?> _whyUnsafe;
    private bool _ask = true;

    /// <summary>파일에 있는 그대로 (쓰지 않는 항목도 — 저장할 때 잃지 않게).</summary>
    private readonly List<string> _saved = [];

    /// <summary>쓰는 것: 검사를 통과한 최종 경로.</summary>
    private readonly List<string> _folders = [];

    public string? LoadError { get; }

    /// <summary>파일에 있지만 쓰지 않는 폴더와 이유 (설정 화면 경고).</summary>
    public IReadOnlyList<string> LoadWarnings { get; }

    /// <param name="path">app-writes.json 경로.</param>
    /// <param name="whyUnsafe">폴더를 늘 허용하면 안 되는 까닭 (null이면 됨) — 작업 폴더 목록의 <see cref="WorkspaceStore.WhyUnsafe"/>.</param>
    public AppWriteStore(string path, Func<string, string?> whyUnsafe)
    {
        _path = path;
        _whyUnsafe = whyUnsafe;
        var warnings = new List<string>();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<WriteFile>(File.ReadAllText(path), Json) is { } file)
            {
                _ask = file.Ask;
                // "folders": null도 빈 목록으로 — 손으로 고친 파일 때문에 앱이 켜지지 않게
                foreach (var folder in (file.Folders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)))
                {
                    _saved.Add(folder);
                    if (_whyUnsafe(folder) is { } why) warnings.Add($"{folder}: {why}");
                    else if (WorkspaceStore.FinalFolder(folder) is not { } final) warnings.Add($"{folder}: 폴더의 실제 경로를 확인하지 못했습니다.");
                    else if (WhyTooBroad(final) is { } broad) warnings.Add($"{folder}: {broad}");
                    else _folders.Add(final);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            _ask = true;
        }
        LoadWarnings = warnings;
    }

    /// <summary>앱 단계 폴더 밖 쓰기를 카드로 묻는지 (false = 그 전처럼 auto가 그냥 허용).</summary>
    public bool Ask
    {
        get { lock (_lock) return _ask; }
    }

    /// <summary>늘 허용한 폴더 (최종 경로, 더한 순서).</summary>
    public IReadOnlyList<string> Folders()
    {
        lock (_lock) return [.. _folders];
    }

    /// <summary>파일에 적힌 폴더 전부 (쓰지 않는 항목 포함 — 설정 화면이 빼기 단추를 보인다).</summary>
    public IReadOnlyList<string> SavedFolders()
    {
        lock (_lock) return [.. _saved];
    }

    /// <summary>묻기 · 그냥 허용 저장. 오류 문구(null이면 저장).</summary>
    public string? SetAsk(bool ask)
    {
        lock (_lock)
        {
            if (LoadError is not null) return $"{Path.GetFileName(_path)}을 읽지 못해 쓰지 않습니다: {LoadError}";
            var before = _ask;
            _ask = ask;
            if (TrySave() is { } error)
            {
                _ask = before;
                return error;
            }
        }
        return null;
    }

    /// <summary>
    /// 폴더 늘 허용 (최종 경로로 저장). 오류 문구(null이면 더함 · 이미 있으면 그대로 null).
    /// 카드의 "이 폴더 늘 허용"과 설정 화면이 같이 쓴다.
    /// </summary>
    public string? AddFolder(string? folder)
    {
        lock (_lock)
        {
            if (Check(folder, out var final) is { } why) return why;
            if (_folders.Contains(final, StringComparer.OrdinalIgnoreCase)) return null;
            // 켤 때 경고로 빠진 같은 줄이 있으면 그 줄을 살린다 — 두 줄로 보이지 않게
            var stale = _saved.FindIndex(f => string.Equals(f, final, StringComparison.OrdinalIgnoreCase));
            if (stale < 0) _saved.Add(final);
            _folders.Add(final);
            if (TrySave() is { } error)
            {
                if (stale < 0) _saved.RemoveAt(_saved.Count - 1);
                _folders.RemoveAt(_folders.Count - 1);
                return error;
            }
        }
        return null;
    }

    /// <summary>
    /// 이 폴더를 늘 허용할 수 없는 까닭 (null이면 됨) — 카드가 "이 폴더 늘 허용" 단추를 보이기 전에 같은 검사로 본다.
    /// </summary>
    public string? WhyNotFolder(string? folder)
    {
        lock (_lock) return Check(folder, out _);
    }

    /// <summary>늘 허용 폴더의 최종 경로 (링크 · subst · 짧은 이름이 풀린 것). 카드에 이 값을 보이고 그대로 저장한다.</summary>
    public static string? FinalOf(string folder) => WorkspaceStore.FinalFolder(folder);

    /// <summary>
    /// 너무 넓은 폴더 — 사용자 홈 바로 아래(Documents · AppData …)와 AppData 바로 아래(Roaming · Local · LocalLow).
    /// 그 안에는 실행으로 이어지는 사용자 설정(파이썬 site-packages · 편집기 설정 …)이 흩어져 있어 통째로 묻지 않게 두지 않는다.
    /// </summary>
    public static string? WhyTooBroad(string final)
    {
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (homePath.Length <= 3 || (WorkspaceStore.FinalFolder(homePath) ?? homePath) is not { } home) return null;
        var appData = Path.Combine(home, "AppData");
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(final));
        bool Same(string? a, string b) => a is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
        return Same(parent, home) || Same(parent, appData)
            ? "사용자 홈 바로 아래 · AppData 바로 아래 폴더는 너무 넓어 늘 허용하지 않습니다 — 그 안의 작업 폴더를 고르세요."
            : null;
    }

    /// <summary>잠금 안에서: 읽기 오류 · 작업 폴더와 같은 검사 · 최종 경로 · 너무 넓은 폴더.</summary>
    private string? Check(string? folder, out string final)
    {
        final = "";
        if (string.IsNullOrWhiteSpace(folder)) return "폴더 경로가 비었습니다.";
        var raw = folder.Trim();
        if (LoadError is not null) return $"{Path.GetFileName(_path)}을 읽지 못해 쓰지 않습니다: {LoadError}";
        if (_whyUnsafe(raw) is { } why) return why;
        if (WorkspaceStore.FinalFolder(raw) is not { } resolved) return "폴더의 실제 경로를 확인하지 못했습니다.";
        final = resolved;
        return WhyTooBroad(final);
    }

    /// <summary>늘 허용 빼기 (쓰지 않는 항목 포함, 적힌 글자 그대로 비교). 오류 문구(null이면 뺌).</summary>
    public string? RemoveFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "폴더 경로가 비었습니다.";
        var raw = folder.Trim();
        lock (_lock)
        {
            if (LoadError is not null) return $"{Path.GetFileName(_path)}을 읽지 못해 쓰지 않습니다: {LoadError}";
            var index = _saved.FindIndex(f => string.Equals(f, raw, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return "목록에 없는 폴더입니다.";
            var savedBefore = _saved.ToList();
            var foldersBefore = _folders.ToList();
            _saved.RemoveAt(index);
            _folders.RemoveAll(f => string.Equals(f, raw, StringComparison.OrdinalIgnoreCase)
                                    || WorkspaceStore.FinalFolder(raw) is { } final && string.Equals(f, final, StringComparison.OrdinalIgnoreCase));
            if (TrySave() is { } error)
            {
                _saved.Clear();
                _saved.AddRange(savedBefore);
                _folders.Clear();
                _folders.AddRange(foldersBefore);
                return error;
            }
        }
        return null;
    }

    private string? TrySave()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new WriteFile { Ask = _ask, Folders = [.. _saved] }, Json));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"저장하지 못했습니다: {ex.Message}";
        }
    }

    private sealed class WriteFile
    {
        public int Version { get; set; } = 1;
        public bool Ask { get; set; } = true;
        public List<string>? Folders { get; set; } = [];
    }
}
