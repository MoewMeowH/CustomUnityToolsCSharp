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

    public double Recall => ExpectedCount == 0 ? 0 : (double)Found.Count / ExpectedCount;
}

/// <summary>
/// 규칙 짝짓기 (LLM 판정자 없음 — 채점이 비용·변동을 만들지 않게).
/// 짝 후보: (같은 파일 이름 + 줄이 범위 ±<see cref="LineTolerance"/>) 또는 (키워드가 문제·상세에 있음 + 파일이 어긋나지 않음).
/// 둘 다 맞으면 강도 3, 키워드만 2, 줄만 1. 기대 지적·지적 하나에 짝은 하나씩이고, 짝 수가 최대가 되게 정한다 (강한 후보부터 시도).
/// 기대 지적과 짝이 안 된 지적만 함정과 대조한다.
/// </summary>
public static class GoldenScorer
{
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
        var weak = WeakPairs(c.Expected.Where(e => !foundIds.Contains(e.Id)).ToList(), rest, out var unmatched);
        return new GoldenScore(c.Info.Id, c.Expected.Count, findings.Count,
            found, [.. c.Expected.Where(e => !foundIds.Contains(e.Id)).Select(e => e.Id)],
            trapHits, [.. unmatched.Select(f => f.Id)])
        {
            Weak = weak,
        };
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
            // 1) 찾음으로 세는 짝(강도 2 이상)만으로 최대 매칭. 강한 후보가 있는 항목부터, 지적 하나가 기대 지적 둘에 걸리면 증가 경로로 옮긴다.
            foreach (var i in Enumerable.Range(0, items.Count).OrderByDescending(i => edges[i].Count == 0 ? 0 : edges[i][0].Strength))
                TryAugment(i, new bool[findings.Count]);
            SwapForStrength();
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

        int StrengthOf(int item, int finding) => edges[item].FirstOrDefault(e => e.Finding == finding).Strength; // 짝 후보가 아니면 0

        // 짝 수는 그대로 두고, 두 짝의 지적을 맞바꾸면 강도 합이 커질 때 바꾼다 (예: 키워드가 서로 엇갈려 맞은 두 지적 — rec-ffd7 sol 실행).
        void SwapForStrength()
        {
            for (var changed = true; changed;)
            {
                changed = false;
                for (var f = 0; f < findings.Count; f++)
                    for (var g = f + 1; g < findings.Count; g++)
                    {
                        if (owner[f] is not { } i || owner[g] is not { } j) continue;
                        int now = StrengthOf(i, f) + StrengthOf(j, g), swapped = StrengthOf(i, g) + StrengthOf(j, f);
                        if (StrengthOf(i, g) == 0 || StrengthOf(j, f) == 0 || swapped <= now) continue;
                        (owner[f], owner[g]) = (j, i);
                        changed = true;
                    }
                // 짝 없는 지적이 더 강하게 맞으면 바꾼다 (지적 수가 같으므로 짝 수는 그대로).
                for (var f = 0; f < findings.Count; f++)
                    for (var g = 0; g < findings.Count; g++)
                    {
                        if (owner[f] is not { } i || owner[g] is not null || StrengthOf(i, g) <= StrengthOf(i, f)) continue;
                        (owner[f], owner[g]) = (null, i);
                        changed = true;
                    }
            }
        }

        // 빈 지적부터 쓰고, 그다음에만 다른 항목의 짝을 옮긴다 (옮기면 그 항목이 약한 짝으로 밀릴 수 있음).
        bool TryAugment(int item, bool[] seen)
        {
            foreach (var (f, _, _) in edges[item].Where(e => e.Strength >= MinFoundStrength))
            {
                if (seen[f] || owner[f] is not null) continue;
                seen[f] = true;
                owner[f] = item;
                return true;
            }
            foreach (var (f, _, _) in edges[item].Where(e => e.Strength >= MinFoundStrength))
            {
                if (seen[f]) continue;
                seen[f] = true;
                if (TryAugment(owner[f]!.Value, seen))
                {
                    owner[f] = item;
                    return true;
                }
            }
            return false;
        }
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
        var bothHaveFile = name is not null && item.Files.Count > 0;
        var sameFile = bothHaveFile && item.Files.Any(x => string.Equals(FileName(x), name, StringComparison.OrdinalIgnoreCase));

        var near = sameFile && item.From is { } from && item.To is { } to && finding.Line is { } line
                   && line >= from - LineTolerance && line <= to + LineTolerance;

        var text = finding.Problem + "\n" + finding.Detail;
        var keyword = item.Keywords.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k) && ContainsKeyword(text, k));
        // 키워드만으로 짝을 지을 때 다른 파일을 가리키는 지적은 뺀다 (같은 낱말이 다른 결함에도 나오므로).
        var byKeyword = keyword is not null && (!bothHaveFile || sameFile);

        var lineText = item.From == item.To ? $"{item.From}" : $"{item.From}-{item.To}";
        return (near, byKeyword) switch
        {
            (true, true) => (3, $"{finding.Location} (기대 {lineText} ±{LineTolerance}) + 키워드 '{keyword}'"),
            (false, true) => (2, $"키워드 '{keyword}'{(sameFile ? $" + 같은 파일 {name}" : "")}"),
            (true, false) => (1, $"{finding.Location} (기대 {lineText} ±{LineTolerance}), 키워드 없음"),
            _ => null,
        };
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
