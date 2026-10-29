using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using HB.Chat;
using HB.Orchestration.Shared;

namespace HB.Chat.Tests;

/// <summary>곁칸 "문서" 탭: 작업 폴더 docs 찾기 · 경로 검사 · 색인 상태 칸 · ?handler=Docs · Doc.</summary>
public sealed class ProjectDocsTests : IDisposable
{
    private readonly string ws = Path.Combine(Path.GetTempPath(), "hbchat-docs-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        // junction은 대상까지 지우지 않게 먼저 뗀다
        foreach (var dir in Directory.Exists(ws) ? Directory.EnumerateDirectories(ws, "*", SearchOption.AllDirectories).ToList() : [])
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is not null) Directory.Delete(dir);
        if (Directory.Exists(ws)) Directory.Delete(ws, true);
    }

    private string Write(string rel, string text = "# t", DateTime? at = null)
    {
        var path = Path.Combine(ws, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (at is { } t) File.SetLastWriteTime(path, t);
        return path;
    }

    private static bool Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    [Fact]
    public void Find_ProjectsUnderDepth_PlansNewestFirst_PairedLogAndStatus()
    {
        var day = new DateTime(2026, 10, 30, 9, 0, 0);
        Write("Orchestration/HB_Chat/docs/plans/2026-10-29_옛-계획.md", at: day.AddDays(-1));
        Write("Orchestration/HB_Chat/docs/plans/2026-10-30_곁칸-문서.md", at: day);
        Write("Orchestration/HB_Chat/docs/worklog/2026-10-30_곁칸-문서.md", "[계획서](../plans/2026-10-30_곁칸-문서.md)");
        Write("Orchestration/HB_Chat/docs/worklog/2026-10-20_묶음-로그.md");
        Write("Orchestration/HB_Chat/docs/worklog.md",
            "| 날짜 | 작업 | 계획서 | 로그 | 상태 |\n|---|---|---|---|---|\n| 2026-10-30 | 곁칸 | [계획서](plans/2026-10-30_곁칸-문서.md) | - | 구현 중 |\n");
        Write("docs/plans/2026-10-01_루트.md");
        Write("a/b/c/d/docs/plans/2026-10-01_너무-깊음.md");      // docs 부모가 4단계 — 찾지 않음
        Write("src/bin/docs/plans/2026-10-01_산출물.md");          // 건너뛸 폴더
        Write(".hidden/docs/plans/2026-10-01_점폴더.md");

        var projects = ProjectDocs.Find(ws);
        Assert.Equal(["HB_Chat", Path.GetFileName(ws)], projects.Select(p => p.Name).OrderBy(n => n == "HB_Chat" ? 0 : 1));
        var chat = projects.Single(p => p.Name == "HB_Chat");
        Assert.Equal("Orchestration/HB_Chat/docs", chat.DocsRel);
        Assert.Equal("Orchestration/HB_Chat/docs/worklog.md", chat.Index?.Rel);
        Assert.Equal(["곁칸 문서", "옛 계획"], chat.Plans.Select(r => r.Plan.Title));
        Assert.Equal("2026-10-30", chat.Plans[0].Plan.Date);
        Assert.Equal("Orchestration/HB_Chat/docs/worklog/2026-10-30_곁칸-문서.md", chat.Plans[0].Log?.Rel);
        Assert.Equal("구현 중", chat.Plans[0].Status);
        Assert.Null(chat.Plans[1].Log);
        Assert.Null(chat.Plans[1].Status);
        Assert.Equal(["묶음 로그"], chat.LooseLogs.Select(l => l.Title));
    }

    private static FileMention Mention(int line, FileMentionKind kind, string? path, string name) =>
        new(line, new DateTimeOffset(2026, 10, 30, 0, line, 0, TimeSpan.Zero), kind, path, name);

    [Fact]
    public void FindForRoom_OnlyTouchedDocs_LastTouchedFirst_WithPairsAndStatus()
    {
        var old = new DateTime(2026, 10, 1);
        var plan = Write("Orchestration/HB_Chat/docs/plans/2026-10-30_가.md", at: old);
        Write("Orchestration/HB_Chat/docs/worklog/2026-10-30_가.md", at: old);
        Write("Orchestration/HB_Chat/docs/plans/2026-10-29_나.md", at: old);
        Write("Orchestration/HB_Chat/docs/worklog/2026-10-28_묶음.md", at: old);
        Write("Orchestration/HB_Chat/docs/plans/2026-10-27_안-다룸.md");                  // 가장 최근 수정이지만 이 방에서 다루지 않음
        Write("Orchestration/HB_Chat/docs/worklog.md",
            "| 날짜 | 작업 | 계획서 | 로그 | 상태 |\n|---|---|---|---|---|\n| 2026-10-30 | 가 | [계획서](plans/2026-10-30_가.md) | - | 구현 중 |\n");
        var orchPlan = Write("Orchestration/HB_Orchestration/docs/plans/2026-10-29_나.md", at: old); // 이름이 같은 다른 프로젝트 계획서

        var (projects, room) = ProjectDocs.Find(ws,
        [
            Mention(1, FileMentionKind.Read, plan.ToUpperInvariant(), "2026-10-30_가.md"),   // 경로 대소문자 무시
            Mention(2, FileMentionKind.Shell, null, "2026-10-29_나.md"),
            Mention(3, FileMentionKind.Shell, null, "worklog.md"),                           // 색인은 방 목록에 넣지 않음
            Mention(4, FileMentionKind.Edit, Path.Combine(ws, "밖", "2026-10-28_묶음.md"), "2026-10-28_묶음.md"), // 경로가 다르면 이름이 같아도 아님
            Mention(5, FileMentionKind.Shell, null, "2026-10-28_묶음.md"),
            Mention(6, FileMentionKind.Read, Path.Combine(ws, "secret.md"), "secret.md"),    // docs 밖
            Mention(7, FileMentionKind.Edit, orchPlan, "2026-10-29_나.md"),                  // 마지막으로 다룬 프로젝트가 위로
            Mention(8, FileMentionKind.Read, "Orchestration/HB_Chat/docs/plans/2026-10-27_안-다룸.md", "2026-10-27_안-다룸.md"), // 상대 경로는 버림
        ]);

        Assert.Equal(2, projects.Count);
        Assert.Equal(["HB_Orchestration", "HB_Chat"], room.Select(p => p.Name));
        var chat = room.Single(p => p.Name == "HB_Chat");
        Assert.Equal(["묶음", "나", "가"], chat.Rows.Select(r => (r.Plan ?? r.Log)!.Title));
        Assert.Null(chat.Rows[0].Plan);                                              // 짝 없는 로그
        Assert.Equal(FileMentionKind.Shell, chat.Rows[0].Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 0, 5, 0, TimeSpan.Zero), chat.Rows[0].At);
        Assert.Equal("Orchestration/HB_Chat/docs/worklog/2026-10-30_가.md", chat.Rows[2].Log?.Rel); // 계획서만 다뤄도 짝 로그
        Assert.Equal(FileMentionKind.Read, chat.Rows[2].Kind);
        Assert.Equal("구현 중", chat.Rows[2].Status);
        Assert.Equal(["나"], room.Single(p => p.Name == "HB_Orchestration").Rows.Select(r => r.Plan!.Title)); // 이름 맞추기는 두 프로젝트 모두

        Assert.Empty(ProjectDocs.Find(ws, null).Room);
        Assert.Empty(ProjectDocs.Find(ws, []).Room);
    }

    [Fact]
    public void FindForRoom_EditWins_AndKeepsNewestRows()
    {
        var log = Write("docs/worklog/2026-10-30_가.md");
        Write("docs/plans/2026-10-30_가.md");
        var mentions = new List<FileMention> { Mention(1, FileMentionKind.Edit, log, "2026-10-30_가.md"), Mention(2, FileMentionKind.Read, null, "2026-10-30_가.md") };
        for (var i = 0; i < ProjectDocs.MaxRoomRows + 2; i++)
        {
            Write($"docs/plans/2026-09-{i + 1:00}_p{i}.md");
            mentions.Add(Mention(10 + i, FileMentionKind.Shell, null, $"2026-09-{i + 1:00}_p{i}.md"));
        }

        var rows = Assert.Single(ProjectDocs.Find(ws, mentions).Room).Rows;

        Assert.Equal(ProjectDocs.MaxRoomRows, rows.Count);
        Assert.Equal($"p{ProjectDocs.MaxRoomRows + 1}", rows[0].Plan!.Title);
        Assert.DoesNotContain(rows, r => r.Plan?.Title == "가");                   // 가장 먼저 다룬 것부터 잘림
        var all = Assert.Single(ProjectDocs.Find(ws, mentions.Take(2).ToList()).Room).Rows;
        Assert.Equal(FileMentionKind.Edit, Assert.Single(all).Kind);               // 로그를 고치고 계획서를 읽음 → 고침
    }

    [Fact]
    public void Find_KeepsOnlyNewestPlans_AndSkipsJunctionedFolders()
    {
        for (var i = 0; i < ProjectDocs.MaxPlans + 3; i++)
            Write($"docs/plans/2026-10-{i + 1:00}_p{i}.md", at: new DateTime(2026, 10, i + 1));
        Assert.Equal(ProjectDocs.MaxPlans, ProjectDocs.Find(ws).Single().Plans.Count);
        Assert.Equal($"p{ProjectDocs.MaxPlans + 2}", ProjectDocs.Find(ws).Single().Plans[0].Plan.Title);

        var outside = Path.Combine(ws + "-out");
        Directory.CreateDirectory(Path.Combine(outside, "docs", "plans"));
        File.WriteAllText(Path.Combine(outside, "docs", "plans", "2026-10-01_밖.md"), "secret");
        try
        {
            if (!Junction(Path.Combine(ws, "linked"), outside)) return; // junction을 못 만드는 환경
            Assert.DoesNotContain(ProjectDocs.Find(ws), p => p.Name == "linked");
            Assert.Null(ProjectDocs.Resolve(ws, "linked/docs/plans/2026-10-01_밖.md"));
        }
        finally
        {
            if (Directory.Exists(Path.Combine(ws, "linked"))) Directory.Delete(Path.Combine(ws, "linked"));
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Resolve_OnlyDocsMarkdownInsideWorkspace()
    {
        var plan = Write("Orchestration/HB_Chat/docs/plans/2026-10-30_곁칸.md");
        Write("Orchestration/HB_Chat/docs/worklog.md");
        Write("Orchestration/HB_Chat/docs/research/note.md");
        Write("Orchestration/HB_Chat/docs/plans/image.png");
        Write("Orchestration/HB_Chat/README.md");
        Write("docs/plans/big.md", new string('x', (int)ProjectDocs.MaxBytes + 1));

        Assert.Equal(plan, ProjectDocs.Resolve(ws, "Orchestration/HB_Chat/docs/plans/2026-10-30_곁칸.md"));
        Assert.NotNull(ProjectDocs.Resolve(ws, "Orchestration/HB_Chat/docs/worklog.md"));
        foreach (var bad in new[]
        {
            null, "", "Orchestration/HB_Chat/docs/research/note.md", "Orchestration/HB_Chat/docs/plans/image.png", "Orchestration/HB_Chat/README.md",
            "Orchestration/HB_Chat/docs/plans/../../README.md", "Orchestration\\HB_Chat\\docs\\worklog.md", "/docs/worklog.md",
            "C:/Windows/docs/worklog.md", "a/b/c/d/docs/worklog.md", "src/bin/docs/worklog.md", "docs/plans/big.md",
            "docs/plans/없음.md", "Orchestration//HB_Chat/docs/worklog.md",
            "bin./docs/worklog.md", "src/bin /docs/worklog.md", "REVIEW~1/docs/worklog.md", "Orchestration/HB_Chat/docs/plans/2026-10-30_곁칸.md.",
            "a/docs/b/docs/plans/x.md", "docs/docs/worklog.md",
        })
            Assert.Null(ProjectDocs.Resolve(ws, bad));
        Assert.Null(ProjectDocs.Resolve(null, "docs/worklog.md"));
    }

    /// <summary>Markdown 속성 문법 {…}으로 이벤트 처리기 · style을 붙이지 못한다 (문서 탭이 작업 폴더 글을 그대로 그린다 — sub_security caea21ac40 C1).</summary>
    [Theory]
    [InlineData("![a](/x.png){onerror=alert(1)}")]
    [InlineData("[b](https://example.com){onclick=\"alert(1)\" style=\"position:fixed\"}")]
    [InlineData("# 제목 {onmouseover=alert(1)}")]
    [InlineData("문단\n{onclick=alert(1)}")]
    public void SafeMarkdown_DropsGenericAttributes(string markdown)
    {
        // 글자로 남는 것(이스케이프된 본문 · 제목 자동 id)은 괜찮다 — 태그 안의 on…= · style= 속성만 없으면 된다
        var html = HB.Orchestration.ChatUi.SafeMarkdown.ToHtml(markdown);
        Assert.DoesNotMatch(@"<[^>]*\s(on[a-z]+|style)\s*=", html);
    }

    /// <summary>제목 자동 id와 #앵커에 md- — 앱 화면 id(#text · #send …)와 겹치지 않는다.</summary>
    [Fact]
    public void SafeMarkdown_PrefixesHeadingIdsAndAnchors()
    {
        var html = HB.Orchestration.ChatUi.SafeMarkdown.ToHtml("# text\n\n[아래](#text) [밖](https://example.com/#text)");
        Assert.Contains("id=\"md-text\"", html);
        Assert.Contains("href=\"#md-text\"", html);
        Assert.Contains("href=\"https://example.com/#text\"", html);
        Assert.DoesNotContain("id=\"text\"", html);
    }

    [Theory]
    [InlineData("| 2026-10-30 | x | [계획서](plans/a.md) | [로그](worklog/a.md) | 끝 |", "끝")]
    [InlineData("| 2026-10-30 | x | [계획서](plans/a.md) | - |  |", null)]
    [InlineData("- [계획서](plans/a.md) 끝", null)]  // 표가 아니면 못 찾음
    [InlineData("| 2026-10-30 | x | [계획서](plans/b.md) | - | 끝 |", null)]
    public void StatusOf_LastCellOfThePlanRow(string line, string? expected) =>
        Assert.Equal(expected, ProjectDocs.StatusOf("| 날짜 | 작업 | 계획서 | 로그 | 상태 |\n" + line + "\n", "a.md"));
}

/// <summary>?handler=Docs · Doc — 작업 폴더(시험 폴더 ws)의 docs만, 본문은 SafeMarkdown.</summary>
public sealed class ProjectDocsPageTests(ChatApp app) : IClassFixture<ChatApp>
{
    private HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = app.BaseAddress };

    [Fact]
    public async Task Docs_ListsPlans_DocRendersSafeMarkdown_RejectsOtherPaths()
    {
        var docs = Path.Combine(app.Dir, "ws", "docs");
        Directory.CreateDirectory(Path.Combine(docs, "plans"));
        File.WriteAllText(Path.Combine(docs, "plans", "2026-10-30_곁칸-문서.md"), "# 제목\n\n[로그](../worklog/2026-10-30_곁칸-문서.md) <script>alert(1)</script> [x](javascript:alert(1))");
        File.WriteAllText(Path.Combine(app.Dir, "ws", "secret.md"), "secret");
        var room = app.Room("docs", ChatApp.User("hi", 0));
        using var client = Client();

        var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/Room/{room.Id}?handler=Docs"));
        Assert.Contains("data-doc=\"docs/plans/2026-10-30_곁칸-문서.md\"", list);
        Assert.Contains("곁칸 문서", list);

        using var doc = await client.GetAsync($"/Room/{room.Id}?handler=Doc&path=" + Uri.EscapeDataString("docs/plans/2026-10-30_곁칸-문서.md"));
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
        Assert.Equal("no-store", doc.Headers.CacheControl?.ToString());
        var html = await doc.Content.ReadAsStringAsync();
        Assert.Contains("<h1", html);
        Assert.Contains("href=\"../worklog/2026-10-30_%EA%B3%81%EC%B9%B8-%EB%AC%B8%EC%84%9C.md\"", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html);

        foreach (var bad in new[] { "secret.md", "docs/plans/../../secret.md", "docs/plans/없음.md" })
        {
            using var res = await client.GetAsync($"/Room/{room.Id}?handler=Doc&path=" + Uri.EscapeDataString(bad));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task Docs_RoomSection_TouchedDocsFirst_FolderListFolded_CodexRoomHasNone()
    {
        var plans = Path.Combine(app.Dir, "ws", "docs", "plans");
        Directory.CreateDirectory(plans);
        var touched = Path.Combine(plans, "2026-10-30_방에서-읽음.md");
        File.WriteAllText(touched, "# 읽음");
        File.WriteAllText(Path.Combine(plans, "2026-10-30_다른-방.md"), "# 다른 방");
        var room = app.Room("docs-room", ChatApp.User("hi", 0), ChatApp.Tool("Read", new JsonObject { ["file_path"] = touched }, 1));
        var fresh = app.Room("docs-fresh", ChatApp.User("hi", 0));
        var codex = app.Store.Update(app.Room("docs-codex", ChatApp.User("hi", 0)).Id, r => r with { Engine = RoomEngines.Codex })!;
        using var client = Client();

        var list = WebUtility.HtmlDecode(await client.GetStringAsync($"/Room/{room.Id}?handler=Docs"));
        var roomPart = list[..list.IndexOf("<details", StringComparison.Ordinal)];
        Assert.Contains("이 방에서 다룬 문서", roomPart);
        Assert.Contains("data-doc=\"docs/plans/2026-10-30_방에서-읽음.md\"", roomPart);
        Assert.Contains("읽음 · ", roomPart);
        Assert.DoesNotContain("다른 방", roomPart);
        Assert.Contains("<details class=\"docs-folder\">", list);                 // 방 기준이 있으면 접어 둔다
        Assert.Contains("data-doc=\"docs/plans/2026-10-30_다른-방.md\"", list);

        var none = WebUtility.HtmlDecode(await client.GetStringAsync($"/Room/{fresh.Id}?handler=Docs"));
        Assert.Contains("이 방에서 다룬 계획서 · 로그가 없습니다", none);
        Assert.Contains("<details class=\"docs-folder\" open=\"open\">", none);    // 방 목록이 없으면 펼친 채로

        var codexList = WebUtility.HtmlDecode(await client.GetStringAsync($"/Room/{codex.Id}?handler=Docs"));
        Assert.DoesNotContain("이 방에서 다룬", codexList);
        Assert.Contains("<details class=\"docs-folder\" open=\"open\">", codexList); // 방 기준이 없으면 펼친 채로
    }
}
