using System.Text;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HB.Orchestration.Tests;

/// <summary>
/// 응답 검사기. 언어 검사는 3장의 실제 응답으로 확인한다:
/// cross_p1 / Cross_P1_A(run 3169f4a54e)는 코드 블록 뒤부터 일본어로 바뀐 응답, cross_p2 / Cross_P2_A(run d49d0cf935)는 정상 한국어 응답.
/// </summary>
public class ResponseCheckerTests
{
    private static readonly ResponseChecksOptions Ko = new();

    private static string Data(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name), Encoding.UTF8);

    private static bool HasLanguageWarning(IReadOnlyList<string> warnings) =>
        warnings.Any(w => w.StartsWith("[경고] 응답 언어 이탈 감지", StringComparison.Ordinal));

    [Fact]
    public void Ch3_JapaneseDrift_IsWarned()
    {
        var warnings = ResponseChecker.Warnings(Data("ch3_cross_p1_A_japanese_drift.md"), ChatFinishReason.Stop, 7000, Ko);
        Assert.True(HasLanguageWarning(warnings), string.Join(" / ", warnings));
        Assert.Contains("[경고] 응답 언어 이탈 감지 (가나 비율 10.7%, 308자)", warnings);
    }

    [Fact]
    public void Ch3_KoreanResponse_HasNoWarning()
    {
        Assert.Empty(ResponseChecker.Warnings(Data("ch3_cross_p2_A_korean.md"), ChatFinishReason.Stop, 5000, Ko));
    }

    [Fact]
    public void JapaneseOnlyInCodeBlock_IsNotWarned()
    {
        // 코드 블록의 일본어 주석과 인라인 코드의 일본어 식별자는 응답 언어가 아니다.
        const string text = """
            ## 1. 지적 목록
            `カウント` 필드를 여러 스레드가 동시에 증가시키므로 결과가 틀어질 수 있습니다.

            ```csharp
            // スレッドセーフではないカウンター。ここで競合が発生する。
            // 修正案: Interlocked.Increment を使う。
            cellCounts[cell]++; // ここで値が失われる
            ```

            ~~~
            // 別のフェンスでもコメントは日本語のまま
            ~~~

            수정은 `Interlocked.Increment`로 바꾸는 것이 가장 단순합니다. 검증은 병렬 테스트로 합니다.
            """;

        Assert.Empty(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 300, Ko));
    }

    [Fact]
    public void UnclosedFence_ExcludesRestOfText()
    {
        const string text = "한국어 본문입니다. 충분히 긴 설명을 붙입니다.\n```\n// 閉じていないフェンスの中身はすべてコード扱い\n";
        Assert.Empty(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 50, Ko));
    }

    [Fact]
    public void FewKana_BelowMinimum_IsNotWarned()
    {
        // 가나 4자: 비율은 높아도 최소 글자 수(5) 미만이면 고유명사 수준이라 경고하지 않는다.
        Assert.Empty(ResponseChecker.Warnings("짧은 응답 カタカ 입니다", ChatFinishReason.Stop, 10, Ko));
    }

    [Fact]
    public void LanguageCheck_Off_WhenExpectedLanguageEmpty()
    {
        var off = new ResponseChecksOptions { ExpectedLanguage = "" };
        Assert.Empty(ResponseChecker.Warnings(Data("ch3_cross_p1_A_japanese_drift.md"), ChatFinishReason.Stop, 7000, off));
    }

    [Fact]
    public void UnsupportedLanguage_SkipsLanguageCheck()
    {
        var en = new ResponseChecksOptions { ExpectedLanguage = "en" };
        Assert.False(ResponseChecker.IsSupportedLanguage("en"));
        Assert.Empty(ResponseChecker.Warnings(Data("ch3_cross_p1_A_japanese_drift.md"), ChatFinishReason.Stop, 7000, en));
    }

    [Fact]
    public void KatakanaMiddleDot_IsNotCountedAsKana()
    {
        // 한국어 문서에도 쓰이는 '・'(U+30FB)는 가타카나 블록이지만 문장 부호라 세지 않는다.
        Assert.Equal((0, 4), ResponseChecker.CountKana("가나・다라"));
    }

    [Fact]
    public void Truncated_IsWarned()
    {
        var warnings = ResponseChecker.Warnings("한국어 응답인데 중간에서", ChatFinishReason.Length, 12000, Ko);
        Assert.Contains(warnings, w => w.StartsWith("[경고] 출력 상한에서 잘림", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyResponse_StillFails_WithLimitHint()
    {
        var agent = new AgentOptions { Name = "a", MaxOutputTokens = 10000 };
        Assert.StartsWith("[빈 응답] 출력 토큰 상한(10,000)", ResponseChecker.EmptyFailure("", ChatFinishReason.Length, 3000, agent));
        Assert.StartsWith("[빈 응답] 에이전트가 내용 없이", ResponseChecker.EmptyFailure(" ", ChatFinishReason.Stop, 10, agent));
        Assert.Null(ResponseChecker.EmptyFailure("내용", ChatFinishReason.Stop, 10, agent));
    }

    [Theory]
    [InlineData("""{"Orchestration":{}}""", "ko")]                                          // 키 없음 → 기본 ko (검사 켬)
    [InlineData("""{"Orchestration":{"ResponseChecks":{}}}""", "ko")]
    [InlineData("""{"Orchestration":{"ResponseChecks":{"ExpectedLanguage":""}}}""", "")]  // "" → 검사 끔
    public void ExpectedLanguage_Binding(string json, string expected)
    {
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var options = config.GetSection("Orchestration").Get<OrchestrationOptions>() ?? new OrchestrationOptions();
        Assert.Equal(expected, options.ResponseChecks.ExpectedLanguage ?? "");
    }
}

/// <summary>검사기가 실행 경로에 붙었는지: 경고가 기록·결과에 남고, 응답은 그대로 성공으로 전달된다.</summary>
public sealed class AgentRunnerCheckTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"hb-orch-test-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_log)) File.Delete(_log);
    }

    private async Task<StepResult> Run(ScriptedChatClient client)
    {
        var runner = new AgentRunner(NullLogger<AgentRunner>.Instance, (_, _) => Task.CompletedTask, _log);
        var call = new StepCall("run0000002", "review", 1, "Reviewer",
            new AgentEntry(new AgentOptions { Name = "fake", Provider = "openai", Model = "m" }, client),
            "x", "p", new CallMeta(null, []));
        return await runner.RunStepAsync(call, CancellationToken.None);
    }

    [Fact]
    public async Task Warnings_AreRecorded_AndResponseIsDelivered()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ch3_cross_p1_A_japanese_drift.md"));
        var result = await Run(new ScriptedChatClient(ScriptedChatClient.Reply(text, ChatFinishReason.Length)));

        Assert.True(result.Success);
        Assert.Equal(text, result.Text);   // 응답은 바꾸지 않는다
        Assert.Equal(2, result.Warnings.Count);
        Assert.Equal(result.Warnings, result.Notices);

        var record = Assert.Single(CallLog.ReadAll(_log));
        Assert.True(record.Success);
        Assert.Equal(result.Warnings, record.Warnings);
    }

    [Fact]
    public async Task NormalResponse_HasNoWarnings()
    {
        var result = await Run(new ScriptedChatClient(ScriptedChatClient.Reply("문제 없습니다. 근거는 다음과 같습니다.")));
        Assert.Empty(result.Warnings);
        Assert.Empty(Assert.Single(CallLog.ReadAll(_log)).Warnings);
    }
}
