using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace HB.Chat;

/// <summary>
/// 데이터 백업 zip (사용자 결정: 설정 단추로 내려받기만 · 앱 데이터 + 세션 기록 · 복원은 zip 안 글대로 수동 · 암호 없음).
/// data/ = 데이터 폴더(브라우저 캐시 · 로그 · 실행 때 다시 만드는 파일 빼고), claude/projects/ · claude/tasks/ = 이 앱 방들(휴지통 · 단톡방 구성원 포함)의
/// 세션 기록 · 서브에이전트 기록 · 할 일만. 다른 Claude 세션은 넣지 않는다.
/// </summary>
public static class ChatBackup
{
    /// <summary>
    /// 데이터 폴더 맨 위에서 빼는 폴더 — 브라우저 캐시 · 지휘자 브라우저 프로필 · 화면 사진 · 로그 · 자가 시험 폴더 · 데스크톱 알림 그림 (다시 만들어진다).
    /// 들어가지도 않는다 (webview2 · browser는 캐시 파일이 수만 개).
    /// </summary>
    public static readonly IReadOnlyList<string> SkipFolders = ["webview2", "browser", "browser-shots", "logs", "selftest", "toast-images"];

    /// <summary>실행 때 다시 만드는 MCP 설정 (맨 위만).</summary>
    private static bool SkipTopFile(string name) =>
        name.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name != "mcp-servers.json";

    /// <summary>쓰다 만 임시 파일 (모든 깊이) — 저장 중 .tmp · 첨부 올리는 중 .part.</summary>
    private static bool SkipPartial(string name) =>
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);

    private static readonly EnumerationOptions Recurse = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions TopOnly = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>담을 파일 (원본 경로 → zip 안 경로) · 합친 크기 · 세션 기록 수.</summary>
    public sealed record Plan(IReadOnlyList<(string Source, string Entry)> Files, long Bytes, int Sessions);

    public static Plan Collect(ChatOptions o, IEnumerable<Room> rooms, ChatTranscripts transcripts)
    {
        var files = new List<(string, string)>();
        var data = Path.GetFullPath(o.DataFolder);
        if (Directory.Exists(data))
        {
            foreach (var file in Directory.EnumerateFiles(data, "*", TopOnly))
            {
                var name = Path.GetFileName(file);
                if (!SkipTopFile(name) && !SkipPartial(name)) files.Add((file, "data/" + name));
            }
            foreach (var dir in Directory.EnumerateDirectories(data, "*", TopOnly))
            {
                var name = Path.GetFileName(dir);
                if (!SkipFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) AddFolder(files, dir, "data/" + name);
            }
        }
        var sessions = 0;
        foreach (var sid in rooms.Select(r => r.SessionId).Where(s => Guid.TryParse(s, out _)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Find는 세션 기록 폴더 바로 아래 <폴더>/<세션>.jsonl만 찾는다
            if (transcripts.Find(sid) is { } path)
            {
                sessions++;
                var entry = "claude/projects/" + Path.GetFileName(Path.GetDirectoryName(path)!) + "/" + sid;
                files.Add((path, entry + ".jsonl"));
                // 같은 이름 폴더 = 서브에이전트 기록 등
                var side = Path.Combine(Path.GetDirectoryName(path)!, sid);
                if (Directory.Exists(side)) AddFolder(files, side, entry);
            }
            if (o.TasksRoot is { } tasks && Path.Combine(tasks, sid) is var taskDir && Directory.Exists(taskDir))
                AddFolder(files, taskDir, "claude/tasks/" + sid);
        }
        long bytes = 0;
        foreach (var (source, _) in files)
        {
            try { bytes += new FileInfo(source).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new Plan(files, bytes, sessions);
    }

    private static void AddFolder(List<(string, string)> files, string folder, string entryRoot)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", Recurse))
            if (!SkipPartial(Path.GetFileName(file)))
                files.Add((file, entryRoot + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/')));
    }

    /// <summary>zip 항목 시각이 받는 범위 (1980~2107년). 밖이면 이 값.</summary>
    private static readonly DateTime ZipEpoch = new(1980, 1, 1);

    /// <summary>
    /// zip을 <paramref name="output"/>에 쓴다. 쓰는 중인 파일도 ReadWrite 공유로 열어 메모리로 다 읽고 바로 닫는다 — 압축하는 동안 원본을 잡고 있으면
    /// 앱의 저장(임시 파일 → 덮어쓰기 이동)이 거부되고, 복사 중 실패하면 잘린 항목이 남기 때문. 읽지 못한 파일은 건너뛰어 "빠진 파일.txt"에 적는다.
    /// 맨 앞에 "복원 방법.txt". 건너뛴 수를 돌려준다.
    /// </summary>
    public static int Write(Stream output, Plan plan, ChatOptions o, DateTimeOffset now)
    {
        var missing = new List<string>();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        AddText(zip, "복원 방법.txt", Readme(o, plan, now));
        var buffer = new MemoryStream();
        foreach (var (source, entry) in plan.Files)
        {
            DateTime time;
            buffer.SetLength(0);
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // 경로로 다시 물으면 그사이 지워진 · 이름이 바뀐 파일은 1601년이 나온다 — 열어 둔 핸들에서 읽는다
                time = File.GetLastWriteTime(input.SafeFileHandle);
                input.CopyTo(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missing.Add($"{entry} — {ex.Message}");
                continue;
            }
            var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
            e.LastWriteTime = time.Year is >= 1980 and <= 2107 ? time : ZipEpoch;
            using var to = e.Open();
            to.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        if (missing.Count > 0) AddText(zip, "빠진 파일.txt", "읽지 못해 넣지 못한 파일:\n" + string.Join("\n", missing) + "\n");
        return missing.Count;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(true));
        w.Write(text.Replace("\n", "\r\n"));
    }

    public static string FileName(DateTimeOffset now) => $"HB_Chat-backup-{now:yyyyMMdd-HHmm}.zip";

    private static string Readme(ChatOptions o, Plan plan, DateTimeOffset now)
    {
        var version = typeof(ChatBackup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        return $"""
            HB_Chat 데이터 백업
            만든 시각: {now:yyyy-MM-dd HH:mm:ss zzz} · 앱 판: {version}
            파일 {plan.Files.Count}개 · 세션 기록 {plan.Sessions}개

            원래 자리
            - data/            → {Path.GetFullPath(o.DataFolder)}
            - claude/projects/ → {Path.GetFullPath(o.ProjectsRoot)}
            - claude/tasks/    → {o.TasksRoot ?? "(없음)"}

            복원 방법 (수동)
            1. HB_Chat(웹 서버 · 데스크톱 앱)을 모두 끈다.
            2. data/ 안의 것을 데이터 폴더(기본 %LOCALAPPDATA%\HB_Chat)에 복사한다. 같은 이름 파일은 덮어쓴다 — 지금 것을 먼저 따로 두는 것을 권한다.
            3. claude/projects/ 안의 폴더를 세션 기록 폴더(위 "원래 자리", 다른 PC면 그 PC의 ~/.claude/projects)에, claude/tasks/ 안의 폴더를 할 일 폴더(~/.claude/tasks)에 복사한다.
            4. 앱을 켜고 설정 › 작업 폴더 · Unity 프로젝트 경로가 이 PC에 맞는지 본다.
               작업 폴더는 원래와 같은 경로로 두기를 권한다 — 세션 기록 폴더 이름이 작업 폴더 경로에서 나와, 경로가 다르면 대화는 보여도 claude가 이어 가지 못할 수 있다.
            5. 방을 열어 대화가 보이는지 확인한다.

            빠진 것: 브라우저 캐시(webview2) · 지휘자 브라우저 프로필 · 로그 · 실행 때 다시 만드는 MCP 설정 파일 ·
            단톡방 Codex 구성원의 스레드(~/.codex/sessions — 복원하면 단톡방 대화는 보이지만 Codex 구성원은 맥락 없이 새로 시작한다).
            받을 때 실행 중이던 방은 그 순간까지의 기록만 담긴다.
            이 zip에는 대화 · 첨부 · MCP 서버 설정(토큰이 있을 수 있음)이 들어 있다 — 다른 사람에게 보내지 않는다.

            """;
    }
}
