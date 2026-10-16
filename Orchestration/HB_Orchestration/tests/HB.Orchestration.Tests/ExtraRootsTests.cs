using System.Diagnostics;
using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Configuration;

namespace HB.Orchestration.Tests;

/// <summary>
/// Context.ExtraRoots: 비어 있으면 기존 동작 그대로, 등록한 루트 안 정상 경로 성공, 루트 밖·링크·DenyPatterns 거절,
/// 참조한 루트만 검사, 이름 대소문자 무시, '@'를 안 쓰면 ContextRoots 기록 생략.
/// </summary>
public sealed class ExtraRootsTests : IDisposable
{
    private readonly string _main = Directory.CreateTempSubdirectory("hb-orch-main-").FullName;
    private readonly string _extra = Directory.CreateTempSubdirectory("hb-orch-extra-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("hb-orch-outside-").FullName;

    public ExtraRootsTests()
    {
        Write(_main, "Assets/A.cs", "class A\n{\n}\n");
        Write(_extra, "src/B.cs", "// B\nclass B { }\n");
        Write(_extra, ".env", "SECRET=1\n");
        Write(_outside, "C.cs", "class C { }\n");
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _main, _extra, _outside })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private (ContextLoader.Result? Result, string? Error) Load(Dictionary<string, string> extraRoots, params ContextRange[] ranges)
    {
        var options = new ContextOptions { ExtraRoots = new(extraRoots, StringComparer.OrdinalIgnoreCase) };
        return ContextLoader.Load(ranges, options, _main, maxChars: 100_000);
    }

    private static readonly ContextRange MainRange = new("Assets/A.cs", 1, 3);

    /// <summary>기존 구현이 만들던 첨부 텍스트 (AppendLine = Environment.NewLine).</summary>
    private static string ExpectedMainAttachment()
    {
        var nl = Environment.NewLine;
        return $"{nl}## 첨부 코드 (서버가 디스크에서 읽음, 줄 번호 포함){nl}{nl}### Assets/A.cs:1-3{nl}```csharp{nl}" +
               $"1| class A{nl}2| {{{nl}3| }}{nl}```{nl}";
    }

    // ── 비어 있을 때 / 쓰지 않을 때: 기존 동작과 바이트 동일 ─────────────────

    [Fact]
    public void Empty_KeepsExistingOutput()
    {
        var (result, error) = Load([], MainRange);
        Assert.Null(error);
        Assert.Equal(ExpectedMainAttachment(), result!.Text);
        Assert.Equal(["Assets/A.cs:1-3"], result.Sources);
        Assert.Null(result.Roots);
    }

    [Fact]
    public void Registered_ButOnlyMainPaths_IsByteIdentical()
    {
        var (empty, _) = Load([], MainRange);
        var (registered, error) = Load(new() { ["orch"] = _extra }, MainRange);

        Assert.Null(error);
        Assert.Equal(Encoding.UTF8.GetBytes(empty!.Text), Encoding.UTF8.GetBytes(registered!.Text));
        Assert.Equal(empty.Sources, registered.Sources);
        Assert.Null(registered.Roots);
    }

    [Fact]
    public void BrokenUnusedEntries_DoNotBlockOtherCalls()
    {
        var broken = new Dictionary<string, string>
        {
            ["missing"] = Path.Combine(_outside, "no-such-folder"),
            ["relative"] = "relative/path",
            ["x"] = _extra,              // 이름 규칙 위반 (1자)
        };
        var (result, error) = Load(broken, MainRange);

        Assert.Null(error);
        Assert.Equal(ExpectedMainAttachment(), result!.Text);
    }

    [Fact]
    public void Empty_AtPrefix_IsPlainPathAsBefore()
    {
        // 등록되지 않은 이름이면 메인 루트 기준 상대 경로로 처리된다 (기존 동작).
        Assert.Equal("'@orch/src/B.cs': 파일이 없습니다.", Load([], new ContextRange("@orch/src/B.cs", 1, 2)).Error);
    }

    // ── 등록한 루트 ─────────────────────────────────────────────────────

    [Fact]
    public void RegisteredRoot_ReadsFile_AndHidesAbsolutePath()
    {
        var (result, error) = Load(new() { ["orch"] = _extra }, MainRange, new ContextRange("@orch/src/B.cs", 1, 2));

        Assert.Null(error);
        Assert.Equal(["Assets/A.cs:1-3", "@orch/src/B.cs:1-2"], result!.Sources);
        Assert.Contains($"### @orch/src/B.cs:1-2{Environment.NewLine}", result.Text);
        Assert.Contains("1| // B", result.Text);
        Assert.DoesNotContain(_extra, result.Text);                 // 에이전트 입력에 로컬 절대 경로 없음
        Assert.DoesNotContain(_extra.Replace('\\', '/'), result.Text);
        Assert.Equal(new Dictionary<string, string> { ["orch"] = _extra }, result.Roots);
    }

    [Fact]
    public void RootName_IsCaseInsensitive_AndShownAsRegistered()
    {
        var (result, error) = Load(new() { ["Orch"] = _extra }, new ContextRange("@ORCH/src\\B.cs", 1, 1));

        Assert.Null(error);
        Assert.Equal(["@Orch/src/B.cs:1-1"], result!.Sources);
        Assert.Equal(["Orch"], result.Roots!.Keys);
    }

    [Fact]
    public void Binding_KeepsCaseInsensitiveLookup()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            Orchestration = new { Context = new { ExtraRoots = new Dictionary<string, string> { ["Orch"] = _extra } } },
        });
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var options = config.GetSection("Orchestration").Get<OrchestrationOptions>()!;

        Assert.True(options.Context.TryGetExtraRoot("orch", out var key, out _));
        Assert.Equal("Orch", key);
        Assert.Empty(new OrchestrationOptions().Context.ExtraRoots);   // 기본은 비어 있음
    }

    [Fact]
    public void RegisteredRoot_OutsideIsRejected()
    {
        var name = Path.GetFileName(_outside);
        var error = Load(new() { ["orch"] = _extra }, new ContextRange($"@orch/../{name}/C.cs", 1, 1)).Error;
        Assert.Equal($"'@orch/../{name}/C.cs': 프로젝트 루트 밖입니다.", error);
    }

    [Fact]
    public void RegisteredRoot_DenyPatternsApply()
    {
        Assert.StartsWith("'@orch/.env': 외부 전송 금지 파일입니다", Load(new() { ["orch"] = _extra }, new ContextRange("@orch/.env", 1, 1)).Error);
    }

    [Fact]
    public void RegisteredRoot_LinkIsRejected()
    {
        // junction은 관리자 권한 없이 만들 수 있다.
        var link = Path.Combine(_extra, "linked");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(link)), "junction을 만들지 못했습니다");

        var error = Load(new() { ["orch"] = _extra }, new ContextRange("@orch/linked/C.cs", 1, 1)).Error;
        Assert.Equal("'@orch/linked/C.cs': 심볼릭 링크·junction을 거치는 경로는 받지 않습니다.", error);

        Directory.Delete(link); // junction만 지운다 (대상 폴더는 그대로)
    }

    [Theory]
    [InlineData("relative/path", "절대 경로가 아닙니다.")]
    [InlineData(null, "폴더가 없습니다.")]
    public void ReferencedBrokenRoot_IsRejected(string? rootValue, string problem)
    {
        var root = rootValue ?? Path.Combine(_outside, "no-such-folder");
        var error = Load(new() { ["orch"] = root }, new ContextRange("@orch/src/B.cs", 1, 1)).Error;
        Assert.Equal($"'@orch/src/B.cs': ExtraRoots 'orch' — {problem}", error);
    }

    // ── 기록 · save_result ──────────────────────────────────────────────

    [Fact]
    public async Task ContextRoots_IsOmitted_WhenNotUsed()
    {
        var log = Path.Combine(_main, "calls.jsonl");
        await CallLog.AppendAsync(Record(null), log);
        await CallLog.AppendAsync(Record(new Dictionary<string, string> { ["orch"] = _extra }), log);

        var lines = File.ReadAllLines(log);
        Assert.DoesNotContain("contextRoots", lines[0]);
        Assert.Contains("\"contextRoots\":{\"orch\":", lines[1]);
        Assert.Equal(_extra, CallLog.ReadAll(log)[0].ContextRoots!["orch"]);   // 최신순: 두 번째 기록
    }

    private CallRecord Record(IReadOnlyDictionary<string, string>? roots) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = DateTimeOffset.Now, Tool = "review", Model = "m", Prompt = "p",
        ProjectDir = _main, Success = true, Response = "ok", ContextRoots = roots,
    };

    [Theory]
    [InlineData("@orch/out.md")]
    [InlineData("@notregistered/out.md")]
    public async Task SaveResult_RejectsExtraRootPaths(string path)
    {
        var log = Path.Combine(_main, "save.jsonl");
        await CallLog.AppendAsync(Record(null) with { RunId = "r1", Step = 1 }, log);

        var result = ResultSaver.Save("r1", 1, path, null, false, ContextOptions.BuiltInDenyPatterns, log);
        Assert.StartsWith($"[저장 불가] '{path}': 추가 루트(@이름)에는 쓸 수 없습니다", result);
        Assert.False(Directory.Exists(Path.Combine(_main, path.Split('/')[0])));
    }
}
