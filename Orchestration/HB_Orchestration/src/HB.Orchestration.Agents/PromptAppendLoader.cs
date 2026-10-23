using System.Text.RegularExpressions;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>
/// 모델별 프롬프트 덧붙임: modes.json 옆 prompts/&lt;역할&gt;/&lt;에이전트&gt;.md를 시스템 프롬프트 맨 끝에 붙인다
/// (계획서 2026-10-23_모델별-프롬프트-덧붙임). 공통 프롬프트는 그대로 두고 모델 버릇만 고치는 자리 — 역할 사본(Reviewer_DS)을 만들지 않는다.
/// 실행마다 다시 읽고, 파일이 없으면 붙이지 않는다. 에이전트는 교체(agents) 뒤 실제 이름으로 찾는다.
/// 골든 사례가 있는 역할에만 두는 규칙은 서버가 막지 않고 list_modes · 대시보드에 보이게만 한다 (하네스 계획서 결정).
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
    /// 덧붙임 파일이 없거나 본문이 공백뿐이면 null. 역할·에이전트 이름이 경로로 쓸 수 없는 글자(영문·숫자·_·- 밖)를 가지면
    /// 덧붙임 파일을 둘 수 없으므로 역시 null — 실행은 막지 않는다 (역할 이름에는 글자 제한이 없다, /self-review f3ecfed5ff C1).
    /// </summary>
    /// <exception cref="IOException">파일이 있는데 읽지 못했을 때 (호출부가 실행을 거절한다 — 조용히 빼면 다른 프롬프트로 돈다).</exception>
    public static Result? Load(string modesPath, string role, string agent)
    {
        if (!NameRegex().IsMatch(role) || !NameRegex().IsMatch(agent)) return null;
        var path = System.IO.Path.Combine(RootFor(modesPath), role, agent + ".md");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var body = GuidelinesLoader.DecodeLikeReadAllText(bytes);
        if (string.IsNullOrWhiteSpace(body)) return null; // 빈 자리표시 파일은 시스템 프롬프트·해시를 바꾸지 않는다 (C5)
        return new Result(Header + body, System.IO.Path.GetFullPath(path), bytes, BlobStore.Sha256Hex(bytes));
    }

    /// <summary>prompts 폴더 (modes.json과 같은 폴더 아래).</summary>
    public static string RootFor(string modesPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(modesPath))!, Folder);

    /// <summary>list_modes · 대시보드 표시용: 있는 덧붙임 파일의 "역할@에이전트" 목록 (이름 순). 폴더가 없거나 못 읽으면 빈 목록.</summary>
    public static IReadOnlyList<string> List(string modesPath)
    {
        try
        {
            var root = RootFor(modesPath);
            if (!Directory.Exists(root)) return [];
            return [.. Directory.EnumerateDirectories(root)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.md").Select(f => (Role: System.IO.Path.GetFileName(dir), Agent: System.IO.Path.GetFileNameWithoutExtension(f))))
                .Where(x => NameRegex().IsMatch(x.Role) && NameRegex().IsMatch(x.Agent))
                .Select(x => $"{x.Role}@{x.Agent}")
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NameRegex();
}
