using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents;

/// <summary>
/// 응답 검사기. 빈 응답은 실패, 출력 상한에서 잘림·언어 이탈은 경고다.
/// 경고는 응답을 막지 않는다: 그대로 전달하고 기록(CallRecord.Warnings)과 run_mode 결과에 한 줄씩 붙인다.
/// </summary>
public static partial class ResponseChecker
{
    /// <summary>가나 · 한자 각각 이 수보다 적으면 비율과 관계없이 경고하지 않는다 (짧은 응답에서 고유명사 하나로 오탐하지 않도록).</summary>
    public const int MinForeignChars = 5;

    /// <summary>지금 언어 검사를 지원하는 값. 다른 값이면 언어 검사를 건너뛴다.</summary>
    public static bool IsSupportedLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "ko", StringComparison.OrdinalIgnoreCase);

    /// <summary>빈 응답이면 실패 문구, 아니면 null. 기존 빈 응답 검사와 같고, 종료 이유가 길이 제한이면 상한 안내를 확실히 한다.</summary>
    public static string? EmptyFailure(string? text, ChatFinishReason? finishReason, long? outTokens, AgentOptions agent)
    {
        if (!string.IsNullOrWhiteSpace(text)) return null;

        // 추론 모델은 생각 토큰도 MaxOutputTokens에 포함된다. 상한을 다 쓰면 답변 없이 "성공"으로 끝나므로
        // Claude가 "문제 없음"으로 오해하지 않도록 실패로 돌려준다.
        var max = agent.MaxOutputTokens;
        var hitLimit = finishReason == ChatFinishReason.Length || (max is { } m && outTokens >= m * 0.95);
        return hitLimit
            ? $"[빈 응답] 출력 토큰 상한({(max is { } mm ? mm.ToString("N0") : "제공사 기본")})을 추론에 다 써서 답변을 못 했습니다. appsettings.json에서 '{agent.Name}'의 MaxOutputTokens를 올리세요."
            : $"[빈 응답] 에이전트가 내용 없이 응답했습니다. (출력 토큰 {outTokens?.ToString("N0") ?? "-"})";
    }

    /// <summary>내용이 있는 응답의 경고 목록. 경고가 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> Warnings(string text, ChatFinishReason? finishReason, long? outTokens, ResponseChecksOptions options)
    {
        var warnings = new List<string>();

        if (finishReason == ChatFinishReason.Length)
            warnings.Add($"[경고] 출력 상한에서 잘림 (출력 {outTokens?.ToString("N0") ?? "-"} 토큰). 응답 끝부분이 빠졌을 수 있습니다.");

        if (!string.IsNullOrWhiteSpace(options.ExpectedLanguage) && IsSupportedLanguage(options.ExpectedLanguage))
        {
            // 가나가 기준을 넘으면 일본어로 보고 가나만 알린다. 아니면 한자가 기준을 넘을 때 중국어 이탈로 알린다
            // (가나가 기준 밑인 한자 위주 일본어도 한자로 표시된다 — 경고는 나므로 그대로 둔다)
            var (kana, han, letters) = CountScripts(StripCode(text));
            var threshold = options.EffectiveForeignScriptRatio;
            if (Exceeds(kana, letters, threshold, out var kanaRatio))
                warnings.Add($"[경고] 응답 언어 이탈 감지 (가나 비율 {Percent(kanaRatio)}%, {kana:N0}자)");
            else if (Exceeds(han, letters, threshold, out var hanRatio))
                warnings.Add($"[경고] 응답 언어 이탈 감지 (한자 비율 {Percent(hanRatio)}%, {han:N0}자)");
        }

        return warnings;
    }

    private static bool Exceeds(int count, int letters, double threshold, out double ratio)
    {
        ratio = letters == 0 ? 0 : (double)count / letters;
        return count >= MinForeignChars && ratio >= threshold;
    }

    private static string Percent(double ratio) => (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 코드 블록(``` · ~~~ 펜스, 닫히지 않은 펜스는 끝까지)과 인라인 코드(`…`)를 뺀 본문.
    /// 코드 주석·식별자의 일본어는 응답 언어가 아니다.
    /// </summary>
    public static string StripCode(string text) => InlineCode().Replace(CodeFences.WithoutBlocks(text), " ");

    /// <summary>
    /// (가나 글자 수, 한자 글자 수, 전체 글자 수). 글자 = char.IsLetter. 가나 블록의 문장 부호(・ 등)는 글자가 아니라 세지 않는다.
    /// 한자는 CJK 통합 · 확장 A · 호환 한자 (BMP 밖 확장은 서로게이트라 IsLetter가 아니어서 세지 않는다).
    /// </summary>
    public static (int Kana, int Han, int Letters) CountScripts(string text)
    {
        int kana = 0, han = 0, letters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c is >= '぀' and <= 'ヿ'      // 히라가나 · 가타카나
                  or >= 'ㇰ' and <= 'ㇿ'      // 가타카나 음성 확장
                  or >= 'ｦ' and <= 'ﾝ')     // 반각 가타카나
                kana++;
            // 호환 한자는 글자로 쓰면 편집기가 통합 한자로 정규화할 수 있어 숫자로 둔다
            else if (c is >= (char)0x4E00 and <= (char)0x9FFF   // CJK 통합 한자
                       or >= (char)0x3400 and <= (char)0x4DBF   // 확장 A
                       or >= (char)0xF900 and <= (char)0xFAFF)  // 호환 한자
                han++;
        }
        return (kana, han, letters);
    }

    [GeneratedRegex("`[^`\n]*`")]
    private static partial Regex InlineCode();
}
