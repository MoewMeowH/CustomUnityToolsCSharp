using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// 모델별 프롬프트 덧붙임: prompts/&lt;역할&gt;/&lt;에이전트&gt;.md를 시스템 프롬프트 맨 끝에 붙인다
///. 공통 프롬프트는 그대로 두고 모델 버릇 · 자주 하는 실수만 고치는 자리 — 역할 사본(Reviewer_DS)을 만들지 않는다.
/// prompts 폴더는 설정 Orchestration:PromptsPath, 비우면 modes.json 옆.
/// 실행마다 다시 읽고, 파일이 없거나 HTML 주석을 뺀 본문이 비면 붙이지 않는다 (기본 파일은 서식 안내 주석뿐, 결정 4).
/// 에이전트는 교체(agents) 뒤 실제 이름으로 찾는다.
/// </summary>
public static partial class PromptAppendLoader
{
    public const string Folder = "prompts";
    public const string Header = "\n\n## 모델별 덧붙임\n";

    /// <summary>붙일 문자열(Text = 머리 + 본문)과 원본 파일 바이트·해시.</summary>
    public sealed record Result(string Text, string Path, byte[] Bytes, string Sha256)
    {
        public FileProvenance Provenance => new() { Path = Path, Sha256 = Sha256, Bytes = Bytes.Length };
    }

    /// <summary>
    /// 덧붙임 파일이 없거나 본문(HTML 주석 제외)이 공백뿐이면 null. 역할·에이전트 이름이 경로로 쓸 수 없는 글자(영문·숫자·_·- 밖)를 가지면
    /// 덧붙임 파일을 둘 수 없으므로 역시 null — 실행은 막지 않는다 (역할 이름에는 글자 제한이 없다, /self-review f3ecfed5ff C1).
    /// 해시·원문 기록은 주석을 포함한 파일 그대로다.
    /// </summary>
    /// <param name="root">prompts 폴더 (<see cref="ResolveRoot"/>).</param>
    /// <exception cref="IOException">파일이 있는데 읽지 못했을 때 (호출부가 실행을 거절한다 — 조용히 빼면 다른 프롬프트로 돈다).</exception>
    /// <exception cref="InvalidDataException">주석 짝이 맞지 않을 때 (<see cref="Body"/>, 호출부가 실행을 거절한다).</exception>
    public static Result? Load(string root, string role, string agent)
    {
        if (!NameRegex().IsMatch(role) || !NameRegex().IsMatch(agent)) return null;
        var path = System.IO.Path.Combine(root, role, agent + ".md");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var body = Body(GuidelinesLoader.DecodeLikeReadAllText(bytes));
        if (string.IsNullOrWhiteSpace(body)) return null; // 빈 파일 · 주석뿐인 기본 파일은 시스템 프롬프트·해시를 바꾸지 않는다 (C5)
        return new Result(Header + body, System.IO.Path.GetFullPath(path), bytes, BlobStore.Sha256Hex(bytes));
    }

    /// <summary>보낼 본문: HTML 주석(&lt;!-- --&gt;)을 빼고 앞쪽 빈 줄을 지운다. 주석이 없으면 원문 그대로.</summary>
    /// <exception cref="InvalidDataException">주석을 뺀 뒤에도 &lt;!-- · --&gt;가 남을 때 (중첩 · 닫히지 않은 주석) — 안내문이 에이전트에게 새어 나간다.</exception>
    public static string Body(string text)
    {
        if (!text.Contains("<!--", StringComparison.Ordinal) && !text.Contains("-->", StringComparison.Ordinal)) return text;
        var body = CommentRegex().Replace(text, "");
        if (body.Contains("<!--", StringComparison.Ordinal) || body.Contains("-->", StringComparison.Ordinal))
            throw new InvalidDataException("HTML 주석 짝이 맞지 않습니다 (주석 안에 주석을 넣었거나 닫지 않음, 또는 본문에 화살표 같은 `-->` · `<!--` 문자열이 있음).");
        return body.TrimStart('\r', '\n');
    }

    /// <summary>
    /// prompts 폴더. 설정 PromptsPath가 있으면 그 폴더(상대 경로는 서버 폴더 기준 — ModesPath와 같다), 없으면 modes.json 옆.
    /// 설정했는데 폴더가 없으면 Error — modes.json 옆으로 넘어가면 덧붙임이 빠진 채 아무도 모르게 돈다.
    /// </summary>
    /// <param name="serverDir">서버 폴더 (appsettings.json이 있는 곳).</param>
    public static (string? Root, string? Error) ResolveRoot(string? configured, string? serverDir, string modesPath)
    {
        if (string.IsNullOrWhiteSpace(configured)) return (RootFor(modesPath), null);
        string full;
        try { full = System.IO.Path.GetFullPath(serverDir is not null && !System.IO.Path.IsPathRooted(configured) ? System.IO.Path.Combine(serverDir, configured) : configured); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, $"설정 Orchestration:PromptsPath 경로 형식 오류: '{configured}' ({ex.Message})"); }
        return Directory.Exists(full)
            ? (full, null)
            : (null, $"설정 Orchestration:PromptsPath로 지정한 폴더가 없습니다: {full} (modes.json 옆으로 넘어가지 않습니다 — 경로를 고치거나 지정을 지우세요)");
    }

    /// <summary>modes.json 옆 prompts 폴더 (PromptsPath를 비웠을 때).</summary>
    public static string RootFor(string modesPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(modesPath))!, Folder);

    /// <summary>
    /// list_modes · 대시보드 표시용: 본문(주석 제외)이 있는 덧붙임 파일의 "역할@에이전트" 목록 (이름 순).
    /// 기본 파일(주석뿐)은 빠진다. 못 읽는 파일은 실행이 거절되므로 보이게 남긴다. 폴더가 없거나 못 읽으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<string> List(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return [.. Directory.EnumerateDirectories(root)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.md").Select(f => (Role: System.IO.Path.GetFileName(dir), Agent: System.IO.Path.GetFileNameWithoutExtension(f), File: f)))
                .Where(x => NameRegex().IsMatch(x.Role) && NameRegex().IsMatch(x.Agent) && HasBody(x.File))
                .Select(x => $"{x.Role}@{x.Agent}")
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool HasBody(string file)
    {
        try { return !string.IsNullOrWhiteSpace(Body(GuidelinesLoader.DecodeLikeReadAllText(File.ReadAllBytes(file)))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return true; }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
}
