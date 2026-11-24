using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using HB.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>곁칸 변경 탭: 쓰기 도구 경로 · 방별 편집 기록 · git 읽기 · 같은 저장소 다른 방 · 말 끝 알림 줄.</summary>
public sealed class ChangesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-changes-" + Guid.NewGuid().ToString("N"));

    public ChangesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestGit.Delete(_dir);

    // ---- 쓰기 도구 경로 ----

    [Fact]
    public void Parser_WriteToolPath_OnlyForWriteTools_IncludingSubagent()
    {
        var main = Assert.IsType<UsageEvent>(StreamParser.Parse("""
            {"type":"assistant","message":{"content":[
              {"type":"tool_use","id":"a","name":"Edit","input":{"file_path":"C:\\w\\a.cs","old_string":"x","new_string":"y"}},
              {"type":"tool_use","id":"b","name":"NotebookEdit","input":{"notebook_path":"C:\\w\\n.ipynb"}},
              {"type":"tool_use","id":"c","name":"Bash","input":{"command":"sed -i x C:\\w\\b.cs"}},
              {"type":"tool_use","id":"d","name":"Read","input":{"file_path":"C:\\w\\r.cs"}}],"usage":{"input_tokens":1}}}
            """.Replace("\n", "")));
        Assert.Equal([@"C:\w\a.cs", @"C:\w\n.ipynb", null, null], main.Tools.Select(t => t.Path));
        var sub = Assert.IsType<SubagentToolsEvent>(StreamParser.Parse(
            """{"type":"assistant","parent_tool_use_id":"p","message":{"content":[{"type":"tool_use","id":"e","name":"Write","input":{"file_path":"C:\\w\\s.md","content":"x"}}]}}"""));
        Assert.Equal(@"C:\w\s.md", sub.Tools[0].Path);
    }

    // ---- 방별 편집 기록 ----

    private const string RoomA = "aaaaaaaaaaaa", RoomB = "bbbbbbbbbbbb";

    [Fact]
    public void RoomEdits_AddForByPath_RewriteWindow_Forget()
    {
        Assert.True(RoomStore.IsRoomId(RoomA));
        var edits = new RoomEdits(() => _dir);
        var t = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(9));
        edits.Add(RoomA, @"C:\W\a.cs", "Edit", t);
        edits.Add(RoomA, "relative.cs", "Edit", t); // 전체 경로가 아니면 버림
        edits.Add("not-a-room", @"C:\W\a.cs", "Edit", t);
        edits.Add(RoomA, @"c:\w\A.CS", "Write", t.AddSeconds(30)); // 1분 안 같은 파일 (대소문자 무시) — 다시 안 씀
        Assert.Equal([("Edit", t)], edits.For(RoomA).Select(e => (e.Tool, e.At)));
        edits.Add(RoomA, @"C:\W\a.cs", "Write", t.AddMinutes(2));
        edits.Add(RoomB, @"C:\W\a.cs", "Edit", t.AddMinutes(3));
        edits.Add(RoomB, @"C:\W\b.cs", "Edit", t.AddMinutes(3));

        var reread = new RoomEdits(() => _dir); // 파일에서 다시
        Assert.Equal("Write", reread.For(RoomA).Single().Tool);
        var byPath = reread.ByPath([RoomA, RoomB]);
        Assert.Equal([RoomB, RoomA], byPath[@"c:\w\a.cs"].Select(h => h.RoomId)); // 새것부터
        Assert.Equal([RoomB], byPath[@"C:\W\b.cs"].Select(h => h.RoomId));

        reread.Forget(RoomB);
        Assert.Empty(reread.For(RoomB));
        Assert.False(File.Exists(Path.Combine(_dir, "room-edits", RoomB + ".json")));
    }

    [Fact]
    public void RoomEdits_KeepsLatest()
    {
        var edits = new RoomEdits(() => _dir);
        var t = DateTimeOffset.Now;
        for (var i = 0; i < RoomEdits.Keep + 5; i++) edits.Add(RoomA, $@"C:\W\f{i}.cs", "Edit", t.AddSeconds(i));
        var kept = new RoomEdits(() => _dir).For(RoomA);
        Assert.Equal(RoomEdits.Keep, kept.Count);
        Assert.DoesNotContain(kept, e => e.Path.EndsWith(@"\f0.cs", StringComparison.Ordinal));
    }

    // ---- git 읽기 ----

    [Fact]
    public async Task Git_StatusDiff_UntrackedRenameKorean_HeadTime_LargeDiffCut()
    {
        var repo = TestGit.Init(Path.Combine(_dir, "repo"));
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(repo, "old.txt"), "keep\n");
        TestGit.Run(repo, "add", ".");
        TestGit.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\ntwo\n");
        TestGit.Run(repo, "mv", "old.txt", "new.txt");
        File.WriteAllText(Path.Combine(repo, "한글.md"), "새 파일\n");
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        File.WriteAllText(Path.Combine(repo, "sub", "s.txt"), "x");

        var git = new GitChanges();
        var status = await git.StatusAsync(Path.Combine(repo, "sub")); // 하위 폴더에서도 저장소 전체
        Assert.Null(status.Error);
        Assert.Equal(Path.GetFullPath(repo), status.Root, ignoreCase: true);
        Assert.True(status.HeadAt is { } head && DateTimeOffset.Now - head < TimeSpan.FromMinutes(5));
        var byPath = status.Entries.ToDictionary(e => e.Path);
        Assert.Equal(('M', false), (byPath["a.txt"].Shown, byPath["a.txt"].Staged));
        Assert.Equal(('R', true, "old.txt"), (byPath["new.txt"].Shown, byPath["new.txt"].Staged, byPath["new.txt"].OldPath));
        Assert.True(byPath["한글.md"].Untracked);
        Assert.True(byPath["sub/s.txt"].Untracked);

        var (diff, note) = await git.DiffAsync(status.Root!, byPath["a.txt"]);
        Assert.Null(note);
        Assert.Contains("\n+two", diff);
        var (fresh, freshNote) = await git.DiffAsync(status.Root!, byPath["한글.md"]);
        Assert.Equal("+새 파일\n+", fresh);
        Assert.Contains("새 파일", freshNote);

        // 큰 diff는 상한까지만 읽고 끊는다 (/self-review sub C7)
        File.WriteAllText(Path.Combine(repo, "a.txt"), string.Concat(Enumerable.Repeat("0123456789abcdef\n", GitChanges.MaxDiffChars / 8)));
        var (big, bigNote) = await git.DiffAsync(status.Root!, byPath["a.txt"]);
        Assert.Equal((GitChanges.MaxDiffChars, "앞부분만 보입니다 (차이가 큽니다)."), (big!.Length, bigNote));

        Assert.Equal("git 저장소가 아니거나 git이 답하지 않았습니다.", (await git.StatusAsync(Directory.CreateDirectory(Path.Combine(_dir, "plain")).FullName)).Error);
        Assert.Equal("git을 찾지 못했습니다.", (await new GitChanges(Path.Combine(_dir, "no-git.exe")).StatusAsync(repo)).Error);
    }

    // ---- 출처 판정 (/self-review sub C1) ----

    [Fact]
    public void Origin_LatestRecordAfterHead_Wins()
    {
        var root = @"C:\Repo";
        var head = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var staged = new GitEntry('M', ' ', "core.cs");
        var names = new Dictionary<string, string> { [RoomB] = "옆 방" };
        IReadOnlyDictionary<string, IReadOnlyList<(string, RoomEdit)>> By(params (string Room, DateTimeOffset At)[] hits) =>
            new Dictionary<string, IReadOnlyList<(string, RoomEdit)>>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Repo\core.cs"] = [.. hits.OrderByDescending(h => h.At).Select(h => (h.Room, new RoomEdit(@"C:\Repo\core.cs", h.At, "Edit")))],
            };
        Pages.ChangeOrigin O(DateTimeOffset? headAt, params (string, DateTimeOffset)[] hits) => Pages.RoomModel.Origin(RoomA, root, headAt, staged, By(hits), names).Origin;

        // 이 방이 지난 커밋 전에 고친 파일을 오늘 다른 세션이 Bash로 고쳐 올림 → 이 방 아님 (계기 사고)
        Assert.Equal(Pages.ChangeOrigin.Unknown, O(head, (RoomA, head.AddDays(-7))));
        // 이 방이 예전에 고쳤어도 커밋 뒤 마지막 기록이 다른 방이면 다른 방
        Assert.Equal(Pages.ChangeOrigin.Other, O(head, (RoomA, head.AddMinutes(5)), (RoomB, head.AddMinutes(10))));
        Assert.Equal(Pages.ChangeOrigin.Mine, O(head, (RoomB, head.AddMinutes(5)), (RoomA, head.AddMinutes(10))));
        // 커밋이 없는 새 저장소면 기록 전부로
        Assert.Equal(Pages.ChangeOrigin.Mine, O(null, (RoomA, head.AddDays(-7))));
    }

    // ---- 말 끝 알림 줄 (RoomHub) ----

    [Fact]
    public async Task RepoNote_AppendsLine_SkipsSlash_NeverBlocks()
    {
        Func<string, CancellationToken, Task<string?>> line = (_, _) => Task.FromResult<string?>(RepoPeers.Marker + "알림");
        Assert.Equal("고쳐줘\n\n" + RepoPeers.Marker + "알림", await RoomHub.WithRepoNoteAsync(line, RoomA, "고쳐줘\n", TimeSpan.FromSeconds(2), default));
        Assert.Equal("/compact", await RoomHub.WithRepoNoteAsync(line, RoomA, "/compact", TimeSpan.FromSeconds(2), default));
        Assert.Equal(" \n/review x", await RoomHub.WithRepoNoteAsync(line, RoomA, " \n/review x", TimeSpan.FromSeconds(2), default)); // 앞 공백 뒤 '/'도 명령 (C5)
        Assert.Equal("말", await RoomHub.WithRepoNoteAsync((_, _) => Task.FromResult<string?>(null), RoomA, "말", TimeSpan.FromSeconds(2), default));
        // 던지거나(C2) 오래 걸리면(C3) 말만
        Assert.Equal("말", await RoomHub.WithRepoNoteAsync((_, _) => throw new ArgumentException("x"), RoomA, "말", TimeSpan.FromSeconds(2), default));
        var started = DateTimeOffset.Now;
        Assert.Equal("말", await RoomHub.WithRepoNoteAsync(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "늦음"; }, RoomA, "말",
            TimeSpan.FromMilliseconds(200), default));
        Assert.True(DateTimeOffset.Now - started < TimeSpan.FromSeconds(5));
        // 실행이 이미 취소됐어도 예외 없이 말만 (취소는 실행이 처리)
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal("말", await RoomHub.WithRepoNoteAsync((_, ct) => Task.FromCanceled<string?>(ct), RoomA, "말", TimeSpan.FromSeconds(2), cancelled.Token));
    }

    [Fact]
    public void Git_ParsePorcelain()
    {
        var entries = GitChanges.Parse(" M a.txt\0R  new.txt\0old.txt\0?? 새.md\0A  b.txt\0");
        Assert.Equal(["a.txt", "new.txt", "새.md", "b.txt"], entries.Select(e => e.Path));
        Assert.Equal([false, true, false, true], entries.Select(e => e.Staged));
        Assert.Equal("old.txt", entries[1].OldPath);
    }

    // ---- 같은 저장소 다른 방 ----

    private static Room R(string id, string ws, string? title = null, string? groupId = null, IReadOnlyList<GroupMember>? members = null, DateTimeOffset? deleted = null) =>
        new() { Id = id, SessionId = id, Workspace = ws, Level = PermissionLevel.Command, Model = "opus", CreatedAt = DateTimeOffset.Now, Title = title,
            GroupId = groupId, Members = members ?? [], DeletedAt = deleted };

    [Fact]
    public void Peers_SameRoot_NamesAndLine()
    {
        var me = R("111111111111", "ws");
        var sameSub = R("222222222222", "ws-sub", "다른 방");
        var otherRepo = R("333333333333", "elsewhere", "남의 저장소");
        var trashed = R("444444444444", "ws", "지운 방", deleted: DateTimeOffset.Now);
        var group = R("555555555555", "ws", "단톡", members: [new GroupMember("m1", "검토자", "역할", "666666666666")]);
        var child = R("666666666666", "ws", groupId: "555555555555");
        var all = new[] { me, sameSub, otherRepo, trashed, group, child };
        var roots = new Dictionary<string, string?> { ["ws"] = @"C:\Repo", ["ws-sub"] = @"c:\repo", ["elsewhere"] = @"C:\Other" };
        var activity = new Dictionary<string, RoomActivity> { [sameSub.Id] = RoomActivity.Busy, [child.Id] = RoomActivity.Idle };

        var peers = RepoPeers.Find(me, all, ws => roots.GetValueOrDefault(ws), id => activity.GetValueOrDefault(id), id => all.FirstOrDefault(r => r.Id == id));
        Assert.Equal(["다른 방", "단톡 — 검토자"], peers.Select(p => p.Name));
        Assert.Equal([true, false], peers.Select(p => p.Working));

        var line = RepoPeers.Line(@"C:\Repo", peers)!;
        Assert.StartsWith(RepoPeers.Marker, line);
        Assert.Contains("'다른 방'", line);
        Assert.DoesNotContain("검토자", line); // 쉬는 방은 넣지 않음 (결정 "다른 방이 작업 중일 때만")
        Assert.Null(RepoPeers.Line(@"C:\Repo", [peers[1]]));
        Assert.Empty(RepoPeers.Find(R("777777777777", "none"), all, ws => roots.GetValueOrDefault(ws), _ => RoomActivity.Busy, _ => null));
    }

    [Fact]
    public void Attachments_Split_DropsRepoLine_KeepsAttachments()
    {
        var text = "고쳐줘\n[첨부] C:\\a\\x.png\n\n" + RepoPeers.Marker + "같은 git 저장소(C:\\r)에서 다른 방 'B'이(가) 작업 중입니다 — …";
        var (body, paths) = Attachments.Split(text);
        Assert.Equal(("고쳐줘", 1), (body, paths.Count));
        Assert.Equal("고쳐줘", Attachments.WithoutLines("고쳐줘\n\n" + RepoPeers.Marker + "…"));
        Assert.Equal("본문 속 [같은 저장소] 글은 그대로", Attachments.WithoutLines("본문 속 [같은 저장소] 글은 그대로"));
    }
}

/// <summary>실제 앱: 변경 탭 · diff · 같은 저장소 판정 (작업 폴더를 임시 git 저장소로).</summary>
public sealed class ChangesPageTests(ChatApp app) : IClassFixture<ChatApp>
{
    [Fact]
    public async Task ChangesTab_OriginsDiffAndPathGuard()
    {
        var ws = Path.Combine(app.Dir, "ws");
        TestGit.Init(ws);
        File.WriteAllText(Path.Combine(ws, "mine.txt"), "a\n");
        File.WriteAllText(Path.Combine(ws, "theirs.txt"), "a\n");
        TestGit.Run(ws, "add", ".");
        TestGit.Run(ws, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(ws, "mine.txt"), "a\nb\n");
        File.WriteAllText(Path.Combine(ws, "theirs.txt"), "a\nc\n");
        TestGit.Run(ws, "add", "theirs.txt"); // 다른 방이 올려 둔 인덱스
        File.WriteAllText(Path.Combine(ws, "bash.txt"), "x\n");

        var me = app.Room("변경 탭");
        var other = app.Room("옆 방");
        var edits = app.Services.GetRequiredService<RoomEdits>();
        edits.Add(me.Id, Path.Combine(ws, "mine.txt"), "Edit");
        edits.Add(other.Id, Path.Combine(ws, "theirs.txt"), "Edit");

        using var client = new HttpClient { BaseAddress = app.BaseAddress };
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Room/{me.Id}?handler=Changes"));
        Assert.Contains("이 방 <b>1</b> · 다른 방 <b>1</b> · 출처 모름 <b>1</b>", html);
        Assert.Contains("인덱스(staged)에 이 방이 고치지 않은 파일 1개", html);
        Assert.Contains("theirs.txt (옆 방)", html);

        var diff = WebUtility.HtmlDecode(await client.GetStringAsync($"/Room/{me.Id}?handler=ChangeDiff&path=mine.txt"));
        Assert.Contains("<span class=\"add\">+b</span>", diff);
        // 변경 목록에 없는 경로는 읽지 않는다
        foreach (var bad in new[] { "../../secret.txt", "mine.txt.bak", Path.Combine(ws, "mine.txt") })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Room/{me.Id}?handler=ChangeDiff&path={Uri.EscapeDataString(bad)}")).StatusCode);

        var peers = JsonNode.Parse(await client.GetStringAsync($"/Room/{me.Id}?handler=RepoPeers"))!["working"]!.AsArray();
        Assert.Empty(peers); // 옆 방은 쉬는 중
    }
}

/// <summary>시험용 git 실행 (user.name 등은 명령마다 -c로).</summary>
internal static class TestGit
{
    public static string Init(string dir)
    {
        Directory.CreateDirectory(dir);
        Run(dir, "init", "-q");
        return dir;
    }

    public static void Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
    }

    /// <summary>.git 안 읽기 전용 파일까지 지운다.</summary>
    public static void Delete(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
