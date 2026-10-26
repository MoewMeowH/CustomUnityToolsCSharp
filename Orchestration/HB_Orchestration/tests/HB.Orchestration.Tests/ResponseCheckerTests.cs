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
        Assert.Equal((0, 0, 4), ResponseChecker.CountScripts("가나・다라"));
    }

    [Fact]
    public void CountScripts_CountsKanaAndHanSeparately()
    {
        // 漢字 2 + 호환 한자 U+F900 1 / カナ 2 / 한글 2 / 영문 2
        var text = "漢字" + (char)0xF900 + " カナ 한글 ab";
        Assert.Equal((2, 3, 9), ResponseChecker.CountScripts(text));
    }

    [Fact]
    public void ChineseDrift_IsWarned()
    {
        // 2026-10-24 perf_main(runId 679d390e34) gpt_main_sol 응답 일부 — 가나 없이 한자만 쓴 중국어
        const string text = """
            - 1-F1 [low] HLogConsoleWindow.cs:577 — 选择项随环形缓冲区淘汰时，会反复分配完整可见集合及捕获闭包。 / 在窗口中复用一个清理用 HashSet，仅在选择减少时 Clear 并填充。 / 확신도(high)
              상세: 事实：选择减少时，此分支会从 filteredLogs 新建 HashSet，并为捕获 visible 的谓词产生分配。
            """;
        var warnings = ResponseChecker.Warnings(text, ChatFinishReason.Stop, 900, Ko);
        Assert.Contains(warnings, w => w.StartsWith("[경고] 응답 언어 이탈 감지 (한자 비율 ", StringComparison.Ordinal));
    }

    [Fact]
    public void JapaneseDrift_ReportsKanaOnly()
    {
        // 가나와 한자가 같이 기준을 넘어도 경고는 가나 한 줄 (일본어)
        var data = Data("ch3_cross_p1_A_japanese_drift.md");
        var (_, han, letters) = ResponseChecker.CountScripts(ResponseChecker.StripCode(data));
        Assert.True(han >= ResponseChecker.MinForeignChars && (double)han / letters >= Ko.EffectiveForeignScriptRatio,
            $"전제: 한자도 기준을 넘어야 한다 (한자 {han} / 글자 {letters})");

        var warnings = ResponseChecker.Warnings(data, ChatFinishReason.Stop, 7000, Ko);
        Assert.Single(warnings, w => w.StartsWith("[경고] 응답 언어 이탈 감지", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("한자 비율", StringComparison.Ordinal));
    }

    [Fact]
    public void FewHan_InKoreanResponse_IsNotWarned()
    {
        // 한자 4자(靜的 · 同期): 최소 글자 수(5) 미만
        Assert.Empty(ResponseChecker.Warnings("이 값은 정적(靜的) 동기(同期) 상태로 남는다", ChatFinishReason.Stop, 20, Ko));

        // 한자 5자(同期化 · 費用)지만 긴 한국어 본문에서 비율이 2% 미만
        var longKorean = string.Concat(Enumerable.Repeat("한국어로 쓴 리뷰 본문입니다. 선택 정리는 필드를 재사용합니다. ", 20)) + "동기화(同期化) 비용(費用)";
        Assert.Empty(ResponseChecker.Warnings(longKorean, ChatFinishReason.Stop, 500, Ko));
    }

    [Fact]
    public void FiveHan_InShortText_IsWarned()
    {
        // 경계: 한자 정확히 5자(靜的 · 同期化)면 최소 글자 수를 채워 경고 (짧아서 비율도 2% 이상)
        var warnings = ResponseChecker.Warnings("이 값은 정적(靜的) 동기화(同期化) 상태로 남는다", ChatFinishReason.Stop, 20, Ko);
        Assert.Contains(warnings, w => w.StartsWith("[경고] 응답 언어 이탈 감지 (한자 비율 ", StringComparison.Ordinal) && w.EndsWith(", 5자)", StringComparison.Ordinal));
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

    // ── 영어 문장 섞임 (계획서 2026-10-27_한국어-응답-지시-강화: sol 5e81a2ed47의 작업 메모) ──

    private static bool HasEnglishWarning(IReadOnlyList<string> warnings) =>
        warnings.Any(w => w.StartsWith("[경고] 영어 문장 섞임", StringComparison.Ordinal));

    [Fact]
    public void EnglishWorkNotes_AreWarned()
    {
        // 5e81a2ed47 Preflight_P3_A 응답에 섞인 작업 메모 두 줄 (원문)
        var text = "## 2. 최종 수정안\n설계안을 정리한다.\n\nBoth need Korean no accidental. continue mentally must not Japanese output. compose normal\n\n"
                 + "Need not generate too long and foreign errors must final clean. We have budget enough actual final ~\n";
        var warnings = ResponseChecker.Warnings(text, ChatFinishReason.Stop, 7018, Ko);
        Assert.True(HasEnglishWarning(warnings), string.Join(" / ", warnings));
        Assert.Contains("(2줄, 예: \"Both need Korean no accidental.", Assert.Single(warnings));
    }

    [Theory]
    [InlineData("Need not generate too long and foreign errors must final clean.\n한국어 본문은 정상이다.")]       // 영어 한 줄 (기준 2줄 미만)
    [InlineData("`NullReferenceException: Object reference not set to an instance` 오류가 난다.\n`Object reference not set to an instance of an object` 도 같다.")] // 인라인 코드
    [InlineData("```\nThis is a long English comment inside a code block here\nAnother long English comment line inside code\n```\n설명은 한국어다.")] // 코드 블록
    [InlineData("이 함수는 Dispose Track Untrack Register Unregister 를 부른다.\nPlayMode EditMode TearDown SetUp OneTimeSetUp 순서다.")] // 한글이 있는 줄
    [InlineData("{\"findings\": [\n{\"title\": \"This is an English title with many words here\"},\n{\"title\": \"Another English title with many words here\"}]}")] // 구조화 출력(JSON)
    [InlineData("경로는 Assets/Tools/HB_TestFrameworkHelper/Editor/Foo.cs 와\nAssets/Tools/HB/Runtime/Bar.cs src/a/b.cs c/d.cs e/f.cs 이다")] // 경로는 낱말 아님
    public void NotEnglishDrift(string text) =>
        Assert.False(HasEnglishWarning(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 100, Ko)));

    [Fact]
    public void EnglishCheck_FollowsExpectedLanguage()
    {
        var text = "Both need Korean no accidental. continue mentally must not\nNeed not generate too long and foreign errors must final clean.\n";
        Assert.False(HasEnglishWarning(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 10, new ResponseChecksOptions { ExpectedLanguage = "" })));
    }

    // ── 출력 형식 절 빠짐 (스스로 멈춘 응답: 종료 이유 Stop · 상한의 11%) ──

    [Fact]
    public void SolStyle_Truncated_ReportsMissingSections()
    {
        // 5e81a2ed47 P3_A 꼴: "## 1." · "## 2." 뒤 하위 제목과 본문 번호 목록만 있고 끝남
        var text = "## 1. 상대 Phase 2 판정에 대한 응답\n| B2-01 | 수용 | 근거 |\n\n## 2. 최종 수정안\n### A3-17. 적용 범위\n"
                 + "   3. 들여 쓴 번호 목록은 절이 아니다\n4. 이 줄은 맨 앞부터지만 제목이 아니라 아주 긴 문장이어서 본문 번호 목록으로 보아야 하는 경우다\n"
                 + "```\n## 5. 코드 블록 안 제목은 세지 않는다\n```\n";
        Assert.Equal([3, 4, 5, 6, 7], ResponseChecker.MissingSections(text, 7));
        var warning = Assert.Single(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 7018, Ko, outputSections: 7));
        Assert.Equal("[경고] 출력 형식 절 빠짐 (3., 4., 5., 6., 7. / 전체 7절). 응답이 중간에 끊겼을 수 있습니다.", warning);
    }

    [Theory]
    [InlineData("## 1. 가\n내용\n## 2. 나\n## 3. 다\n## 4. 라\n")]                                // sol 꼴
    [InlineData("## **1. 가**\n## __2. 나__\n## 3) 다\n### 1) 하위 제목\n## **4) 라**\n")]         // # 뒤 굵게 · N) (3c0fdb8eb4 C1)
    [InlineData("**1. 가**\n내용\n**2. 나**\n**3. 다**\n**4. 라**\n")]                             // 굵게만
    [InlineData("1. 새로 찾은 문제\n   1. 긴 본문 목록\n2. 확정 사항 위반\n3. 수정 방향\n4. 결론\n")]   // argon 꼴 (맨 앞부터 짧은 제목)
    [InlineData("1. 지적 목록\n2. 확인 필요\n3. 수정 방향\n4. 결론: 수정 후 진행 — C-02 해제 누락이 남아 있어 고쳐야 하며 다른 항목은 확인됨\n")] // 결론 줄에 답 (C2)
    public void CompleteSections_NoWarning(string text)
    {
        Assert.Empty(ResponseChecker.MissingSections(text, 4));
        Assert.Empty(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 100, Ko, outputSections: 4));
    }

    [Theory]
    [InlineData("## 1. 지적 목록\n### 1. F1\n### 2. F2\n### 3. F3\n### 4. F4\n", new[] { 2, 3, 4 })]   // 다른 단계 하위 제목은 절 아님 (C3)
    [InlineData("## 1. 지적 목록\n3. 짧은 목록\n4. 짧은 목록\n## 2. 확인 필요\n", new[] { 3, 4 })]      // 제목 꼴이 있으면 평문 꼴은 무시 (C3)
    [InlineData("## 1. 가\n## 2. 나\n**3. 다**\n### 4. 라\n", new[] { 3, 4 })]                        // 단계가 다른 제목
    public void OtherLevelOrPlainLines_AreNotSections(string text, int[] missing) =>
        Assert.Equal(missing, ResponseChecker.MissingSections(text, 4));

    [Fact]
    public void LengthStop_OnlyTruncationWarning()
    {
        // 상한에서 잘렸으면 잘림 경고 하나만 (C7)
        var warning = Assert.Single(ResponseChecker.Warnings("## 1. 가\n", ChatFinishReason.Length, 64000, Ko, outputSections: 4));
        Assert.StartsWith("[경고] 출력 상한에서 잘림", warning);
    }

    [Theory]
    [InlineData("[A3-01] 항목\nBoth need Korean no accidental and continue mentally must\nNeed not generate too long and foreign errors must final\n")] // [로 시작하는 마크다운 (C6)
    public void MarkdownStartingWithBracket_IsChecked(string text) =>
        Assert.True(HasEnglishWarning(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 100, Ko)));

    [Theory]
    [InlineData("GetComponent FindObjectOfType PlayMode EditMode TearDown OneTimeSetUp\nNullReferenceException ArgumentException InvalidOperationException ObjectDisposedException IOException\n")] // CamelCase (C4)
    [InlineData("> Object reference not set to an instance of an object\n> Object reference not set to an instance of an object again\n")] // 인용 줄 (C4)
    [InlineData("| Severity | File | Line | Summary | Suggestion |\n| major | the file name is here | line | summary text goes here | fix this one |\n")] // 표 줄 (C4)
    public void IdentifiersQuotesTables_NotEnglishDrift(string text) =>
        Assert.False(HasEnglishWarning(ResponseChecker.Warnings(text, ChatFinishReason.Stop, 100, Ko)));

    [Fact]
    public void NoOutputSections_NoSectionCheck() =>
        Assert.Empty(ResponseChecker.Warnings("## 1. 가\n", ChatFinishReason.Stop, 10, Ko));

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
    // 기록 옆에 호출 중 표시(inflight/)·blobs/가 생기므로 테스트마다 폴더를 따로 둔다.
    private readonly string _log = Path.Combine(Directory.CreateTempSubdirectory("hb-orch-test-").FullName, "calls.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_log)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
    public async Task OutputSections_ReachChecker_FromModesJson()
    {
        // modes.json 역할의 OutputSections → PlannedStep → StepCall → 응답 검사기
        using var h = new ModeRunnerHarness("""{ "Roles": { "P": { "Prompt": "역할", "OutputSections": 3 } }, "Modes": { "m": { "Steps": [ { "Role": "P", "Agent": "a1" } ] } } }""");
        h.Client.Script = [ScriptedChatClient.Reply("## 1. 가\n## 2. 나\n")];
        await h.Run("m");
        Assert.Contains("[경고] 출력 형식 절 빠짐 (3. / 전체 3절). 응답이 중간에 끊겼을 수 있습니다.", Assert.Single(h.Records()).Warnings);
    }

    [Fact]
    public async Task NormalResponse_HasNoWarnings()
    {
        var result = await Run(new ScriptedChatClient(ScriptedChatClient.Reply("문제 없습니다. 근거는 다음과 같습니다.")));
        Assert.Empty(result.Warnings);
        Assert.Empty(Assert.Single(CallLog.ReadAll(_log)).Warnings);
    }
}
