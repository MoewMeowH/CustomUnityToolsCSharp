using System.Security.Cryptography;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Dashboard;

/// <summary>호출 기록의 어느 목록인지. context = 첨부 코드, files = 관련 파일, images = 보낸 이미지.</summary>
public enum AttachmentKind { Context, Files, Images }

/// <summary>이미지 파일이 기록(sha256 앞 12자)과 같은지.</summary>
public enum ImageMatch { Same, Changed, Missing, NotImage }

/// <summary>
/// 기록에 적힌 항목 하나를 디스크 파일로 찾은 결과. Error가 있으면 열 수 없다 (경로 거절·없음).
/// StartLine·EndLine은 기록에 줄 범위가 있을 때만 (context "a.cs:10-40", files "a.cs:42").
/// </summary>
public sealed record AttachmentTarget(string Display, string? FullPath, string? Error, int? StartLine, int? EndLine, string? RecordedSha12)
{
    public bool CanOpen => Error is null && FullPath is not null;
}

/// <summary>
/// 대시보드 안 첨부 보기. 기록에 적힌 항목만 연다 — 임의 경로는 받지 않는다 (대시보드가 로컬 파일 서버가 되지 않도록).
/// 경로 규칙은 서버와 같다: 기록의 ProjectDir(+ ContextRoots) 기준, SafePath(루트 밖·링크 거절) + 전송 금지 패턴.
/// </summary>
public static class Attachments
{
    public static IReadOnlyList<string> Items(CallRecord call, AttachmentKind kind) => kind switch
    {
        AttachmentKind.Context => call.Context,
        AttachmentKind.Files => call.Files,
        _ => call.Images ?? [],
    };

    public static bool TryParseKind(string? raw, out AttachmentKind kind) =>
        Enum.TryParse(raw, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    /// <returns>순번이 범위 밖이면 null (404).</returns>
    public static AttachmentTarget? Find(CallRecord call, AttachmentKind kind, int index, IEnumerable<string> denyPatterns)
    {
        var items = Items(call, kind);
        if (index < 0 || index >= items.Count) return null;
        var raw = items[index];

        string path;
        int? start = null, end = null;
        string? sha12 = null;
        if (kind == AttachmentKind.Images)
            (path, sha12) = SplitImageRecord(raw);
        else
            (path, start, end) = SplitRange(raw);

        var baseDir = call.ProjectDir;
        var relative = path;
        if (ContextLoader.TrySplitRootPrefix(path, out var name, out var rest))
        {
            if (call.ContextRoots?.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) is not { Value: { } rootDir })
                return new AttachmentTarget(path, null, $"추가 루트 '@{name}'의 경로가 기록에 없습니다.", start, end, sha12);
            baseDir = rootDir;
            relative = rest;
        }
        if (string.IsNullOrEmpty(baseDir))
            return new AttachmentTarget(path, null, "기록에 프로젝트 폴더가 없습니다.", start, end, sha12);

        var (full, error) = SafePath.Resolve(relative, baseDir, denyPatterns, SafePath.Access.Read);
        return new AttachmentTarget(path, full, error, start, end, sha12);
    }

    /// <summary>"docs/a.png (512x288, 41,234B, sha256:0123456789ab)" → (경로, sha 앞 12자). 형식이 다르면 (원문, null).</summary>
    public static (string Path, string? Sha12) SplitImageRecord(string raw)
    {
        var open = raw.LastIndexOf(" (", StringComparison.Ordinal);
        var shaAt = raw.LastIndexOf("sha256:", StringComparison.Ordinal);
        if (open <= 0 || shaAt < open || !raw.EndsWith(')')) return (raw, null);
        var sha = raw[(shaAt + "sha256:".Length)..^1];
        return (raw[..open], sha.Length == 12 && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null);
    }

    /// <summary>"a.cs:10-40" → (a.cs, 10, 40), "a.cs:42" → (a.cs, 42, 42), "a.cs" → (a.cs, null, null).</summary>
    public static (string Path, int? Start, int? End) SplitRange(string raw)
    {
        var (path, line) = FileLinks.Split(raw.Trim());
        if (line is null) return (path, null, null);
        var tail = raw.Trim()[(path.Length + 1)..];
        var dash = tail.IndexOf('-');
        var end = dash >= 0 && int.TryParse(tail[(dash + 1)..], out var e) && e >= line ? e : line;
        return (path, line, end);
    }

    /// <summary>이미지 파일을 읽어 기록과 비교한다. 형식은 서버와 같은 검사(png·jpeg 머리)로 다시 확인한다.</summary>
    public static (ImageMatch Match, byte[]? Bytes, string? MediaType) ReadImage(AttachmentTarget target, long maxBytes)
    {
        if (!target.CanOpen) return (ImageMatch.Missing, null, null);
        byte[] bytes;
        try
        {
            var info = new FileInfo(target.FullPath!);
            if (!info.Exists) return (ImageMatch.Missing, null, null);
            if (maxBytes > 0 && info.Length > maxBytes) return (ImageMatch.NotImage, null, null);
            bytes = File.ReadAllBytes(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ImageMatch.Missing, null, null);
        }

        if (ImageLoader.ReadHeader(bytes) is not { } header) return (ImageMatch.NotImage, null, null);
        var sha12 = Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
        var match = target.RecordedSha12 is { } recorded && recorded == sha12 ? ImageMatch.Same : ImageMatch.Changed;
        return (match, bytes, header.MediaType);
    }

    public static string Describe(ImageMatch match) => match switch
    {
        ImageMatch.Same => "보낸 이미지와 같음",
        ImageMatch.Changed => "기록 이후 파일이 바뀜 — 보낸 이미지가 아님",
        ImageMatch.Missing => "파일 없음 (지워졌거나 옮겨짐)",
        _ => "이미지 파일이 아님",
    };
}
