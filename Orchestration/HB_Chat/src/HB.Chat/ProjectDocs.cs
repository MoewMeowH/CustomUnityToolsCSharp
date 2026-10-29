using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>
/// 방 작업 폴더의 계획서 · 작업 로그 (곁칸 "문서" 탭). 작업 폴더와 그 아래 <see cref="MaxDepth"/>단계의 <c>docs/</c>에서
/// <c>plans/*.md</c> · <c>worklog/*.md</c> · 색인 <c>worklog.md</c>를 찾는다 (프로젝트가 여럿인 저장소는 프로젝트 폴더마다 docs).
/// 화면에 내주는 파일은 같은 모양의 경로만 — 작업 폴더 안 · 건너뛸 폴더 밖 · 경로 중간 링크 · junction 없음 · .md · <see cref="MaxBytes"/> 이하.
/// 방 기준 목록은 그 방 세션 기록의 도구 입력(<see cref="FileMention"/>)에 나온 계획서 · 로그다 — 색인은 거의 모든 작업에서 고치므로 넣지 않는다.
/// </summary>
public static class ProjectDocs
{
    public const int MaxDepth = 3;
    public const int MaxPlans = 10;
    public const int MaxLooseLogs = 5;
    /// <summary>프로젝트마다 방 기준 줄 상한 (오래 다룬 것부터 자른다).</summary>
    public const int MaxRoomRows = 20;
    public const long MaxBytes = 1024 * 1024;
    /// <summary>docs를 찾으며 들르는 폴더 상한 — 큰 저장소(유니티)에서 곁칸이 늦지 않게.</summary>
    public const int MaxVisited = 2000;

    /// <summary>찾지 않는 폴더 (빌드 산출물 · 패키지 · 유니티 캐시 · 리뷰 임시 폴더). 점으로 시작하는 폴더도 건너뛴다.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "Library", "Temp", "Logs", "Build", "Builds", "UserSettings", "review_tmp", "subagent_in",
    };

    /// <summary>문서 하나. Rel은 작업 폴더 기준 '/' 경로, Date는 파일 이름 앞 날짜(없으면 null), Title은 날짜 뒤 이름.</summary>
    public sealed record Doc(string Rel, string Title, string? Date, DateTime Modified);
    /// <summary>계획서 줄: 짝 로그(worklog/같은 이름.md)와 색인의 상태 칸.</summary>
    public sealed record PlanRow(Doc Plan, Doc? Log, string? Status);
    /// <summary>docs 폴더 하나 = 프로젝트 하나. Name은 docs의 부모 폴더 이름, LooseLogs는 짝 계획서가 없는 로그.</summary>
    public sealed record Project(string Name, string DocsRel, Doc? Index, IReadOnlyList<PlanRow> Plans, IReadOnlyList<Doc> LooseLogs);

    /// <summary>
    /// 방에서 다룬 문서 줄: 계획서 · 짝 로그(둘 중 하나는 있음) · 색인 상태, 이 방에서 마지막으로 다룬 시각과 방식
    /// (둘 중 하나만 다뤄도 짝을 함께 — 방식은 고침 → 읽음 → 셸 순으로 센 것).
    /// </summary>
    public sealed record RoomRow(Doc? Plan, Doc? Log, string? Status, DateTimeOffset? At, FileMentionKind Kind);
    public sealed record RoomProject(string Name, string DocsRel, IReadOnlyList<RoomRow> Rows);

    public static IReadOnlyList<Project> Find(string? workspace) => Find(workspace, null).Projects;

    /// <summary>작업 폴더 목록과 방 기준 목록을 한 번에 (docs 찾기는 한 번). mentions가 null이면 방 기준 목록은 비어 있다.</summary>
    public static (IReadOnlyList<Project> Projects, IReadOnlyList<RoomProject> Room) Find(string? workspace, IReadOnlyList<FileMention>? mentions)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return ([], []);
        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(Path.GetFullPath(workspace));
            if (!root.Exists) return ([], []);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return ([], []); }
        var touched = mentions is { Count: > 0 } ? new Touches(mentions) : null;
        var projects = new List<Project>();
        var room = new List<(int Line, RoomProject Project)>();
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited++ < MaxVisited)
        {
            var (dir, depth) = queue.Dequeue();
            foreach (var child in SafeDirs(dir))
            {
                if (child.Name.Equals("docs", StringComparison.OrdinalIgnoreCase))
                {
                    var (project, roomProject, roomLine) = Read(root.FullName, dir, child, touched);
                    if (project is not null) projects.Add(project);
                    if (roomProject is not null) room.Add((roomLine, roomProject));
                    continue; // docs 안은 더 찾지 않는다
                }
                if (depth < MaxDepth && !Skip(child.Name)) queue.Enqueue((child, depth + 1));
            }
        }
        // 프로젝트 사이도 이 방에서 마지막으로 다룬 차례로 — docs를 찾은 차례로 두면 방금 다룬 프로젝트가 아래로 간다 (abc6476218 C1)
        return (projects, [.. room.OrderByDescending(r => r.Line).Select(r => r.Project)]);
    }

    /// <summary>다룬 흔적 하나로 모은 것: 마지막 줄 · 시각과 다룬 방식들.</summary>
    private sealed class Touch
    {
        public int Line = -1;
        public DateTimeOffset? At;
        public bool Read, Edit, Shell;

        public void Add(FileMention m)
        {
            if (m.Line >= Line) { Line = m.Line; At = m.Timestamp ?? At; }
            switch (m.Kind)
            {
                case FileMentionKind.Read: Read = true; break;
                case FileMentionKind.Edit: Edit = true; break;
                default: Shell = true; break;
            }
        }
    }

    /// <summary>흔적 묶기: 파일 도구는 정규화한 전체 경로로, 셸은 파일 이름으로 (둘 다 대소문자 무시).</summary>
    private sealed record Touches(Dictionary<string, Touch> ByPath, Dictionary<string, Touch> ByName)
    {
        public Touches(IReadOnlyList<FileMention> mentions) : this(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var m in mentions)
            {
                string key;
                Dictionary<string, Touch> into;
                if (m.Path is { } path)
                {
                    // 상대 경로는 HB_Chat 프로세스의 현재 폴더 기준으로 풀려 다른 파일과 맞을 수 있어 버린다 (파일 도구는 절대 경로를 받음, abc6476218 J1)
                    if (!Path.IsPathFullyQualified(path)) continue;
                    try { key = Path.GetFullPath(path); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                    into = ByPath;
                }
                else { key = m.Name; into = ByName; }
                if (!into.TryGetValue(key, out var touch)) into[key] = touch = new Touch();
                touch.Add(m);
            }
        }

        /// <summary>그 파일의 흔적 (경로 · 이름 둘 다 맞으면 합침). 없으면 null.</summary>
        public Touch? Of(FileInfo file)
        {
            ByPath.TryGetValue(file.FullName, out var byPath);
            ByName.TryGetValue(file.Name, out var byName);
            if (byPath is null || byName is null) return byPath ?? byName;
            var both = new Touch();
            foreach (var t in new[] { byPath, byName })
            {
                if (t.Line >= both.Line) { both.Line = t.Line; both.At = t.At ?? both.At; }
                both.Read |= t.Read; both.Edit |= t.Edit; both.Shell |= t.Shell;
            }
            return both;
        }
    }

    /// <summary>계획서 · 짝 로그 중 다룬 것이 있는 줄만, 마지막으로 다룬 차례(줄 번호)가 늦은 것부터. LastLine은 그중 가장 늦은 줄 (없으면 -1).</summary>
    private static (int LastLine, List<RoomRow> Rows) RoomRows(string root, List<FileInfo> plans, List<FileInfo> logs, string? indexText, Touches touched)
    {
        var planByName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans) planByName.TryAdd(plan.Name, plan);
        var logByName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs) logByName.TryAdd(log.Name, log);
        var rows = new List<(int Line, RoomRow Row)>();
        foreach (var name in planByName.Keys.Union(logByName.Keys, StringComparer.OrdinalIgnoreCase))
        {
            planByName.TryGetValue(name, out var plan);
            logByName.TryGetValue(name, out var log);
            var hits = new[] { plan is null ? null : touched.Of(plan), log is null ? null : touched.Of(log) }.OfType<Touch>().ToList();
            if (hits.Count == 0) continue;
            var last = hits.MaxBy(t => t.Line)!;
            var kind = hits.Any(t => t.Edit) ? FileMentionKind.Edit : hits.Any(t => t.Read) ? FileMentionKind.Read : FileMentionKind.Shell;
            rows.Add((last.Line, new RoomRow(plan is null ? null : ToDoc(root, plan), log is null ? null : ToDoc(root, log),
                plan is null ? null : StatusOf(indexText, plan.Name), last.At, kind)));
        }
        var ordered = rows.OrderByDescending(r => r.Line).Take(MaxRoomRows).ToList();
        return (ordered.Count == 0 ? -1 : ordered[0].Line, [.. ordered.Select(r => r.Row)]);
    }

    /// <summary>
    /// 화면이 고른 문서의 실제 경로. rel은 작업 폴더 기준 '/' 경로이고 <c>…/docs/plans/*.md</c> · <c>…/docs/worklog/*.md</c> · <c>…/docs/worklog.md</c>만 —
    /// docs는 작업 폴더 아래 <see cref="MaxDepth"/>단계 안, 건너뛸 폴더 · 점 폴더 · 링크 · junction을 지나지 않는다. 아니면 null.
    /// </summary>
    public static string? Resolve(string? workspace, string? rel)
    {
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrEmpty(rel) || rel.Contains('\\') || rel.Contains(':') || rel.StartsWith('/')) return null;
        var parts = rel.Split('/');
        // 끝 점 · 공백은 Windows가 지워 다른 이름이 되고(bin. → bin), ~는 8.3 짧은 이름(REVIEW~1)이라 건너뛸 폴더 검사를 비켜 간다 (caea21ac40 C3)
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains('~'))) return null;
        var docs = Array.FindIndex(parts, p => p.Equals("docs", StringComparison.OrdinalIgnoreCase));
        if (docs < 0 || docs > MaxDepth) return null; // 첫 docs 기준 — Find가 들어가지 않는 docs 안의 docs는 모양 검사에서 떨어진다
        var tail = parts[(docs + 1)..];
        var shapeOk = tail switch
        {
            [var index] => index.Equals("worklog.md", StringComparison.OrdinalIgnoreCase),
            [var folder, var name] => (folder.Equals("plans", StringComparison.OrdinalIgnoreCase) || folder.Equals("worklog", StringComparison.OrdinalIgnoreCase))
                                      && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!shapeOk || parts[..docs].Any(Skip)) return null;
        string root, full;
        try
        {
            root = Path.GetFullPath(workspace);
            full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        // 정규화가 경로를 바꿨으면(위에서 못 거른 OS 규칙) 거절 — 검사한 이름과 여는 이름이 같아야 한다
        if (!string.Equals(Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/'), rel, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = new FileInfo(full);
            if (!file.Exists || file.LinkTarget is not null || file.Length > MaxBytes) return null;
            // 파일 바로 위부터 작업 폴더 아래까지 링크 · junction이 하나라도 있으면 거절 (작업 폴더 밖이 보이지 않게).
            // 작업 폴더 자신은 사용자가 고른 자리라 링크여도 받는다.
            for (var dir = file.Directory; dir is not null && !SamePath(dir.FullName, root); dir = dir.Parent)
                if (dir.LinkTarget is not null) return null;
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>색인 표에서 그 계획서 줄의 마지막 칸 (상태). 표 모양이 다르면 null.</summary>
    public static string? StatusOf(string? indexText, string planFileName)
    {
        if (string.IsNullOrEmpty(indexText)) return null;
        var key = "plans/" + planFileName;
        foreach (var raw in indexText.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|') || !line.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            return cells.Length >= 2 && cells[^1].Length > 0 ? cells[^1] : null;
        }
        return null;
    }

    private static (Project?, RoomProject?, int RoomLine) Read(string root, DirectoryInfo parent, DirectoryInfo docs, Touches? touched)
    {
        if (docs.LinkTarget is not null) return (null, null, -1);
        var plans = Files(docs, "plans");
        var logs = Files(docs, "worklog");
        var indexFile = new FileInfo(Path.Combine(docs.FullName, "worklog.md"));
        Doc? index = null;
        string? indexText = null;
        try
        {
            // 찾은 뒤 지워지거나 잠긴 색인 하나로 목록 전체가 실패하지 않게 (256b92a166 C7)
            if (indexFile.Exists && indexFile.LinkTarget is null && indexFile.Length <= MaxBytes)
            {
                indexText = File.ReadAllText(indexFile.FullName);
                index = ToDoc(root, indexFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (plans.Count == 0 && logs.Count == 0 && index is null) return (null, null, -1);
        // 대소문자를 가리는 폴더(WSL 등)에 a.md · A.md가 같이 있어도 예외 없이 첫 것
        var logByName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs) logByName.TryAdd(log.Name, log);
        var planNames = new HashSet<string>(plans.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var rows = plans.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxPlans)
            .Select(f => new PlanRow(ToDoc(root, f), logByName.TryGetValue(f.Name, out var log) ? ToDoc(root, log) : null, StatusOf(indexText, f.Name)))
            .ToList();
        var loose = logs.Where(f => !planNames.Contains(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxLooseLogs)
            .Select(f => ToDoc(root, f)).ToList();
        var docsRel = Rel(root, docs.FullName);
        var (roomLine, roomRows) = touched is null ? (-1, []) : RoomRows(root, plans, logs, indexText, touched);
        return (new Project(parent.Name, docsRel, index, rows, loose), roomRows.Count == 0 ? null : new RoomProject(parent.Name, docsRel, roomRows), roomLine);
    }

    private static List<FileInfo> Files(DirectoryInfo docs, string folder)
    {
        var dir = new DirectoryInfo(Path.Combine(docs.FullName, folder));
        try
        {
            if (!dir.Exists || dir.LinkTarget is not null) return [];
            return [.. dir.EnumerateFiles("*.md").Where(f => f.LinkTarget is null && f.Length <= MaxBytes)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<DirectoryInfo> SafeDirs(DirectoryInfo dir)
    {
        DirectoryInfo[] children;
        try { children = dir.GetDirectories(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
        {
            bool link;
            try { link = child.LinkTarget is not null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { link = true; }
            if (!link) yield return child;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool Skip(string name) => name.StartsWith('.') || SkipDirs.Contains(name);

    private static Doc ToDoc(string root, FileInfo f)
    {
        var stem = Path.GetFileNameWithoutExtension(f.Name);
        string? date = stem.Length > 11 && stem[10] == '_' && DateOnly.TryParseExact(stem[..10], "yyyy-MM-dd", out _) ? stem[..10] : null;
        var title = date is null ? stem : stem[11..];
        return new Doc(Rel(root, f.FullName), title.Replace('-', ' '), date, f.LastWriteTime);
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
}
