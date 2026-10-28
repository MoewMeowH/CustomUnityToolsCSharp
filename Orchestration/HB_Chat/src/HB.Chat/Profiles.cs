using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace HB.Chat;

/// <summary>주체 하나의 프로필: 보이는 이름 · 아바타 색(#rrggbb) · 아바타 그림 파일 이름(프로필 폴더 안). 비어 있으면 기본값.</summary>
public sealed record Profile(string? Name = null, string? Color = null, string? Image = null);

/// <summary>
/// 주체별 프로필 (3차 e — 노션 "각 주체 별 프로필 수정기능", 해석 "이름 · 그림 · 색 + 지휘자 표정"): 나 · 지휘자 · 외부 에이전트(이름별).
/// 데이터 폴더 profiles.json(원자적 쓰기, 깨졌으면 덮지 않고 쓰기 거절)과 profiles/ 그림. 그림은 올린 파일의 내용으로 형식을 보고(png · jpg · webp),
/// 이름은 앱이 정한다 — 올린 이름은 경로에 쓰지 않는다. 말투 · 프롬프트는 범위 밖(결정 4 — 리뷰 품질 · 골든에 영향).
/// </summary>
public sealed partial class ProfileStore
{
    public const string User = "user", Conductor = "conductor", AgentPrefix = "agent:";
    public const int MaxNameChars = 30, MaxAgentChars = 60, MaxImageBytes = 1024 * 1024, MaxAgents = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly string _file;
    private readonly string _imageDir;
    private Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);
    private Dictionary<string, string> _faces = new(StringComparer.OrdinalIgnoreCase);

    public string? LoadError { get; }

    public ProfileStore(string dataFolder)
    {
        // 절대 경로로 — 그림 끝점이 PhysicalFile로 보내고, 데이터 폴더가 상대 경로여도 현재 폴더에 따라 바뀌지 않게
        _file = Path.GetFullPath(Path.Combine(dataFolder, "profiles.json"));
        _imageDir = Path.GetFullPath(Path.Combine(dataFolder, "profiles"));
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(_file), Json) is { } saved)
            {
                // 읽을 때도 모양을 본다 — 앱 밖에서 고친 값이 화면(style · src)에 그대로 들어가지 않게
                foreach (var (key, p) in saved.Profiles ?? [])
                    if (IsKey(key) && p is not null)
                        _profiles[key] = new Profile(CleanName(p.Name, key), IsColor(p.Color) ? p.Color : null, IsImageName(p.Image) ? p.Image : null);
                foreach (var (face, tag) in saved.Faces ?? [])
                    if (IsFaceName(face) && !string.IsNullOrWhiteSpace(tag) && tag.Length <= 40) _faces[face] = tag.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public Profile Get(string key)
    {
        lock (_lock) return _profiles.TryGetValue(key, out var p) ? p : new Profile();
    }

    /// <summary>프로필을 둔 외부 에이전트 이름들.</summary>
    public IReadOnlyList<string> Agents()
    {
        lock (_lock) return [.. _profiles.Keys.Where(k => k.StartsWith(AgentPrefix, StringComparison.Ordinal)).Select(k => k[AgentPrefix.Length..]).Order()];
    }

    /// <summary>지휘자 표정 덮어쓰기 (상태 이름 → 스티커 태그). 설정 파일 Chat:Expressions보다 먼저.</summary>
    public string? FaceTag(Face face)
    {
        lock (_lock) return _faces.TryGetValue(face.ToString(), out var t) ? t : null;
    }

    /// <summary>이름 · 색 저장 (빈 값이면 기본으로). 오류 문구(null이면 저장).</summary>
    public string? Save(string? key, string? name, string? color)
    {
        if (Check(key, name, color) is { } error) return error;
        var n = name?.Trim();
        var c = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        return Change(key!, p => p with { Name = string.IsNullOrEmpty(n) ? null : n, Color = c });
    }

    /// <summary>이름 · 색 검사만 (저장 없이) — 설정 화면이 그림보다 먼저 본다. 오류 문구(null이면 통과).</summary>
    public static string? Check(string? key, string? name, string? color)
    {
        if (key is null || !IsKey(key)) return "알 수 없는 주체입니다.";
        var n = name?.Trim();
        if (n is { Length: > MaxNameChars }) return $"이름은 {MaxNameChars}자까지입니다.";
        if (n is not null && n.Any(char.IsControl)) return "이름에 줄바꿈 · 제어 문자를 넣을 수 없습니다.";
        var c = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        if (c is not null && !IsColor(c)) return "색은 #rrggbb 모양입니다.";
        return null;
    }

    /// <summary>
    /// 아바타 그림 바꾸기. 내용의 첫 바이트로 형식을 본다(png · jpg · webp). 1MB까지. 앞 그림 파일은 지운다. 오류 문구(null이면 저장).
    /// </summary>
    public async Task<string?> SetImageAsync(string? key, Stream content, CancellationToken ct)
    {
        if (key is null || !IsKey(key)) return "알 수 없는 주체입니다.";
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes) return $"그림은 {MaxImageBytes / 1024}KB까지입니다.";
            buffer.Write(chunk, 0, read);
        }
        if (Sniff(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) is not { } ext) return "png · jpg · webp 그림만 받습니다.";
        var file = $"{Hash(key)}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}.{ext}";
        try
        {
            Directory.CreateDirectory(_imageDir);
            await File.WriteAllBytesAsync(Path.Combine(_imageDir, file), buffer.ToArray(), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"그림을 저장하지 못했습니다: {ex.Message}";
        }
        string? old = null;
        var error = Change(key, p => { old = p.Image; return p with { Image = file }; });
        DeleteImage(error is null ? old : file); // 저장에 실패하면 새 그림을, 성공하면 앞 그림을 지운다
        return error;
    }

    /// <summary>아바타 그림 지우기 (글자 아바타로).</summary>
    public string? ClearImage(string? key)
    {
        if (key is null || !IsKey(key)) return "알 수 없는 주체입니다.";
        string? old = null;
        var error = Change(key, p => { old = p.Image; return p with { Image = null }; });
        if (error is null) DeleteImage(old);
        return error;
    }

    /// <summary>지휘자 표정 저장: 상태 이름 → 스티커 태그(있는 태그만, 빈 값이면 기본). 오류 문구(null이면 저장).</summary>
    public string? SetFaces(IReadOnlyDictionary<string, string?> faces, IReadOnlyCollection<string> knownTags)
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (face, tag) in faces)
        {
            if (!IsFaceName(face)) return $"알 수 없는 표정 상태입니다: {face}";
            if (string.IsNullOrWhiteSpace(tag)) continue;
            // 이미 저장한 태그는 목록에서 빠졌어도 그대로 둘 수 있다 (설정 화면이 그 값을 선택된 채 다시 보낸다)
            if (!knownTags.Contains(tag.Trim()) && FaceTag(Enum.Parse<Face>(face)) != tag.Trim()) return $"없는 스티커 태그입니다: {tag}";
            next[face] = tag.Trim();
        }
        lock (_lock)
        {
            if (LoadError is not null) return $"profiles.json을 읽지 못해 쓰지 않습니다: {LoadError}";
            var before = _faces;
            _faces = next;
            return TrySave(() => _faces = before);
        }
    }

    /// <summary>프로필 그림 파일 경로 (보기 요청). 이름 모양이 아니거나 없으면 null.</summary>
    public string? ImagePath(string? file)
    {
        if (!IsImageName(file)) return null;
        var path = Path.Combine(_imageDir, file!);
        return File.Exists(path) ? path : null;
    }

    public static string ContentType(string file) => Path.GetExtension(file) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>외부 에이전트 키 (이름은 앞뒤 공백 걷고 60자 · 제어 문자 없음). 모양이 아니면 null.</summary>
    public static string? AgentKey(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) || n.Length > MaxAgentChars || n.Any(char.IsControl) ? null : AgentPrefix + n;
    }

    private string? Change(string key, Func<Profile, Profile> change)
    {
        lock (_lock)
        {
            if (LoadError is not null) return $"profiles.json을 읽지 못해 쓰지 않습니다: {LoadError}";
            if (!_profiles.ContainsKey(key) && key.StartsWith(AgentPrefix, StringComparison.Ordinal)
                && _profiles.Keys.Count(k => k.StartsWith(AgentPrefix, StringComparison.Ordinal)) >= MaxAgents)
                return $"외부 에이전트 프로필은 {MaxAgents}개까지입니다.";
            var had = _profiles.TryGetValue(key, out var before);
            var next = change(before ?? new Profile());
            if (next == new Profile()) _profiles.Remove(key); else _profiles[key] = next;
            return TrySave(() => { if (had) _profiles[key] = before!; else _profiles.Remove(key); });
        }
    }

    /// <summary>원자적 쓰기 (잠금 안에서). 실패하면 되돌리고 오류 문구.</summary>
    private string? TrySave(Action rollback)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new ProfileFile { Profiles = new(_profiles), Faces = new(_faces) }, Json), new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rollback();
            return $"저장하지 못했습니다: {ex.Message}";
        }
    }

    private void DeleteImage(string? file)
    {
        if (ImagePath(file) is not { } path) return;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>그림 형식 (내용의 첫 바이트). 모르면 null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "png"
        : b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "jpg"
        : b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "webp"
        : null;

    private static bool IsKey(string key) =>
        key is User or Conductor || (key.StartsWith(AgentPrefix, StringComparison.Ordinal) && AgentKey(key[AgentPrefix.Length..]) == key);

    private static string? CleanName(string? name, string key) =>
        name is { Length: > 0 and <= MaxNameChars } && !name.Any(char.IsControl) ? name : null;

    /// <summary>정의된 표정 이름 그대로만 ("1" 같은 숫자 · "Idle,Done" 같은 조합 · 대소문자 다른 이름은 받지 않는다).</summary>
    private static bool IsFaceName(string? s) =>
        Enum.TryParse<Face>(s, out var f) && Enum.IsDefined(f) && string.Equals(f.ToString(), s, StringComparison.Ordinal);

    private static bool IsColor(string? c) => c is not null && ColorShape().IsMatch(c);

    private static bool IsImageName(string? f) => f is not null && ImageShape().IsMatch(f);

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    private sealed class ProfileFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Profile>? Profiles { get; set; }
        public Dictionary<string, string>? Faces { get; set; }
    }

    // 끝은 \z — $는 끝 줄바꿈 하나를 허용한다
    [GeneratedRegex(@"^#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColorShape();

    [GeneratedRegex(@"^[0-9a-f]{16}-[0-9a-f]{8}\.(png|jpg|webp)\z")]
    private static partial Regex ImageShape();
}

/// <summary>
/// 화면용 프로필 (3차 e): 보이는 이름 · 아바타 HTML. 아바타는 그림이 있으면 그림, 없으면 이름 첫 글자(나는 "나") · 색.
/// 회사 모드는 CSS가 그림을 숨기고 글자를 보인다 (표정 스티커와 같은 규칙).
/// </summary>
public sealed class ProfileView(ProfileStore store)
{
    public ProfileStore Store => store;

    public string UserName => store.Get(ProfileStore.User).Name ?? "나";
    public string ConductorName => store.Get(ProfileStore.Conductor).Name ?? "지휘자";
    public string AgentName(string name) => ProfileStore.AgentKey(name) is { } k ? store.Get(k).Name ?? name : name;

    /// <summary>그림 주소 — 파일이 있을 때만 (지워진 그림이면 깨진 그림 대신 글자 아바타).</summary>
    public string? ImageUrl(string? key) =>
        key is not null && store.Get(key).Image is { } f && store.ImagePath(f) is not null ? "/profile-image/" + f : null;

    /// <summary>아바타 글자: 나는 이름이 없으면 "나", 지휘자는 "C", 그 밖은 이름 첫 글자(서로게이트 쌍 · 결합 문자를 자르지 않게 글자 단위).</summary>
    public string Letter(string? key, string displayName) =>
        key == ProfileStore.User && store.Get(key).Name is null ? "나"
        : key == ProfileStore.Conductor && store.Get(key).Name is null ? "C"
        : displayName.Length > 0 ? StringInfo.GetNextTextElement(displayName).ToUpperInvariant() : "?";

    /// <summary>아바타 HTML (말풍선 · 목록). 글은 모두 인코딩한다. key가 null이면 프로필을 찾지 않는 글자 아바타.</summary>
    public HtmlString Avatar(string? key, string displayName, string? title = null)
    {
        var p = key is null ? new Profile() : store.Get(key);
        var sb = new StringBuilder("<div class=\"avatar\"");
        if (p.Color is { } c) sb.Append(" style=\"background:").Append(c).Append('"'); // IsColor로 거른 #rrggbb만
        if (title is not null) sb.Append(" title=\"").Append(WebUtility.HtmlEncode(title)).Append('"');
        sb.Append('>');
        if (ImageUrl(key) is { } url) sb.Append("<img class=\"profile-img\" src=\"").Append(WebUtility.HtmlEncode(url)).Append("\" alt=\"\" />");
        sb.Append("<span class=\"avatar-letter\">").Append(WebUtility.HtmlEncode(Letter(key, displayName))).Append("</span></div>");
        return new HtmlString(sb.ToString());
    }

    /// <summary>화면 스크립트용 (진행 중 말풍선): 나 · 지휘자의 이름 · 글자 · 색 · 그림.</summary>
    public string Json() => JsonSerializer.Serialize(new
    {
        user = new { name = UserName, letter = Letter(ProfileStore.User, UserName), color = store.Get(ProfileStore.User).Color, image = ImageUrl(ProfileStore.User) },
        conductor = new { name = ConductorName, letter = Letter(ProfileStore.Conductor, ConductorName), color = store.Get(ProfileStore.Conductor).Color, image = ImageUrl(ProfileStore.Conductor) },
    });
}
