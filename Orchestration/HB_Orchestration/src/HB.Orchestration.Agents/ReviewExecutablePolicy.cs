namespace HB.Orchestration.Agents;

/// <summary>자료 도구의 설치 실행 파일·자식 PATH에 같은 경로 정책을 적용한다.</summary>
internal static class ReviewExecutablePolicy
{
    internal static string SearchPath(string root, string? value) => string.Join(Path.PathSeparator,
        (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"')).Where(p => Allowed(p, root, directory: true)));

    internal static bool Allowed(string path, string root, bool directory = false)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.Contains('~') || path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // 드라이브 루트(F:\)는 trim해도 구분자로 끝난다 — 하나 더 붙이면 모든 경로가 "루트 밖"이 된다 (556a41088d C1)
            if (full.Equals(dir, comparison) || full.StartsWith(ReviewWorkFolder.RootPrefix(dir), comparison)) return false;
            if (directory ? !Directory.Exists(full) : !File.Exists(full)) return false;
            // 최종 파일뿐 아니라 PATH 디렉터리의 상위 junction/symlink도 거절한다.
            for (FileSystemInfo? item = directory ? new DirectoryInfo(full) : new FileInfo(full); item is not null;
                item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    internal static string? Git(string root, string searchPath) => searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "git.exe" : "git"))
        .FirstOrDefault(p => Allowed(p, root));
}
