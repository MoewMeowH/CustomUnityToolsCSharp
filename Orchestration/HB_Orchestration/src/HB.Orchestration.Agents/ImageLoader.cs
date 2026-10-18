using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HB.Orchestration.Agents;

/// <summary>
/// run_mode images 한 장. Path는 에이전트·기록에 보이는 경로 (추가 루트면 "@이름/…", 로컬 절대 경로는 넣지 않음).
/// </summary>
public sealed record AttachedImage(string Path, string MediaType, byte[] Data, int Width, int Height, string Sha256)
{
    /// <summary>호출 기록 한 줄: "docs/image/a.png (512x288, 41,234B, sha256:0123456789ab)". 원문(base64)은 기록에 남기지 않는다.</summary>
    public string RecordText => $"{Path} ({Width}x{Height}, {Data.Length:N0}B, sha256:{Sha256[..12]})";

    /// <summary>"@이름/…"으로 읽었으면 (설정의 이름, 그때의 절대 경로). 기록의 ContextRoots에 합쳐 대시보드가 같은 파일을 찾게 한다.</summary>
    public (string Name, string Path)? Root { get; init; }
}

/// <summary>
/// 줄여 둔 이미지 파일을 읽어 그대로 보낸다. 서버는 크기를 바꾸지 않는다 — 보내기 전 모습을 사용자가 미리 볼 수 있게
/// 축소는 image_prep.py shrink가 한다. 긴 변 상한은 축소를 빠뜨린 호출을 막는 안전장치다.
/// 경로 규칙은 context와 같다 (<see cref="SafePath"/>, "@이름/" 추가 루트). 외부 호출 전에 모두 검증하고, 하나라도 문제가 있으면 전체를 거절한다.
/// </summary>
public static class ImageLoader
{
    public static (IReadOnlyList<AttachedImage>? Images, string? Error) Load(
        IReadOnlyList<string>? paths, ImageOptions options, ContextOptions context, string rootDir, CancellationToken ct = default)
    {
        if (paths is null || paths.Count == 0) return ([], null);

        if (options.MaxCount > 0 && paths.Count > options.MaxCount)
            return (null, $"이미지가 {paths.Count}장 / 상한 {options.MaxCount}장. 필요한 것만 남기세요.");

        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootDir));
        var images = new List<AttachedImage>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) return (null, "images에 빈 항목이 있습니다.");

            var shown = raw;
            var relative = raw;
            var imageRoot = root;
            (string, string)? usedRoot = null;
            if (ContextLoader.TrySplitRootPrefix(raw, out var name, out var rest) && context.TryGetExtraRoot(name, out var key, out var rootPath))
            {
                if (ContextLoader.ExtraRootProblem(key, rootPath) is { } problem) return (null, $"'{raw}': ExtraRoots '{key}' — {problem}");
                imageRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(rootPath));
                relative = rest;
                shown = $"@{key}/{rest.Replace('\\', '/')}";
                usedRoot = (key, imageRoot);
            }
            else
            {
                shown = raw.Replace('\\', '/');
            }

            var (full, pathError) = SafePath.Resolve(relative, imageRoot, context.AllDenyPatterns, SafePath.Access.Read);
            if (pathError is not null) return (null, $"'{shown}': {pathError}");

            byte[] data;
            try
            {
                using var stream = new FileStream(full!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (options.MaxBytes > 0 && stream.Length > options.MaxBytes)
                    return (null, $"'{shown}': {stream.Length:N0}바이트 / 상한 {options.MaxBytes:N0}바이트. image_prep.py shrink로 줄인 파일을 넣으세요.");
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, $"'{shown}': 읽기 실패 ({ex.GetType().Name}: {ex.Message})");
            }

            if (ReadHeader(data) is not { } header)
                return (null, $"'{shown}': png·jpeg 파일이 아니거나 머리가 깨졌습니다.");

            var (mediaType, width, height) = header;
            if (options.MaxDimension > 0 && Math.Max(width, height) > options.MaxDimension)
                return (null, $"'{shown}': {width}x{height} / 긴 변 상한 {options.MaxDimension}px. image_prep.py shrink를 먼저 돌리세요.");

            var sha = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!seen.Add(sha)) return (null, $"'{shown}': 같은 이미지가 이미 들어 있습니다.");

            images.Add(new AttachedImage(shown, mediaType, data, width, height, sha) { Root = usedRoot });
        }

        return (images, null);
    }

    /// <summary>파일 머리에서 형식과 크기를 읽는다. png·jpeg가 아니거나 읽을 수 없으면 null.</summary>
    public static (string MediaType, int Width, int Height)? ReadHeader(ReadOnlySpan<byte> data)
    {
        // PNG: 8바이트 서명 + IHDR(길이 4 · "IHDR" 4 · 너비 4 · 높이 4)
        if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            && data[12..16].SequenceEqual("IHDR"u8))
        {
            var w = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
            var h = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
            return w > 0 && h > 0 ? ("image/png", w, h) : null;
        }

        // JPEG: SOI 뒤 세그먼트를 따라가며 SOF(C0~CF, C4·C8·CC 제외)의 높이·너비를 읽는다.
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF) return null;
                var marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }            // 채움 바이트
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // 길이 없는 표지
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..(i + 4)]);
                if (length < 2) return null;
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                {
                    if (i + 9 > data.Length) return null;
                    var h = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..(i + 7)]);
                    var w = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..(i + 9)]);
                    return w > 0 && h > 0 ? ("image/jpeg", w, h) : null;
                }
                i += 2 + length;
            }
        }

        return null;
    }
}
