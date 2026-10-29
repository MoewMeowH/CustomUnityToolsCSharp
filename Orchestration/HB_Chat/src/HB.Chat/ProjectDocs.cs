namespace HB.Chat;

/// <summary>
/// 방 작업 폴더의 계획서 · 작업 로그 (곁칸 "문서" 탭). 작업 폴더와 그 아래 <see cref="MaxDepth"/>단계의 <c>docs/</c>에서
/// <c>plans/*.md</c> · <c>worklog/*.md</c> · 색인 <c>worklog.md</c>를 찾는다 (프로젝트가 여럿인 저장소는 프로젝트 폴더마다 docs).
/// 화면에 내주는 파일은 같은 모양의 경로만 — 작업 폴더 안 · 건너뛸 폴더 밖 · 경로 중간 링크 · junction 없음 · .md · <see cref="MaxBytes"/> 이하.
/// </summary>
public static class ProjectDocs
{
    public const int MaxDepth = 3;
    public const int MaxPlans = 10;
    public const int MaxLooseLogs = 5;
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

    public static IReadOnlyList<Project> Find(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return [];
        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(Path.GetFullPath(workspace));
            if (!root.Exists) return [];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { return []; }
        var projects = new List<Project>();
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
                    if (Read(root.FullName, dir, child) is { } project) projects.Add(project);
                    continue; // docs 안은 더 찾지 않는다
                }
                if (depth < MaxDepth && !Skip(child.Name)) queue.Enqueue((child, depth + 1));
            }
        }
        return projects;
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

    private static Project? Read(string root, DirectoryInfo parent, DirectoryInfo docs)
    {
        if (docs.LinkTarget is not null) return null;
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
        if (plans.Count == 0 && logs.Count == 0 && index is null) return null;
        // 대소문자를 가리는 폴더(WSL 등)에 a.md · A.md가 같이 있어도 예외 없이 첫 것
        var logByName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs) logByName.TryAdd(log.Name, log);
        var planNames = new HashSet<string>(plans.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var rows = plans.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxPlans)
            .Select(f => new PlanRow(ToDoc(root, f), logByName.TryGetValue(f.Name, out var log) ? ToDoc(root, log) : null, StatusOf(indexText, f.Name)))
            .ToList();
        var loose = logs.Where(f => !planNames.Contains(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxLooseLogs)
            .Select(f => ToDoc(root, f)).ToList();
        return new Project(parent.Name, Rel(root, docs.FullName), index, rows, loose);
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
