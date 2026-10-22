using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>채점할 지적 하나. 구조화 출력(findings@1)과 self-reviewer 마크다운을 같은 모양으로 맞춘 것.</summary>
public sealed record GoldenFinding(string Id, string? Severity, string? File, int? Line, string Problem, string? Detail)
{
    public static GoldenFinding From(FindingRecord f) => new(f.Id, f.Severity, f.File, f.Line, f.Problem, f.Detail);

    public string Location => File is null ? "(위치 없음)" : Line is { } l ? $"{File}:{l}" : File;
}

/// <summary>짝 하나. Basis는 사람이 확인할 근거 (파일·줄·맞은 키워드).</summary>
public sealed record GoldenPair(string ItemId, string FindingId, int Strength, string Basis);

/// <summary>
/// 사례 하나의 점수. 함정 걸림은 감점하지 않고 "검토 필요"로만 보인다.
/// Weak(강도 1, 줄만 맞음)은 찾음에 세지 않는다 — 같은 줄 범위의 다른 결함과 구분하지 못한다 (seed-safepath sol 실행: 대소문자 지적이 ADS로 짝지어짐).
/// Missed에는 Weak의 기대 지적도 들어간다.
/// </summary>
public sealed record GoldenScore(
    string CaseId, int ExpectedCount, int FindingCount,
    IReadOnlyList<GoldenPair> Found, IReadOnlyList<string> Missed,
    IReadOnlyList<GoldenPair> TrapHits, IReadOnlyList<string> Unmatched)
{
    public IReadOnlyList<GoldenPair> Weak { get; init; } = [];

    /// <summary>이미 찾음이 된 항목을 한 번 더 짚은 지적 (필수 키워드 + 위치 모두 일치). 찾음에도 짝 없음에도 넣지 않는다 (W7).</summary>
    public IReadOnlyList<GoldenPair> Duplicates { get; init; } = [];

    public double Recall => ExpectedCount == 0 ? 0 : (double)Found.Count / ExpectedCount;
}

/// <summary>
/// 규칙 짝짓기 (LLM 판정자 없음 — 채점이 비용·변동을 만들지 않게). 판 <see cref="Version"/> (계획서 2026-10-22_채점기-구조-개선).
/// 짝 후보: (위치 중 하나와 같은 파일 이름 + 줄이 범위 ±<see cref="LineTolerance"/>) 또는 (필수 키워드가 문제·상세에 있음 + 파일이 어긋나지 않음).
/// 둘 다 맞으면 강도 3, 필수 키워드만 2, 위치만 또는 보조 키워드만 1. 필수 키워드가 없는 항목(예전 정답지·함정)은 키워드가 필수 역할.
/// 기대 지적·지적 하나에 짝은 하나씩이고, 찾음 수 → 강도 합 순으로 최대가 되게 정확히 푼다 (헝가리안).
/// 기대 지적과 짝이 안 된 지적만 함정 → 중복 → 확인 필요 순으로 대조한다.
/// </summary>
public static class GoldenScorer
{
    /// <summary>결과 줄에 남기는 채점 방식 판. 정답지 해시만으로는 채점 방식이 바뀐 걸 구분할 수 없다.</summary>
    public const string Version = "golden-scorer@2";

    public const int LineTolerance = 5;

    /// <summary>찾음으로 세는 최소 강도. 1(줄만)은 확인 필요로만 보인다.</summary>
    public const int MinFoundStrength = 2;

    public static GoldenScore Score(GoldenCase c, IReadOnlyList<GoldenFinding> findings)
    {
        // 순서: 강한 짝(찾음) → 함정(강도 2 이상) → 남은 기대 지적에 줄만 맞은 짝.
        // 줄만 맞은 짝을 함정보다 먼저 붙이면 함정에 걸릴 지적을 가져간다 (rec-1133: 넓은 줄 범위의 N-ADS가 junction 지적을 가져감).
        var found = Assign(c.Expected, findings, out var leftover);
        var trapHits = Assign(c.Traps, leftover, out var rest, onePerItem: false);
        var foundIds = found.Select(p => p.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicates = DuplicatePairs(c.Expected.Where(e => foundIds.Contains(e.Id)).ToList(), rest, out var afterDup);
        var weak = WeakPairs(c.Expected.Where(e => !foundIds.Contains(e.Id)).ToList(), afterDup, out var unmatched);
        return new GoldenScore(c.Info.Id, c.Expected.Count, findings.Count,
            found, [.. c.Expected.Where(e => !foundIds.Contains(e.Id)).Select(e => e.Id)],
            trapHits, [.. unmatched.Select(f => f.Id)])
        {
            Weak = weak,
            Duplicates = duplicates,
        };
    }

    /// <summary>
    /// 이미 찾음이 된 항목을 필수 키워드 + 위치(강도 3)로 한 번 더 짚은 지적. 같은 결함을 두 번 말한 것이라 짝 없음(노이즈)에서 뺀다.
    /// 찾음에는 넣지 않는다 — 같은 결함을 여러 번 말할수록 점수가 오르면 안 되므로 (W7).
    /// </summary>
    private static List<GoldenPair> DuplicatePairs(IReadOnlyList<GoldenItem> foundItems, IReadOnlyList<GoldenFinding> findings, out List<GoldenFinding> leftover)
    {
        var pairs = new List<GoldenPair>();
        leftover = [];
        foreach (var f in findings)
        {
            // 위치 + 필수 키워드(강도 3)이고 필수 키워드가 제목에 있을 때만 — 상세에 지나가듯 나온 말로 다른 결함을 중복으로 숨기지 않게
            var hit = foundItems.Select(i => (Item: i, M: Match(i, f))).FirstOrDefault(x => x.M is { Strength: 3 } && RequiredInTitle(x.Item, f));
            if (hit.M is { } m) pairs.Add(new GoldenPair(hit.Item.Id, f.Id, m.Strength, "중복 — " + m.Basis));
            else leftover.Add(f);
        }
        return pairs;
    }

    /// <summary>남은 기대 지적마다 줄만 맞은 빈 지적 하나 (앞에서부터). 확인 필요로만 보인다.</summary>
    private static List<GoldenPair> WeakPairs(IReadOnlyList<GoldenItem> items, IReadOnlyList<GoldenFinding> findings, out List<GoldenFinding> leftover)
    {
        var used = new HashSet<int>();
        var pairs = new List<GoldenPair>();
        foreach (var item in items)
        {
            for (var f = 0; f < findings.Count; f++)
            {
                if (used.Contains(f) || Match(item, findings[f]) is not { } m) continue;
                used.Add(f);
                pairs.Add(new GoldenPair(item.Id, findings[f].Id, m.Strength, m.Basis));
                break;
            }
        }
        leftover = [.. findings.Where((_, f) => !used.Contains(f))];
        return pairs;
    }

    /// <param name="onePerItem">
    /// true(기대 지적): 짝 수가 최대가 되게 한다 (이분 매칭, 증가 경로). 후보는 강한 것부터 시도한다.
    /// false(함정): 한 함정에 여러 지적이 걸릴 수 있어 지적마다 가장 강한 함정 하나. 어느 쪽이든 지적 하나는 한 번만 쓴다.
    /// </param>
    private static List<GoldenPair> Assign(IReadOnlyList<GoldenItem> items, IReadOnlyList<GoldenFinding> findings, out List<GoldenFinding> leftover, bool onePerItem = true)
    {
        var edges = new List<(int Finding, int Strength, string Basis)>[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            edges[i] = [];
            for (var f = 0; f < findings.Count; f++)
                if (Match(items[i], findings[f]) is { } m) edges[i].Add((f, m.Strength, m.Basis));
            edges[i] = [.. edges[i].OrderByDescending(e => e.Strength).ThenBy(e => e.Finding)];
        }

        var owner = new int?[findings.Count]; // 지적 → 짝 지은 항목
        if (onePerItem)
        {
            // 찾음으로 세는 짝(강도 2 이상)만으로, 찾음 수 → 강도 합 → 필수 키워드가 제목에 있음 → 앞 지적 순으로 최대가 되게 정확히 푼다 (W2).
            // 예전의 증가 경로 + 맞바꾸기는 줄이 가깝고 흔한 키워드가 걸린 틀린 짝(3)이 맞는 짝(2)을 밀어낼 수 있었다.
            // 제목 보너스(500)는 강도 한 단계(1000)보다 작고 순번 차(< 100)보다 크다 — 상세에 지나가듯 나온 말보다 제목이 결함을 말하는 지적을 고른다.
            var weight = new long[items.Count, findings.Count];
            for (var i = 0; i < items.Count; i++)
                foreach (var (f, strength, _) in edges[i])
                    if (strength >= MinFoundStrength)
                        weight[i, f] = 1_000_000L + strength * 1_000L + (RequiredInTitle(items[i], findings[f]) ? 500L : 0L) - f;
            var assigned = MaxWeightAssignment(weight);
            for (var i = 0; i < items.Count; i++)
                if (assigned[i] is { } f && weight[i, f] > 0) owner[f] = i;
        }
        else
        {
            // 함정도 강도 2 이상만 (줄만 맞은 함정은 다른 지적을 함정으로 잘못 보이게 한다).
            for (var f = 0; f < findings.Count; f++)
            {
                var bestStrength = MinFoundStrength - 1;
                for (var i = 0; i < items.Count; i++)
                    foreach (var e in edges[i])
                        if (e.Finding == f && e.Strength > bestStrength) (owner[f], bestStrength) = (i, e.Strength);
            }
        }

        var pairs = new List<GoldenPair>();
        for (var f = 0; f < findings.Count; f++)
            if (owner[f] is { } i && edges[i].First(x => x.Finding == f) is var edge)
                pairs.Add(new GoldenPair(items[i].Id, findings[f].Id, edge.Strength, edge.Basis));
        leftover = [.. findings.Where((_, f) => owner[f] is null)];
        return [.. pairs.OrderBy(p => IndexOf(items, p.ItemId)).ThenBy(p => p.FindingId, StringComparer.Ordinal)];

    }

    /// <summary>
    /// 최대 가중치 배정 (헝가리안, 행 = 항목 · 열 = 지적, 0은 짝 아님). 행마다 배정된 열(없으면 null).
    /// 크기가 작아(항목 ≤ 수십 · 지적 ≤ 수십) O(n³)로 충분하다. 열이 모자라면 가짜 열을 더해 정사각으로 푼다.
    /// </summary>
    internal static int?[] MaxWeightAssignment(long[,] weight)
    {
        int rows = weight.GetLength(0), cols = weight.GetLength(1), n = Math.Max(rows, cols);
        if (rows == 0) return [];
        long max = 0;
        foreach (var w in weight) max = Math.Max(max, w);
        // 최소 비용으로 바꿔 푼다: cost = max - weight (가짜 칸은 weight 0)
        var cost = new long[n + 1, n + 1];
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= n; j++)
                cost[i, j] = max - (i <= rows && j <= cols ? weight[i - 1, j - 1] : 0);

        var u = new long[n + 1]; var v = new long[n + 1];
        var p = new int[n + 1]; var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(long.MaxValue, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                var delta = long.MaxValue;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j]) continue;
                    var cur = cost[i0, j] - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (var j = 0; j <= n; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do { var j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
        }

        var result = new int?[rows];
        for (var j = 1; j <= n; j++)
            if (p[j] is var i && i >= 1 && i <= rows && j <= cols) result[i - 1] = j - 1;
        return result;
    }

    private static int IndexOf(IReadOnlyList<GoldenItem> items, string id)
    {
        for (var i = 0; i < items.Count; i++) if (items[i].Id == id) return i;
        return -1;
    }

    /// <summary>짝이면 (강도, 근거), 아니면 null.</summary>
    internal static (int Strength, string Basis)? Match(GoldenItem item, GoldenFinding finding)
    {
        var name = FileName(finding.File);
        var locations = item.AllLocations;
        var withFile = locations.Where(l => FileName(l.File) is not null).ToList();
        var bothHaveFile = name is not null && withFile.Count > 0;
        var sameFile = bothHaveFile && withFile.Any(l => string.Equals(FileName(l.File), name, StringComparison.OrdinalIgnoreCase));

        // 위치 목록 중 하나라도 (같은 파일 + 줄 범위 ±허용) 맞으면 위치 일치 (W4: 원인·증상 지점이 다른 결함)
        var nearAt = !sameFile || finding.Line is not { } line ? null
            : withFile.FirstOrDefault(l => string.Equals(FileName(l.File), name, StringComparison.OrdinalIgnoreCase)
                                           && l.From is { } from && line >= from - LineTolerance && line <= (l.To ?? from) + LineTolerance);
        var near = nearAt is not null;

        var text = finding.Problem + "\n" + finding.Detail;
        // 필수 키워드가 없으면 예전처럼 키워드가 필수 역할 (예전 정답지 · 함정)
        var hasRequired = item.Required.Count > 0;
        var required = (hasRequired ? item.Required : item.Keywords).FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && ContainsTerm(text, k));
        var auxiliary = hasRequired && required is null
            ? item.Keywords.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && ContainsTerm(text, k)) : null;
        // 키워드만으로 짝을 지을 때 다른 파일을 가리키는 지적은 뺀다 (같은 낱말이 다른 결함에도 나오므로).
        var fileOk = !bothHaveFile || sameFile;
        var byRequired = required is not null && fileOk;
        var byAuxiliary = auxiliary is not null && fileOk;

        var where = nearAt is null ? "" : $"{finding.Location} (기대 {LineText(nearAt)} ±{LineTolerance})";
        var termName = hasRequired ? "필수" : "키워드";
        return (near, byRequired) switch
        {
            (true, true) => (3, $"{where} + {termName} '{required}'"),
            (false, true) => (2, $"{termName} '{required}'{(sameFile ? $" + 같은 파일 {name}" : "")}"),
            (true, false) => (1, $"{where}, {(byAuxiliary ? $"보조 '{auxiliary}'만" : $"{termName} 없음")}"),
            _ when byAuxiliary => (1, $"보조 '{auxiliary}'만{(sameFile ? $" + 같은 파일 {name}" : "")}"),
            _ => null,
        };

        static string LineText(GoldenLocation l) => $"{FileName(l.File)}:{(l.From == (l.To ?? l.From) ? $"{l.From}" : $"{l.From}-{l.To}")}";
    }

    /// <summary>필수 키워드(없으면 키워드)가 지적의 제목(problem)에 있는지. 배정 동점 정렬과 중복 판정에 쓴다.</summary>
    internal static bool RequiredInTitle(GoldenItem item, GoldenFinding finding) =>
        (item.Required.Count > 0 ? item.Required : item.Keywords).Any(k => !string.IsNullOrWhiteSpace(k) && ContainsTerm(finding.Problem, k));

    /// <summary>
    /// 리뷰 채점(판 2)의 키워드 검사. 대소문자 무시 포함 검사이고, 키워드 첫 글자가 ASCII 영숫자면 그 앞,
    /// 끝 글자가 ASCII 영숫자면 그 뒤도 ASCII 영숫자가 아니어야 한다 ('NUL'이 'null'에 걸리지 않게, W5).
    /// 키워드 끝의 '*'는 앞부분 일치 ("ArgumentOutOfRange*" → "ArgumentOutOfRangeException"). 한국어는 그대로 부분 문자열.
    /// crash 채점은 예전 <see cref="ContainsKeyword"/>를 그대로 쓴다 (정답지를 다시 보지 않은 채 경계 규칙을 바꾸지 않음).
    /// </summary>
    internal static bool ContainsTerm(string text, string keyword)
    {
        var prefix = keyword.Length > 1 && keyword[^1] == '*';
        var term = prefix ? keyword[..^1] : keyword;
        static bool Word(char c) => char.IsAsciiLetterOrDigit(c);
        for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var leftOk = !Word(term[0]) || at == 0 || !Word(text[at - 1]);
            var end = at + term.Length;
            var rightOk = prefix || !Word(term[^1]) || end == text.Length || !Word(text[end]);
            if (leftOk && rightOk) return true;
        }
        return false;
    }

    /// <summary>
    /// 대소문자 무시 포함 검사. 키워드 첫 글자가 ASCII 영숫자면 그 앞 글자는 ASCII 영숫자가 아니어야 한다
    /// ("ADS"가 "loads"·"threads" 안에서 맞지 않게, "rsp"는 "csc.rsp"에서는 맞게). 오른쪽은 보지 않는다 —
    /// 식별자 앞부분 키워드("ArgumentOutOfRange" → "ArgumentOutOfRangeException")를 살리기 위해 (sol rec-4942에서 확인).
    /// 한국어 키워드는 그대로 부분 문자열.
    /// </summary>
    internal static bool ContainsKeyword(string text, string keyword)
    {
        static bool Word(char c) => char.IsAsciiLetterOrDigit(c);
        for (var at = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(keyword, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (!Word(keyword[0]) || at == 0 || !Word(text[at - 1])) return true;
        }
        return false;
    }

    /// <summary>경로 앞부분(review_tmp/…, @orch/…)과 ":줄"을 떼고 파일 이름만.</summary>
    internal static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        var colon = p.LastIndexOf(':');
        if (colon > 0 && colon < p.Length - 1 && p[(colon + 1)..].All(ch => char.IsAsciiDigit(ch) || ch == '-')) p = p[..colon];
        var name = p[(p.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? null : name;
    }

    // ── 지적 읽기 ───────────────────────────────────────────────────────

    // "- C1 [major] 파일:줄 — 문제 / 제안 / 확신도(high)". 위치는 "파일:줄", "파일:줄-줄", "파일", 또는 여러 개(첫 번째만 씀).
    // ID가 없는 "- [major] 파일:줄 — 문제"(구조화 출력 전의 review 응답)도 읽고 P1, P2…를 붙인다 (과거 결과를 기준점으로 채점).
    private static readonly Regex SelfReviewLine = new(
        @"^\s*[-*]\s*(?:(?<id>C\d+)\s*)?\[(?<sev>[A-Za-z]+)\]\s*(?<loc>.*?)\s+[—–]\s+(?<rest>.+)$", RegexOptions.CultureInvariant);

    private static readonly Regex Location = new(
        @"(?<file>[^\s,;():]+\.[A-Za-z0-9]+)(?::(?<line>\d+))?", RegexOptions.CultureInvariant); // 폴더 이름의 점(HB.Orchestration.Agents/)에서 끊기지 않게 greedy

    /// <summary>
    /// 채점용 지적 목록. JSON(findings@1 응답 또는 그 해석 결과)이면 구조화 출력으로, 아니면 self-reviewer 마크다운으로 읽는다.
    /// 읽은 지적이 없으면 Error에 이유 (빈 결과를 "0개 찾음"으로 조용히 채점하지 않도록).
    /// </summary>
    public static (List<GoldenFinding> Findings, string? Error) ReadFindings(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith("```json", StringComparison.Ordinal))
        {
            var parsed = FindingParser.Parse(FindingFormat.Findings, 1, text, null, "file");
            return parsed.Parsed
                ? ([.. parsed.Findings.Select(GoldenFinding.From)], null)
                : ([], $"구조화 출력으로 읽지 못했습니다: {parsed.Error}");
        }

        var findings = ReadSelfReview(text);
        if (findings.Count > 0) return (findings, null);
        return text.Contains("문제 없음", StringComparison.Ordinal)
            ? ([], null)
            : ([], "self-reviewer 형식('- C1 [심각도] 파일:줄 — 문제' 또는 '- [심각도] 파일:줄 — 문제')의 지적을 찾지 못했습니다.");
    }

    /// <summary>"## 지적" 절(없으면 전체)의 "- C1 [sev] 위치 — 문제" 줄과 그 아래 "상세:" 줄.</summary>
    internal static List<GoldenFinding> ReadSelfReview(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("## 지적", StringComparison.Ordinal));
        var result = new List<GoldenFinding>();
        (string Id, string Sev, string? File, int? Line, string Problem, List<string> Detail)? current = null;

        void Flush()
        {
            if (current is { } c) result.Add(new GoldenFinding(c.Id, c.Sev, c.File, c.Line, c.Problem, c.Detail.Count == 0 ? null : string.Join("\n", c.Detail)));
            current = null;
        }

        for (var i = start < 0 ? 0 : start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (start >= 0 && line.StartsWith("## ", StringComparison.Ordinal)) break;

            var m = SelfReviewLine.Match(line);
            if (m.Success)
            {
                Flush();
                var loc = Location.Match(m.Groups["loc"].Value);
                string? file = loc.Success ? loc.Groups["file"].Value : null;
                // 범위 밖 줄 번호(모델이 지어낸 큰 수)는 줄 없음으로 본다 (int.Parse 예외로 채점이 죽지 않게).
                int? lineNo = loc.Success && loc.Groups["line"].Success && int.TryParse(loc.Groups["line"].Value, out var n) && n > 0 ? n : null;
                var id = m.Groups["id"].Success ? m.Groups["id"].Value : $"P{result.Count + 1}";
                // 같은 ID가 다시 나오면 뒤의 것에 접미사 (짝·출력이 앞 지적과 섞이지 않게).
                for (var k = 2; result.Any(f => f.Id == id) || current?.Id == id; k++) id = $"{(m.Groups["id"].Success ? m.Groups["id"].Value : id)}#{k}";
                // "문제 / 제안 / 확신도"에서 문제만 (JSON 경로와 같이 problem·detail만 대조 — 제안 문장의 키워드로 짝이 지어지지 않게).
                var rest = m.Groups["rest"].Value.Trim();
                var slash = rest.IndexOf(" / ", StringComparison.Ordinal);
                current = (id, m.Groups["sev"].Value.ToLowerInvariant(), file, lineNo, slash > 0 ? rest[..slash].Trim() : rest, []);
                continue;
            }
            if (current is not null && line.Trim().Length > 0 && char.IsWhiteSpace(line[0]))
                current.Value.Detail.Add(line.Trim().StartsWith("상세:", StringComparison.Ordinal) ? line.Trim()["상세:".Length..].Trim() : line.Trim());
        }
        Flush();
        return result;
    }
}
