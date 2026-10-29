using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// 리플레이 골든 사례 만들기. 보관된 검토형 서브에이전트 결과를 그 리뷰의 세션 기록에서 복원한다:
/// 위임 메시지 = 첫 사용자 메시지, 파일 = Read 결과(toolUseResult.file — 원문 · 시작 줄 · 읽은 줄 수 · 전체 줄 수).
/// 정답지 = 당시 판정 (그 리뷰어 지적 중 채택 · 부분 = expected, 기각 = traps, 지휘자 추가 채택 = expected origin conductor).
/// 읽은 파일을 하나라도 통째로 읽지 않았으면 사례로 만들지 않는다 (모르는 줄을 비워 두면 리뷰어가 그 빈 줄을 코드로 본다).
/// 세션 기록 형식은 비공식 — 2026-10-27 확인한 모양(Claude Code 2.1.30x)에만 의존한다.
/// </summary>
public static partial class ReplayCaseBuilder
{
    /// <summary>후보 하나. Skip이 있으면 사례로 만들지 않는 이유.</summary>
    public sealed record Candidate(string RunId, SubagentResult Result, string? Mode, string? Skip);

    /// <summary>
    /// 세션 기록에서 꺼낸 것. Base = 위임 폴더(읽은 파일 경로에 가장 많이 나온 subagent_in/&lt;실행&gt;, 없으면 cwd),
    /// Files = 통째로 읽은 파일(Base 기준 상대 경로 → 원문), Partial = 일부만 읽었거나 Base 밖인 파일.
    /// </summary>
    public sealed record Transcript(string? Delegation, IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Partial)
    {
        public string Base { get; init; } = "";

        /// <summary>Base 밖에서 읽은 파일 (전체 경로). 작업 폴더 자리를 정할 수 없어 사례로 만들지 않는다.</summary>
        public IReadOnlyList<string> Outside { get; init; } = [];

        /// <summary>이 결과보다 앞에 다른 SubagentHandback이 있다 = SendMessage로 이어 부른 결과 (위임 메시지 · 파일 판본이 맞지 않음).</summary>
        public bool Continued { get; init; }
    }

    private static readonly Dictionary<string, string> ModeOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["self-reviewer"] = "sub_self", ["perf-reviewer"] = "sub_perf", ["security-reviewer"] = "sub_security", ["direction-reviewer"] = "sub_direction",
    };

    private static readonly JsonSerializerOptions CaseJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// 보관된 결과마다 후보와 건너뛸 이유. selfReview 실행(/self-review 기록)과 결과 원문이 같은 것만 잇는다 —
    /// /cross-review의 ownReview로 쓰인 결과는 판정이 review_* 실행에 섞여 있어 이번 판은 다루지 않는다 (사용자 결정 "/self-review 18건").
    /// </summary>
    /// <param name="verdictPath">판정 기록 파일 (보통 VerdictLog.PathFor(logPath) — 테스트가 실제 파일을 건드리지 않게 인자로 받는다).</param>
    public static List<Candidate> FindCandidates(string logPath, string verdictPath, string archiveFolder, Func<SubagentResult, bool> excluded)
    {
        // 같은 원문으로 selfReview 실행이 여럿이면 판정이 있는 실행을 쓴다. 판정 있는 실행이 둘 이상이면 모호해 건너뛴다 (sub_self 2f9f8ff874 C7).
        var judged = VerdictLog.Latest(VerdictLog.ReadAll(verdictPath)).Where(v => !v.IsRequest).Select(v => v.RunId).ToHashSet(StringComparer.Ordinal);
        var selfRuns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in CallLog.ReadAll(logPath))
            if (c is { Tool: VerdictRecorder.SelfReviewTool, Success: true, RunId: { } run, Response: { } response })
                (selfRuns.TryGetValue(Normalize(response), out var runs) ? runs : selfRuns[Normalize(response)] = []).Add(run);

        var list = new List<Candidate>();
        foreach (var r in SubagentArchive.ReadAll(archiveFolder).Results.OrderBy(r => r.ReturnedAt))
        {
            var runs = selfRuns.GetValueOrDefault(Normalize(r.Message)) ?? [];
            var judgedRuns = runs.Where(judged.Contains).Distinct().ToList();
            var run = judgedRuns.Count == 1 ? judgedRuns[0] : runs.FirstOrDefault();
            var mode = ModeOf.GetValueOrDefault(r.AgentType);
            var skip = run is null ? "selfReview 실행 없음 (cross-review ownReview 등)"
                : judgedRuns.Count > 1 ? $"같은 원문의 판정 있는 실행이 여럿 ({string.Join(", ", judgedRuns)})"
                : !judged.Contains(run) ? "판정 없음"
                : mode is null ? $"검토형 아님 ({r.AgentType})"
                : excluded(r) ? $"제외 프로젝트 ({r.ProjectDir})"
                : r.SourcePath is null || !File.Exists(r.SourcePath) ? "세션 기록 없음 (Claude Code가 지웠을 수 있음)"
                : r.Cwd is null ? "작업 폴더(cwd) 모름"
                : null;
            list.Add(new Candidate(run ?? "-", r, mode, skip));
        }
        return list;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    /// <summary>
    /// 세션 기록(jsonl) 읽기. 깨진 줄(JSON 오류 · 짝 없는 서로게이트)은 건너뛴다. Claude Code가 쓰는 중이어도 막지 않게 넓은 공유 모드로 연다.
    /// 서브에이전트의 cwd가 프로젝트 루트인 기록도 있어(파일은 subagent_in/&lt;실행&gt;/ 아래) 기준 폴더는 읽은 경로에서 정한다.
    /// handbackId가 있으면 그 SubagentHandback까지만 읽는다 — 이어 부른 기록에서 넘긴 뒤의 Read가 섞이지 않게 (sub_self 2f9f8ff874 C1).
    /// </summary>
    public static Transcript ReadTranscript(string path, string cwd, string? handbackId = null)
    {
        string? delegation = null;
        var continued = false;
        var reads = new List<(string Path, string Text, int Start, int Count, int Total)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                try
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    // 위임 메시지 = 첫 사용자 메시지(글). 도구 결과(배열)는 아니다.
                    if (delegation is null && Str(root, "type") == "user"
                        && root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                        && m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        delegation = content.GetString();

                    if (handbackId is not null && HandbackIds(root).ToList() is { Count: > 0 } handbacks)
                    {
                        if (handbacks.Contains(handbackId)) break;
                        continued = true; // 이 결과 앞에 다른 결과를 넘겼다
                    }

                    if (!root.TryGetProperty("toolUseResult", out var t) || t.ValueKind != JsonValueKind.Object
                        || !t.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.Object) continue;
                    if (Str(f, "filePath") is not { } filePath || Str(f, "content") is not { } text) continue; // 이미지 · PDF 등은 content가 없다
                    // 줄 정보가 없으면(형식이 바뀜) 통째로 읽었다고 보지 않는다 — Total -1은 Stitch가 거절 (C6)
                    var (start, count, total) = (Int(f, "startLine"), Int(f, "numLines"), Int(f, "totalLines"));
                    reads.Add((Path.GetFullPath(Path.Combine(cwd, filePath)), text, Math.Max(1, start ?? 1),
                        count ?? 0, start is null || count is null || total is null ? -1 : total.Value)); // 상대 경로로 읽은 기록은 cwd 기준
                }
                catch (InvalidOperationException) { } // 짝 없는 서로게이트 등 (SubagentTranscriptReader와 같이 그 줄만 건너뜀 — C2)
            }
        }

        var baseDir = reads.Select(r => SubagentRunDir(r.Path)).OfType<string>()
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? cwd;
        var full = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var partial = new List<string>();
        var outside = new List<string>();
        foreach (var file in reads.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (Relative(baseDir, file.Key) is not { } rel) outside.Add(file.Key);
            else if (Stitch([.. file.Select(r => (r.Text, r.Start, r.Count, r.Total))]) is { } text) full[rel] = text;
            else partial.Add(rel);
        }
        return new Transcript(delegation, full, [.. partial.Order(StringComparer.Ordinal)])
        {
            Base = baseDir, Outside = [.. outside.Order(StringComparer.Ordinal)], Continued = continued,
        };
    }

    /// <summary>이 기록 줄의 SubagentHandback tool_use id들.</summary>
    private static IEnumerable<string> HandbackIds(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
            || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object && Str(block, "type") == "tool_use"
                && Str(block, "name") == SubagentTranscriptReader.HandbackToolName && Str(block, "id") is { } id)
                yield return id;
    }

    /// <summary>
    /// 한 파일의 Read 결과들로 원문을 만든다. 한 번이라도 통째로 읽었으면 그 원문, 아니면 나눠 읽은 범위를 줄 단위로 이어 붙여
    /// 1 ~ 전체 줄이 다 덮이면 그 줄들을 "\n"으로 (마지막 줄바꿈은 알 수 없어 붙인다). 덮이지 않거나, 전체 줄 수가 읽을 때마다 다르거나
    /// (그 사이 파일이 바뀜 — 판본이 섞임, C1) 줄 정보가 없으면(Total -1) null.
    /// </summary>
    internal static string? Stitch(IReadOnlyList<(string Text, int Start, int Count, int Total)> reads)
    {
        if (reads.Select(r => r.Total).Distinct().Count() != 1) return null;
        var total = reads[0].Total;
        if (total < 0) return null;
        if (reads.FirstOrDefault(r => r.Start <= 1 && r.Count >= r.Total) is { Text: { } whole }) return whole;
        if (total == 0) return null;
        var lines = new string?[total];
        foreach (var (text, start, count, _) in reads)
        {
            var got = text.Split('\n');
            for (var i = 0; i < Math.Min(count, got.Length) && start - 1 + i < total; i++) lines[start - 1 + i] = got[i].TrimEnd('\r');
        }
        return lines.Any(l => l is null) ? null : string.Join('\n', lines) + "\n";
    }

    /// <summary>경로에 "subagent_in/&lt;실행&gt;/"이 있으면 그 폴더 (전체 경로), 없으면 null.</summary>
    internal static string? SubagentRunDir(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var at = Array.FindIndex(parts, p => p.Equals("subagent_in", StringComparison.OrdinalIgnoreCase));
        return at < 0 || at + 2 >= parts.Length ? null : Path.GetFullPath(string.Join('/', parts[..(at + 2)]));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>cwd 안이면 '/' 상대 경로, 밖이면 null.</summary>
    internal static string? Relative(string cwd, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : null;
    }

    /// <summary>
    /// 사례 폴더 replay-&lt;runId&gt;를 만든다. 이미 있으면 건드리지 않는다. Notes = 정답지에서 뺐거나 보강이 필요한 항목.
    /// exclude 글자가 위임 폴더 경로 · 위임 메시지에 있어도 뺀다 (개인 프로젝트 subagent_in에 회사 코드를 복사한 기록 — C5). 실패하면 만든 폴더를 지운다.
    /// </summary>
    public static (string? Folder, string? Skip, IReadOnlyList<string> Notes) Build(Candidate c, string goldenRoot, string verdictPath, IReadOnlyList<string>? exclude = null)
    {
        var notes = new List<string>();
        var id = $"replay-{c.RunId}";
        var folder = Path.Combine(goldenRoot, id);
        if (Directory.Exists(folder)) return (null, "이미 있음", notes);

        Transcript tr;
        // 결과 키 = <agentId>_<SubagentHandback id>
        var handbackId = c.Result.Key.StartsWith(c.Result.AgentId + "_", StringComparison.Ordinal) ? c.Result.Key[(c.Result.AgentId.Length + 1)..] : null;
        try { tr = ReadTranscript(c.Result.SourcePath!, c.Result.Cwd!, handbackId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, $"세션 기록을 읽지 못함 ({ex.Message})", notes); }
        if (tr.Continued) return (null, "이어 부른 결과 (위임 메시지 · 파일 판본이 맞지 않음)", notes);
        if (tr.Delegation is null) return (null, "위임 메시지를 찾지 못함", notes);
        if ((exclude ?? []).FirstOrDefault(x => tr.Base.Contains(x, StringComparison.OrdinalIgnoreCase) || tr.Delegation.Contains(x, StringComparison.OrdinalIgnoreCase)) is { } word)
            return (null, $"제외 글자 '{word}'가 위임 폴더 · 위임 메시지에 있음", notes);
        if (tr.Outside.Count > 0) return (null, $"위임 폴더 밖 파일을 읽음: {string.Join(", ", tr.Outside.Take(3).Select(Path.GetFileName))}{(tr.Outside.Count > 3 ? " …" : "")}", notes);
        if (tr.Partial.Count > 0) return (null, $"통째로 읽지 않은 파일: {string.Join(", ", tr.Partial.Take(3))}{(tr.Partial.Count > 3 ? " …" : "")}", notes);
        if (tr.Files.Count == 0) return (null, "읽은 파일 없음", notes);

        var verdicts = VerdictLog.Latest(VerdictLog.ReadAll(verdictPath))
            .Where(v => v.RunId == c.RunId && !v.IsRequest).ToList();
        var findings = GoldenScorer.ReadSelfReview(c.Result.Message);
        var (expected, traps) = Key(verdicts, findings, notes, c.Result.Message);
        if (expected.Count == 0) return (null, "채택된 지적 없음 (정답지가 빔)", notes);

        try
        {
            var files = Path.Combine(folder, GoldenCaseLoader.FilesFolder);
            var context = new List<ContextRange>();
            foreach (var (rel, text) in tr.Files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var dest = Path.GetFullPath(Path.Combine(files, rel));
                if (!dest.StartsWith(Path.GetFullPath(files) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"폴더 밖 경로: {rel}");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, text, new UTF8Encoding(false));
                if (CountLines(text) is var lines and > 0) context.Add(new ContextRange(rel, 1, lines));
            }

            Write(folder, GoldenCaseLoader.CaseFile, new GoldenCaseInfo
            {
                Id = id, Kind = GoldenCaseInfo.Replay, Mode = c.Mode!,
                Source = $"runId {c.RunId} · {c.Result.AgentType} · {c.Result.ReturnedAt?.ToLocalTime():yyyy-MM-dd HH:mm} · {Path.GetFileName(c.Result.ProjectDir?.TrimEnd('/', '\\'))}",
                Description = c.Result.Description,
            });
            File.WriteAllText(Path.Combine(folder, GoldenCaseLoader.InputFile), RelativizeDelegation(tr.Delegation, tr.Base), new UTF8Encoding(false));
            Write(folder, GoldenCaseLoader.ContextFile, context);
            Write(folder, GoldenCaseLoader.ExpectedFile, expected);
            if (traps.Count > 0) Write(folder, GoldenCaseLoader.TrapsFile, traps);

            // 만든 사례가 골든 사례로 읽히는지 바로 확인 (형식 오류면 지우고 이유를 돌려준다)
            if (GoldenCaseLoader.Load(folder).Error is { } loadError) throw new IOException($"만든 사례를 읽지 못함: {loadError}");
            return (folder, null, notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return (null, $"쓰기 실패 ({ex.Message})", notes);
        }
    }

    private static void Write<T>(string folder, string file, T value) =>
        File.WriteAllText(Path.Combine(folder, file), JsonSerializer.Serialize(value, CaseJson) + "\n", new UTF8Encoding(false));

    /// <summary>줄 수 (마지막 줄바꿈 뒤 빈 줄은 세지 않음, 빈 글은 0).</summary>
    internal static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + (text[^1] == '\n' ? 0 : 1);

    /// <summary>
    /// 위임 메시지의 subagent_in 폴더 절대 경로를 작업 폴더 기준으로 바꾼다: "&lt;폴더&gt;\a.cs" → "a.cs", 폴더 자체("&lt;폴더&gt;\" 뒤가 공백 · 끝) → "./".
    /// </summary>
    internal static string RelativizeDelegation(string text, string cwd)
    {
        var root = Path.TrimEndingDirectorySeparator(cwd);
        foreach (var form in new[] { root, root.Replace('\\', '/') }.Distinct())
        {
            text = Regex.Replace(text, Regex.Escape(form) + @"[\\/](?=[^\s\\/])", "", RegexOptions.IgnoreCase);
            // 폴더 자체: 뒤가 구분자 · ASCII 이름 글자가 아닌 것 · 끝일 때만 ("…_x2\a.cs"처럼 이웃 폴더 이름의 앞부분은 건드리지 않음 — C11)
            text = Regex.Replace(text, Regex.Escape(form) + @"(?=[\\/]|[^A-Za-z0-9_.\-]|$)[\\/]?", "./", RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// 판정 → 정답지. 리뷰어 지적(steps에 1): 채택 · 부분 = expected(origin self-review), 기각 = traps. 지휘자 추가(steps 없음) 채택 · 부분 = expected(origin conductor).
    /// 보류 · 중복은 넣지 않는다. 위치는 리뷰어 지적 줄(없으면 판정의 file), 필수 키워드는 지적 문장의 코드 이름.
    /// </summary>
    internal static (List<GoldenItem> Expected, List<GoldenItem> Traps) Key(IReadOnlyList<VerdictRecord> verdicts, IReadOnlyList<GoldenFinding> findings, List<string> notes, string review = "")
    {
        var expected = new List<GoldenItem>();
        var traps = new List<GoldenItem>();
        var byId = findings.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var v in verdicts.OrderBy(v => v.FindingKey, StringComparer.Ordinal))
        {
            var reviewer = v.Steps.Contains(1);
            var finding = reviewer ? FindingFor(v.FindingKey, byId) : null;
            if (reviewer && finding is null) notes.Add($"{v.FindingKey}: 리뷰어 지적 줄을 찾지 못해 판정의 요약 · 파일만 씀");
            switch (v.Verdict)
            {
                case Verdicts.Adopted or Verdicts.Partial:
                    if (Item(v, finding, reviewer ? "self-review" : "conductor", null, notes, review) is { } e) expected.Add(e);
                    break;
                case Verdicts.Rejected when reviewer:
                    if (Item(v, finding, "self-review", v.Reason, notes, review) is { } t) traps.Add(t);
                    break;
            }
        }
        return (expected, traps);
    }

    private static GoldenFinding? FindingFor(string key, IReadOnlyDictionary<string, GoldenFinding> byId) =>
        byId.TryGetValue(key, out var f) ? f
        : FindingKeyId().Match(key) is { Success: true } m && byId.TryGetValue(m.Value, out var g) ? g
        : null;

    [GeneratedRegex(@"C\d+")]
    private static partial Regex FindingKeyId();

    [GeneratedRegex(@"(?<file>[^\s,;():]+\.[A-Za-z0-9]+)(?::(?<line>\d+))?")]
    private static partial Regex FileLine();

    // 지적 머리 줄: "- C1 [major] 위치들 — 문제"
    [GeneratedRegex(@"^\s*[-*]\s*(?<id>C\d+)\s*\[[A-Za-z]+\]\s*(?<loc>.*?)\s+[—–]\s+", RegexOptions.Multiline)]
    private static partial Regex FindingHead();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthesized();

    [GeneratedRegex(@"^(?<file>[^\s:]+\.[A-Za-z0-9]+)(?::(?<from>\d+)(?:-(?<to>\d+))?)?$|^(?<from>\d+)(?:-(?<to>\d+))?$")]
    private static partial Regex LocationToken();

    /// <summary>
    /// 지적 머리의 위치를 모두 읽는다: "a.cs:237, b.cs:82-87" · "a.cs:124-129, 188-202, 88"(앞 파일의 다른 범위) · 괄호 설명은 뺀다.
    /// 원인 · 증상 지점이 다른 결함도 어느 한 곳만 짚으면 짝이 되게 (시범 replay-65843a0873: 다른 파일 줄로 짚어 놓침).
    /// </summary>
    internal static List<GoldenLocation> HeadLocations(string review, string id)
    {
        var head = FindingHead().Matches(review).FirstOrDefault(m => m.Groups["id"].Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        var result = new List<GoldenLocation>();
        if (head is null) return result;
        string? file = null;
        foreach (var raw in Parenthesized().Replace(head.Groups["loc"].Value, " ").Split([',', '·', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LocationToken().Match(raw) is not { Success: true } m) continue;
            if (m.Groups["file"].Success) file = m.Groups["file"].Value;
            if (file is null || !m.Groups["from"].Success || !int.TryParse(m.Groups["from"].Value, out var from) || from < 1) continue;
            int? to = m.Groups["to"].Success && int.TryParse(m.Groups["to"].Value, out var t) && t >= from ? t : null;
            result.Add(new GoldenLocation { File = file, From = from, To = to });
        }
        return result;
    }

    private static GoldenItem? Item(VerdictRecord v, GoldenFinding? f, string origin, string? why, List<string> notes, string review)
    {
        string? file = f?.File;
        int? line = f?.Line;
        if (file is null && v.File is { } vf && FileLine().Match(vf) is { Success: true } m)
        {
            file = m.Groups["file"].Value;
            line = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var n) && n > 0 ? n : null;
        }
        var required = Identifiers(f?.Problem ?? v.Summary);
        var keywords = Identifiers(v.Summary).Where(k => !required.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (line is null && required.Count == 0 && keywords.Count == 0)
        {
            notes.Add($"{v.FindingKey}: 줄 위치도 코드 이름도 없어 뺐다 — {Shorten(v.Summary)}");
            return null;
        }
        if (required.Count == 0)
            notes.Add($"{v.FindingKey}: 필수 키워드 없음 — 줄만 맞으면 '확인 필요'로만 보인다 (손으로 required 보강)");
        var locations = f is null ? [] : HeadLocations(review, f.Id);
        return new GoldenItem
        {
            Id = v.FindingKey, Summary = v.Summary, Severity = v.Severity ?? f?.Severity, Origin = origin, Why = why,
            Files = file is null ? [] : [file], Lines = file is not null && line is { } l ? [l] : [],
            Locations = locations.Count > 1 ? locations : [],   // 하나뿐이면 Files · Lines와 같다
            Required = required, Keywords = keywords,
        };
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "md", "json", "py", "ps1", "psm1", "txt", "shader", "hlsl", "cginc", "cpp", "h", "hpp", "c", "asmdef", "cshtml", "js", "ts", "yaml", "yml", "xml", "diff", "jsonl", "bat",
    };

    // 코드 이름: 백틱 안(공백 없음) · 점으로 이은 이름 · camelCase · 여러 혹 PascalCase · 밑줄 이름 · 0x 숫자.
    // 경계는 ASCII로만 본다 — \b는 한글도 글자로 봐서 "ReadText가"처럼 조사가 붙은 이름을 놓친다.
    [GeneratedRegex(@"`(?<tick>[^`\s]{2,60})`|(?<![A-Za-z0-9_])(?<code>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+|[a-z][a-z0-9]*[A-Z][A-Za-z0-9]*|[A-Z][a-z0-9]+[A-Z][A-Za-z0-9]*|[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+|0x[0-9A-Fa-f]+)(?![A-Za-z0-9_])")]
    private static partial Regex CodeName();

    /// <summary>
    /// 문장의 코드 이름 최대 4개 (필수 키워드 후보). 파일 이름(확장자로 끝남)은 뺀다 — 같은 파일의 다른 지적에도 다 맞아 짝을 흐린다.
    /// 끝의 "()"는 뗀다. 자동 추출이라 지휘자가 결과를 보고 보강한다.
    /// </summary>
    // 백틱 안에 흔히 나오는 예약어 · 기본 형식 — 어느 지적에나 맞아 짝을 흐린다 (sub_self 2f9f8ff874 C12)
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "true", "false", "this", "base", "var", "new", "void", "string", "int", "bool", "object", "async", "await", "return", "default", "static", "public", "private",
    };

    internal static List<string> Identifiers(string text)
    {
        var result = new List<string>();
        foreach (Match m in CodeName().Matches(text))
        {
            var name = (m.Groups["tick"].Success ? m.Groups["tick"].Value : m.Groups["code"].Value).Trim().TrimEnd('(', ')', ';', ',', '.');
            if (name.Length < 3 || !name.Any(char.IsAsciiLetter) || CommonWords.Contains(name)) continue;
            var dot = name.LastIndexOf('.');
            if (dot > 0 && FileExtensions.Contains(name[(dot + 1)..])) continue;
            // 점 이름 중 한 글자 조각(e.g) · 세 조각 이상 대문자 시작(HB.Orchestration.Agents 같은 네임스페이스 경로)은 뺀다
            var segments = name.Split('.');
            if (segments.Length > 1 && (segments.Any(s => s.Length <= 1) || (segments.Length >= 3 && segments.All(s => char.IsAsciiLetterUpper(s[0]))))) continue;
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
            if (result.Count == 4) break;
        }
        return result;
    }
}
