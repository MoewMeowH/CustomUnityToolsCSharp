using System.Text;
using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>
/// 방 첨부. 끌어 놓기 · 캡처 붙여 넣기 · 첨부 단추로 올린 파일을 데이터 폴더 attachments/&lt;방ID&gt;에 저장하고,
/// 보낼 때 본문 끝에 <c>[첨부] 절대 경로</c> 줄을 붙인다. 실행 인자에 그 폴더를 --add-dir로 더해 지휘자가 Read로 연다
/// (2-2 0번 실측: 그림도 모델에 보임).
/// 명령 방은 그 폴더 편집을 막는다 — 추가 폴더의 .claude/skills를 CLI가 불러오기 때문 (/self-review 0c3b7e3514 C2 실측).
/// </summary>
public static partial class Attachments
{
    /// <summary>파일 하나 · 한 말의 상한.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int MaxPerMessage = 10;

    /// <summary>저장 이름에 남기는 원래 이름 길이 — 이름이 프롬프트에 실리므로 짧게 (0c3b7e3514 C1).</summary>
    public const int NameChars = 40;

    /// <summary>본문의 첨부 줄 머리.</summary>
    public const string Marker = "[첨부] ";

    /// <summary>화면에 그대로 보여 줄 그림 (확장자 → 형식). SVG는 스크립트가 들 수 있어 넣지 않는다 — 내려받기로.</summary>
    public static readonly IReadOnlyDictionary<string, string> Images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    /// <summary>그 방 첨부 폴더 (절대 경로 — 데이터 폴더가 상대 경로여도 [첨부] 줄 · --add-dir가 같은 곳을 가리키게, 5b26cff7df C5).</summary>
    public static string Folder(string dataFolder, string roomId) => Path.GetFullPath(Path.Combine(dataFolder, "attachments", roomId));

    public static bool IsImage(string name) => Images.ContainsKey(Path.GetExtension(name));

    /// <summary>
    /// 저장할 이름: 원래 이름의 마지막 부분만, 허용 문자(글자 · 숫자 · . _ - 공백 · 괄호) 밖은 _, 앞 점 · 끝 점 · 공백 걷기,
    /// 확장자를 빼고 <see cref="NameChars"/>자. 비면 "file". Windows 예약 이름(CON 등)은 앞에 _.
    /// </summary>
    public static string SafeName(string? name)
    {
        var last = (name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var sb = new StringBuilder(last.Length);
        foreach (var c in last) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' or '(' or ')' ? c : '_');
        var safe = sb.ToString().Trim().Trim('.').Trim();
        if (safe.Length == 0) safe = "file";
        var ext = Path.GetExtension(safe);
        if (ext.Length > 12) ext = "";
        var stem = safe[..^ext.Length].TrimEnd();
        if (stem.Length > NameChars) stem = RoomList.Cut(stem, NameChars).TrimEnd();
        if (stem.Length == 0) stem = "file";
        if (Reserved().IsMatch(stem)) stem = "_" + stem;
        return stem + ext;
    }

    /// <summary>
    /// 방 첨부 폴더에 저장한다. 이름은 시각_걸러 낸 이름, 같은 이름이 있으면 -2, -3 ….
    /// 임시 파일(.part, 요청마다 다른 이름)에 쓰고 옮기며, 옮길 때 이름이 겹치면 다음 번호로. 실패 · 취소면 임시 파일을 지운다 (5b26cff7df C4).
    /// 폴더가 재분석 지점(링크 · 정션)이면 쓰지 않는다 (0c3b7e3514 C3). 저장한 절대 경로.
    /// </summary>
    public static async Task<string> SaveAsync(string folder, string originalName, Stream content, DateTimeOffset now, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        if (IsReparse(folder)) throw new IOException("첨부 폴더가 링크(재분석 지점)라 쓰지 않습니다.");
        var safe = SafeName(originalName);
        var stem = $"{now:yyyyMMdd-HHmmss}_{Path.GetFileNameWithoutExtension(safe)}";
        var ext = Path.GetExtension(safe);
        var tmp = Path.Combine(folder, $".{Guid.NewGuid():N}.part");
        try
        {
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(file, ct);
            for (var i = 1; ; i++)
            {
                var path = Path.Combine(folder, i == 1 ? stem + ext : $"{stem}-{i}{ext}");
                if (File.Exists(path)) continue;
                try
                {
                    File.Move(tmp, path);
                    return path;
                }
                catch (IOException) when (File.Exists(path) && i < 100) { } // 같은 순간 같은 이름 — 다음 번호로
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// 첨부 보기 요청의 이름 → 그 방 폴더 안 파일 경로. 이름에 경로 구분자 · .. · : 가 있거나, 폴더 밖이거나, 없거나,
    /// 폴더 · 파일이 재분석 지점이면 null.
    /// </summary>
    public static string? Resolve(string folder, string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Contains(':')) return null;
        string full, root;
        try
        {
            root = Path.GetFullPath(folder);
            full = Path.GetFullPath(Path.Combine(root, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
        return IsReparse(root) || IsReparse(full) ? null : full;
    }

    /// <summary>보내지 않은 첨부를 남겨 두는 시간 — 입력칸에 올려 둔 채인 초안 첨부를 지우지 않게 넉넉히.</summary>
    public static readonly TimeSpan UnsentKeep = TimeSpan.FromDays(1);

    /// <summary>
    /// 보내지 않은 첨부 정리: 휴지통 밖 방의 첨부 폴더에서 <see cref="UnsentKeep"/>보다 오래됐고
    /// 그 방 · 그 방을 이어받은 갈래 방들의 세션 기록 어디에도 이름이 안 나오는 파일과, 끊긴 업로드의 임시 파일(.part)을 지운다.
    /// 지우는 쪽으로 틀리지 않게: 실행 중인 방, 대화한 적 있는데(그 방 · 갈래 중 하나라도) 기록 파일을 못 찾는 방, 기록을 읽다 실패한 방은 건너뛴다
    /// (/self-review security 4c300badae C1 · 70170a8801 C1 · C6 — 기록 폴더 설정이 바뀌거나 CLI가 오래된 기록을 지우면 보낸 첨부까지 지웠다). 지운 파일 수.
    /// </summary>
    public static int CleanUnsent(string dataFolder, IReadOnlyList<Room> rooms, Func<string, string?> transcriptPath, DateTimeOffset now,
        Func<string, bool>? isBusy = null)
    {
        var removed = 0;
        foreach (var room in rooms.Where(r => r.DeletedAt is null && RoomStore.IsRoomId(r.Id) && isBusy?.Invoke(r.Id) != true))
        {
            var folder = Folder(dataFolder, room.Id);
            try
            {
                if (!Directory.Exists(folder) || IsReparse(folder)) continue;
                var old = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => now - new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) > UnsentKeep).ToList();
                if (old.Count == 0) continue;
                // 그 방과 그 방 첨부를 이어받은 갈래 방들의 기록 (갈래 기록에도 원래 방 [첨부] 줄이 들어 있다)
                var related = rooms.Where(r => r.Id == room.Id || r.AttachFrom.Contains(room.Id)).ToList();
                var text = new StringBuilder();
                var missing = false;
                foreach (var r in related)
                {
                    if (transcriptPath(r.SessionId) is { } path)
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        text.Append(reader.ReadToEnd());
                    }
                    else if (r.LastRunAt is not null || r.KnownSize is not null) missing = true; // 대화했는데 기록이 없다 — 모르면 지우지 않는다
                }
                if (missing) continue;
                var all = text.ToString();
                foreach (var file in old)
                {
                    var unfinished = file.Name.StartsWith('.') && file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                    if (!unfinished && (file.Name.StartsWith('.') || Mentioned(all, file.Name))) continue;
                    try
                    {
                        file.Delete();
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { } // 그 방은 다음에
        }
        return removed;
    }

    /// <summary>기록(jsonl 원문)에 그 파일 이름이 있는지 — 그대로 또는 JSON 이스케이프(\uXXXX)된 모양.</summary>
    private static bool Mentioned(string transcript, string name) =>
        transcript.Contains(name, StringComparison.Ordinal)
        || transcript.Contains(System.Text.Json.JsonSerializer.Serialize(name)[1..^1], StringComparison.Ordinal);

    /// <summary>곁칸 "첨부" 탭에 보일 파일 상한.</summary>
    public const int MaxListed = 200;

    /// <summary>
    /// 그 방 첨부 파일 목록 (곁칸 "첨부" 탭, 3차 c): 최근 것부터 <see cref="MaxListed"/>개 + 넘쳤는지 볼 하나 더 (화면은 MaxListed개만 그린다).
    /// 쓰는 중인 임시 파일(.part) · 숨김(. 시작) · 하위 폴더는 빼고, 보기 요청 <see cref="Resolve"/>가 거절하는 이름(가운데 .. 등 · 재분석 지점)도 뺀다 —
    /// 목록에 보이는데 열면 404였다 (/self-review c216951e79 C4). 읽지 못하면 빈 목록.
    /// </summary>
    public static IReadOnlyList<(string Name, long Bytes, DateTimeOffset At)> List(string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || IsReparse(folder)) return [];
            return [.. new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => !f.Name.StartsWith('.') && Resolve(folder, f.Name) is not null)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxListed + 1)
                .Select(f => (f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// --add-dir로 열어도 되는 첨부 폴더인지: 있고, 재분석 지점이 아니고, .claude가 없다 —
    /// 추가 폴더의 .claude/skills를 CLI가 불러오므로 (0c3b7e3514 C2 실측). 이유(null이면 열어도 됨).
    /// </summary>
    public static string? WhyNotOpen(string folder)
    {
        if (!Directory.Exists(folder)) return "없음";
        if (IsReparse(folder)) return "첨부 폴더가 링크(재분석 지점)라 이번 실행에서는 첨부를 열지 않습니다.";
        if (Directory.Exists(Path.Combine(folder, ".claude")))
            return "첨부 폴더에 .claude 폴더가 있어 이번 실행에서는 첨부를 열지 않습니다 (CLI가 그 안의 스킬을 불러옴) — 그 폴더를 확인해 지우세요.";
        return null;
    }

    private static bool IsReparse(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; } // 못 보면 거절 쪽
    }

    /// <summary>
    /// 본문 끝에 이어진 첨부 줄만 떼어 낸다 (빈 줄은 건너뜀): (첨부 줄을 뺀 본문, 첨부 경로들 — 줄 차례).
    /// 본문 가운데(코드 블록 등)의 같은 모양 줄은 그대로 둔다 (5b26cff7df C9).
    /// </summary>
    public static (string Body, IReadOnlyList<string> Paths) Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var end = lines.Length;
        var paths = new List<string>();
        while (end > 0)
        {
            var line = lines[end - 1];
            if (line.Trim().Length == 0) { end--; continue; }
            if (!line.StartsWith(Marker, StringComparison.Ordinal) || line.Length <= Marker.Length || line[Marker.Length..].Trim().Length == 0) break;
            paths.Add(line[Marker.Length..].Trim());
            end--;
        }
        paths.Reverse();
        return (string.Join('\n', lines[..end]).TrimEnd(), paths);
    }

    /// <summary>목록 미리보기 · 제목용 글: 첨부 줄을 뺀 본문, 본문이 없으면 "첨부 n개" (5b26cff7df C7).</summary>
    public static string WithoutLines(string text)
    {
        var (body, paths) = Split(text);
        return paths.Count == 0 ? text : body.Length > 0 ? body : $"첨부 {paths.Count}개";
    }

    /// <summary>경로가 그 방 첨부 폴더 바로 아래 파일이면 파일 이름, 아니면 null (말풍선이 그 방 첨부만 칩 · 미리보기로 그린다).</summary>
    public static string? NameInFolder(string folder, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            return dir is not null && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();
}
