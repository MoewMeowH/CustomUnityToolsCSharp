using System.Diagnostics;
using System.Text.Json;
using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

public sealed class ReviewEvidenceGitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-evidence-git-").FullName;
    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Test";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000)) { process.Kill(true); throw new TimeoutException(); }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().Trim();
    }
    private void Put(string path, string body)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }
    private void Commit() { Git("add", "-A"); Git("-c", "commit.gpgSign=false", "commit", "-qm", "fixture"); }
    private static JsonElement Data(string response) => JsonSerializer.Deserialize<JsonElement>(response).GetProperty("data");
    private static void Error(string response) => Assert.True(JsonSerializer.Deserialize<JsonElement>(response).TryGetProperty("error", out _));

    [Fact]
    public void History_pins_head_and_reads_korean_pages_without_worktree_content()
    {
        Git("init", "-q"); Put("a.cs", new string('가', 5999) + "😀\n과거"); Commit();
        var old = Git("rev-parse", "HEAD");
        Put("a.cs", "현재"); Commit();
        var service = new ReviewEvidence(_root, original: true);
        var history = Data(service.GitHistory("a.cs"));
        Assert.Equal(2, history.GetProperty("commits").GetArrayLength());
        var first = Data(service.GitRead("a.cs", old));
        var last = Data(service.GitRead("a.cs", old, first.GetProperty("nextOffset").GetInt32()));
        Assert.Equal(new string('가', 5999) + "😀\n과거", first.GetProperty("text").GetString() + last.GetProperty("text").GetString());
        Assert.Equal(first.GetProperty("sha256").GetString(), last.GetProperty("sha256").GetString());
        Put("a.cs", "새 커밋"); Commit();
        Assert.Equal(history.GetProperty("head").GetString(), Data(service.GitHistory("a.cs")).GetProperty("head").GetString());
        Error(service.GitRead("a.cs", Git("rev-parse", "HEAD")));
    }

    [Fact]
    public void Denies_excluded_files_revision_expressions_and_cross_path_commits()
    {
        Git("init", "-q"); Put("a.cs", "allowed"); Put("b.cs", "b");
        Put("docs/worklog/old.md", "commander"); Put("release.keystore", "dummy"); Put("private/config.json", "dummy"); Commit();
        var old = Git("rev-parse", "HEAD");
        Put("a.cs", "changed"); Commit(); var head = Git("rev-parse", "HEAD");
        var service = new ReviewEvidence(_root, ["private/**"], original: true);
        foreach (var path in new[] { "docs/worklog/old.md", "release.keystore", "private/config.json", "../a.cs", ".git/config", ":(glob)*" })
            Error(service.GitHistory(path));
        Error(service.GitRead("a.cs", old));
        service.GitHistory("a.cs");
        Error(service.GitRead("a.cs", "HEAD")); Error(service.GitRead("a.cs", old + ":a.cs"));
        service.GitHistory("b.cs"); Error(service.GitRead("b.cs", head));
        Error(new ReviewEvidence(_root).GitHistory("a.cs"));
        Directory.CreateDirectory(Path.Combine(_root, "child")); Put("child/a.cs", "nested");
        Error(new ReviewEvidence(Path.Combine(_root, "child"), original: true).GitHistory("a.cs"));
    }

    [Fact]
    public void History_does_not_follow_renames_even_with_local_follow_setting()
    {
        Git("init", "-q"); Put("old.cs", "unchanged"); Commit(); var old = Git("rev-parse", "HEAD");
        Git("mv", "old.cs", "new.cs"); Commit(); Git("config", "log.follow", "true");
        var service = new ReviewEvidence(_root, original: true);
        var commits = Data(service.GitHistory("new.cs")).GetProperty("commits").EnumerateArray().Select(c => c.GetString()).ToArray();
        Assert.Single(commits); Assert.DoesNotContain(old, commits);
    }

    [Fact]
    public void Rejects_removed_from_head_but_retained_on_disk()
    {
        Git("init", "-q"); Put("config.json", "synthetic"); Commit();
        Git("rm", "--cached", "config.json"); Git("-c", "commit.gpgSign=false", "commit", "-qm", "remove");
        var service = new ReviewEvidence(_root, original: true);
        Assert.Equal("synthetic", Data(service.Read("config.json")).GetProperty("text").GetString());
        Error(service.GitHistory("config.json"));
    }

    [Fact]
    public void Truncated_history_does_not_authorize_older_or_unreturned_commits()
    {
        Git("init", "-q");
        var commits = new List<string>();
        for (var i = 0; i < 21; i++) { Put("dir/a.cs", "body" + i); Commit(); commits.Add(Git("rev-parse", "HEAD")); }
        var service = new ReviewEvidence(_root, original: true);
        var history = Data(service.GitHistory("dir/a.cs"));
        Assert.True(history.GetProperty("truncated").GetBoolean());
        Assert.Equal(20, history.GetProperty("commits").GetArrayLength());
        Error(service.GitRead("dir/a.cs", commits[0]));
        Assert.Equal("body20", Data(service.GitRead("dir/a.cs", commits[^1])).GetProperty("text").GetString());
        Error(service.GitRead("dir/a.cs", commits[^1], offset: 999));
        Error(service.GitRead("dir/a.cs", commits[^1], maxChars: 1));
        Error(service.GitRead("dir/a.cs", commits[^1], maxChars: ReviewEvidence.PageChars + 1));
        var limited = new ReviewEvidence(_root, original: true);
        // 응답 직전 예산 부족을 주입해 권한 발급의 성공 확정 시점을 검증한다.
        typeof(ReviewEvidence).GetField("_returned", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(limited, ReviewEvidence.MaxReturnedChars - 500);
        Error(limited.GitHistory("dir/a.cs"));
        Error(limited.GitRead("dir/a.cs", commits[^1], maxChars: 2));
        typeof(ReviewEvidence).GetField("_returned", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, ReviewEvidence.MaxReturnedChars - 500);
        Assert.Equal("bo", Data(service.GitRead("dir/a.cs", commits[^1], maxChars: 2)).GetProperty("text").GetString());
    }

    [Fact]
    public void Utf8_bom_is_not_part_of_text_and_missing_repository_is_classified()
    {
        Git("init", "-q"); File.WriteAllText(Path.Combine(_root, "a.cs"), "한글", new System.Text.UTF8Encoding(true)); Commit();
        var commit = Git("rev-parse", "HEAD");
        var service = new ReviewEvidence(_root, original: true); service.GitHistory("a.cs");
        Assert.Equal("한글", Data(service.GitRead("a.cs", commit)).GetProperty("text").GetString());
        using var empty = new EmptyRepositoryFixture();
        var error = JsonSerializer.Deserialize<JsonElement>(new ReviewEvidence(empty.Root, original: true).GitHistory("a.cs"));
        Assert.Equal("git_not_repository", error.GetProperty("code").GetString());
    }

    private sealed class EmptyRepositoryFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("hb-no-git-").FullName;
        public EmptyRepositoryFixture() => File.WriteAllText(Path.Combine(Root, "a.cs"), "code");
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public void Rejects_historical_symlink_and_binary_blobs()
    {
        Git("init", "-q"); Put("a.cs", "outside.cs"); Put("b.cs", "binary\0");
        Git("add", "a.cs", "b.cs");
        var blob = Git("hash-object", "a.cs"); Git("update-index", "--cacheinfo", $"120000,{blob},a.cs");
        Git("-c", "commit.gpgSign=false", "commit", "-qm", "fixture"); var old = Git("rev-parse", "HEAD");
        var service = new ReviewEvidence(_root, original: true);
        service.GitHistory("a.cs"); service.GitHistory("b.cs");
        Error(service.GitRead("a.cs", old)); Error(service.GitRead("b.cs", old));
    }
}
