using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HB.Orchestration.Agents;
using HB.Orchestration.Agents.Providers;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Extensions.AI;
using OpenAI;

namespace HB.Orchestration.Tests;

/// <summary>
/// run_mode images: 파일 머리 검사(png·jpeg), 경로·장수·크기·긴 변 상한과 중복 거절, 입력을 받는 단계에만 붙음,
/// 첨부 자료 → 이름표+이미지 → 요청 순서, Vision=false 거절, 기록에는 요약만 (base64 없음), detail 전달.
/// </summary>
public sealed class ImageAttachmentTests : IDisposable
{
    private readonly ModeRunnerHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ── 파일 머리 ───────────────────────────────────────────────────────

    /// <summary>머리만 있는 png (서버는 머리만 읽는다). seed로 내용을 달리해 해시를 다르게 한다.</summary>
    private static byte[] Png(int width, int height, byte seed = 0)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[32] = seed;
        return bytes;
    }

    /// <summary>SOI · APP0 · SOF0 순서의 jpeg 머리.</summary>
    private static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,                              // APP0 (길이 4)
        0xFF, 0xC0, 0x00, 0x0B, 0x08,                                    // SOF0, 정밀도 8
        (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
    ];

    [Fact]
    public void Header_PngAndJpeg()
    {
        Assert.Equal(("image/png", 512, 288), ImageLoader.ReadHeader(Png(512, 288)));
        Assert.Equal(("image/jpeg", 640, 480), ImageLoader.ReadHeader(Jpeg(640, 480)));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x00, 0x00 })]                 // jpeg 시작 뒤 깨짐
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0 })] // gif
    public void Header_NotPngOrJpeg_IsNull(byte[] data) => Assert.Null(ImageLoader.ReadHeader(data));

    // ── 불러오기 ────────────────────────────────────────────────────────

    private string WriteImage(string relative, byte[] data)
    {
        var path = Path.Combine(_h.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return relative;
    }

    private (IReadOnlyList<AttachedImage>? Images, string? Error) Load(ImageOptions? options = null, ContextOptions? context = null, params string[] paths) =>
        ImageLoader.Load(paths, options ?? new ImageOptions(), context ?? new ContextOptions(), _h.Root);

    [Fact]
    public void Load_ValidImages_KeepsOrderAndShownPath()
    {
        WriteImage("docs/image/a.png", Png(512, 288));
        WriteImage("docs/image/b.jpg", Jpeg(300, 400));

        var (images, error) = Load(null, null, "docs/image/a.png", "docs\\image\\b.jpg");

        Assert.Null(error);
        Assert.Collection(images!,
            a => { Assert.Equal("docs/image/a.png", a.Path); Assert.Equal("image/png", a.MediaType); Assert.Equal(512, a.Width); },
            b => { Assert.Equal("docs/image/b.jpg", b.Path); Assert.Equal("image/jpeg", b.MediaType); Assert.Equal(400, b.Height); });
    }

    [Fact]
    public void Load_NoneIsEmpty() => Assert.Empty(Load().Images!);

    [Fact]
    public void Load_TooMany_Rejected()
    {
        var paths = Enumerable.Range(0, 3).Select(i => WriteImage($"i{i}.png", Png(10, 10, (byte)i))).ToArray();

        var (_, error) = Load(new ImageOptions { MaxCount = 2 }, null, paths);

        Assert.Contains("3장 / 상한 2장", error);
    }

    [Fact]
    public void Load_TooLargeDimension_AsksForShrink()
    {
        WriteImage("big.png", Png(1920, 1080));

        var (_, error) = Load(null, null, "big.png");

        Assert.Contains("1920x1080", error);
        Assert.Contains("shrink", error);
    }

    [Fact]
    public void Load_TooManyBytes_Rejected()
    {
        WriteImage("a.png", Png(10, 10));

        var (_, error) = Load(new ImageOptions { MaxBytes = 10 }, null, "a.png");

        Assert.Contains("상한 10바이트", error);
    }

    [Fact]
    public void Load_SameImageTwice_Rejected()
    {
        WriteImage("a.png", Png(10, 10));
        WriteImage("copy.png", Png(10, 10));

        var (_, error) = Load(null, null, "a.png", "copy.png");

        Assert.Contains("'copy.png': 같은 이미지", error);
    }

    [Theory]
    [InlineData("../outside.png", "루트 밖")]
    [InlineData("secrets.png", "")]          // DenyPatterns (secrets*)
    [InlineData("none.png", "파일이 없습니다")]
    [InlineData("note.txt", "png·jpeg 파일이 아니")]
    public void Load_BadPath_RejectsWholeCall(string bad, string expected)
    {
        WriteImage("ok.png", Png(10, 10));
        WriteImage("secrets.png", Png(10, 10, 1));
        WriteImage("note.txt", Encoding.UTF8.GetBytes("글"));

        var (images, error) = Load(null, null, "ok.png", bad);

        Assert.Null(images);
        Assert.StartsWith($"'{bad}'", error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Load_ExtraRoot_ShowsNameNotLocalPath()
    {
        var extra = Directory.CreateTempSubdirectory("hb-orch-img-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(extra, "x.png"), Png(10, 10));
            var context = new ContextOptions { ExtraRoots = new(StringComparer.OrdinalIgnoreCase) { ["shots"] = extra } };

            var (images, error) = Load(null, context, "@SHOTS/x.png");

            Assert.Null(error);
            Assert.Equal("@shots/x.png", Assert.Single(images!).Path);
        }
        finally { Directory.Delete(extra, recursive: true); }
    }

    // ── ModeRunner 경로 ─────────────────────────────────────────────────

    private string ShotsRoot()
    {
        _h.Options.Context.ExtraRoots["tc"] = _h.Root;
        WriteImage("a.png", Png(512, 288, 1));
        WriteImage("b.png", Png(512, 288, 2));
        return "@tc/";
    }

    private Task<string> Run(string mode, string[] images, ContextRange[]? context = null) =>
        _h.Runner.RunAsync(mode, "질문", null, null, context, images, ModeRunnerHarness.DefaultReason, null, CancellationToken.None);

    [Fact]
    public async Task Run_SendsLabelsAndImagesBeforeRequest()
    {
        var root = ShotsRoot();

        await Run("seq", [root + "a.png", root + "b.png"]);

        var contents = _h.Client.Received.Single().Messages.Single(m => m.Role == ChatRole.User).Contents;
        Assert.Collection(contents,
            c => Assert.Equal("[이미지 1] @tc/a.png", Assert.IsType<TextContent>(c).Text),
            c => Assert.Equal("image/png", Assert.IsType<DataContent>(c).MediaType),
            c => Assert.Equal("[이미지 2] @tc/b.png", Assert.IsType<TextContent>(c).Text),
            c => Assert.IsType<DataContent>(c),
            c => Assert.StartsWith("## 요청", Assert.IsType<TextContent>(c).Text));
    }

    [Fact]
    public async Task Run_WithContext_ContextBlockStaysFirst()
    {
        var root = ShotsRoot();
        File.WriteAllText(Path.Combine(_h.Root, "doc.md"), "# 표\n| 1 | 버튼 |\n");

        await Run("seq", [root + "a.png"], [new ContextRange(root + "doc.md", 1, 2)]);

        var contents = _h.Client.Received.Single().Messages.Single(m => m.Role == ChatRole.User).Contents;
        Assert.StartsWith("## 첨부 코드", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("[이미지 1] @tc/a.png", Assert.IsType<TextContent>(contents[1]).Text);
        Assert.IsType<DataContent>(contents[2]);
        Assert.StartsWith("## 요청", Assert.IsType<TextContent>(contents[3]).Text);
        Assert.Equal(_h.Records().Single().Prompt, ((TextContent)contents[0]).Text + ((TextContent)contents[3]).Text);
    }

    [Fact]
    public async Task Run_RecordsSummaryWithoutBase64()
    {
        var root = ShotsRoot();

        await Run("seq", [root + "a.png"]);

        var image = Assert.Single(_h.Records().Single().Images!);
        Assert.StartsWith("@tc/a.png (512x288, 33B, sha256:", image);
        Assert.DoesNotContain(Convert.ToBase64String(Png(512, 288, 1)), File.ReadAllText(_h.LogPath));
    }

    [Fact]
    public async Task Run_NoImages_RecordHasNoField()
    {
        await _h.Run("seq");

        Assert.Null(_h.Records().Single().Images);
        Assert.DoesNotContain("\"Images\"", File.ReadAllText(_h.LogPath));
    }

    [Fact]
    public async Task Run_Concurrent_EveryStepGetsImages()
    {
        var root = ShotsRoot();

        await Run("conc", [root + "a.png"]);

        Assert.Equal(2, _h.Client.Received.Count);
        Assert.All(_h.Client.Received, r => Assert.Single(r.Messages.Single(m => m.Role == ChatRole.User).Contents.OfType<DataContent>()));
    }

    [Fact]
    public async Task Run_StepWithoutInput_GetsNoImages()
    {
        // 2단계가 Previous면 입력을 안 받는다 → 이미지도 없음 (기본 InputAndPrevious는 받는다)
        File.WriteAllText(_h.ModesPath, ModeRunnerHarness.DefaultModes.Replace(
            "{ \"Role\": \"NoG\", \"Agent\": \"a1\" } ] }",
            "{ \"Role\": \"NoG\", \"Agent\": \"a1\", \"Sees\": \"Previous\" } ] }"));
        Assert.Contains("\"Sees\": \"Previous\"", File.ReadAllText(_h.ModesPath));
        var root = ShotsRoot();

        await Run("two", [root + "a.png"]);

        var records = _h.Records();
        Assert.NotNull(records[0].Images);
        Assert.Null(records[1].Images);
        Assert.Empty(_h.Client.Received[1].Messages.SelectMany(m => m.Contents).OfType<DataContent>());
    }

    [Fact]
    public async Task Run_VisionFalseAgent_RejectedBeforeCall()
    {
        var root = ShotsRoot();
        _h.Options.Agents[0].Vision = false;

        var result = await Run("seq", [root + "a.png"]);

        Assert.StartsWith("[모드 'seq'] 이미지를 받지 않는 에이전트(Vision=false): a1", result);
        Assert.Empty(_h.Client.Received);
        Assert.Contains("응답", await _h.Run("seq")); // 거절은 실행 횟수를 쓰지 않는다 (이미지 없이는 그대로 실행)
    }

    [Fact]
    public async Task Run_BadImage_ReturnsErrorWithoutCall()
    {
        var result = await Run("seq", ["none.png"]);

        Assert.StartsWith("[첨부 이미지 오류] 'none.png'", result);
        Assert.Empty(_h.Client.Received);
    }

    [Fact]
    public async Task Run_ProviderDetail_SetOnImageParts()
    {
        var root = ShotsRoot();
        _h.Options.Providers["fake"].ImageDetail = "low";

        await Run("seq", [root + "a.png"]);

        var data = _h.Client.Received.Single().Messages.SelectMany(m => m.Contents).OfType<DataContent>().Single();
        Assert.Equal("low", data.AdditionalProperties?["detail"]);
    }

    // ── 실제 HTTP 전송 본문 (OpenAI SDK + MEAI 경로) ─────────────────────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string reply = """
                {"id":"c","object":"chat.completion","created":1,"model":"m",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],
                 "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData("low")]
    [InlineData(null)]
    public async Task Http_ImagePart_IsDataUrlWithDetail(string? detail)
    {
        var handler = new CaptureHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        options.AddPolicy(new PromptCachePolicy(), PipelinePosition.PerCall);
        var client = new OpenAIClient(new ApiKeyCredential("k"), options).GetChatClient("m").AsIChatClient();
        var agent = new AgentEntry(new AgentOptions { Name = "a", Provider = "p", Model = "m" }, client)
        {
            ProviderOptions = new ProviderOptions { ImageDetail = detail },
        };
        var prefix = new string('x', StepCall.MinContextCacheChars);
        var image = new AttachedImage("a.png", "image/png", Png(10, 10), 10, 10, new string('0', 64));
        var call = new StepCall("r", "m", 1, "R", agent, "i", prefix + "## 요청", new CallMeta(null, [])) { ContextPrefix = prefix, Images = [image] };

        using (PromptCacheScope.Enter(PromptCacheMode.Context))
            await client.GetResponseAsync([AgentRunner.WithImages(call)]);

        var content = JsonNode.Parse(handler.Body!)!["messages"]!.AsArray().Last()!["content"]!.AsArray();
        Assert.Equal(4, content.Count);
        Assert.NotNull(content[0]!["prompt_cache_breakpoint"]); // 저장 지점은 첨부 자료 블록에 그대로
        var part = content[2]!["image_url"]!;
        Assert.StartsWith("data:image/png;base64,", part["url"]!.GetValue<string>());
        Assert.Equal(detail, part["detail"]?.GetValue<string>());
    }
}
