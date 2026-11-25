using System.Text.Json.Nodes;
using HB.Chat;
using HB.Chat.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace HB.Chat.Tests;

/// <summary>이 방 파일만 add 명령 · .hbchatignore 미추적 숨김 · 지시 상용구.</summary>
public sealed class SnippetsAndAddTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-snip-" + Guid.NewGuid().ToString("N"));

    public SnippetsAndAddTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => TestGit.Delete(_dir);

    // ---- 이 방 파일만 add ----

    private static ChangeRow Row(char x, char y, string path, ChangeOrigin origin, string? old = null) => new(new GitEntry(x, y, path, old), origin);

    [Fact]
    public void AddCommand_MineWorktreeOnly_LiteralPathspecs_RiskySkipped_StagedNote()
    {
        var rows = new[]
        {
            Row(' ', 'M', "src/a.cs", ChangeOrigin.Mine),
            Row('R', ' ', "src/new.cs", ChangeOrigin.Mine, old: "src/old.cs"), // 이미 인덱스에 다 오름 — 빼고 옛 경로도 안 넣음 (C2)
            Row('D', ' ', "gone.cs", ChangeOrigin.Mine), // git rm으로 staged된 삭제 — 빼기 (경로가 어디에도 없음)
            Row(' ', 'D', "deleted.cs", ChangeOrigin.Mine), // 작업 트리 삭제 — add가 삭제를 올림
            Row('M', 'M', "both.cs", ChangeOrigin.Mine),
            Row('?', '?', "note[1].txt", ChangeOrigin.Mine), // 패턴 글자는 --literal-pathspecs로 그대로 (C1)
            Row(' ', 'M', "src/$weird.cs", ChangeOrigin.Mine),
            Row(' ', 'M', "hi!.cs", ChangeOrigin.Mine),
            Row(' ', 'M', "“smart”.cs", ChangeOrigin.Mine), // PowerShell이 따옴표로 읽는 글자 (C3)
            Row('M', ' ', "other.cs", ChangeOrigin.Other),
            Row('?', '?', "bash.txt", ChangeOrigin.Unknown),
        };
        var (command, prompt) = ChangesView_Add(@"C:\Repo", rows, stagedNotMine: 1)!.Value;
        Assert.Equal("git -C \"C:\\Repo\" --literal-pathspecs add -- \"src/a.cs\" \"deleted.cs\" \"both.cs\" \"note[1].txt\"", command);
        Assert.StartsWith("이 방에서 고친 파일만 올려 줘", prompt);
        Assert.Contains(command, prompt);
        Assert.Contains("인덱스에 이 방이 고치지 않은 파일 1개", prompt);
        Assert.Contains("경로 3개는 명령에서 뺐음", prompt);
        Assert.DoesNotContain("other.cs", prompt);

        Assert.Null(ChangesView_Add(@"C:\Repo", [rows[9], rows[10]], 0)); // 이 방 줄이 없으면 단추 없음
        Assert.Null(ChangesView_Add(@"C:\Repo", [rows[1], rows[2]], 0)); // 이미 다 staged면 올릴 것 없음
        Assert.Null(ChangesView_Add(@"C:\Re$po", rows, 0)); // 루트에 위험 글자
        Assert.Null(ChangesView_Add(@"C:\Repo", [rows[6]], 0)); // 넣을 경로가 하나도 없으면
    }

    [Fact]
    public void AddCommand_TooLong_SplitsAndNotes()   // C4
    {
        var rows = Enumerable.Range(0, 400).Select(i => Row('?', '?', $"folder/sub/{new string('x', 40)}{i}.txt", ChangeOrigin.Mine)).ToList();
        var (command, prompt) = ChangesView_Add(@"C:\Repo", rows, 0)!.Value;
        Assert.True(command.Length <= ChangesView.MaxCommandChars);
        Assert.Contains("명령이 너무 길어 경로", prompt);
    }

    private static (string Command, string Prompt)? ChangesView_Add(string root, IReadOnlyList<ChangeRow> rows, int stagedNotMine) =>
        ChangesView.AddCommand(root, rows, stagedNotMine);

    // ---- .hbchatignore ----

    [Fact]
    public async Task HbChatIgnore_HidesUntrackedOnly()
    {
        var repo = TestGit.Init(Path.Combine(_dir, "repo"));
        File.WriteAllText(Path.Combine(repo, "tracked.log"), "a\n");
        TestGit.Run(repo, "add", ".");
        TestGit.Run(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        File.WriteAllText(Path.Combine(repo, "tracked.log"), "a\nb\n"); // 추적 파일 변경은 패턴에 맞아도 보임
        Directory.CreateDirectory(Path.Combine(repo, "local", "deep"));
        File.WriteAllText(Path.Combine(repo, "local", "deep", "x.md"), "x");
        File.WriteAllText(Path.Combine(repo, "note.log"), "x");
        File.WriteAllText(Path.Combine(repo, "staged.log"), "x");
        TestGit.Run(repo, "add", "staged.log"); // staged는 보임
        File.WriteAllText(Path.Combine(repo, "keep.txt"), "x");

        var git = new GitChanges();
        Assert.Equal(0, (await git.StatusAsync(repo)).Hidden); // 무시 파일이 없으면 그대로

        File.WriteAllText(Path.Combine(repo, GitChanges.IgnoreFile), "# 로컬 사본\nlocal/\n*.log\n.hbchatignore\n");
        var status = await git.StatusAsync(repo);
        Assert.Equal(["keep.txt", "staged.log", "tracked.log"], status.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, status.Hidden); // local/deep/x.md · note.log · .hbchatignore
    }

    // ---- 지시 상용구 ----

    [Fact]
    public void Snippets_SaveRenameRemove_Limits_BrokenFileBlocks()
    {
        var s = new Snippets(() => _dir);
        Assert.Empty(s.All()); // 처음은 비움 (결정)
        Assert.Null(s.Save(null, "리뷰", "/self-review sub\r\n"));
        Assert.Null(s.Save(null, "커밋", "커밋 푸시까지 하자"));
        Assert.Equal([("리뷰", "/self-review sub"), ("커밋", "커밋 푸시까지 하자")], s.All().Select(x => (x.Name, x.Text)));

        Assert.NotNull(s.Save(null, "리뷰", "다른 글")); // 같은 이름 (대소문자 무시)
        Assert.Null(s.Save("리뷰", "검토", "/self-review sub security")); // 이름 바꾸기
        Assert.Equal(["검토", "커밋"], s.All().Select(x => x.Name));
        Assert.NotNull(s.Save("검토", "커밋", "x")); // 다른 것과 겹치는 이름으로는 못 바꿈
        Assert.Contains("그 사이 지워졌거나", s.Save("리뷰", "리뷰", "되살림")); // 이름을 바꾼 옛 폼으로 저장 — 되살리지 않음 (C7)
        Assert.Equal(["검토", "커밋"], s.All().Select(x => x.Name));

        Assert.NotNull(s.Save(null, "", "x"));
        Assert.NotNull(s.Save(null, new string('a', Snippets.MaxName + 1), "x"));
        Assert.NotNull(s.Save(null, "줄\n바꿈", "x"));
        Assert.NotNull(s.Save(null, "빈 글", "  "));
        Assert.NotNull(s.Save(null, "긴 글", new string('x', Snippets.MaxText + 1)));

        Assert.Null(s.Remove("검토"));
        Assert.Null(s.Remove("없는 이름"));
        Assert.Equal(["커밋"], s.All().Select(x => x.Name));

        File.WriteAllText(Path.Combine(_dir, "snippets.json"), "{ 깨짐");
        Assert.Empty(s.All());
        Assert.Contains("덮어쓰지 않음", s.Save(null, "새것", "x"));
        Assert.Equal("{ 깨짐", File.ReadAllText(Path.Combine(_dir, "snippets.json")));
    }

    [Fact]
    public void Snippets_MaxCount()
    {
        var s = new Snippets(() => _dir);
        for (var i = 0; i < Snippets.MaxCount; i++) Assert.Null(s.Save(null, "s" + i, "x"));
        Assert.Contains("까지", s.Save(null, "넘침", "x"));
        Assert.Null(s.Save("s0", "s0", "고침")); // 고치기는 개수 상한과 상관없음
    }
}

/// <summary>실제 앱: 입력칸 목록 응답에 상용구가 실린다.</summary>
public sealed class SnippetsPageTests(ChatApp app) : IClassFixture<ChatApp>
{
    [Fact]
    public async Task Commands_IncludeSnippets()
    {
        app.Services.GetRequiredService<Snippets>().Save(null, "리뷰", "/self-review sub");
        var room = app.Room("상용구");
        using var client = new HttpClient { BaseAddress = app.BaseAddress };
        var json = JsonNode.Parse(await client.GetStringAsync($"/Room/{room.Id}?handler=Commands"))!;
        var snip = Assert.Single(json["snippets"]!.AsArray());
        Assert.Equal(("리뷰", "/self-review sub"), (snip!["name"]!.GetValue<string>(), snip["text"]!.GetValue<string>()));
    }
}
