using System.IO.Compression;
using System.Text.RegularExpressions;
using HB.Chat;

namespace HB.Chat.Tests;

/// <summary>
/// 데이터 백업 zip (결정: 앱 데이터 + 세션 기록, 복원 방법 글): 기록은 담고 브라우저 캐시 · 로그 · 다시 만드는 파일은 빼고,
/// 이 앱 방의 세션 기록 · 서브에이전트 기록 · 할 일만 (다른 Claude 세션은 빼고), 읽지 못한 파일은 "빠진 파일.txt"로.
/// </summary>
public sealed class ChatBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Put(string rel, string text = "x")
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Zip_HasRecordsAndOwnSessions_SkipsCacheAndOthers()
    {
        var mine = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        foreach (var rel in new[] { "data/rooms.json", "data/approvals.jsonl", "data/groups/g.jsonl", "data/attachments/r1/pic.png", "data/profiles.json",
                     "data/mcp-servers.json", "data/webview2/cache.bin", "data/browser/prof.db", "data/logs/a.log", "data/selftest/ws/x",
                     "data/mcp-hb-orchestration-abc.json", "data/rooms.json.tmp", "data/attachments/r1/.abc.part", "data/toast-images/t.png",
                     $"claude/projects/F--ws/{mine}.jsonl", $"claude/projects/F--ws/{mine}/subagents/a.jsonl", $"claude/projects/F--ws/{other}.jsonl",
                     $"claude/tasks/{mine}/1.json", $"claude/tasks/{other}/1.json" })
            Put(rel);
        var options = new ChatOptions { DataDir = Path.Combine(_dir, "data"), ClaudeProjectsDir = Path.Combine(_dir, "claude", "projects") };
        var store = new RoomStore(Path.Combine(_dir, "rooms-index.json"));
        var room = store.Create("ws", PermissionLevel.Read, "opus", "백업") with { };
        store.Update(room.Id, r => r with { SessionId = mine });
        var plan = ChatBackup.Collect(options, store.All(), new ChatTranscripts(() => options.ProjectsRoot));
        Assert.Equal(1, plan.Sessions);

        using var ms = new MemoryStream();
        Assert.Equal(0, ChatBackup.Write(ms, plan, options, DateTimeOffset.Now));
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var want in new[] { "복원 방법.txt", "data/rooms.json", "data/approvals.jsonl", "data/groups/g.jsonl", "data/attachments/r1/pic.png",
                     "data/profiles.json", "data/mcp-servers.json", $"claude/projects/F--ws/{mine}.jsonl", $"claude/projects/F--ws/{mine}/subagents/a.jsonl",
                     $"claude/tasks/{mine}/1.json" })
            Assert.Contains(want, names);
        Assert.DoesNotContain(names, n => n.StartsWith("data/webview2/") || n.StartsWith("data/browser/") || n.StartsWith("data/logs/") || n.StartsWith("data/selftest/")
            || n.StartsWith("data/toast-images/"));
        Assert.DoesNotContain("data/mcp-hb-orchestration-abc.json", names);
        Assert.DoesNotContain("data/rooms.json.tmp", names);
        Assert.DoesNotContain("data/attachments/r1/.abc.part", names); // 올리는 중인 첨부는 어느 깊이든 빼고
        Assert.DoesNotContain(names, n => n.Contains(other, StringComparison.OrdinalIgnoreCase)); // 다른 Claude 세션은 빼고
        Assert.DoesNotContain("빠진 파일.txt", names);
        using var readme = new StreamReader(zip.GetEntry("복원 방법.txt")!.Open());
        var text = readme.ReadToEnd();
        Assert.Contains("복원 방법", text);
        Assert.Contains(Path.GetFullPath(options.DataFolder), text);
        Assert.Contains(Path.GetFullPath(options.ProjectsRoot), text);
        Assert.Contains("Codex 구성원의 스레드", text); // 빠지는 것도 밝힌다
        Assert.Equal(new FileInfo(Path.Combine(_dir, "data", "rooms.json")).LastWriteTime, zip.GetEntry("data/rooms.json")!.LastWriteTime.DateTime, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Zip_LockedFile_ListedAsMissing()
    {
        Put("data/rooms.json");
        Put("data/locked.json");
        var options = new ChatOptions { DataDir = Path.Combine(_dir, "data"), ClaudeProjectsDir = Path.Combine(_dir, "claude", "projects") };
        var plan = ChatBackup.Collect(options, [], new ChatTranscripts(() => options.ProjectsRoot));
        using var hold = new FileStream(Path.Combine(_dir, "data", "locked.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var ms = new MemoryStream();
        Assert.Equal(1, ChatBackup.Write(ms, plan, options, DateTimeOffset.Now));
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("data/rooms.json"));
        Assert.Null(zip.GetEntry("data/locked.json"));
        using var missing = new StreamReader(zip.GetEntry("빠진 파일.txt")!.Open());
        Assert.Contains("data/locked.json", missing.ReadToEnd());
    }

    [Fact]
    public void FileName_HasDateTime() =>
        Assert.Matches(new Regex(@"^HB_Chat-backup-\d{8}-\d{4}\.zip$"), ChatBackup.FileName(DateTimeOffset.Now));
}

/// <summary>설정 화면의 백업 단추: POST(antiforgery)로 zip을 내려받는다 · 화면에 담길 것 줄.</summary>
public sealed class ChatBackupPageTests(ChatApp app) : IClassFixture<ChatApp>
{
    [Fact]
    public async Task Settings_BackupDownload()
    {
        app.Room("백업 시험", ChatApp.User("안녕", 0));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = app.BaseAddress };
        var page = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/Settings"));
        Assert.Contains("id=\"backup\"", page);
        Assert.Contains("세션 기록 1개", page);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/Settings?handler=Backup")
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token)]),
        };
        req.Headers.Add("Origin", app.BaseAddress.GetLeftPart(UriPartial.Authority));
        using var res = await client.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"HB_Chat-backup-\d{8}-\d{4}\.zip", res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName ?? "");
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("복원 방법.txt"));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("claude/projects/", StringComparison.Ordinal) && e.FullName.EndsWith(".jsonl", StringComparison.Ordinal));
        Assert.Contains(zip.Entries, e => e.FullName == "data/rooms.json");
        zip.Dispose();
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetTempPath(), "hbchat-backup-*.zip"),
            f => System.IO.File.GetCreationTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1) && !IsOpen(f)); // 보낸 뒤 임시 zip이 지워진다
    }

    private static bool IsOpen(string path)
    {
        try { using var _ = System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
}
