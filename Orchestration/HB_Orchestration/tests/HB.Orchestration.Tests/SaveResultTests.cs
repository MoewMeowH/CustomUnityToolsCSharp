using System.Security.Cryptography;
using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>save_result 경로 규칙 (지시서 기준: ../ · 절대 경로 거절, 루트 안 정상 경로 성공).</summary>
public sealed class SafePathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-orch-root-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (string? Full, string? Error) Write(string path) =>
        SafePath.Resolve(path, _root, ContextOptions.BuiltInDenyPatterns, SafePath.Access.Write);

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("docs/../../outside.md")]
    [InlineData("..\\outside.md")]
    public void ParentTraversal_IsRejected(string path)
    {
        Assert.Equal("프로젝트 루트 밖입니다.", Write(path).Error);
    }

    [Fact]
    public void AbsolutePath_IsRejected()
    {
        Assert.Equal("프로젝트 루트 기준 상대 경로만 받습니다.", Write(Path.Combine(_root, "a.md")).Error);
        Assert.Equal("프로젝트 루트 기준 상대 경로만 받습니다.", Write("C:\\temp\\a.md").Error);
    }

    [Fact]
    public void PathInsideRoot_IsAccepted_EvenIfFoldersDoNotExistYet()
    {
        var (full, error) = Write("cross_test/new_folder/result.md");
        Assert.Null(error);
        Assert.Equal(Path.Combine(_root, "cross_test", "new_folder", "result.md"), full);
    }

    [Fact]
    public void DenyPattern_IsRejected_ForWrite()
    {
        Assert.StartsWith("보호된 파일이라 쓸 수 없습니다", Write(".env").Error);
    }

    [Fact]
    public void ExistingFolder_IsRejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Assert.Equal("파일이 아니라 폴더입니다.", Write("docs").Error);
    }

    [Fact]
    public void DriveRoot_AsProjectRoot_AcceptsChildPath()
    {
        // 2-3 review: 루트가 "C:\"면 구분자를 한 번 더 붙여 "C:\\"와 비교해 모든 경로를 거절하던 문제. (쓰기는 하지 않음)
        var driveRoot = Path.GetPathRoot(_root)!;
        var (full, error) = SafePath.Resolve($"hb-orch-none-{Guid.NewGuid():N}/a.md", driveRoot, [], SafePath.Access.Write);
        Assert.Null(error);
        Assert.StartsWith(driveRoot, full);
        // 드라이브 루트 위로는 올라갈 수 없어 "../a.md"는 "C:\a.md"로 정규화된다 (루트 안).
        Assert.Equal(Path.Combine(driveRoot, "a.md"), SafePath.Resolve("../a.md", driveRoot, [], SafePath.Access.Write).FullPath);
    }

    [Fact]
    public void Read_RequiresExistingFile()
    {
        Assert.Equal("파일이 없습니다.", SafePath.Resolve("none.cs", _root, [], SafePath.Access.Read).Error);
    }
}

/// <summary>save_result: 기록 선택, 원문 그대로 저장, SHA-256 일치, 코드 블록, 덮어쓰기.</summary>
public sealed class ResultSaverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-orch-root-").FullName;
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"hb-orch-test-{Guid.NewGuid():N}.jsonl");

    // 줄바꿈 혼합 · 한글 · '<' 를 넣어 원문이 그대로 저장되는지 본다 (2-5 &lt; 조사와 같은 문자).
    private const string Response = "## 결과\r\nList<int> 비교: a < b\n\n```csharp\nvar x = new List<int>();\r\n// 주석\n```\n\n~~~\nsecond block\n~~~\n";

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        if (File.Exists(_log)) File.Delete(_log);
    }

    private async Task Log(int step, int attempt, bool success, string? response = Response, string runId = "run000000a")
    {
        await CallLog.AppendAsync(new CallRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.Now,
            Tool = "review", RunId = runId, Mode = "review", Step = step, Attempt = attempt,
            Agent = "gpt_main_sol", Model = "m", Prompt = "p",
            ProjectDir = _root,
            Response = success ? response : null,
            Success = success,
            Error = success ? null : "[실패] Timeout",
        }, _log);
    }

    private string Save(string path, int step = 1, int? codeBlock = null, bool overwrite = false, string runId = "run000000a") =>
        ResultSaver.Save(runId, step, path, codeBlock, overwrite, ContextOptions.BuiltInDenyPatterns, _log);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task SavesResponseVerbatim_AndHashMatchesRecord()
    {
        await Log(1, 1, true);

        var result = Save("cross_test/review.md");

        var file = Path.Combine(_root, "cross_test", "review.md");
        var bytes = File.ReadAllBytes(file);
        var expected = Encoding.UTF8.GetBytes(Response);
        Assert.Equal(expected, bytes);                       // BOM 없음, 줄바꿈·'<' 그대로
        Assert.StartsWith($"저장: cross_test/review.md ({expected.Length:N0}바이트, SHA-256 {Sha(expected)}", result);
    }

    [Fact]
    public async Task UsesSuccessfulAttempt_WhenStepWasRetried()
    {
        await Log(1, 1, false);
        await Log(1, 2, true, "두 번째 시도의 응답");

        Assert.StartsWith("저장:", Save("out.md"));
        Assert.Equal("두 번째 시도의 응답", File.ReadAllText(Path.Combine(_root, "out.md")));
    }

    [Fact]
    public async Task CodeBlock_SavesOnlyThatBlock()
    {
        await Log(1, 1, true);

        Assert.Contains("코드 블록 1/2", Save("a.cs", codeBlock: 1));
        Assert.Equal("var x = new List<int>();\r\n// 주석\n", File.ReadAllText(Path.Combine(_root, "a.cs")));

        Save("b.txt", codeBlock: 2);
        Assert.Equal("second block\n", File.ReadAllText(Path.Combine(_root, "b.txt")));

        Assert.Equal("[저장 불가] 응답에 코드 블록이 2개라 3번째가 없습니다.", Save("c.txt", codeBlock: 3));
    }

    [Fact]
    public async Task ExistingFile_RequiresOverwrite()
    {
        await Log(1, 1, true);
        File.WriteAllText(Path.Combine(_root, "keep.md"), "원래 내용");

        Assert.StartsWith("[저장 불가] 'keep.md'이(가) 이미 있습니다", Save("keep.md"));
        Assert.Equal("원래 내용", File.ReadAllText(Path.Combine(_root, "keep.md")));

        Assert.StartsWith("저장:", Save("keep.md", overwrite: true));
        Assert.Equal(Response, File.ReadAllText(Path.Combine(_root, "keep.md")));
    }

    [Fact]
    public async Task RejectsPathOutsideRoot_BeforeWriting()
    {
        await Log(1, 1, true);
        Assert.Equal("[저장 불가] '../x.md': 프로젝트 루트 밖입니다.", Save("../x.md"));
        Assert.StartsWith("[저장 불가] '", Save(Path.Combine(_root, "abs.md")));
        Assert.False(File.Exists(Path.Combine(_root, "abs.md")));
    }

    [Fact]
    public async Task UnknownRunOrFailedStep_IsReported()
    {
        await Log(1, 1, false);
        await Log(1, 2, false);

        Assert.StartsWith("[저장 불가] runId 'nope' 단계 1의 기록이 없습니다", Save("x.md", runId: "nope"));
        Assert.StartsWith("[저장 불가] runId 'run000000a' 단계 1에 성공한 시도가 없습니다 (시도 2회 모두 실패)", Save("x.md"));
        Assert.StartsWith("[저장 불가] runId 'run000000a' 단계 2의 기록이 없습니다", Save("x.md", step: 2));
    }
}

public class CodeFencesTests
{
    [Fact]
    public void LongerFence_IsNotClosedByShorterInnerFence()
    {
        const string text = "앞\n````markdown\n```csharp\ninner\n```\n````\n뒤\n";
        var blocks = CodeFences.Find(text);
        Assert.Single(blocks);
        Assert.Equal("```csharp\ninner\n```\n", CodeFences.Content(text, 1));
        Assert.Equal("앞\n뒤\n", CodeFences.WithoutBlocks(text));
    }

    [Fact]
    public void UnclosedFence_RunsToEnd()
    {
        Assert.Equal("a\nb", CodeFences.Content("x\n```\na\nb", 1));
    }

    [Fact]
    public void FenceWithInfoString_DoesNotClose()
    {
        // 닫는 펜스는 펜스 문자만 있어야 한다. 안에 "```js"가 있어도 닫히지 않는다.
        const string text = "```\na\n```js\nb\n```\n";
        Assert.Equal("a\n```js\nb\n", CodeFences.Content(text, 1));
    }
}

/// <summary>run_mode 결과 머리말 (runId, 단계 제목의 에이전트·토큰·시간, 재시도·경고 줄).</summary>
public class ResultFormatterTests
{
    private static StepResult Step(int n, bool ok = true, string text = "본문", IReadOnlyList<string>? failedKinds = null, IReadOnlyList<string>? warnings = null) =>
        new(n, $"Role{n}", "gpt_main_sol", ok, text)
        {
            InputTokens = 15551, OutputTokens = 7502, ElapsedMs = 144_000,
            Attempts = 1 + (failedKinds?.Count ?? 0), FailedKinds = failedKinds ?? [], Warnings = warnings ?? [],
        };

    [Fact]
    public void Header_HasRunIdAndStepTitle()
    {
        var text = ResultFormatter.Format("dfa393514b", [Step(1)], returnAll: true, note: null);
        Assert.Equal("runId: dfa393514b\n### 1. Role1 · gpt_main_sol (입력 15,551 / 출력 7,502, 144.0초)\n본문", text);
    }

    [Fact]
    public void Headroom_line_goes_right_after_run_id()   // 계획서 2026-10-27_외부-첨부-범위-규칙
    {
        var header = ResultFormatter.AttachmentHeadroom(12_000, 3_000, 350_000);
        Assert.Equal("첨부 12,000자 + input 3,000자 = 15,000자 / 상한 350,000자 (4%)", header);
        Assert.Equal("첨부 0자 + input 10자 = 10자 / 상한 100자 (10%)", ResultFormatter.AttachmentHeadroom(0, 10, 100));
        Assert.Null(ResultFormatter.AttachmentHeadroom(5, 5, 0));                                  // 상한 없음
        Assert.Equal("첨부 50자 + input 0자 = 50자 / 상한 100자 (50%)", ResultFormatter.AttachmentHeadroom(50, 0, 100));   // 절반까지는 경고 없음
        Assert.Equal("첨부 995자 + input 0자 = 995자 / 상한 1,000자 (99%) · followUp 여유 적음 (원래 프롬프트 + 응답 + 보충이 같은 상한)",
            ResultFormatter.AttachmentHeadroom(995, 0, 1000));                                     // 내림 · 절반 넘으면 followUp 경고 (sub_self 11b9c52eba C3 · C8)

        var lines = ResultFormatter.Format("dfa393514b", [Step(1)], returnAll: true, note: null, header).Split('\n');
        Assert.Equal(["runId: dfa393514b", header!], lines[..2]);                                 // 첫 줄은 늘 runId만
        Assert.StartsWith("### 1. Role1", lines[2]);
    }

    [Fact]
    public void LastMode_ShowsLastStepWithHeader_AndHiddenNoticesAtEnd()
    {
        var results = new[] { Step(1, failedKinds: ["Timeout"]), Step(2, text: "최종") };
        var text = ResultFormatter.Format("r", results, returnAll: false, note: null);

        Assert.StartsWith("runId: r\n### 2. Role2", text);
        Assert.DoesNotContain("### 1.", text);
        Assert.EndsWith("최종\n\n단계 1 (재시도 1회 후 성공 · 1회차 Timeout)", text);
    }

    [Fact]
    public void LastMode_FailureAfterRetriedStep_KeepsEarlierRetryNote()
    {
        // 2-2 review R2: 앞 단계가 재시도 후 성공하고 뒤 단계가 실패해도 앞 단계 알림이 남아야 한다.
        var results = new[] { Step(1, failedKinds: ["Server"]), Step(2, ok: false, text: "[실패] Client 402 · …") };
        var text = ResultFormatter.Format("r", results, returnAll: false, note: "(단계 2에서 실패해 이후 0단계는 실행하지 않음)");

        Assert.Contains("### 2. Role2", text);
        Assert.Contains("단계 1 (재시도 1회 후 성공 · 1회차 Server)", text);
    }

    [Fact]
    public void Notices_AreUnderTitle_WhenStepIsShown()
    {
        var results = new[] { Step(1, failedKinds: ["Timeout"], warnings: ["[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)"]) };
        var lines = ResultFormatter.Format("r", results, returnAll: true, note: null).Split('\n');

        Assert.Equal("(재시도 1회 후 성공 · 1회차 Timeout)", lines[2]);
        Assert.Equal("[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)", lines[3]);
        Assert.Equal("본문", lines[4]);
    }

    [Fact]
    public void MissingTokens_ShowDash()
    {
        var r = new StepResult(1, "R", "a", false, "[실패]") { ElapsedMs = 5_000 };
        Assert.Equal("### 1. R · a (입력 - / 출력 -, 5.0초)", ResultFormatter.Title(r));
    }

    [Fact]
    public void ReturnAll_ShowsFailedStepNextToSucceededOne()
    {
        // review_high처럼 동시 단계 중 하나만 실패하면 반쪽 결과가 조용히 남지 않고 실패 단계도 보여야 한다
        var results = new[]
        {
            new StepResult(1, "Reviewer", "gpt_main_sol", true, "지적"),
            new StepResult(2, "Reviewer", "deepseek_main_flash", false, "[실패] Client 400 · context length exceeded"),
        };
        var text = ResultFormatter.Format("r", results, returnAll: true, note: null);

        Assert.Contains("### 1. Reviewer · gpt_main_sol", text);
        Assert.Contains("### 2. Reviewer · deepseek_main_flash", text);
        Assert.Contains("[실패] Client 400 · context length exceeded", text);
    }
}
