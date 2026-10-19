using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// crash 사례 한 번의 점수. Found는 찾음, KeywordOnly는 "확인 필요"(search 항목인데 정답 URL 없이 키워드만 맞음 — 찾음에 안 셈).
/// Missed에는 KeywordOnly의 항목도 들어간다 (리뷰 사례와 같게 Found + Missed = 기대 수). GoldenPair.FindingId는 단계 이름("triage"·"search").
/// </summary>
public sealed record GoldenCrashScore(
    int ExpectedCount,
    IReadOnlyList<GoldenPair> Found,
    IReadOnlyList<GoldenPair> KeywordOnly,
    IReadOnlyList<string> Missed,
    IReadOnlyList<GoldenPair> TrapHits,
    int SearchCalls,
    int SourceCount,
    IReadOnlyList<string> Unverified);

/// <summary>
/// crash 모드(분류 → 웹검색) 채점 (계획서 2026-10-19_골든-crash사례). 두 단계 모두 자유 형식이라 리뷰처럼 지적을 짝짓지 않고,
/// 분류 본문의 키워드와 검색 단계가 실제로 본 URL(참고 · 인용 · 직접 연 페이지)로 매긴다. LLM 판정자 없음.
/// 키워드는 부정 문맥("이중 해제는 아니다")에서도 맞는다 — 근거 줄을 보고 사람이 확인한다.
/// </summary>
public static class GoldenCrashScorer
{
    /// <summary>함정은 이 표기와 같은 줄에 나올 때만 걸린다 (그럴듯한 오답을 '낮음'·'배제'로 나열하는 것은 정상이므로).</summary>
    public static readonly IReadOnlyList<string> ConfidentMarkers = ["높음", "확인됨", "유력", "high", "confirmed"];

    public static GoldenCrashScore Score(GoldenCase c, string? triageText, string? searchText, WebSearchRecord? search)
    {
        var cited = search is null ? [] : search.Citations.Concat(search.Opened).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var listed = search is null ? [] : search.Sources.Except(cited, StringComparer.OrdinalIgnoreCase).ToList();
        var unverified = search?.Unverified ?? [];
        var found = new List<GoldenPair>();
        var weak = new List<GoldenPair>();
        var missed = new List<string>();

        foreach (var item in c.Expected)
        {
            var match = item.Step == GoldenItem.StepTriage
                ? Keyword(item, triageText, GoldenItem.StepTriage, "분류 본문", strength: 2)
                : SearchMatch(item, searchText, cited, listed, unverified);
            if (match is { Strength: >= GoldenScorer.MinFoundStrength }) found.Add(match);
            else
            {
                missed.Add(item.Id);
                if (match is not null) weak.Add(match);
            }
        }

        var traps = new List<GoldenPair>();
        foreach (var trap in c.Traps)
        {
            var hit = (trap.Step is null or GoldenItem.StepTriage ? ConfidentTrap(trap, triageText, GoldenItem.StepTriage, "분류 본문") : null)
                      ?? (trap.Step is null or GoldenItem.StepSearch ? ConfidentTrap(trap, searchText, GoldenItem.StepSearch, "검색 본문") : null);
            if (hit is not null) traps.Add(hit);
        }

        return new GoldenCrashScore(c.Expected.Count, found, weak, missed, traps, search?.Calls ?? 0, cited.Count + listed.Count, unverified);
    }

    /// <summary>
    /// search 항목: 정답 URL 조각이 검색 단계가 실제로 본 URL에 있으면 찾음(강도 3). 근거에 인용·열람한 URL인지, 검색 결과 목록에만 있던 URL인지 남긴다.
    /// 없으면 본문 키워드 — urls가 있는 항목이면 "확인 필요"(강도 1, 출처 없이 내용만 맞음), urls가 없는 항목이면 찾음(강도 2).
    /// 조각이 지어낸 URL(Unverified)에만 있으면 확인 필요로 남긴다.
    /// </summary>
    private static GoldenPair? SearchMatch(GoldenItem item, string? text, IReadOnlyList<string> cited, IReadOnlyList<string> listed, IReadOnlyList<string> unverified)
    {
        var fragments = item.Urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        foreach (var fragment in fragments)
        {
            if (cited.FirstOrDefault(u => ContainsUrlFragment(u, fragment)) is { } citedUrl)
                return new GoldenPair(item.Id, GoldenItem.StepSearch, 3, $"인용·열람한 URL {citedUrl} ⊇ '{fragment}'");
            if (listed.FirstOrDefault(u => ContainsUrlFragment(u, fragment)) is { } listedUrl)
                return new GoldenPair(item.Id, GoldenItem.StepSearch, 3, $"검색 결과에만 있던 URL {listedUrl} ⊇ '{fragment}' (인용은 안 함)");
        }
        if (fragments.FirstOrDefault(f => unverified.Any(u => ContainsUrlFragment(u, f))) is { } fake)
            return new GoldenPair(item.Id, GoldenItem.StepSearch, 1, $"'{fake}'가 검색 결과에 없는 URL에만 있음 — 확인 필요");
        var hasUrls = fragments.Count > 0;
        return Keyword(item, text, GoldenItem.StepSearch, hasUrls ? "검색 본문 (정답 URL 없음 — 확인 필요)" : "검색 본문", hasUrls ? 1 : 2);
    }

    /// <summary>
    /// 대소문자 무시 포함 검사. 조각 뒤 글자가 ASCII 영숫자면 맞지 않는다 ("issues/8932"가 "issues/89321"에 맞지 않게).
    /// 앞은 보지 않는다 (조각은 보통 호스트나 경로 중간에서 시작한다).
    /// </summary>
    internal static bool ContainsUrlFragment(string url, string fragment)
    {
        for (var at = url.IndexOf(fragment, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = url.IndexOf(fragment, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + fragment.Length;
            if (end == url.Length || !char.IsAsciiLetterOrDigit(url[end]) || !char.IsAsciiLetterOrDigit(fragment[^1])) return true;
        }
        return false;
    }

    private static GoldenPair? Keyword(GoldenItem item, string? text, string step, string where, int strength)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var keyword = item.Keywords.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && GoldenScorer.ContainsKeyword(text, k));
        return keyword is null ? null : new GoldenPair(item.Id, step, strength, $"{where} 키워드 '{keyword}'");
    }

    /// <summary>함정: 키워드와 확신 표기(ConfidentMarkers)가 같은 줄에 있을 때만.</summary>
    private static GoldenPair? ConfidentTrap(GoldenItem trap, string? text, string step, string where)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var keyword = trap.Keywords.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && GoldenScorer.ContainsKeyword(line, k));
            var marker = keyword is null ? null : ConfidentMarkers.FirstOrDefault(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (marker is not null) return new GoldenPair(trap.Id, step, 2, $"{where} 키워드 '{keyword}' + '{marker}' 같은 줄");
        }
        return null;
    }
}
