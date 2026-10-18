using System.Buffers.Binary;
using System.Security.Cryptography;
using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>
/// 대시보드 첨부 보기: 기록에 적힌 항목만 (순번 밖은 null), 기록 형식 파싱, ProjectDir·ContextRoots 기준 경로,
/// 루트 밖·금지 패턴 거절, 이미지 sha 비교 (같음·바뀜·없음·이미지 아님).
/// </summary>
public sealed class AttachmentViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-orch-view-").FullName;
    private readonly string _extra = Directory.CreateTempSubdirectory("hb-orch-view-extra-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _extra })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static byte[] Png(int w, int h, byte seed = 0)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), w);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), h);
        bytes[32] = seed;
        return bytes;
    }

    private static string Sha12(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data))[..12];

    private void Write(string dir, string relative, byte[] data)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private CallRecord Record(IReadOnlyList<string>? context = null, IReadOnlyList<string>? files = null, IReadOnlyList<string>? images = null,
        Dictionary<string, string>? roots = null) => new()
    {
        Id = "abc", Timestamp = DateTimeOffset.Now, Tool = "seq", Model = "m", Prompt = "p", ProjectDir = _root,
        Context = context ?? [], Files = files ?? [], Images = images, ContextRoots = roots,
    };

    private static readonly IReadOnlyList<string> Deny = ContextOptions.BuiltInDenyPatterns;

    // ── 기록 형식 ───────────────────────────────────────────────────────

    [Fact]
    public void SplitImageRecord_PathAndSha()
    {
        Assert.Equal(("docs/a (1).png", "0123456789ab"), Attachments.SplitImageRecord("docs/a (1).png (512x288, 41,234B, sha256:0123456789AB)"));
        Assert.Equal(("그냥 경로.png", null), Attachments.SplitImageRecord("그냥 경로.png"));
    }

    [Theory]
    [InlineData("a.cs:10-40", "a.cs", 10, 40)]
    [InlineData("a.cs:42", "a.cs", 42, 42)]
    [InlineData("a.cs", "a.cs", null, null)]
    [InlineData("C:/x/a.cs:5", "C:/x/a.cs", 5, 5)]
    public void SplitRange(string raw, string path, int? start, int? end) =>
        Assert.Equal((path, start, end), Attachments.SplitRange(raw));

    [Theory]
    [InlineData("context", AttachmentKind.Context)]
    [InlineData("IMAGES", AttachmentKind.Images)]
    public void TryParseKind_Known(string raw, AttachmentKind expected)
    {
        Assert.True(Attachments.TryParseKind(raw, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("7")]
    [InlineData(null)]
    public void TryParseKind_Unknown(string? raw) => Assert.False(Attachments.TryParseKind(raw, out _));

    // ── 찾기 ────────────────────────────────────────────────────────────

    [Fact]
    public void Find_OnlyRecordedIndexes()
    {
        var call = Record(context: ["a.cs:1-2"]);

        Assert.NotNull(Attachments.Find(call, AttachmentKind.Context, 0, Deny));
        Assert.Null(Attachments.Find(call, AttachmentKind.Context, 1, Deny));
        Assert.Null(Attachments.Find(call, AttachmentKind.Context, -1, Deny));
        Assert.Null(Attachments.Find(call, AttachmentKind.Images, 0, Deny));
    }

    [Fact]
    public void Find_ContextUnderProjectDir_WithRange()
    {
        Write(_root, "src/A.cs", "class A {}"u8.ToArray());

        var target = Attachments.Find(Record(context: ["src/A.cs:3-9"]), AttachmentKind.Context, 0, Deny)!;

        Assert.True(target.CanOpen);
        Assert.Equal(Path.Combine(_root, "src", "A.cs"), target.FullPath);
        Assert.Equal((3, 9), (target.StartLine, target.EndLine));
    }

    [Fact]
    public void Find_ExtraRootFromRecord()
    {
        Write(_extra, "x.png", Png(10, 10));

        var target = Attachments.Find(Record(images: ["@shots/x.png (10x10, 33B, sha256:000000000000)"], roots: new() { ["Shots"] = _extra }),
            AttachmentKind.Images, 0, Deny)!;

        Assert.True(target.CanOpen);
        Assert.Equal(Path.Combine(_extra, "x.png"), target.FullPath);
    }

    [Fact]
    public void Find_ExtraRootNotInRecord_Error()
    {
        var target = Attachments.Find(Record(images: ["@shots/x.png (10x10, 33B, sha256:000000000000)"]), AttachmentKind.Images, 0, Deny)!;

        Assert.False(target.CanOpen);
        Assert.Contains("@shots", target.Error);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(".env")]
    [InlineData("secrets.json")]
    public void Find_OutsideOrDenied_Error(string path)
    {
        Write(_root, ".env", "K=1"u8.ToArray());
        Write(_root, "secrets.json", "{}"u8.ToArray());

        var target = Attachments.Find(Record(files: [path]), AttachmentKind.Files, 0, Deny)!;

        Assert.False(target.CanOpen);
        Assert.NotNull(target.Error);
    }

    // ── 이미지 비교 ─────────────────────────────────────────────────────

    private AttachmentTarget ImageTarget(string relative, string sha12) =>
        Attachments.Find(Record(images: [$"{relative} (10x10, 33B, sha256:{sha12})"]), AttachmentKind.Images, 0, Deny)!;

    [Fact]
    public void ReadImage_Same()
    {
        var data = Png(10, 10, 1);
        Write(_root, "a.png", data);

        var (match, bytes, type) = Attachments.ReadImage(ImageTarget("a.png", Sha12(data)), 2_000_000);

        Assert.Equal(ImageMatch.Same, match);
        Assert.Equal(data, bytes);
        Assert.Equal("image/png", type);
    }

    [Fact]
    public void ReadImage_ChangedAfterRecord()
    {
        Write(_root, "a.png", Png(10, 10, 2));

        Assert.Equal(ImageMatch.Changed, Attachments.ReadImage(ImageTarget("a.png", Sha12(Png(10, 10, 1))), 2_000_000).Match);
    }

    [Fact]
    public void ReadImage_Missing()
    {
        var target = ImageTarget("gone.png", "000000000000");

        Assert.Equal(ImageMatch.Missing, Attachments.ReadImage(target, 2_000_000).Match);
    }

    [Fact]
    public void ReadImage_NotImageOrTooBig_NoBytes()
    {
        Write(_root, "a.png", "글"u8.ToArray());
        Write(_root, "b.png", Png(10, 10));

        var (match, bytes, _) = Attachments.ReadImage(ImageTarget("a.png", "000000000000"), 2_000_000);
        Assert.Equal(ImageMatch.NotImage, match);
        Assert.Null(bytes);
        Assert.Equal(ImageMatch.NotImage, Attachments.ReadImage(ImageTarget("b.png", Sha12(Png(10, 10))), maxBytes: 10).Match);
    }
}
