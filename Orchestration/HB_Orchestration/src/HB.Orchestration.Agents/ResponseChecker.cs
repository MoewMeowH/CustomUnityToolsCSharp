using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace HB.Orchestration.Agents;

/// <summary>
/// 응답 검사기. 빈 응답은 실패, 출력 상한에서 잘림·언어 이탈·영어 문장 섞임·출력 형식 절 빠짐은 경고다.
/// 경고는 응답을 막지 않는다: 그대로 전달하고 기록(CallRecord.Warnings)과 run_mode 결과에 한 줄씩 붙인다.
/// </summary>
public static partial class ResponseChecker
{
    /// <summary>가나 · 한자 각각 이 수보다 적으면 비율과 관계없이 경고하지 않는다 (짧은 응답에서 고유명사 하나로 오탐하지 않도록).</summary>
    public const int MinForeignChars = 5;

    /// <summary>
    /// 소량 섞임: 비율 경고가 없을 때, 코드 · 따옴표 밖 가나가 이 수 이상이면 경고 ('ログ 수집' 같은 낱말 하나도).
    /// 기준은 지난 응답 836건으로 정함 (계획서 2026-10-28_소량-외국어-섞임-경고).
    /// </summary>
    public const int MinMixedKana = 2;

    /// <summary>
    /// 영어 문장 줄: 코드 밖, 한글이 없고 영어 낱말(글자 2개 이상)이 이만큼 있는 줄. 이런 줄이 <see cref="MinEnglishLines"/>줄 이상이면 경고.
    /// 기준은 지난 응답 754건으로 정함 — 걸린 것은 sol의 작업 메모 · 이탈 두 건(5e81a2ed47 · 8f8065bf09)뿐 (계획서 2026-10-27_한국어-응답-지시-강화).
    /// </summary>
    public const int MinEnglishWords = 5;

    public const int MinEnglishLines = 2;

    /// <summary>맨 앞부터 쓴 "N. 제목" 줄을 절 제목으로 볼 제목 길이 상한 — 본문 번호 목록(긴 문장)과 가른다 (지난 preflight 응답 36건으로 확인).</summary>
    public const int MaxPlainSectionTitle = 40;

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
    /// <param name="outputSections">역할의 출력 형식 절 개수 (modes.json OutputSections). 있으면 1..N 절 제목이 모두 있는지 본다.</param>
    public static IReadOnlyList<string> Warnings(string text, ChatFinishReason? finishReason, long? outTokens, ResponseChecksOptions options,
        int? outputSections = null)
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
            // 비율 문턱 아래의 몇 줄 · 낱말 하나 섞임 — 비율 경고가 났으면 같은 사건이라 알리지 않는다
            else if (MixedForeign(text) is { } mixed)
                warnings.Add(mixed);

            // 가나 · 한자 비율로는 안 잡히는 영어 이탈 · 작업 메모("Need not generate too long…")
            if (EnglishLines(text) is { Count: >= MinEnglishLines } english)
                warnings.Add($"[경고] 영어 문장 섞임 ({english.Count}줄, 예: \"{Clip(english[0], 60)}\"). 작업 메모가 새었거나 영어로 바뀌었을 수 있습니다.");
        }

        // 상한에 닿지 않고 모델이 스스로 멈춘 응답은 종료 이유로 알 수 없다 → 출력 형식의 절이 다 있는지로 본다.
        // 상한에서 잘렸으면 위 잘림 경고가 같은 사건을 이미 알린다 (/self-review 3c0fdb8eb4 C7)
        if (finishReason != ChatFinishReason.Length && outputSections is > 0 && MissingSections(text, outputSections.Value) is { Count: > 0 } missing)
            warnings.Add($"[경고] 출력 형식 절 빠짐 ({string.Join(", ", missing.Select(n => $"{n}."))} / 전체 {outputSections}절). 응답이 중간에 끊겼을 수 있습니다.");

        return warnings;
    }

    /// <summary>
    /// 소량 외국어 섞임 경고 문구, 없으면 null. 코드 · 큰따옴표류(`"…"` · `“…”` · `「…」` · `『…』`, 인용한 문자열) 밖에서
    /// 가나가 <see cref="MinMixedKana"/>자 이상이거나, 한글 · 영문자 · 가나에 바로 붙은 한자 덩어리("디렉터리를指定的하면" · "실제既存")가 있으면.
    /// 괄호 안이 한자뿐인 한자 병기("주(州)" · "정적(靜的)")는 보지 않는다. 해석에 실패한 구조화 출력(JSON 원문)은 따옴표를 빼지 않는다.
    /// </summary>
    public static string? MixedForeign(string text)
    {
        var body = StripCode(text);
        if (!JsonStart().IsMatch(text)) body = Quoted().Replace(body, " ");
        body = HanGloss().Replace(body, " ");

        int kana = 0, firstKana = -1;
        for (var i = 0; i < body.Length; i++)
        {
            if (!char.IsLetter(body[i]) || !IsKana(body[i])) continue;
            kana++;
            if (firstKana < 0) firstKana = i;
        }
        var attached = AttachedHan().Matches(body);
        var hasKana = kana >= MinMixedKana;
        if (!hasKana && attached.Count == 0) return null;

        var at = hasKana && (attached.Count == 0 || firstKana < attached[0].Index) ? firstKana : attached[0].Index;
        var parts = new List<string>(2);
        if (hasKana) parts.Add($"가나 {kana:N0}자");
        if (attached.Count > 0) parts.Add($"한자 {attached.Count:N0}곳");
        var start = Math.Max(0, at - 15);
        var sample = body[start..Math.Min(body.Length, at + 25)].Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"[경고] 외국어 소량 섞임 ({string.Join(" · ", parts)}, 예: \"{Clip(sample, 60)}\")";
    }

    /// <summary>
    /// 코드 밖의 영어 문장 줄 (한글 없음, 영어 낱말 <see cref="MinEnglishWords"/>개 이상). 낱말 = 소문자로 이어지는 영어 낱말 —
    /// CamelCase · 대문자 약어 · 경로 · 밑줄 · 숫자가 든 토큰은 식별자로 보고 세지 않는다. `>` 인용 줄 · `|` 표 줄(인용한 로그 · 영어 표 머리줄)은 보지 않는다.
    /// 해석에 실패한 구조화 출력(JSON 원문)은 보지 않는다.
    /// </summary>
    public static IReadOnlyList<string> EnglishLines(string text)
    {
        if (JsonStart().IsMatch(text)) return [];
        var lines = new List<string>();
        foreach (var raw in StripCode(text).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '>' or '|' || line.Any(IsHangul)) continue;
            var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Count(t => EnglishWord().IsMatch(t.Trim(WordEdge)));
            if (words >= MinEnglishWords) lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// 1..count 중 제목 줄이 없는 절 번호 (코드 밖). 두 꼴 중 하나로 본다 (/self-review 3c0fdb8eb4 C1 · C2 · C3):
    /// - 제목 꼴: `#` · `**` · `__`(함께 와도 됨) 뒤의 "N." · "N)" (sol 꼴 "## 3. …", "## **3. …**"). 한 줄이라도 있으면 이 꼴만 보고,
    ///   1절과 같은 단계(`#` 개수, `#` 없이 굵게만이면 굵게)만 센다 — 다른 단계의 "### 3." 하위 제목은 절이 아니다.
    /// - 평문 꼴 (제목 꼴이 하나도 없을 때): 맨 앞부터 쓴 "N. 제목" · "N) 제목", 제목 부분(첫 `—` · `–` · `:` · ` - ` 앞)이
    ///   <see cref="MaxPlainSectionTitle"/>자 이하 (argon 꼴 "3. 수정 방향", "4. 결론: 수정 후 진행 — …"). 들여 쓴 줄 · 긴 문장은 본문 번호 목록.
    /// </summary>
    public static IReadOnlyList<int> MissingSections(string text, int count)
    {
        var headings = new List<(int Number, string Level)>();
        var plain = new HashSet<int>();
        foreach (var raw in CodeFences.WithoutBlocks(text).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (HeadingSection().Match(line) is { Success: true } h && (h.Groups["hash"].Success || h.Groups["bold"].Success))
                headings.Add((int.Parse(h.Groups["n"].Value, CultureInfo.InvariantCulture), h.Groups["hash"].Success ? h.Groups["hash"].Value : "**"));
            else if (PlainSection().Match(line) is { Success: true } p && PlainTitle(p.Groups[2].Value).Length <= MaxPlainSectionTitle)
                plain.Add(int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        HashSet<int> found;
        if (headings.Count > 0)
        {
            var level = headings.FirstOrDefault(x => x.Number == 1) is { Level: { } one } ? one : headings[0].Level;
            found = [.. headings.Where(x => x.Level == level).Select(x => x.Number)];
        }
        else found = plain;
        return [.. Enumerable.Range(1, count).Where(n => !found.Contains(n))];
    }

    /// <summary>평문 꼴 제목 부분: 굵게 표시를 벗기고 첫 `—` · `–` · `:` · ` - ` 앞까지 (결론 절은 답을 같은 줄에 잇는다).</summary>
    private static string PlainTitle(string rest)
    {
        var s = rest.Trim().Trim('*').Trim();
        var cut = s.Length;
        foreach (var sep in (string[])["—", "–", ":", " - "])
            if (s.IndexOf(sep, StringComparison.Ordinal) is var i and >= 0 && i < cut) cut = i;
        return s[..cut].Trim();
    }

    private static readonly char[] WordEdge = ['"', '\'', '(', ')', '[', ']', '{', '}', '*', '_', ',', '.', ';', ':', '!', '?', '…', '<', '>', '|'];

    private static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ';

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
            if (IsKana(c)) kana++;
            else if (IsHan(c)) han++;
        }
        return (kana, han, letters);
    }

    private static bool IsKana(char c) =>
        c is >= '぀' and <= 'ヿ'      // 히라가나 · 가타카나
          or >= 'ㇰ' and <= 'ㇿ'      // 가타카나 음성 확장
          or >= 'ｦ' and <= 'ﾝ';     // 반각 가타카나

    // 호환 한자는 글자로 쓰면 편집기가 통합 한자로 정규화할 수 있어 숫자로 둔다
    private static bool IsHan(char c) =>
        c is >= (char)0x4E00 and <= (char)0x9FFF   // CJK 통합 한자
          or >= (char)0x3400 and <= (char)0x4DBF   // 확장 A
          or >= (char)0xF900 and <= (char)0xFAFF;  // 호환 한자

    /// <summary>인용한 문자열: 큰따옴표 · 둥근 큰따옴표 · 낫표 · 겹낫표 (한 줄 안).</summary>
    [GeneratedRegex("\"[^\"\n]*\"|“[^”\n]*”|「[^」\n]*」|『[^』\n]*』")]
    private static partial Regex Quoted();

    /// <summary>한자 병기: 괄호(반각 · 전각) 안이 한자 · 가운뎃점 · 공백뿐.</summary>
    [GeneratedRegex(@"[(（][㐀-䶿一-鿿豈-﫿·・\s]+[)）]")]
    private static partial Regex HanGloss();

    /// <summary>앞이나 뒤가 한글 · 영문자 · 가나에 바로 붙은 한자 덩어리.</summary>
    [GeneratedRegex(@"(?<=[가-힣A-Za-z぀-ヿ])[㐀-䶿一-鿿豈-﫿]+|[㐀-䶿一-鿿豈-﫿]+(?=[가-힣A-Za-z぀-ヿ])")]
    private static partial Regex AttachedHan();

    [GeneratedRegex("`[^`\n]*`")]
    private static partial Regex InlineCode();

    /// <summary>영어 낱말: 첫 글자만 대문자일 수 있고 나머지는 소문자 (CamelCase · 약어는 식별자).</summary>
    [GeneratedRegex(@"^[A-Za-z][a-z]+(?:['-][a-z]+)*$")]
    private static partial Regex EnglishWord();

    /// <summary>JSON 원문: `{`로 시작하거나 `[` 뒤에 `{` · `"` · `]`가 온다 (`[A3-01] …` · `[링크](…)` 마크다운은 아님).</summary>
    [GeneratedRegex(@"^\s*(?:\{|\[\s*[{""\]])")]
    private static partial Regex JsonStart();

    [GeneratedRegex(@"^\s*(?<hash>#{1,6})?\s*(?<bold>\*\*|__)?\s*(?<n>\d{1,3})[.)]")]
    private static partial Regex HeadingSection();

    [GeneratedRegex(@"^(\d{1,3})[.)]\s+(.*)$")]
    private static partial Regex PlainSection();
}
