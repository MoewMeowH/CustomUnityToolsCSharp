using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>5장 D: 호출 상세의 지침·설정 표와 원문 열기 허용 범위. 실제 실행으로 만든 기록을 쓴다.</summary>
public sealed class ProvenanceViewTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Rows_ShowHashes_AndBlobAvailability()
    {
        await _h.Run("seq");
        var p = _h.Records().Single().Provenance!;

        var rows = ProvenanceView.Rows(p);

        Assert.Equal(["시스템 프롬프트", "리뷰 지침", "modes.json", "서버 설정"], rows.Select(r => r.Label));
        Assert.True(rows[0].BlobAvailable);
        Assert.True(rows[1].BlobAvailable);
        Assert.True(rows[2].BlobAvailable);
        Assert.False(rows[3].BlobAvailable);              // 서버 설정은 원문을 저장하지 않는다 (기본)
        Assert.Contains("원문 저장 안 함", rows[3].Note);
        Assert.Contains("키 검사 통과", rows[3].Note);
    }

    [Fact]
    public async Task Rows_BlobMissingOnDisk_NotAvailable()
    {
        await _h.Run("seq");
        var p = _h.Records().Single().Provenance!;
        foreach (var f in Directory.GetFiles(_h.DefaultBlobFolder)) File.Delete(f);

        Assert.All(ProvenanceView.Rows(p), r => Assert.False(r.BlobAvailable));
    }

    [Fact]
    public async Task Rows_GuidelinesNotAttached_ShowsReason()
    {
        await _h.Run("noG");
        var g = ProvenanceView.Rows(_h.Records().Single().Provenance!)[1];
        Assert.Null(g.Sha256);
        Assert.Contains(GuidelinesProvenance.ReasonRoleDisabled, g.Note);
    }

    [Fact]
    public async Task AllowedShas_OnlyThisRecordsBlobs_SettingsOnlyWhenStored()
    {
        await _h.Run("seq");
        var p = _h.Records().Single().Provenance!;

        var allowed = ProvenanceView.AllowedShas(p).ToHashSet();

        Assert.Equal(new HashSet<string> { p.InstructionsSha256!, p.Guidelines!.Sha256!, p.Modes!.Sha256! }, allowed);
        Assert.DoesNotContain(p.Settings!.Sha256!, allowed);
    }

    [Theory]
    [InlineData("../calls")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // 대문자
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678")]  // 63자
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678g")] // hex 아님
    public void InvalidSha_Rejected(string sha) => Assert.False(BlobStore.IsValidSha(sha));
}
