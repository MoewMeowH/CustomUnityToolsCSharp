using System.Text;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>직접 지정한 리뷰 지침 경로(설정·환경 변수)에 파일이 없음. run_mode는 실행 전에 거절한다.</summary>
public sealed class GuidelinesPathMissingException(string message) : IOException(message);

/// <summary>공통 리뷰 지침을 실행할 때마다 읽는다. Unity 프로젝트 쪽 파일을 고치면 바로 반영된다.</summary>
public static class GuidelinesLoader
{
    public const string Header = "\n\n## 프로젝트 지침\n";

    /// <summary>
    /// 시스템 프롬프트에 붙일 문자열(Text)과 출처. 파일이 없으면 Text는 "", Path는 null.
    /// Sha256·Bytes는 원본 파일 바이트 기준이다. 해시한 바이트와 붙인 본문이 같은 시점의 파일이 되도록 파일은 한 번만 읽는다.
    /// </summary>
    public sealed record Result(string Text, string? Path, byte[]? Bytes, string? Sha256);

    public const string EnvVar = "REVIEW_GUIDELINES_PATH";
    public const string FileName = "REVIEW_GUIDELINES.md";

    public static Result Load(OrchestrationOptions options) => Load(options, Environment.GetEnvironmentVariable, Environment.CurrentDirectory);

    /// <summary>
    /// 찾는 순서: 설정 GuidelinesPath → 환경 변수 REVIEW_GUIDELINES_PATH → 현재 폴더(세션 프로젝트 루트)의 REVIEW_GUIDELINES.md.
    /// 직접 지정한 경로(앞의 둘 중 첫 지정)에 파일이 없으면 <see cref="GuidelinesPathMissingException"/> — 다음 후보로 조용히 넘어가면
    /// 오타가 있을 때 다른 지침으로 돈다 (2026-10-22, modes.json 지정 경로와 같은 규칙). 지정이 없을 때만 현재 폴더를 찾는다.
    /// </summary>
    internal static Result Load(OrchestrationOptions options, Func<string, string?> env, string currentDirectory)
    {
        string? path;
        var (specified, source) = !string.IsNullOrWhiteSpace(options.GuidelinesPath) ? (options.GuidelinesPath, "설정 Orchestration:GuidelinesPath")
                                : env(EnvVar) is { } e && !string.IsNullOrWhiteSpace(e) ? (e, $"환경 변수 {EnvVar}")
                                : (null, null);
        if (specified is not null)
        {
            if (!File.Exists(specified))
                throw new GuidelinesPathMissingException(
                    $"{source}로 지정한 리뷰 지침 파일이 없습니다: {specified} (다른 지침 파일로 넘어가지 않습니다 — 경로를 고치거나 지정을 지우세요)");
            path = specified;
        }
        else
        {
            var found = System.IO.Path.Combine(currentDirectory, FileName);
            path = File.Exists(found) ? found : null;
        }
        if (path is null) return new Result("", null, null, null);

        var bytes = File.ReadAllBytes(path);
        return new Result(Header + DecodeLikeReadAllText(bytes), System.IO.Path.GetFullPath(path), bytes, BlobStore.Sha256Hex(bytes));
    }

    /// <summary>File.ReadAllText와 같은 디코딩 (UTF-8 기본, BOM이 있으면 BOM을 따르고 BOM은 본문에서 뺀다).</summary>
    internal static string DecodeLikeReadAllText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
