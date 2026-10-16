using System.Text;
using HB.Orchestration.Tests.Fakes;

namespace HB.Orchestration.Tests;

/// <summary>
/// 5장 B: run_mode의 reason(모든 모드 필수) · ownReview(RequireOwnReview 모드 필수).
/// 없으면 에이전트 호출 전에 '['로 거절하고 실행 횟수는 그대로. 두 값은 에이전트에게 가지 않고 기록에만 원문 그대로 남는다.
/// </summary>
public sealed class ConductorNotesTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    private void AssertRejectedWithoutCost(string result, string mode, string prefix)
    {
        Assert.StartsWith(prefix, result);
        Assert.Equal(0, _h.Sessions.Get(mode));
        Assert.Equal(0, _h.Client.Calls);
        Assert.Empty(_h.Records());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public async Task MissingReason_Rejected_CountUnchanged(string? reason) =>
        AssertRejectedWithoutCost(await _h.Run("seq", reason: reason), "seq", "[reason 필요]");

    [Fact]
    public async Task ReviewMode_MissingOwnReview_Rejected_CountUnchanged() =>
        AssertRejectedWithoutCost(await _h.Run("rev", ownReview: " "), "rev", "[ownReview 필요]");

    [Fact]
    public async Task OtherMode_WithoutOwnReview_Succeeds()
    {
        var result = await _h.Run("seq");
        Assert.DoesNotContain("[ownReview", result);
        Assert.Equal(1, _h.Sessions.Get("seq"));
        Assert.Null(_h.Records().Single().OwnReview);
    }

    [Fact]
    public async Task ReasonTooLong_Rejected() =>
        AssertRejectedWithoutCost(await _h.Run("seq", reason: new string('가', 501)), "seq", "[reason이 너무 깁니다]");

    [Fact]
    public async Task OwnReviewTooLong_Rejected() =>
        AssertRejectedWithoutCost(await _h.Run("rev", ownReview: new string('a', 30_001)), "rev", "[ownReview가 너무 깁니다]");

    [Fact]
    public async Task Limits_AtBoundary_Accepted()
    {
        await _h.Run("rev", reason: new string('가', 500), ownReview: new string('a', 30_000));
        Assert.Equal(1, _h.Sessions.Get("rev"));
    }

    [Fact]
    public async Task Limits_ZeroMeansUnlimited()
    {
        _h.Options.Records.MaxReasonChars = 0;
        await _h.Run("seq", reason: new string('가', 2_000));
        Assert.Equal(1, _h.Sessions.Get("seq"));
    }

    [Theory]
    [InlineData("rev")]   // Sequential 2단계 (2단계는 앞 단계 결과도 본다)
    [InlineData("crev")]  // Concurrent
    [InlineData("deb")]   // Debate 토론자 + 판정자
    public async Task NotesNeverReachAgents_AndAreRecordedVerbatim(string mode)
    {
        const string reason = "REASON-7f3a 부른 이유: 스레드 변경";
        const string ownReview = "OWNREVIEW-91c2\n- [major] 자체 검토 원문 \"따옴표\" <태그> \\ 백슬래시\n  들여쓰기 유지  ";

        await _h.Run(mode, reason: reason, ownReview: ownReview);

        Assert.NotEmpty(_h.Client.Received);
        foreach (var received in _h.Client.Received)
        {
            var all = ScriptedChatClient.SystemText(received) + "\n" + ScriptedChatClient.UserText(received);
            Assert.DoesNotContain("REASON-7f3a", all);
            Assert.DoesNotContain("OWNREVIEW-91c2", all);
            Assert.DoesNotContain("부른 이유", all);
        }

        var records = _h.Records();
        Assert.NotEmpty(records);
        foreach (var r in records)
        {
            Assert.DoesNotContain("REASON-7f3a", r.Prompt);
            Assert.DoesNotContain("OWNREVIEW-91c2", r.Prompt);
            // 원문 그대로 (공백·줄바꿈·특수 문자 유지)
            Assert.Equal(Encoding.UTF8.GetBytes(reason), Encoding.UTF8.GetBytes(r.Reason!));
            Assert.Equal(Encoding.UTF8.GetBytes(ownReview), Encoding.UTF8.GetBytes(r.OwnReview!));
        }

        // 원문 저장 폴더(시스템 프롬프트 · 지침 · modes)에도 없다
        foreach (var blob in Directory.GetFiles(_h.DefaultBlobFolder))
        {
            var text = File.ReadAllText(blob);
            Assert.DoesNotContain("REASON-7f3a", text);
            Assert.DoesNotContain("OWNREVIEW-91c2", text);
        }
    }

    [Fact]
    public void ListModes_MarksRequiredOwnReview()
    {
        var lines = _h.Runner.Describe().Split('\n');
        Assert.EndsWith("[ownReview 필수]", lines.Single(l => l.StartsWith("- rev:")).TrimEnd());
        Assert.DoesNotContain("ownReview", lines.Single(l => l.StartsWith("- seq:")));
    }
}
