using System.Text.Json;
using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

public sealed class ReviewEvidenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-evidence-test-").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private void Put(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
    private static JsonElement Result(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [Fact]
    public void Read_pages_preserve_long_lines_korean_and_surrogate_pairs()
    {
        var body = new string('가', ReviewEvidence.PageChars - 1) + "😀\n끝";
        Put("a.cs", body);
        var service = new ReviewEvidence(_root);
        var first = Result(service.Read("a.cs")).GetProperty("data");
        var second = Result(service.Read("a.cs", first.GetProperty("nextOffset").GetInt32())).GetProperty("data");
        Assert.Equal(body, first.GetProperty("text").GetString() + second.GetProperty("text").GetString());
        Assert.Equal(first.GetProperty("sha256").GetString(), second.GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(".env")]
    [InlineData("a.cs:other")]
    [InlineData("AGENTS.md")]
    public void Denies_paths_outside_inventory_or_sensitive_names(string path)
    {
        Put("a.cs", "code");
        Put(".env", "not-a-secret");
        Put("AGENTS.md", "instruction");
        var service = new ReviewEvidence(_root);
        Assert.True(Result(service.Read(path)).TryGetProperty("error", out _));
        var files = service.List();
        Assert.DoesNotContain(".env", files);
        Assert.DoesNotContain("AGENTS.md", files);
    }

    [Fact]
    public void Search_cursor_continues_without_losing_matches()
    {
        Put("a.cs", string.Join('\n', Enumerable.Repeat("한글 needle", 25)));
        var service = new ReviewEvidence(_root);
        var matches = new List<int>();
        int fileIndex = 0, offset = 0;
        do
        {
            var data = Result(service.Search("needle", fileIndex, offset)).GetProperty("data");
            matches.AddRange(data.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("offset").GetInt32()));
            if (data.GetProperty("nextFileIndex").ValueKind == JsonValueKind.Null) break;
            fileIndex = data.GetProperty("nextFileIndex").GetInt32();
            offset = data.GetProperty("nextOffset").GetInt32();
        } while (true);
        Assert.Equal(25, matches.Count);
        Assert.Equal(25, matches.Distinct().Count());
    }

    [Fact]
    public void Invalid_requests_also_consume_call_budget()
    {
        Put("a.cs", "code");
        var service = new ReviewEvidence(_root);
        for (var i = 0; i < ReviewEvidence.MaxCalls; i++) service.Read("missing");
        Assert.True(Result(service.Read("a.cs")).GetProperty("exhausted").GetBoolean());
    }

    [Fact]
    public void Rejects_binary_and_files_added_after_inventory()
    {
        Put("binary.cs", "a\0b");
        var service = new ReviewEvidence(_root);
        Put("later.cs", "new");
        Assert.True(Result(service.Read("binary.cs")).TryGetProperty("error", out _));
        Assert.True(Result(service.Read("later.cs")).TryGetProperty("error", out _));
    }

    [Fact]
    public void Search_preserves_hits_and_resumes_after_unreadable_files()
    {
        Put("a.cs", "needle before");
        Put("b.cs", "binary\0");
        File.WriteAllBytes(Path.Combine(_root, "c.cs"), [0x80]);
        Put("d.cs", new string('x', 2_000_001));
        Put("z.cs", "needle after");
        var service = new ReviewEvidence(_root);
        int fileIndex = 0, offset = 0;
        var hits = new List<string>();
        var skipped = new List<string>();
        var calls = 0;
        do
        {
            var data = Result(service.Search("needle", fileIndex, offset)).GetProperty("data");
            calls++;
            hits.AddRange(data.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("path").GetString()!));
            var failures = data.GetProperty("skipped").EnumerateArray().ToArray();
            Assert.Equal(failures.Length > 0, data.GetProperty("incomplete").GetBoolean());
            skipped.AddRange(failures.Select(h => h.GetProperty("path").GetString()!));
            if (data.GetProperty("nextFileIndex").ValueKind == JsonValueKind.Null) break;
            fileIndex = data.GetProperty("nextFileIndex").GetInt32();
            offset = data.GetProperty("nextOffset").GetInt32();
        } while (true);
        Assert.Equal(new[] { "a.cs", "z.cs" }, hits);
        Assert.Equal(new[] { "b.cs", "c.cs", "d.cs" }, skipped);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Search_batches_failures_and_reuses_format_failure_cache()
    {
        for (var i = 0; i < 23; i++) Put($"bad{i:D2}.cs", "\0");
        Put("z.cs", "needle");
        var service = new ReviewEvidence(_root);
        var first = Result(service.Search("needle")).GetProperty("data");
        Assert.Equal(20, first.GetProperty("skipped").GetArrayLength());
        Assert.True(first.GetProperty("incomplete").GetBoolean());
        var second = Result(service.Search("needle", first.GetProperty("nextFileIndex").GetInt32(), first.GetProperty("nextOffset").GetInt32())).GetProperty("data");
        Assert.Single(second.GetProperty("hits").EnumerateArray());
        Assert.Equal(3, second.GetProperty("skipped").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextFileIndex").ValueKind);
        var repeat = Result(service.Search("other")).GetProperty("data");
        Assert.Equal(20, repeat.GetProperty("cacheHits").GetInt32());
        Assert.Equal(0, repeat.GetProperty("scannedBytes").GetInt64());
        var forced = Result(service.Search("other", retrySkipped: true)).GetProperty("data");
        Assert.Equal(0, forced.GetProperty("cacheHits").GetInt32());
        Assert.Equal(20, forced.GetProperty("scannedBytes").GetInt64());
    }

    [Fact]
    public void Failed_search_file_is_rechecked_when_modified_or_forced()
    {
        Put("a.cs", "\0abc");
        var service = new ReviewEvidence(_root);
        service.Search("hit");
        var full = Path.Combine(_root, "a.cs");
        var stamp = File.GetLastWriteTimeUtc(full);
        Put("a.cs", "hit!");
        File.SetLastWriteTimeUtc(full, stamp.AddSeconds(2));
        var changed = Result(service.Search("hit")).GetProperty("data");
        Assert.Single(changed.GetProperty("hits").EnumerateArray());
        Put("a.cs", "\0abc");
        File.SetLastWriteTimeUtc(full, stamp);
        service.Search("hit");
        Put("a.cs", "hit!");
        File.SetLastWriteTimeUtc(full, stamp);
        Assert.Equal(1, Result(service.Search("hit")).GetProperty("data").GetProperty("cacheHits").GetInt32());
        Assert.Single(Result(service.Search("hit", retrySkipped: true)).GetProperty("data").GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public void Search_scope_pagination_does_not_include_neighbor_folders_or_other_extensions()
    {
        Put("Other/a.cs", "needle");
        Put("SourceExtra/a.cs", "needle");
        Put("Source/a.json", "needle");
        Put("Source/a.cs", string.Join('\n', Enumerable.Repeat("needle", 30)));
        var service = new ReviewEvidence(_root);
        int fileIndex = 0, offset = 0, count = 0;
        do
        {
            var data = Result(service.Search("needle", fileIndex, offset, "Source", "cs")).GetProperty("data");
            foreach (var hit in data.GetProperty("hits").EnumerateArray())
            {
                Assert.Equal("Source/a.cs", hit.GetProperty("path").GetString()); count++;
            }
            if (data.GetProperty("nextFileIndex").ValueKind == JsonValueKind.Null) break;
            fileIndex = data.GetProperty("nextFileIndex").GetInt32(); offset = data.GetProperty("nextOffset").GetInt32();
        } while (true);
        Assert.Equal(30, count);
        Assert.True(Result(service.Search("needle", pathPrefix: "../Source")).TryGetProperty("error", out _));
    }

    [Fact]
    public void Search_does_not_cache_transient_sharing_failures()
    {
        Put("a.cs", "needle");
        var service = new ReviewEvidence(_root);
        using (var locked = new FileStream(Path.Combine(_root, "a.cs"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Single(Result(service.Search("needle")).GetProperty("data").GetProperty("skipped").EnumerateArray());
        var next = Result(service.Search("needle")).GetProperty("data");
        Assert.Equal(0, next.GetProperty("cacheHits").GetInt32());
        Assert.Single(next.GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public void Search_bounds_empty_file_work_without_losing_later_matches()
    {
        for (var i = 0; i < 257; i++) Put($"a{i:D3}.cs", "");
        Put("z.cs", "needle");
        var service = new ReviewEvidence(_root);
        var first = Result(service.Search("needle")).GetProperty("data");
        Assert.Equal(256, first.GetProperty("filesExamined").GetInt32());
        var second = Result(service.Search("needle", first.GetProperty("nextFileIndex").GetInt32(), first.GetProperty("nextOffset").GetInt32())).GetProperty("data");
        Assert.Single(second.GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public void Remaining_budget_matches_data_and_oversized_response_keeps_small_reads_available()
    {
        Put("a.cs", new string('가', ReviewEvidence.PageChars));
        var service = new ReviewEvidence(_root);
        var spent = 0;
        JsonElement reply;
        while (true)
        {
            reply = Result(service.Read("a.cs"));
            if (!reply.TryGetProperty("data", out var data)) break;
            spent += data.GetRawText().Length;
            Assert.Equal(ReviewEvidence.MaxReturnedChars - spent, reply.GetProperty("remainingChars").GetInt32());
        }
        Assert.False(reply.GetProperty("exhausted").GetBoolean());
        Assert.Equal(ReviewEvidence.MaxReturnedChars - spent, reply.GetProperty("remainingChars").GetInt32());
        Assert.True(Result(service.Read("a.cs", maxChars: 2)).TryGetProperty("data", out _));
    }

    [Fact]
    public void Path_case_follows_platform_without_leaving_inventory()
    {
        Put("Upper.cs", "hello");
        var reply = Result(new ReviewEvidence(_root).Read("upper.cs"));
        Assert.Equal(OperatingSystem.IsWindows(), reply.TryGetProperty("data", out _));
    }

    [Fact]
    public void Inventory_accepts_tracked_limit_plus_context_file()
    {
        for (var i = 0; i < ReviewWorkFolder.MaxTrackedFiles; i++) Put($"f{i}.cs", "");
        Put("extra.diff", "diff");
        var service = new ReviewEvidence(_root);
        Assert.Equal(ReviewWorkFolder.MaxTrackedFiles + 1, Result(service.List()).GetProperty("data").GetProperty("total").GetInt32());
        Assert.True(Result(service.Read("extra.diff")).TryGetProperty("data", out _));
    }

    [Fact]
    public void Original_root_reads_untracked_source_but_filters_all_tools()
    {
        Put("Source/Untracked.cs", "needle source");
        Put("config/company.json", "needle private");
        Put("signing/release.keystore", "needle private");
        Put("Library/Cache.cs", "needle generated");
        Put(".git/config", "needle git");
        Put(".mcp.json", "needle private");
        Put("AGENTS.md", "needle instruction");
        var service = new ReviewEvidence(_root, ["config/**"], original: true);
        var listing = Result(service.List());
        Assert.Equal("original", listing.GetProperty("source").GetString());
        var files = listing.GetProperty("data").GetProperty("files").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "Source/Untracked.cs" }, files);
        foreach (var path in new[] { "config/company.json", "signing/release.keystore", ".git/config", ".mcp.json", "AGENTS.md", "Library/Cache.cs", "../outside.cs" })
            Assert.True(Result(service.Read(path)).TryGetProperty("error", out _));
        var hits = Result(service.Search("needle")).GetProperty("data").GetProperty("hits").EnumerateArray().ToArray();
        Assert.Single(hits);
        Assert.Equal("Source/Untracked.cs", hits[0].GetProperty("path").GetString());
        Put("Source/Untracked.cs", "changed source");
        Assert.Equal("changed source", Result(service.Read("Source/Untracked.cs")).GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public void Original_read_reports_hash_changes_after_file_is_updated()
    {
        Put("file.cs", "original");
        var service = new ReviewEvidence(_root, original: true);
        var first = Result(service.Read("file.cs")).GetProperty("data").GetProperty("sha256").GetString();
        Put("file.cs", "updated");
        Assert.NotEqual(first, Result(service.Read("file.cs")).GetProperty("data").GetProperty("sha256").GetString());
    }
}
