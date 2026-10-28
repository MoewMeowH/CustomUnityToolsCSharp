using System.Text.RegularExpressions;

namespace HB.Chat;

/// <summary>입력칸 '/' 목록 한 줄. Kind: command(명령 파일) · skill · builtin(Claude 기본 명령) · terminal(터미널 전용 기본 명령).</summary>
public sealed record CommandInfo(string Name, string? Description, string? Hint, string Kind);

/// <summary>
/// 입력칸 '/' 명령 · 스킬 목록 (Claude 앱 기능 A, 계획서 2026-10-28 명령-자동완성).
/// 정답 목록은 그 방 마지막 실행의 init(slash_commands — CLI가 플러그인 · 관리형 스킬까지 정한 것)이고, 설명은 아는 파일 위치에서 읽는다
/// (작업 폴더 · 사용자 `.claude/commands` · `.claude/skills`, init이 알려 준 플러그인 경로). init이 없으면(앱을 켠 뒤 첫 실행 전) 파일에서 찾은 것만.
/// 기본 명령도 넣는다(결정 "전부") — 설명은 init에 없어 잘 알려진 것만 앱이 짧게 단다. 밑줄 두 개로 시작하는 내부 이름은 뺀다.
/// 파일 내용(설명)은 그대로 돌려주고 화면이 글로만 그린다.
/// </summary>
public static partial class CommandCatalog
{
    public const int MaxFiles = 500, MaxBytes = 64 * 1024, ReadBytes = 8 * 1024, MaxDescription = 160;

    /// <summary>잘 알려진 기본 명령의 짧은 설명 (init에는 이름만 온다).</summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltinDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["compact"] = "대화를 요약해 컨텍스트를 줄임",
        ["clear"] = "대화 기록을 비움",
        ["model"] = "모델 바꾸기",
        ["effort"] = "추론 강도 바꾸기",
        ["context"] = "컨텍스트 사용량 보기",
        ["usage"] = "사용량 · 한도 보기",
        ["init"] = "작업 폴더에 CLAUDE.md 만들기",
        ["rename"] = "세션 이름 바꾸기",
        ["recap"] = "지금까지 한 일 요약",
        ["agents"] = "서브에이전트 관리",
        ["mcp"] = "MCP 서버 상태",
        ["config"] = "설정",
        ["output-style"] = "답 모양(출력 스타일) 바꾸기",
        ["fast"] = "빠른 모드 켜기 · 끄기",
        ["autocompact"] = "자동 압축 설정",
        ["security-review"] = "지금 변경의 보안 리뷰",
        ["ultrareview"] = "클라우드 다중 에이전트 리뷰 (과금)",
        ["insights"] = "사용 기록 분석",
        ["goal"] = "목표 정하기",
    };

    public static IReadOnlyList<CommandInfo> Build(InitEvent? init, string? workspace, string? userClaudeDir)
    {
        var files = new Scanner();
        if (workspace is not null)
        {
            files.Commands(Path.Combine(workspace, ".claude", "commands"), null);
            files.Skills(Path.Combine(workspace, ".claude", "skills"), null);
        }
        if (userClaudeDir is not null)
        {
            files.Commands(Path.Combine(userClaudeDir, "commands"), null);
            files.Skills(Path.Combine(userClaudeDir, "skills"), null);
        }
        foreach (var (plugin, path) in init?.Plugins ?? [])
        {
            if (!IsName(plugin) || !Path.IsPathFullyQualified(path)) continue; // "builtin" 같은 것은 경로가 아니다
            files.Skills(Path.Combine(path, "skills"), plugin);
            files.Commands(Path.Combine(path, "commands"), plugin);
        }

        if (init is null)
            return Sorted(files.Found.Values);

        // 플러그인 접두어 이름은 그 플러그인 파일에서만 설명을 찾는다 — 끝부분 일치로 남의 설명이 붙지 않게 (/self-review db1b130ff2 C2)
        var plugins = new HashSet<string>((init.Plugins ?? []).Select(p => p.Name), StringComparer.Ordinal);
        var list = new List<CommandInfo>();
        foreach (var name in init.SlashCommands.Distinct(StringComparer.Ordinal))
        {
            if (!IsName(name) || name.StartsWith("__", StringComparison.Ordinal)) continue;
            var isSkill = init.Skills.Contains(name, StringComparer.Ordinal);
            var colon = name.IndexOf(':');
            var pluginName = colon > 0 && plugins.Contains(name[..colon]);
            if (files.Lookup(name, allowTail: !pluginName) is { } file)
            {
                // 종류는 init의 스킬 소속이 먼저 (파일 쪽은 설명 · 힌트만)
                list.Add(file with { Name = name, Kind = isSkill ? "skill" : file.Kind });
                continue;
            }
            if (isSkill)
            {
                list.Add(new CommandInfo(name, null, null, "skill"));
                continue;
            }
            var terminal = init.TerminalCommands.Contains(name, StringComparer.Ordinal);
            var known = BuiltinDescriptions.TryGetValue(name, out var d) ? d : "Claude 기본 명령";
            list.Add(terminal
                ? new CommandInfo(name, known + " (터미널 전용 — 앱에서는 안 될 수 있음)", null, "terminal")
                : new CommandInfo(name, known, null, "builtin"));
        }
        return Sorted(list);
    }

    /// <summary>목록 차례: 내 명령 · 스킬 먼저, 기본 명령, 터미널 전용 — 같은 종류는 이름 순(Ordinal — 환경에 따라 바뀌지 않게, db1b130ff2 C8).</summary>
    private static IReadOnlyList<CommandInfo> Sorted(IEnumerable<CommandInfo> items) =>
        [.. items.OrderBy(c => c.Kind switch { "command" => 0, "skill" => 1, "builtin" => 2, _ => 3 }).ThenBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>명령 이름 모양 (글자 · 숫자로 시작, 글자 · 숫자 · : . _ - 80자까지 — 글로만 그리고 글로만 보낸다).</summary>
    public static bool IsName(string? name) => name is not null && NameShape().IsMatch(name);

    /// <summary>
    /// 앞머리(frontmatter — 첫 줄 "---"부터 다음 "---"까지)의 description · argument-hint · name. 값은 따옴표를 벗기고,
    /// "&gt;" · "|" 뒤 들여쓴 줄은 이어 붙인다. 첫 줄만 쓰고 <see cref="MaxDescription"/>자에서 자른다.
    /// </summary>
    public static (string? Name, string? Description, string? Hint) Frontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, null, null);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var m = KeyLine().Match(lines[i]);
            if (!m.Success) continue;
            var value = m.Groups[2].Value.Trim();
            // "> · |" 블록, 또는 값이 비고 다음 줄부터 들여쓴 일반 여러 줄 값 (지휘자 검토 R3)
            if (value is ">" or "|" or ">-" or "|-" || value.Length == 0)
            {
                var parts = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].StartsWith(' ') && lines[i + 1].Trim() != "---") parts.Add(lines[++i].Trim());
                value = string.Join(" ", parts);
            }
            values[m.Groups[1].Value] = Unquote(value);
        }
        return (Get("name"), Cut(Get("description")), Cut(Get("argument-hint")));

        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;

    private static string? Cut(string? s)
    {
        if (s is null) return null;
        var line = s.Split('\n')[0].Trim();
        return line.Length <= MaxDescription ? line : line[..MaxDescription] + "…";
    }

    /// <summary>
    /// 파일 훑기: 위치(폴더)마다 파일 수 상한 · 큰 파일 건너뜀 · 앞 <see cref="ReadBytes"/>만 읽음 · 읽기 실패는 조용히 건너뜀.
    /// 같은 이름은 먼저 찾은 것(작업 폴더)이 이긴다.
    /// </summary>
    private sealed class Scanner
    {
        public Dictionary<string, CommandInfo> Found { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// 로컬에 없는 클라우드 파일(OneDrive 파일 주문형 등 — 읽으면 내려받기가 일어남)만 건너뛴다. 링크는 따라가고 깊이 3이 고리를 막는다
        /// (ReparsePoint 전체를 빼면 클라우드 동기화 폴더의 로컬 파일까지 빠졌다 — /self-review db1b130ff2 C1).
        /// </summary>
        private const FileAttributes NotLocal = FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

        /// <summary>명령 파일 `*.md` — 하위 폴더는 "폴더:이름". 플러그인이면 "플러그인:이름".</summary>
        public void Commands(string dir, string? prefix)
        {
            foreach (var file in Files(dir, "*.md"))
            {
                var rel = Path.GetRelativePath(dir, file);
                var name = string.Join(':', Path.ChangeExtension(rel, null).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (Read(file) is not { } text) continue;
                var (_, desc, hint) = Frontmatter(text);
                Add(Prefixed(prefix, name), desc, hint, "command");
            }
        }

        /// <summary>스킬 `*/SKILL.md` (관리형 스킬처럼 한두 단계 더 깊어도) — 이름은 앞머리 name, 없으면 폴더 이름.</summary>
        public void Skills(string dir, string? prefix)
        {
            foreach (var file in Files(dir, "SKILL.md"))
            {
                if (Read(file) is not { } text) continue;
                var (fmName, desc, hint) = Frontmatter(text);
                var name = fmName ?? Path.GetFileName(Path.GetDirectoryName(file))!;
                Add(Prefixed(prefix, name), desc, hint, "skill");
            }
        }

        /// <summary>
        /// init 이름에 맞는 파일 항목: 같은 이름, 없으면(allowTail일 때만) ':' 뒤 이름이 같은 것이 하나뿐일 때
        /// (관리형 스킬 "anthropic-skills:docx" ↔ 파일 "docx"). 플러그인 이름은 끝부분 일치를 하지 않는다.
        /// </summary>
        public CommandInfo? Lookup(string name, bool allowTail = true)
        {
            if (Found.TryGetValue(name, out var exact)) return exact;
            var colon = name.LastIndexOf(':');
            if (!allowTail || colon < 0) return null;
            var tail = name[(colon + 1)..];
            var hits = Found.Values.Where(c => c.Name == tail || c.Name.EndsWith(":" + tail, StringComparison.Ordinal)).Take(2).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }

        private void Add(string name, string? desc, string? hint, string kind)
        {
            if (IsName(name)) Found.TryAdd(name, new CommandInfo(name, desc, hint, kind));
        }

        private static string Prefixed(string? prefix, string name) => prefix is null ? name : prefix + ":" + name;

        /// <summary>
        /// 한 위치의 파일들 — 위치마다 <see cref="MaxFiles"/>개 (앞 위치에 문서가 많아도 뒤 위치 · 플러그인이 빠지지 않게, db1b130ff2 C3).
        /// 열거는 지연이라 MoveNext마다 예외를 받아 그 위치만 멈춘다 (훑는 중 폴더가 지워져도 목록 요청이 실패하지 않게, C6).
        /// </summary>
        private static IEnumerable<string> Files(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) yield break;
            IEnumerator<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = NotLocal,
                }).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                yield break;
            }
            using (files)
            {
                for (var budget = MaxFiles; budget > 0; budget--)
                {
                    try
                    {
                        if (!files.MoveNext()) yield break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        yield break;
                    }
                    yield return files.Current;
                }
            }
        }

        /// <summary>앞 <see cref="ReadBytes"/>만 읽는다 (앞머리만 쓴다 — 지휘자 검토 R2). 큰 파일은 건너뛴다.</summary>
        private static string? Read(string file)
        {
            try
            {
                using var stream = File.OpenRead(file);
                if (stream.Length > MaxBytes) return null;
                var buffer = new byte[Math.Min(ReadBytes, (int)stream.Length)];
                var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:._-]{0,79}$")]
    private static partial Regex NameShape();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]*)\s*:\s?(.*)$")]
    private static partial Regex KeyLine();
}
