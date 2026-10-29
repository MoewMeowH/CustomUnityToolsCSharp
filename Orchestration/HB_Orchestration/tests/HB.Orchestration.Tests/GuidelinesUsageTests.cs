using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>대시보드 "최근 호출이 붙인 리뷰 지침" 경고 (경로 규칙).</summary>
public sealed class GuidelinesUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(9));

    private static CallRecord Call(GuidelinesProvenance? g, double daysAgo, string? runId, string? projectDir = null) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = Now.AddDays(-daysAgo), Tool = "run_mode", Agent = "a", Model = "m", Prompt = "p",
        RunId = runId, ProjectDir = projectDir,
        Provenance = g is null ? null : new CallProvenance { Guidelines = g },
    };

    private static GuidelinesProvenance File(string path, string sha) => new() { Attached = true, Path = path, Sha256 = sha };
    private static GuidelinesProvenance Off(string reason) => new() { Attached = false, Reason = reason };

    [Theory]
    [InlineData(@"F:\AI\ToolWithCS\REVIEW_GUIDELINES.md", null)]
    [InlineData(@"F:\AI\ToolCreate\review_guidelines.md", null)]                     // 대소문자 무시
    [InlineData("/home/u/proj/REVIEW_GUIDELINES.md", null)]
    [InlineData(@"F:\AI\ToolWithCS\review_tmp\harness\guidelines.dom.md", "review_tmp/ 아래")]
    [InlineData(@"F:\AI\ToolWithCS\review_tmp\harness\REVIEW_GUIDELINES.md", "review_tmp/ 아래")] // 이름이 같아도 임시 폴더면 경고
    [InlineData(@"F:\AI\ToolWithCS\docs\guidelines-v2.md", "이름이 REVIEW_GUIDELINES.md 아님")]
    [InlineData(@"F:\AI\not_review_tmp\REVIEW_GUIDELINES.md", null)]                // 폴더 이름 부분 일치는 아님
    public void Warning_follows_path_rule(string path, string? expected) =>
        Assert.Equal(expected, GuidelinesUsage.Warning(path));

    [Theory]
    [InlineData(true, null)]                                // Windows: 같은 파일
    [InlineData(false, "이름이 REVIEW_GUIDELINES.md 아님")]  // 다른 OS: 기본 탐색이 못 찾는 다른 파일 = 지정 실수
    public void Warning_name_case_follows_os(bool ignoreCase, string? expected) =>
        Assert.Equal(expected, GuidelinesUsage.Warning("/home/u/proj/review_guidelines.md", ignoreCase));

    [Fact]
    public void Recent_groups_by_path_and_sha_and_does_not_compare_content_across_projects()
    {
        var main = @"F:\AI\ToolWithCS\REVIEW_GUIDELINES.md";
        var other = @"F:\AI\ToolCreate\REVIEW_GUIDELINES.md";
        var temp = @"F:\AI\ToolWithCS\review_tmp\harness\guidelines.dom.md";
        var uses = GuidelinesUsage.Recent(
        [
            Call(File(main, "aa"), 1, "r1"), Call(File(main, "aa"), 1, "r1"), Call(File(main.ToUpperInvariant(), "AA"), 2, "r2"),
            Call(File(main, "bb"), 3, "r3"),
            Call(File(other, "cc"), 1, "r4"),           // 다른 프로젝트 · 다른 내용 — 경고 아님
            Call(File(temp, "dd"), 0.5, "r5"),
            Call(File(temp, "dd"), 8, "old"),           // 7일 밖
            Call(Off(GuidelinesProvenance.ReasonNoFile), 1, "r6"),  // 붙이지 않음은 Missing 쪽
            Call(null, 0.1, "noprov"),
        ], Now, ModesUsage.Window);

        Assert.Equal(4, uses.Count);
        var mainAa = Assert.Single(uses, u => u.Sha256 == "aa");
        Assert.Equal((3, 2), (mainAa.Calls, mainAa.Runs));
        Assert.All(uses.Where(u => u.Path != temp), u => Assert.Null(u.Warning));
        var t = Assert.Single(uses, u => u.Warning is not null);
        Assert.Equal((temp, 1), (t.Path, t.Runs));
        Assert.Equal(temp, uses[0].Path); // 마지막 사용이 최근인 순
    }

    [Fact]
    public void Missing_counts_only_no_file_and_groups_by_project()
    {
        var missing = GuidelinesUsage.Missing(
        [
            Call(Off(GuidelinesProvenance.ReasonNoFile), 1, "r1", @"F:\AI\ToolWithCS\GoldenSet\x\src"),
            Call(Off(GuidelinesProvenance.ReasonNoFile), 1, "r1", @"F:\AI\ToolWithCS\GoldenSet\x\src"),
            Call(Off(GuidelinesProvenance.ReasonNoFile), 2, "r2", @"f:\ai\toolwithcs\goldenset\x\src"),
            Call(Off(GuidelinesProvenance.ReasonNoFile), 3, "r3"),                       // 프로젝트 폴더 기록 없음
            Call(Off(GuidelinesProvenance.ReasonRoleDisabled), 1, "r4", @"F:\AI\A"),      // 일부러 끔
            Call(Off(GuidelinesProvenance.ReasonGoldenSkipped), 1, "r5", @"F:\AI\A"),     // 일부러 끔
            Call(Off(GuidelinesProvenance.ReasonNoFile), 9, "old", @"F:\AI\B"),           // 7일 밖
            Call(File(@"F:\AI\A\REVIEW_GUIDELINES.md", "aa"), 1, "r6", @"F:\AI\A"),
        ], Now, ModesUsage.Window);

        Assert.Equal(2, missing.Count);
        var golden = Assert.Single(missing, m => m.ProjectDir is not null);
        Assert.Equal((3, 2), (golden.Calls, golden.Runs));
        Assert.Contains(missing, m => m.ProjectDir is null && m.Runs == 1);
    }
}
