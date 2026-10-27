using System.Text;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Agents;

/// <summary>
/// claude-cli 작업 폴더의 정의 색인 (계획서 2026-10-27_작업폴더-정의-색인).
/// 이 PC의 claude -p는 Read만 주므로(Grep · Glob 없음) 에이전트가 정의 위치를 모른 채 파일을 통째로 읽는다 —
/// 파일 목록에 타입 · 메서드 · 제목 이름과 줄 번호를 붙여 필요한 범위만 읽게 한다.
/// 줄 단위 정규식이라 대략의 위치다 (여러 줄 주석 · 문자열 안은 따지지 않음). 에이전트는 Read로 확인한다.
/// </summary>
public static partial class DefinitionIndex
{
    /// <summary>파일 하나에서 보이는 정의 수. 넘으면 "(… 정의 N개 더)".</summary>
    public const int MaxEntriesPerFile = 80;

    /// <summary>색인 줄 전체 글자 수 상한 (프롬프트에 들어간다). 넘으면 남은 파일은 줄 수만.</summary>
    public const int MaxTotalChars = 20_000;

    /// <summary>이보다 큰 파일은 색인하지 않는다.</summary>
    public const long MaxFileBytes = 1_000_000;

    private const int MaxLabelChars = 60;

    public readonly record struct Entry(int Line, string Label);

    /// <summary>확장자로 고른 규칙의 정의 목록 (줄 순서). 모르는 확장자는 빈 목록.</summary>
    public static IReadOnlyList<Entry> Outline(string path, string text)
    {
        var lines = text.TrimStart('﻿').Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => PerLine(lines, CSharp),
            ".py" => Python(lines),
            ".md" => Markdown(lines),
            ".json" => PerLine(lines, Json),
            ".ps1" or ".psm1" => PerLine(lines, PowerShell),
            _ => [],
        };
    }

    /// <summary>"  정의: class A 3 · Run() 10 · …" 한 줄. 정의가 없으면 null.</summary>
    public static string? Format(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) return null;
        var text = new StringBuilder("  정의: ");
        text.AppendJoin(" · ", entries.Take(MaxEntriesPerFile).Select(e => $"{e.Label} {e.Line}"));
        if (entries.Count > MaxEntriesPerFile) text.Append($" · (… 정의 {entries.Count - MaxEntriesPerFile}개 더)");
        return text.ToString();
    }

    /// <summary>UTF-8로 읽을 수 있고 MaxFileBytes 이하인 파일의 본문. 아니면 null (색인 없음).</summary>
    public static string? ReadText(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) return null;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static List<Entry> PerLine(string[] lines, Func<string, string?> label)
    {
        var entries = new List<Entry>();
        for (var i = 0; i < lines.Length; i++)
            if (label(lines[i]) is { } found) entries.Add(new Entry(i + 1, Clip(found)));
        return entries;
    }

    /// <summary>MaxLabelChars에서 자르되 서로게이트 쌍(이모지 등)을 가르지 않는다.</summary>
    private static string Clip(string label)
    {
        if (label.Length <= MaxLabelChars) return label;
        var cut = char.IsHighSurrogate(label[MaxLabelChars - 1]) ? MaxLabelChars - 1 : MaxLabelChars;
        return label[..cut] + "…";
    }

    // ── C# ─────────────────────────────────────────────────────────────
    // 특성: [이름(…)] — 한 단계 중첩 대괄호까지 ([InlineData(new[] { 1 })]). 문자열 안의 ']'는 따지지 않는다.
    private const string CsAttrs = @"(?:\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*)*";

    // 타입 선언: 특성 · 한정자 뒤 class · struct · interface · enum · record [class|struct] 이름
    [GeneratedRegex(@"^\s*" + CsAttrs + @"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|file|new)\s+)*(?<kind>class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>@?[A-Za-z_]\w*)")]
    private static partial Regex CsType();

    // 멤버는 한정자가 하나 이상 있을 때만 본다 — 한정자 없는 줄은 메서드 호출 문장과 구별이 안 된다 (인터페이스 멤버 · 한정자 없는 private는 빠짐).
    // 'new'는 넣지 않는다 — "new Foo(…)," 같은 컬렉션 초기화 줄이 메서드로 잡힌다 (public new …는 public으로 잡힘).
    [GeneratedRegex(@"^\s*" + CsAttrs + @"(?<mods>(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|partial|readonly|required|const|file)\s+)+)(?<rest>.*)$")]
    private static partial Regex CsMember();

    // 한정자 없이 void · IEnumerator로 시작하는 메서드 (Unity 메시지 메서드 void Update() · IEnumerator Start()) — 호출 문장과 헷갈리지 않는다 (sub_self 535ca00022 C4).
    [GeneratedRegex(@"^\s*" + CsAttrs + @"(?:async\s+)?(?:void|IEnumerator(?:<[^>]*>)?)\s+(?<name>@?[A-Za-z_]\w*)\s*(?:<[\w\s,]*>)?\s*\(")]
    private static partial Regex CsBareMethod();

    // 변환 · 연산자: implicit/explicit operator 형식, operator 기호 — 생성자처럼 보이지 않게 따로 (C9)
    [GeneratedRegex(@"\b(?:(?:implicit|explicit)\s+operator\s+(?<type>[\w.?]+(?:<[^()]*>)?)|operator\s*(?<op>[^\s(]+))\s*\(")]
    private static partial Regex CsOperator();

    // 속성: 이름 바로 뒤 '{' 또는 '=>' (그 앞에 '=' ';' '(' 없음)
    [GeneratedRegex(@"^[^=;(]*?\b(?<name>@?[A-Za-z_]\w*)\s*(?:\{|=>)")]
    private static partial Regex CsProperty();

    // 메서드 · 생성자: 가장 앞의 "이름[<형식 매개변수>](" (반환 형식의 튜플 괄호는 건너뛴다).
    // 형식 매개변수는 이름 · 쉼표만 — "Task<T> GetAsync<T>("에서 Task가 잡히지 않게 '<' '>'를 안에 두지 않는다.
    [GeneratedRegex(@"^[^=;]*?\b(?<name>@?[A-Za-z_]\w*)\s*(?:<[\w\s,]*>)?\s*\(")]
    private static partial Regex CsMethod();

    // const · static readonly 필드: "이름 ="
    [GeneratedRegex(@"^[^=;(]*?\b(?<name>@?[A-Za-z_]\w*)\s*=(?![>=])")]
    private static partial Regex CsField();

    private static readonly HashSet<string> CsNotNames =
        ["if", "for", "foreach", "while", "switch", "catch", "using", "return", "new", "lock", "nameof", "typeof", "sizeof", "default", "base", "this", "when", "await", "throw", "get", "set", "init"];

    private static string? CSharp(string line)
    {
        if (CsType().Match(line) is { Success: true } type)
            return $"{Regex.Replace(type.Groups["kind"].Value, @"\s+", " ")} {type.Groups["name"].Value}";
        if (CsMember().Match(line) is not { Success: true } member)
            return CsBareMethod().Match(line) is { Success: true } bare && !CsNotNames.Contains(bare.Groups["name"].Value) ? bare.Groups["name"].Value + "()" : null;
        var rest = member.Groups["rest"].Value;
        if (CsOperator().Match(rest) is { Success: true } op)
            return "operator " + (op.Groups["type"].Success ? op.Groups["type"].Value : op.Groups["op"].Value);
        if (CsProperty().Match(rest) is { Success: true } property && !CsNotNames.Contains(property.Groups["name"].Value))
            return property.Groups["name"].Value;
        if (CsMethod().Match(rest) is { Success: true } method && !CsNotNames.Contains(method.Groups["name"].Value))
            return method.Groups["name"].Value + "()";
        var mods = member.Groups["mods"].Value;
        var constant = mods.Contains("const ") || (mods.Contains("static ") && mods.Contains("readonly "));
        return constant && CsField().Match(rest) is { Success: true } field ? field.Groups["name"].Value : null;
    }

    // ── Python: 들여쓰기로 중첩을 따라가 Class.method ────────────────────────────
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<kind>async\s+def|def|class)\s+(?<name>[A-Za-z_]\w*)")]
    private static partial Regex PyDef();

    private static List<Entry> Python(string[] lines)
    {
        var entries = new List<Entry>();
        var stack = new List<(int Indent, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            // def · class가 아닌 줄(모듈 수준 if · try · 대입)도 들여쓰기로 중첩을 닫는다 — 빈 줄 · 주석은 건너뛴다 (C5)
            var code = lines[i].TrimStart(' ', '\t');
            if (code.Length == 0 || code[0] == '#') continue;
            var indent = lines[i][..^code.Length].Replace("\t", "    ").Length;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (PyDef().Match(lines[i]) is not { Success: true } m) continue;
            var name = m.Groups["name"].Value;
            var qualified = string.Join('.', stack.Select(s => s.Name).Append(name));
            entries.Add(new Entry(i + 1, Clip(m.Groups["kind"].Value == "class" ? "class " + qualified : qualified + "()")));
            stack.Add((indent, name));
        }
        return entries;
    }

    // ── Markdown: 코드 블록 밖의 제목 ──────────────────────────────────
    // 닫는 '#'은 앞에 공백이 있을 때만 (CommonMark) — "## Unity C#"의 '#'을 지우지 않는다 (C1)
    [GeneratedRegex(@"^[ ]{0,3}(?<level>#{1,6})[ \t]+(?<title>.+?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex MdHeading();

    [GeneratedRegex(@"^[ ]{0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex MdFence();

    private static List<Entry> Markdown(string[] lines)
    {
        var entries = new List<Entry>();
        string? open = null;   // 연 코드 블록의 기호 — 같은 글자로 그 길이 이상, 뒤에 글이 없는 줄만 닫는다 (C2)
        for (var i = 0; i < lines.Length; i++)
        {
            if (MdFence().Match(lines[i]) is { Success: true } fence)
            {
                var mark = fence.Groups["fence"].Value;
                if (open is null) { open = mark; continue; }
                if (mark[0] == open[0] && mark.Length >= open.Length && string.IsNullOrWhiteSpace(fence.Groups["info"].Value)) { open = null; continue; }
            }
            if (open is null && MdHeading().Match(lines[i]) is { Success: true } m)
                entries.Add(new Entry(i + 1, Clip($"{m.Groups["level"].Value} {m.Groups["title"].Value}")));
        }
        return entries;
    }

    // ── JSON: 들여쓰기 4칸 이하의 키 (예: modes.json Roles.X · Modes.Y) ──────────────
    [GeneratedRegex(@"^(?<indent>[ ]{0,4})""(?<key>[^""]+)""\s*:")]
    private static partial Regex JsonKey();

    private static string? Json(string line) => JsonKey().Match(line) is { Success: true } m ? m.Groups["key"].Value : null;

    // ── PowerShell ─────────────────────────────────────────────────────
    [GeneratedRegex(@"^\s*function\s+(?<name>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PsFunction();

    private static string? PowerShell(string line) => PsFunction().Match(line) is { Success: true } m ? $"function {m.Groups["name"].Value}" : null;
}
