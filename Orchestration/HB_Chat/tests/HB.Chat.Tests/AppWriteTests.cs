using System.Diagnostics;
using HB.Chat;
using Route = HB.Chat.WorkspaceStore.AppWriteRoute;

namespace HB.Chat.Tests;

/// <summary>앱 단계 폴더 밖 쓰기 (claude 2.1.316 경로 ask 규칙): 쓰기 판정 · 늘 허용 폴더 저장 · ask 규칙 · 규칙 글.</summary>
public sealed class AppWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hbchat-aw-" + Guid.NewGuid().ToString("N"));
    private readonly string _ws;
    private readonly string _outside;
    private readonly string _claude;
    private readonly string _memory;
    private readonly string[] _guarded;

    public AppWriteTests()
    {
        _ws = Path.Combine(_dir, "ws");
        _outside = Path.Combine(_dir, "outside");
        _claude = Path.Combine(_dir, "dotclaude"); // Claude 설정 폴더 자리 (보호 폴더)
        Directory.CreateDirectory(_ws);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(Path.Combine(_claude, "projects"));
        _memory = Path.Combine(_claude, "projects", HB.Orchestration.Shared.ClaudeTranscriptReader.EncodeProjectDir(_ws), "memory");
        _guarded = [_claude, Path.Combine(_dir, "data")];
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (Route Route, string? Why) Write(string path, params string[] allowed) => WorkspaceStore.AppWrite(path, _guarded, _ws, _memory, allowed);

    [Fact]
    public void InsideWorkspace_AllowsWithoutCard_ButNotGitOrShellSettings()
    {
        Assert.Equal(Route.Allow, Write(Path.Combine(_ws, "src", "new", "a.cs")).Route);
        // 앱 단계는 작업 폴더의 앱 설정 · 스크립트 · 지시 파일을 고칠 수 있다 (그 전 auto와 같은 범위)
        Assert.Equal(Route.Allow, Write(Path.Combine(_ws, "appsettings.json")).Route);
        Assert.Equal(Route.Allow, Write(Path.Combine(_ws, "tools", "build.ps1")).Route);
        // CLI가 auto에서도 스스로 묻던 쪽 — 그 전처럼 거절
        Assert.Equal(Route.Deny, Write(Path.Combine(_ws, ".git", "hooks", "pre-commit")).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(_ws, ".vscode", "tasks.json")).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(_ws, "tool.exe")).Route);
        // 모양이 이상한 경로는 안쪽이어도 거절
        Assert.Equal(Route.Deny, Write(Path.Combine(_ws, "CLAUDE~1.MD")).Route);
    }

    [Fact]
    public void Memory_MarkdownDirectlyInside_Allowed_EvenBeforeFolderExists_RestOfClaudeDenied()
    {
        // 메모리 폴더가 아직 없어도 (그 작업 폴더의 첫 메모리)
        Assert.False(Directory.Exists(_memory));
        Assert.Equal(Route.AllowMemory, Write(Path.Combine(_memory, "MEMORY.md")).Route);
        Directory.CreateDirectory(_memory);
        Assert.Equal(Route.AllowMemory, Write(Path.Combine(_memory, "user-pref.md")).Route);
        // .md가 아니거나 하위 폴더 · 다른 작업 폴더의 메모리 · 설정 파일은 보호 폴더라 거절
        Assert.Equal(Route.Deny, Write(Path.Combine(_memory, "run.ps1")).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(_memory, "sub", "a.md")).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(_claude, "projects", "C--other", "memory", "a.md")).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(_claude, "settings.json")).Route);
        // 메모리를 모르면(null) 보호 폴더 그대로
        Assert.Equal(Route.Deny, WorkspaceStore.AppWrite(Path.Combine(_memory, "a.md"), _guarded, _ws, null, []).Route);
    }

    [Fact]
    public void Outside_Card_AllowedFolder_NoCard_ButStillGuarded()
    {
        var file = Path.Combine(_outside, "report.txt");
        Assert.Equal(Route.Card, Write(file).Route);
        Assert.Equal(Route.AllowFolder, Write(file, _outside).Route);
        Assert.Equal(Route.AllowFolder, Write(Path.Combine(_outside, "deep", "new", "x.txt"), _outside).Route);
        // 늘 허용 폴더 안이어도 실행 · 설정으로 이어지는 이름은 거절
        Assert.Equal(Route.Deny, Write(Path.Combine(_outside, "x.exe"), _outside).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(_outside, "CLAUDE.md"), _outside).Route);
        // 다른 폴더 이름이 앞부분만 같으면 늘 허용이 아니다 (outside2)
        Directory.CreateDirectory(_outside + "2");
        Assert.Equal(Route.Card, Write(Path.Combine(_outside + "2", "x.txt"), _outside).Route);
        // 보호 폴더는 늘 허용 목록에 있어도 거절
        Assert.Equal(Route.Deny, Write(Path.Combine(_claude, "x.txt"), _claude).Route);
        Assert.Equal(Route.Deny, Write(@"rel\x.txt").Route);
    }

    [Fact]
    public void JunctionInsideWorkspace_PointingOutside_IsNotInside()
    {
        if (!OperatingSystem.IsWindows()) return;
        var link = Path.Combine(_ws, "linked");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, _outside]) { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        if (!Directory.Exists(link)) return; // junction을 못 만드는 환경
        // 링크를 거치는 경로는 안쪽으로 보지 않고 거절 (WhyNoWrite와 같은 검사)
        Assert.Equal(Route.Deny, Write(Path.Combine(link, "x.txt")).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(link, "x.txt"), _outside).Route);
    }

    [Fact]
    public void AllowedFolder_ReplacedByJunction_DoesNotOpenJunctionTarget()
    {
        // 늘 허용한 뒤 그 폴더를 다른 곳을 가리키는 정션으로 바꿔도, 정션 대상은 허용되지 않는다 (/cross-review 53a0e1871c 1-F2)
        if (!OperatingSystem.IsWindows()) return;
        var allowed = Path.Combine(_dir, "exports");
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        var final = WorkspaceStore.FinalFolder(_outside)!; // 저장되는 값 = 최종 경로
        Directory.CreateDirectory(allowed);
        var allowedFinal = WorkspaceStore.FinalFolder(allowed)!;
        Assert.Equal(Route.AllowFolder, Write(Path.Combine(allowed, "a.txt"), allowedFinal, final).Route);
        Directory.Delete(allowed);
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", allowed, other]) { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        if (!Directory.Exists(allowed)) return; // junction을 못 만드는 환경
        Assert.Equal(Route.Card, Write(Path.Combine(other, "a.txt"), allowedFinal).Route);
        Assert.Equal(Route.Deny, Write(Path.Combine(allowed, "a.txt"), allowedFinal).Route); // 링크를 거치는 경로
    }

    [Fact]
    public void TooBroad_HomeChildrenAndAppDataChildren()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(AppWriteStore.WhyTooBroad(Path.Combine(home, "Documents")));
        Assert.NotNull(AppWriteStore.WhyTooBroad(Path.Combine(home, "AppData")));
        Assert.NotNull(AppWriteStore.WhyTooBroad(Path.Combine(home, "AppData", "Roaming")));
        Assert.NotNull(AppWriteStore.WhyTooBroad(Path.Combine(home, "AppData", "Local")));
        Assert.Null(AppWriteStore.WhyTooBroad(Path.Combine(home, "Documents", "Exports")));
        Assert.Null(AppWriteStore.WhyTooBroad(_outside));
    }

    // ---- 저장 ----

    [Fact]
    public void Store_NullFolders_ReadsAsEmpty()
    {
        // 손으로 고친 파일 "folders": null — 앱 시작이 멈추지 않고 빈 목록 (/cross-review 53a0e1871c 1-F1)
        var file = Path.Combine(_dir, "data", "nullfolders.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"ask":false,"folders":null}""");
        var store = Store(file);
        Assert.Null(store.LoadError);
        Assert.False(store.Ask);
        Assert.Empty(store.Folders());
        Assert.Null(store.AddFolder(_outside));
    }

    [Fact]
    public void Store_ReAddingWarnedFolder_KeepsOneLine()
    {
        // 켤 때 경고로 빠진 줄을 같은 경로로 다시 더하면 한 줄로 (sub_self 47b774c535 C7)
        var file = Path.Combine(_dir, "data", "warned.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var later = Path.Combine(_dir, "later");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { ask = true, folders = new[] { later } }));
        var store = Store(file);
        Assert.Single(store.LoadWarnings); // 아직 없는 폴더
        Directory.CreateDirectory(later);
        Assert.Null(store.AddFolder(later));
        Assert.Single(store.SavedFolders());
        Assert.Single(store.Folders());
    }

    private AppWriteStore Store(string? file = null) =>
        new(file ?? Path.Combine(_dir, "data", "app-writes.json"), p => Directory.Exists(p) ? p.StartsWith(_claude, StringComparison.OrdinalIgnoreCase) ? "보호 폴더" : null : "없는 폴더입니다.");

    [Fact]
    public void Store_DefaultAsk_SavesAskAndFolders_AcrossReload()
    {
        var store = Store();
        Assert.True(store.Ask);
        Assert.Empty(store.Folders());
        Assert.Null(store.SetAsk(false));
        Assert.Null(store.AddFolder(_outside));
        Assert.Null(store.AddFolder(_outside)); // 이미 있으면 그대로
        Assert.Equal("없는 폴더입니다.", store.AddFolder(Path.Combine(_dir, "nope")));
        Assert.Equal("보호 폴더", store.AddFolder(_claude));

        var again = Store();
        Assert.False(again.Ask);
        Assert.Single(again.Folders());
        Assert.Null(again.RemoveFolder(again.SavedFolders()[0]));
        Assert.Empty(Store().Folders());
        Assert.NotNull(again.RemoveFolder(_outside)); // 목록에 없음
    }

    [Fact]
    public void Store_BrokenFile_RefusesWrites_AndStaysAsk()
    {
        var file = Path.Combine(_dir, "data", "broken.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ not json");
        var store = Store(file);
        Assert.NotNull(store.LoadError);
        Assert.True(store.Ask);
        Assert.NotNull(store.SetAsk(false));
        Assert.NotNull(store.AddFolder(_outside));
        Assert.Equal("{ not json", File.ReadAllText(file)); // 덮어쓰지 않음
    }

    [Fact]
    public void Store_FolderThatBecameUnsafe_IsWarnedNotUsed()
    {
        var file = Path.Combine(_dir, "data", "app-writes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { ask = true, folders = new[] { _claude, _outside } }));
        var store = Store(file);
        Assert.Single(store.Folders());
        Assert.Single(store.LoadWarnings);
        Assert.Equal(2, store.SavedFolders().Count); // 빼기 단추를 보이게 남긴다
    }

    // ---- 인자 · 규칙 글 ----

    [Fact]
    public void AppSettings_AskRuleOnlyWhenAskWrites()
    {
        var spec = new RunSpec(PermissionLevel.App, "opus", "15eb061e-7153-4c14-83ba-083385ed6945", false, "C:/data/mcp.json", "hb-orchestration", "규칙");
        Assert.Contains(ClaudeArgs.AppWriteAskRule, ClaudeArgs.AppSettings(spec with { AskWrites = true }));
        Assert.DoesNotContain("Edit(", ClaudeArgs.AppSettings(spec));
        var args = ClaudeArgs.Build(spec with { AskWrites = true }, ["opus"]).ToList();
        Assert.Contains(ClaudeArgs.AppWriteAskRule, args[args.IndexOf("--settings") + 1]);
    }

    [Fact]
    public void AppRules_SayWhatHappensToOutsideWrites()
    {
        Assert.Contains("작업 폴더 밖에 쓰는 것은 카드로 묻는다", RoomRules.Describe(PermissionLevel.App, askWrites: true));
        Assert.Contains("폴더 밖 쓰기는 막지 않고", RoomRules.Describe(PermissionLevel.App, askWrites: false));
        Assert.Equal(RoomRules.Describe(PermissionLevel.Command, askWrites: true), RoomRules.Describe(PermissionLevel.Command, askWrites: false));
        Assert.NotEqual(RoomRules.Hash(RoomRules.For(PermissionLevel.App, askWrites: true)), RoomRules.Hash(RoomRules.For(PermissionLevel.App, askWrites: false)));
    }
}
