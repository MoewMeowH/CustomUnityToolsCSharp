using System.Text;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Agents;

/// <summary>공통 리뷰 지침을 실행할 때마다 읽는다. Unity 프로젝트 쪽 파일을 고치면 바로 반영된다.</summary>
public static class GuidelinesLoader
{
    public const string Header = "\n\n## 프로젝트 지침\n";

    /// <summary>
    /// 시스템 프롬프트에 붙일 문자열(Text)과 출처. 파일이 없으면 Text는 "", Path는 null.
    /// Sha256·Bytes는 원본 파일 바이트 기준이다. 해시한 바이트와 붙인 본문이 같은 시점의 파일이 되도록 파일은 한 번만 읽는다.
    /// </summary>
    public sealed record Result(string Text, string? Path, byte[]? Bytes, string? Sha256);

    public static Result Load(OrchestrationOptions options)
    {
        var candidates = new[]
        {
            options.GuidelinesPath,
            Environment.GetEnvironmentVariable("REVIEW_GUIDELINES_PATH"),
            System.IO.Path.Combine(Environment.CurrentDirectory, "REVIEW_GUIDELINES.md"),
        };

        var path = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
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
