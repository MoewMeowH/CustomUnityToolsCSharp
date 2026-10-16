using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents;

/// <summary>
/// 응답 검사기. 빈 응답은 실패, 출력 상한에서 잘림·언어 이탈은 경고다.
/// 경고는 응답을 막지 않는다: 그대로 전달하고 기록(CallRecord.Warnings)과 run_mode 결과에 한 줄씩 붙인다.
/// </summary>
public static partial class ResponseChecker
{
    /// <summary>가나가 이 수보다 적으면 비율과 관계없이 경고하지 않는다 (짧은 응답에서 고유명사 하나로 오탐하지 않도록).</summary>
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
            var (kana, letters) = CountKana(StripCode(text));
            var ratio = letters == 0 ? 0 : (double)kana / letters;
            if (kana >= MinForeignChars && ratio >= options.EffectiveForeignScriptRatio)
                warnings.Add($"[경고] 응답 언어 이탈 감지 (가나 비율 {(ratio * 100).ToString("0.0", CultureInfo.InvariantCulture)}%, {kana:N0}자)");
        }

        return warnings;
    }

    /// <summary>
    /// 코드 블록(``` · ~~~ 펜스, 닫히지 않은 펜스는 끝까지)과 인라인 코드(`…`)를 뺀 본문.
    /// 코드 주석·식별자의 일본어는 응답 언어가 아니다.
    /// </summary>
    public static string StripCode(string text)
    {
        var sb = new StringBuilder(text.Length);
        string? fence = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (fence is null)
            {
                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
                {
                    fence = line[..3];
                    continue;
                }
                sb.AppendLine(InlineCode().Replace(rawLine, " "));
            }
            else if (line.StartsWith(fence, StringComparison.Ordinal) && line.Trim().Trim(fence[0]).Length == 0)
            {
                fence = null; // 닫는 펜스: 펜스 문자만 있는 줄
            }
        }
        return sb.ToString();
    }

    /// <summary>(가나 글자 수, 전체 글자 수). 글자 = char.IsLetter. 가나 블록의 문장 부호(・ 등)는 글자가 아니라 세지 않는다.</summary>
    public static (int Kana, int Letters) CountKana(string text)
    {
        int kana = 0, letters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c is >= '぀' and <= 'ヿ'      // 히라가나 · 가타카나
                  or >= 'ㇰ' and <= 'ㇿ'      // 가타카나 음성 확장
                  or >= 'ｦ' and <= 'ﾝ')     // 반각 가타카나
                kana++;
        }
        return (kana, letters);
    }

    [GeneratedRegex("`[^`\n]*`")]
    private static partial Regex InlineCode();
}
