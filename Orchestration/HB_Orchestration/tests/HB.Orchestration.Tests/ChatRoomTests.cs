using System.Text;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.ChatUi;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>
/// 채팅방: 세션 기록 본문 읽기 · 스티커 치환과 파일 범위 · 말풍선 엮기.
/// 세션 기록은 합성 데이터 (실제 기록에는 대화 전체가 있어 커밋하지 않는다).
/// </summary>
public sealed class ChatRoomTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-orch-chat-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Line(object o) => JsonSerializer.Serialize(o);

    private static string Ts(int second) => $"2026-10-27T07:00:{second:00}.000Z";

    private static DateTimeOffset At(int second) => DateTimeOffset.Parse(Ts(second), System.Globalization.CultureInfo.InvariantCulture);

    private static string User(int second, object content, bool meta = false) => Line(new
    {
        type = "user", sessionId = Session, timestamp = Ts(second), isMeta = meta, message = new { role = "user", content },
    });

    private static string Assistant(int second, params object[] blocks) => Line(new
    {
        type = "assistant", sessionId = Session, timestamp = Ts(second), cwd = @"F:\AI\ToolWithCS", message = new { role = "assistant", content = blocks },
    });

    private static object Text(string text) => new { type = "text", text };

    private static object Use(string id, string name, object input) => new { type = "tool_use", id, name, input };

    private static object Result(string useId, string text) => new { type = "tool_result", tool_use_id = useId, content = new object[] { new { type = "text", text } } };

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Session + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    // ── 세션 기록 본문 ──

    [Fact]
    public void Reader_KeepsUserAndConductorText_AndCountsToolsBetween()
    {
        var path = Write(
            Line(new { type = "custom-title", sessionId = Session, customTitle = "디씨콘 논의" }),
            User(1, "ㅎㅇ 기능 고민 중"),
            Assistant(2, new { type = "thinking", thinking = "생각은 빼야 한다" }),
            Assistant(3, Use("t1", "Bash", new { command = "ls" })),
            User(4, new object[] { Result("t1", "파일 목록") }),
            Assistant(5, Use("t2", "Read", new { file_path = "a.cs" })),
            User(6, new object[] { Result("t2", "본문") }),
            Assistant(7, Text("어떤 기능인지 말해 주세요")));

        var chat = ClaudeChatReader.Read(path);

        Assert.Equal("디씨콘 논의", chat.Title);
        Assert.Collection(chat.Entries,
            e => { Assert.Equal(ChatSpeaker.User, e.Speaker); Assert.Equal("ㅎㅇ 기능 고민 중", e.Text); Assert.Equal(0, e.ToolsBefore); },
            e => { Assert.Equal(ChatSpeaker.Conductor, e.Speaker); Assert.Equal("어떤 기능인지 말해 주세요", e.Text); Assert.Equal(2, e.ToolsBefore); });
    }

    [Fact]
    public void Reader_KeepsToolTargets_InToolOrder()
    {
        // HB_Chat 4차 C3: 끝난 턴 칩에도 대상 — 실행 중 칩(ToolTargets.Of)과 같은 글. 모르는 도구는 null
        var path = Write(
            User(1, "봐 줘"),
            Assistant(2, Use("t1", "Read", new { file_path = @"F:\AI\x\Program.cs" }), Use("t2", "Bash", new { command = "git status\ngit log" })),
            User(3, new object[] { Result("t1", "본문"), Result("t2", "출력") }),
            Assistant(4, Use("t3", "mcp__hb-orchestration__run_mode", new { mode = "review_main" }), Use("t4", "Unknown", new { x = 1 })),
            User(5, new object[] { Result("t3", "r"), Result("t4", "u") }),
            Assistant(6, Text("끝")));

        var e = ClaudeChatReader.Read(path).Entries[^1];
        Assert.Equal(["Read", "Bash", "mcp__hb-orchestration__run_mode", "Unknown"], e.Tools);
        Assert.Equal(["Program.cs", "git status", "review_main", null], e.ToolTargets);
        Assert.Equal(new string('a', ToolTargets.MaxChars) + "…",
            ToolTargets.Of("Grep", JsonDocument.Parse($$"""{"pattern":"{{new string('a', 80)}}"}""").RootElement));
    }

    [Fact]
    public void Reader_CollectsQuestionAnswers_OutsideEntries()
    {
        // HB_Chat 4차 D1: AskUserQuestion의 답(toolUseResult.answers)은 Answers로 — 말 목록(대시보드도 씀)에는 넣지 않는다.
        // 거절 · 시간 초과(오류 결과)는 Declined, 다른 도구 결과는 넣지 않는다.
        var questions = new object[]
        {
            new { question = "어느 쪽?", header = "방향", options = new[] { new { label = "A" }, new { label = "B" } }, multiSelect = false },
            new { question = "무엇을 더?", header = "더할 것", options = new[] { new { label = "x" }, new { label = "y" } }, multiSelect = true },
        };
        var answered = Line(new
        {
            type = "user", sessionId = Session, timestamp = Ts(3),
            message = new { role = "user", content = new object[] { Result("q1", "Your questions have been answered: ...") } },
            toolUseResult = new { questions, answers = new Dictionary<string, string> { ["어느 쪽?"] = "A", ["무엇을 더?"] = "x, y" } },
        });
        var declined = Line(new
        {
            type = "user", sessionId = Session, timestamp = Ts(7),
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "q2", is_error = true, content = "답하지 않음: 시간 초과" } } },
            toolUseResult = "Error: 답하지 않음: 시간 초과",
        });
        var path = Write(
            User(1, "골라 줘"),
            Assistant(2, Use("q1", "AskUserQuestion", new { questions })),
            answered,
            Assistant(4, Text("A로 갑니다")),
            Assistant(5, Use("t1", "Bash", new { command = "ls" })),
            User(6, new object[] { Result("t1", "목록") }),
            Assistant(6, Use("q2", "AskUserQuestion", new { questions = new[] { questions[0] } })),
            declined);

        var chat = ClaudeChatReader.Read(path);

        Assert.Equal(["골라 줘", "A로 갑니다"], chat.Entries.Select(e => e.Text));
        Assert.Collection(chat.Answers,
            a =>
            {
                Assert.Equal(3, a.Line); // 답 줄 (파일 셋째 줄)
                Assert.Null(a.Declined);
                Assert.Equal([new ChatAnswerItem("어느 쪽?", "방향", "A"), new ChatAnswerItem("무엇을 더?", "더할 것", "x, y")], a.Items);
            },
            a =>
            {
                Assert.Equal("답하지 않음: 시간 초과", a.Declined);
                Assert.Equal([new ChatAnswerItem("어느 쪽?", "방향", null)], a.Items); // 질문은 tool_use 입력에서
            });

        // 이어 읽기도 같은 결과 — 질문 줄과 답 줄이 다른 읽기로 나뉘어도 (HB_Chat이 카드를 기다리는 동안 방을 열면, e8091295fe C6)
        var lines = File.ReadAllLines(path);
        var split = Path.Combine(_dir, "split.jsonl");
        File.WriteAllText(split, string.Join("\n", lines.Take(2)) + "\n", new UTF8Encoding(false)); // 질문 줄까지
        var (first, resume) = ClaudeChatReader.ReadMore(split, null);
        Assert.Empty(first.Answers);
        File.AppendAllText(split, string.Join("\n", lines.Skip(2)) + "\n", new UTF8Encoding(false));
        var (more, _) = ClaudeChatReader.ReadMore(split, resume);
        Assert.Equal(chat.Answers.Select(a => (a.Line, a.Declined, string.Join("|", a.Items))),
            more.Answers.Select(a => (a.Line, a.Declined, string.Join("|", a.Items))));
    }

    [Fact]
    public void Reader_Answers_FallBackToAskedQuestions_AndCutsNoteSafely()
    {
        // e8091295fe C5: 결과에 questions가 없으면 tool_use 입력의 질문 글로 답을 찾는다. C7: 200자 자르기가 서로게이트 쌍을 가르지 않음
        var answered = Line(new
        {
            type = "user", sessionId = Session, timestamp = Ts(3),
            message = new { role = "user", content = new object[] { Result("q1", "answered") } },
            toolUseResult = new { answers = new Dictionary<string, string> { ["어느 쪽?"] = "B" } },
        });
        var longNote = new string('가', 199) + "😀끝";
        var declined = Line(new
        {
            type = "user", sessionId = Session, timestamp = Ts(5),
            message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "q2", is_error = true, content = longNote } } },
        });
        var ask = new { questions = new[] { new { question = "어느 쪽?", header = "방향", options = new[] { new { label = "A" } } } } };
        var path = Write(Assistant(2, Use("q1", "AskUserQuestion", ask)), answered, Assistant(4, Use("q2", "AskUserQuestion", ask)), declined);

        var answers = ClaudeChatReader.Read(path).Answers;
        Assert.Equal([new ChatAnswerItem("어느 쪽?", "방향", "B")], answers[0].Items);
        Assert.Equal(new string('가', 199) + "…", answers[1].Declined); // 😀의 앞 반쪽에서 자르지 않음
    }

    [Fact]
    public void Reader_SkipsMetaSidechainAndReminders_FencesPastes()
    {
        var path = Write(
            User(1, "스킬 본문", meta: true),
            Line(new { type = "user", sessionId = Session, timestamp = Ts(2), isSidechain = true, message = new { role = "user", content = "서브에이전트 지시" } }),
            User(3, "<system-reminder>지침</system-reminder>\n본문만"),
            User(4, "<pasted_content id=\"ab\">\n# 줄 1\r\n줄 2\n</pasted_content id=\"ab\">\n\n대충 이런 대화"),
            User(5, "<local-command-stdout>출력</local-command-stdout>"),
            User(6, "<command-message>mode</command-message>\n<command-name>/mode</command-name>\n<command-args>design_main x</command-args>"),
            User(7, "<task-notification>\n<task-id>abc</task-id>\n<summary>빌드 끝</summary>\n</task-notification>"),
            User(8, new object[] { Text("[Request interrupted by user for tool use]") }),
            User(9, "<pasted_content id=\"cd\">\n```cs\nx\n```\n</pasted_content id=\"cd\">"));

        var chat = ClaudeChatReader.Read(path);

        Assert.DoesNotContain(chat.Entries, e => e.Text.Contains("스킬 본문") || e.Text.Contains("서브에이전트 지시") || e.Text.Contains("지침") || e.Text.Contains("출력"));
        Assert.Contains(chat.Entries, e => e.Text == "본문만");
        // 붙여 넣은 글은 원문 그대로 코드 블록 (# · 줄바꿈이 Markdown으로 해석되지 않게), 본문에 ```가 있으면 울타리가 더 길다.
        Assert.Contains(chat.Entries, e => e.Text.Contains("```text\n# 줄 1\n줄 2\n```") && e.Text.Contains("대충 이런 대화"));
        Assert.Contains(chat.Entries, e => e.Text.Contains("````text\n```cs\nx\n```\n````"));
        Assert.Contains(chat.Entries, e => e.Speaker == ChatSpeaker.User && e.Text == "`/mode` design_main x");
        Assert.Contains(chat.Entries, e => e.Speaker == ChatSpeaker.System && e.Text == "백그라운드 작업: 빌드 끝");
        Assert.Contains(chat.Entries, e => e.Speaker == ChatSpeaker.System && e.Text == "사용자가 중단함");
        Assert.DoesNotContain(chat.Entries, e => e.Text.Contains("Request interrupted"));
    }

    [Fact]
    public void Reader_QueuedCommand_IsUserText()
    {
        var path = Write(Line(new
        {
            type = "attachment", sessionId = Session, timestamp = Ts(1),
            attachment = new { type = "queued_command", prompt = "아 잠깐만", commandMode = "prompt" },
        }));

        var e = Assert.Single(ClaudeChatReader.Read(path).Entries);
        Assert.Equal(ChatSpeaker.User, e.Speaker);
        Assert.Equal("아 잠깐만", e.Text);
    }

    [Fact]
    public void Summary_RecordVerdicts_LinksInputRunIdAndCreatedRunId()
    {
        var path = Write(
            Assistant(1, Use("v1", "mcp__hb-orchestration__record_verdicts", new { runId = "aaaaaaaaaa", items = Array.Empty<object>() })),
            User(2, new object[] { Result("v1", "판정 1건 기록") }),
            Assistant(3, Use("v2", "mcp__hb-orchestration__record_verdicts", new { selfReview = new { review = "x" } })),
            User(4, new object[] { Result("v2", "self-reviewer 기록 runId: bbbbbbbbbb\n판정 2건 기록") }),
            // 다른 도구 결과의 같은 문구는 연결하지 않는다.
            Assistant(5, Use("g1", "Grep", new { pattern = "runId" })),
            User(6, new object[] { Result("g1", "기록 runId: cccccccccc") }),
            // 기록이 실패한 호출 · 도구 오류 · 결과가 아직 없는 호출의 입력 runId는 잇지 않는다.
            Assistant(7, Use("v3", "mcp__hb-orchestration__record_verdicts", new { runId = "dddddddddd" })),
            User(8, new object[] { Result("v3", "[판정 기록 불가] runId 'dddddddddd'의 기록이 없습니다.") }),
            Assistant(9, Use("v4", "mcp__hb-orchestration__record_verdicts", new { runId = "eeeeeeeeee" })),
            User(10, new object[] { new { type = "tool_result", tool_use_id = "v4", is_error = true, content = "denied" } }),
            Assistant(11, Use("v5", "mcp__hb-orchestration__record_verdicts", new { runId = "ffffffffff" })));

        // 세션 요약이 가진다 (실행 → 방을 본문 없이 찾도록).
        Assert.Equal(["aaaaaaaaaa", "bbbbbbbbbb"], ClaudeTranscriptReader.Read(path).VerdictRunIds);
    }

    [Fact]
    public void Reader_MissingFile_ReturnsFormatError()
    {
        var chat = ClaudeChatReader.Read(Path.Combine(_dir, "없음.jsonl"));
        Assert.NotNull(chat.FormatError);
        Assert.Empty(chat.Entries);
    }

    // ── 스티커 ──

    private StickerCatalog Catalog(bool withDir = true)
    {
        var stickers = Path.Combine(_dir, "stickers");
        Directory.CreateDirectory(stickers);
        File.WriteAllBytes(Path.Combine(stickers, "06-done.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(_dir, "outside.png"), [0x89, 0x50]);
        var options = new StickerOptions
        {
            Dir = withDir ? stickers : null,
            Files = new(StringComparer.Ordinal) { ["해냈다"] = "06-done.png", ["밖"] = "../outside.png", ["실행"] = "run.exe", ["없음"] = "99-none.png" },
            Aliases = new(StringComparer.Ordinal) { ["완료"] = "해냈다" },
            Auto = new(StringComparer.Ordinal) { [AutoSticker.CallNoFindings] = "완료", [AutoSticker.CallFailed] = "" },
        };
        return new StickerCatalog(() => options);
    }

    [Fact]
    public void Catalog_ResolvesAliases_AndKeepsFilesInsideFolder()
    {
        var c = Catalog();

        Assert.Equal("해냈다", c.Resolve("완료"));
        Assert.Null(c.Resolve("모름"));
        Assert.NotNull(c.FileFor("완료"));
        Assert.Null(c.FileFor("밖"));     // 폴더 밖
        Assert.Null(c.FileFor("실행"));   // 그림 확장자 아님
        Assert.Null(c.FileFor("없음"));   // 파일 없음
        Assert.Equal("해냈다", c.ForSituation(AutoSticker.CallNoFindings));
        Assert.Null(c.ForSituation(AutoSticker.CallFailed)); // 비우면 끔
        Assert.Null(c.ForSituation(AutoSticker.CallQuota));  // 설정 없음
    }

    [Fact]
    public void Catalog_FallbackDir_AndUrlBase()
    {
        // HB_Chat 4차 D2 스티커 팩: Dir에 없으면 FallbackDir, 주소 앞부분은 UrlBase (대시보드는 둘 다 비움 → 그대로)
        var root = Path.Combine(_dir, "stickers");
        var pack = Path.Combine(root, "cat");
        Directory.CreateDirectory(pack);
        File.WriteAllBytes(Path.Combine(root, "06-done.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(root, "01-idle.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(pack, "06-done.png"), [0x89, 0x51]);
        var options = new StickerOptions
        {
            Dir = pack, FallbackDir = root, UrlBase = "/sticker-pack/cat/",
            // "밖": 팩에서 풀면 root/outside.png(없음), 기본에서 풀면 _dir/outside.png(있지만 폴더 밖) — 기본 쪽 차단까지 거친다 (ef2c83416c C8)
            Files = new(StringComparer.Ordinal) { ["해냈다"] = "06-done.png", ["대기"] = "01-idle.png", ["밖"] = "../outside.png" },
        };
        File.WriteAllBytes(Path.Combine(_dir, "outside.png"), [0x89, 0x50]);
        var c = new StickerCatalog(() => options);

        Assert.Equal(Path.Combine(pack, "06-done.png"), c.FileFor("해냈다")!.Value.Path); // 팩 것
        Assert.Equal(Path.Combine(root, "01-idle.png"), c.FileFor("대기")!.Value.Path); // 팩에 없음 → 기본
        Assert.Null(c.FileFor("밖")); // 두 폴더 모두 밖이면 없음
        Assert.Contains("src=\"/sticker-pack/cat/%ED%95%B4%EB%83%88%EB%8B%A4\"", StickerRenderer.Html("해냈다", c));
        Assert.Contains("src=\"/sticker/", StickerRenderer.Html("해냈다", Catalog())); // 기본 주소 그대로
    }

    [Fact]
    public void Catalog_FindsOtherExtension_AndDefaultPack()
    {
        // HB_Chat 스티커 GIF: 설정은 .png 이름이어도 팩의 같은 이름 .gif를 찾고 형식은 찾은 파일대로,
        // 스티커 폴더 바로 아래에 그림이 없으면 기본 팩 폴더에서
        var root = Path.Combine(_dir, "stickers");
        var animal = Path.Combine(root, "animal");
        var gpt = Path.Combine(root, "gpt_chan");
        Directory.CreateDirectory(animal);
        Directory.CreateDirectory(gpt);
        File.WriteAllBytes(Path.Combine(animal, "06-done.gif"), [0x47, 0x49]);
        File.WriteAllBytes(Path.Combine(animal, "07-blocked.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(animal, "07-blocked.gif"), [0x47, 0x49]);
        File.WriteAllBytes(Path.Combine(gpt, "06-done.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(gpt, "01-idle.png"), [0x89, 0x50]);
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["해냈다"] = "06-done.png", ["막힘"] = "07-blocked.png", ["대기"] = "01-idle.png" };

        var basic = new StickerCatalog(() => new StickerOptions { Dir = root, DefaultPack = "gpt_chan", Files = files });
        Assert.Equal((Path.Combine(gpt, "06-done.png"), "image/png"), basic.FileFor("해냈다"));       // 바로 아래 없음 → 기본 팩
        Assert.Null(new StickerCatalog(() => new StickerOptions { Dir = root, Files = files }).FileFor("해냈다")); // 기본 팩 설정 없음

        var pack = new StickerCatalog(() => new StickerOptions { Dir = animal, FallbackDir = root, DefaultPack = "gpt_chan", Files = files });
        Assert.Equal((Path.Combine(animal, "06-done.gif"), "image/gif"), pack.FileFor("해냈다"));     // 확장자만 다른 같은 이름
        Assert.Equal(Path.Combine(animal, "07-blocked.png"), pack.FileFor("막힘")!.Value.Path);      // 설정 이름 그대로가 먼저
        Assert.Equal(Path.Combine(gpt, "01-idle.png"), pack.FileFor("대기")!.Value.Path);            // 팩 → 스티커 폴더 → 기본 팩

        foreach (var bad in new[] { "..", "../stickers", "gpt_chan/..", "C:" })
            Assert.Null(new StickerCatalog(() => new StickerOptions { Dir = root, DefaultPack = bad, Files = files }).FileFor("대기"));
    }

    [Fact]
    public void Catalog_WithoutFolder_RendersTextBadge()
    {
        var html = StickerRenderer.Html("해냈다", Catalog(withDir: false));
        Assert.Contains("sticker-text", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Renderer_ReplacesKnownTagsOutsideCodeOnly()
    {
        var c = Catalog();
        var html = "<p>끝 [[icon:완료]] [[icon:모름]]</p><pre><code>[[icon:완료]]</code></pre><p><code>[[icon:완료]]</code></p>";

        var result = StickerRenderer.Replace(html, c);

        Assert.Contains("<img class=\"sticker\" src=\"/sticker/%ED%95%B4%EB%83%88%EB%8B%A4\"", result);
        Assert.Contains("[[icon:모름]]", result);
        Assert.Contains("<pre><code>[[icon:완료]]</code></pre>", result);
        Assert.Contains("<p><code>[[icon:완료]]</code></p>", result);
    }

    // ── 말풍선 Markdown (믿을 수 없는 글) ──

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("HTTP://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("/Call/abc#x", true)]
    [InlineData("#run-1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" JavaScript:alert(1)", false)]
    [InlineData("java\tscript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("vbscript:x", false)]
    public void SafeMarkdown_LinkSchemes(string url, bool safe) => Assert.Equal(safe, SafeMarkdown.IsSafeLink(url));

    [Theory]
    [InlineData("/sticker/완료", true)]
    [InlineData("img/a.png", true)]
    [InlineData("https://evil.example/p.png?q=secret", false)]
    [InlineData("//evil.example/p.png", false)]
    [InlineData("/\\evil.example/p.png", false)]
    [InlineData("", false)]
    public void SafeMarkdown_ImagesSameOriginOnly(string url, bool same) => Assert.Equal(same, SafeMarkdown.IsSameOriginImage(url));

    [Fact]
    public void SafeMarkdown_RewritesUnsafeLinksAndExternalImages()
    {
        var html = SafeMarkdown.ToHtml(
            "[a](javascript:alert(1)) [b](https://ok.example) ![c](https://evil.example/x.png) ![d](/sticker/x) <javascript:alert(2)> <b>raw</b>");

        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a href=\"#\">a</a>", html);
        Assert.Contains(" javascript:alert(2) ", html); // 자동 링크는 글자로만
        Assert.Contains("<a href=\"https://ok.example\">b</a>", html);
        Assert.DoesNotContain("<img src=\"https://evil.example", html);
        Assert.Contains("<a href=\"https://evil.example/x.png\">c</a>", html); // 그림 대신 누르면 열리는 링크
        Assert.Contains("<img src=\"/sticker/x\"", html);
        Assert.DoesNotContain("<b>raw</b>", html);
    }

    [Fact]
    public void SafeMarkdown_KeepLineBreaks_ForUserText()
    {
        Assert.Contains("<br />", SafeMarkdown.ToHtml("줄 1\n줄 2", SafeMarkdown.KeepLineBreaks));
        Assert.DoesNotContain("<br />", SafeMarkdown.ToHtml("줄 1\n줄 2"));
    }

    // ── 말풍선 엮기 ──

    private static CallRecord Call(string id, string runId, int step, int second, bool success = true, string? errorKind = null,
        string tool = "run_mode", string provider = "openai", StructuredOutputRecord? structured = null) => new()
    {
        Id = id, Timestamp = At(second), Tool = tool, RunId = runId, Mode = tool == "run_mode" ? "review_main" : tool, Role = "Review", Step = step,
        Agent = "gpt", Provider = provider, Model = "gpt-x", Prompt = "p", Response = "응답 " + id, Success = success, ErrorKind = errorKind,
        Error = success ? null : "한도 소진", ElapsedMs = 1000, Reason = "50줄 넘는 변경", Structured = structured,
    };

    private static StructuredOutputRecord Findings(int count) => new()
    {
        Format = "findings@2", State = OutputStates.Complete, RequestedBy = "schema",
        Findings = Enumerable.Range(1, count).Select(i => new FindingRecord { Id = $"1-F{i}", Severity = "medium", Problem = "문제", File = "a.cs", Line = 1, Confidence = "high" }).ToList(),
    };

    private static VerdictRecord Verdict(string runId, string key, string verdict, int second) => new()
    {
        Id = key, Timestamp = At(second), RunId = runId, FindingKey = key, Summary = "요약 " + key, Verdict = verdict,
        Reason = verdict == Verdicts.Adopted ? null : "근거", Steps = [1],
    };

    private static ChatTranscript Transcript(params ChatEntry[] entries) => new()
    {
        FilePath = Session + ".jsonl", SessionId = Session, Entries = entries,
    };

    [Fact]
    public void Build_OrdersByTime_AndAddsAutoStickers()
    {
        var transcript = Transcript(
            new ChatEntry(1, At(1), ChatSpeaker.User, "리뷰해 줘", 0),
            new ChatEntry(2, At(2), ChatSpeaker.Conductor, "물어볼게", 0),
            new ChatEntry(9, At(30), ChatSpeaker.Conductor, "정리했어 [[icon:완료]]", 3));
        var calls = new[]
        {
            Call("c1", "r1", 1, 5, structured: Findings(0)),
            Call("c2", "r1", 2, 6, success: false, errorKind: "Quota"),
            Call("x1", "other", 1, 7), // 이 방에 이어지지 않은 실행
        };
        var verdicts = new[] { Verdict("r1", "1-F1", Verdicts.Adopted, 20), Verdict("r1", "1-F2", Verdicts.Adopted, 20), Verdict("r1", "1-F3", Verdicts.Rejected, 20) };
        var auto = new Dictionary<string, string> { [AutoSticker.CallNoFindings] = "좋았어", [AutoSticker.CallQuota] = "왜안됨", [AutoSticker.VerdictMostlyAdopted] = "검토완료" };

        var room = ChatRoomBuilder.Build(transcript, ["r1"], calls, [], verdicts, s => auto.GetValueOrDefault(s));

        Assert.Equal(["user", "conductor", "system", "external", "external", "verdict", "conductor"],
            room.Messages.Select(m => RoleName(m.Role)).ToArray());
        var ok = room.Messages.Single(m => m.Link == "/Call/c1" && m.Role == RoomRole.External); // 실행 시작 줄도 같은 호출로 이어진다
        Assert.Equal("좋았어", ok.Sticker);
        var failed = room.Messages.Single(m => m.Link == "/Call/c2");
        Assert.True(failed.IsError);
        Assert.Equal("왜안됨", failed.Sticker);
        var verdict = room.Messages.Single(m => m.Role == RoomRole.Verdict);
        Assert.Equal("검토완료", verdict.Sticker); // 채택 2 · 기각 1 = 2/3
        Assert.Contains("요약 1-F3 — 근거", verdict.Markdown);
        Assert.DoesNotContain(room.Messages, m => m.Link == "/Call/x1");
        Assert.Equal(["r1"], room.RunIds);
    }

    [Fact]
    public void Build_SelfReviewCall_ShownOnceAsSubagent()
    {
        var transcript = Transcript(new ChatEntry(1, At(1), ChatSpeaker.User, "셀프 리뷰", 0));
        var call = Call("s1", "sr", 1, 3, tool: VerdictRecorder.SelfReviewTool, provider: "claude-code");
        var result = new SubagentResult
        {
            Key = "agent_use", AgentId = "agent", AgentType = "self-reviewer", SessionId = Session, ReturnedAt = At(4), Message = "## 지적\n- 없음",
        };
        var view = new SubagentView(result, new SubagentLink(SubagentLinkKind.Exact, "sr", "selfReview", 1));

        var room = ChatRoomBuilder.Build(transcript, ["sr"], [call], [view], [], _ => null);

        var sub = Assert.Single(room.Messages, m => m.Role == RoomRole.Subagent);
        Assert.Equal("self-reviewer", sub.Name);
        Assert.Equal("sr", sub.RunId);
        Assert.DoesNotContain(room.Messages, m => m.Link == "/Call/s1");
        Assert.DoesNotContain(room.Messages, m => m.Role == RoomRole.System); // self_review에는 실행 시작 줄이 없다
    }

    [Fact]
    public void Build_ResumedSession_MatchesSubagentByFileName()
    {
        // 세션을 이어 열면 기록 안 첫 sessionId는 앞 세션 것이고, 새로 부른 서브에이전트는 파일 이름(새 세션)을 단다.
        var transcript = Transcript() with { SessionId = "00000000-0000-0000-0000-000000000000" };
        var result = new SubagentResult
        {
            Key = "agent_use", AgentId = "agent", AgentType = "security-reviewer", SessionId = Session, ReturnedAt = At(4), Message = "## 지적\n- 없음",
        };

        var room = ChatRoomBuilder.Build(transcript, [], [], [new SubagentView(result, new SubagentLink(SubagentLinkKind.None, null, null, 0))], [], _ => null);

        Assert.Single(room.Messages, m => m.Role == RoomRole.Subagent);
    }

    [Fact]
    public void Build_ApiReview_And_LastAttemptPerStep()
    {
        var first = Call("a1", "r2", 1, 3, success: false, errorKind: "Timeout", provider: "claude-cli") with { Attempt = 1, Mode = "sub_self" };
        var retry = Call("a2", "r2", 1, 9, provider: "claude-cli") with { Attempt = 2, Mode = "sub_self" };

        var room = ChatRoomBuilder.Build(Transcript(), ["r2"], [first, retry], [], [], _ => null);

        var reply = Assert.Single(room.Messages, m => m.Role == RoomRole.ApiReview);
        Assert.Equal("/Call/a2", reply.Link);
        Assert.False(reply.IsError);
    }

    // ── 실행 → 방 · 본문 캐시 ──

    private static TranscriptSummary Summary(string sessionId, params string[] verdictRuns) => new()
    {
        FilePath = Path.Combine("p", sessionId + ".jsonl"), FileBytes = 1, LastWriteUtc = new DateTime(2026, 10, 27), VerdictRunIds = verdictRuns,
    };

    [Fact]
    public void RunRooms_RanksRunModeConfirmed_ThenVerdict_ThenEstimated()
    {
        // newer가 최근 수정된 세션 (AllSessions 순서).
        var newer = Summary("new", "vv00000000", "retro00000");
        var older = Summary("old", "estverdict");
        var c1 = new TranscriptRunCall(1, At(1), "t1", "review_main", null, null);
        var c2 = new TranscriptRunCall(2, At(2), "t2", "review_main", null, null);
        var links = new Dictionary<TranscriptSummary, List<RunLink>>
        {
            [newer] = [new RunLink(c1, RunLinkKind.Estimated, "estverdict", 1), new RunLink(c2, RunLinkKind.Estimated, "r-est", 1),
                       new RunLink(c1, RunLinkKind.Confirmed, "copied0000", 1)],
            [older] = [new RunLink(c1, RunLinkKind.Confirmed, "retro00000", 1), new RunLink(c1, RunLinkKind.Confirmed, "copied0000", 1),
                       new RunLink(c2, RunLinkKind.Ambiguous, null, 2)],
        };

        var rooms = ChatRoomService.RunRooms([newer, older], links);

        Assert.Equal(new RunRoom("old", false), rooms["retro00000"]);  // 실행한 세션(확정)이 나중 세션의 소급 판정을 이긴다
        Assert.Equal(new RunRoom("old", false), rooms["estverdict"]);  // 판정이 다른 세션의 추정을 이긴다
        Assert.Equal(new RunRoom("new", false), rooms["vv00000000"]);  // 판정만
        Assert.Equal(new RunRoom("new", true), rooms["r-est"]);        // 추정만
        Assert.Equal(new RunRoom("new", false), rooms["copied0000"]);  // 같은 순위면 최근 세션 (이어 연 세션 복사본)
        Assert.False(rooms.ContainsKey("golden0000"));
        Assert.Equal(5, rooms.Count);
    }

    [Fact]
    public void ChatCache_RereadsOnChange_AndDropsLeastRecentlyUsed()
    {
        var reads = new List<string>();
        var cache = new ChatTranscriptCache(() => 2, (path, _) => { reads.Add(path); return (new ChatTranscript { FilePath = path }, null); });
        var a = Summary("a");
        var b = Summary("b");
        var c = Summary("c");

        cache.Get(a);
        cache.Get(b);
        cache.Get(a); // a를 최근에 씀 → b가 가장 오래 안 씀
        cache.Get(c);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.Contains(a.FilePath));
        Assert.False(cache.Contains(b.FilePath));
        Assert.True(cache.Contains(c.FilePath));
        Assert.Equal([a.FilePath, b.FilePath, c.FilePath], reads);

        cache.Get(a with { FileBytes = 2 }); // 파일이 바뀌면 다시 읽는다
        Assert.Equal(4, reads.Count);
    }

    [Fact]
    public void ChatCache_ResumesOnGrowth_RereadsWhenOnlyTimeChanged_IgnoresStaleSnapshots()
    {
        var froms = new List<ChatResume?>();
        var token = new ChatResume();
        var cache = new ChatTranscriptCache(() => 10, (path, from) => { froms.Add(from); return (new ChatTranscript { FilePath = path }, token); });
        var t0 = new DateTime(2026, 10, 27, 7, 0, 0, DateTimeKind.Utc);
        var a = Summary("a") with { FileBytes = 100, LastWriteUtc = t0 };

        cache.Get(a);                                                        // 처음: from 없음
        cache.Get(a with { FileBytes = 150, LastWriteUtc = t0.AddSeconds(1) }); // 커짐: 이어 읽기
        cache.Get(a);                                                        // 늦게 온 낡은 요청: 이어 읽기 (처음부터 아님)
        cache.Get(a with { FileBytes = 150, LastWriteUtc = t0.AddSeconds(5) }); // 크기 같고 시각만 다름: 처음부터

        Assert.Equal([null, token, token, null], froms);
    }

    [Fact]
    public void ChatCache_DoesNotKeepFailedReads()
    {
        var fail = true;
        var reads = 0;
        var cache = new ChatTranscriptCache(() => 10, (path, _) =>
        {
            reads++;
            return (new ChatTranscript { FilePath = path, FormatError = fail ? "IOException: 잠김" : null }, null);
        });
        var a = Summary("a");

        Assert.NotNull(cache.Get(a).FormatError);
        Assert.False(cache.Contains(a.FilePath));
        fail = false;
        Assert.Null(cache.Get(a).FormatError); // 파일이 그대로여도 다시 읽는다
        Assert.True(cache.Contains(a.FilePath));
        Assert.Equal(2, reads);
    }

    // ── 실시간 갱신: 이어 읽기 · 말풍선 키 · 바뀜 표시 ──

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    [Fact]
    public void ReadMore_AppendedLines_EqualFullRead_AndWaitsForPartialLine()
    {
        var path = Path.Combine(_dir, Session + ".jsonl");
        File.WriteAllText(path, User(1, "첫 말") + "\n" + Assistant(2, Use("t1", "Bash", new { command = "ls" })) + "\n", Utf8NoBom);

        var (chat1, resume) = ClaudeChatReader.ReadMore(path, null);
        Assert.Single(chat1.Entries);
        Assert.NotNull(resume);

        var partial = User(5, "<pasted_content id=\"x\">\n로그\n</pasted_content id=\"x\">");
        File.AppendAllText(path,
            Assistant(3, Text("둘째"), Text("셋째")) + "\n" + Line(new { type = "custom-title", sessionId = Session, customTitle = "제목" }) + "\n" + partial[..10],
            Utf8NoBom);
        var (chat2, resume2) = ClaudeChatReader.ReadMore(path, resume);

        Assert.Equal(3, chat2.Entries.Count);           // 쓰는 중인 마지막 줄은 아직 읽지 않는다
        Assert.Equal(1, chat2.Entries[1].ToolsBefore);   // 앞 읽기에서 센 도구 수(t1)를 이어 받는다
        Assert.Single(chat1.Entries);                    // 앞서 돌려준 결과는 그대로
        Assert.Equal("제목", chat2.Title);

        File.AppendAllText(path, partial[10..] + "\n", Utf8NoBom);
        var (chat3, _) = ClaudeChatReader.ReadMore(path, resume2);
        var full = ClaudeChatReader.Read(path);

        Assert.Equal(full.Entries, chat3.Entries);
        Assert.Equal(full.Title, chat3.Title);
        Assert.Equal(full.UnknownLines, chat3.UnknownLines);
        Assert.Contains("```text\n로그\n```", chat3.Entries[^1].Text);
    }

    [Fact]
    public void ReadMore_ShrunkFile_ReadsFromStart()
    {
        var path = Path.Combine(_dir, Session + ".jsonl");
        File.WriteAllText(path, User(1, "하나") + "\n" + User(2, "둘") + "\n", Utf8NoBom);
        var (_, resume) = ClaudeChatReader.ReadMore(path, null);

        File.WriteAllText(path, User(1, "새로") + "\n", Utf8NoBom);
        var (chat, _) = ClaudeChatReader.ReadMore(path, resume);

        Assert.Equal(ClaudeChatReader.Read(path).Entries, chat.Entries);
        Assert.Equal("새로", Assert.Single(chat.Entries).Text);
    }

    // ── 도구 입력에 나온 파일 (HB_Chat 곁칸 문서 탭) ──

    [Fact]
    public void Reader_FileMentions_FileToolPathsAndShellMarkdownNames()
    {
        var path = Write(
            Assistant(1,
                Use("t1", "Read", new { file_path = @"F:\ws\docs\plans\2026-10-30_가.md" }),
                Use("t2", "Edit", new { file_path = "F:/ws/docs/worklog/2026-10-30_가.md", old_string = "a", new_string = "b" })),
            Assistant(2, Use("t3", "Bash", new
            {
                command = "cd /f/ws/docs && cat >> \"worklog/2026-10-30_나.md\" <<'EOF'\n본문 x.md\nEOF\nsed -i 's/a/b/' worklog.md && ls a.mdx README.MD",
            })),
            Assistant(3, Use("t4", "PowerShell", new { command = "Get-Content docs\\plans\\2026-10-29_다.md" })),
            Assistant(4, Use("t5", "mcp__x__Read", new { file_path = @"F:\ws\docs\plans\무시.md" }), Use("t6", "Grep", new { pattern = "b.md" })),
            Line(new
            {
                type = "assistant", sessionId = Session, timestamp = Ts(5), isSidechain = true,
                message = new { role = "assistant", content = new object[] { Use("t7", "Read", new { file_path = @"F:\ws\docs\plans\서브.md" }) } },
            }));

        var mentions = ClaudeChatReader.Read(path).FileMentions;

        Assert.Equal(
        [
            (FileMentionKind.Read, @"F:\ws\docs\plans\2026-10-30_가.md", "2026-10-30_가.md"),
            (FileMentionKind.Edit, "F:/ws/docs/worklog/2026-10-30_가.md", "2026-10-30_가.md"),
            (FileMentionKind.Shell, null, "2026-10-30_나.md"),
            (FileMentionKind.Shell, null, "x.md"),
            (FileMentionKind.Shell, null, "worklog.md"),
            (FileMentionKind.Shell, null, "README.MD"),
            (FileMentionKind.Shell, null, "2026-10-29_다.md"),
        ], mentions.Select(m => (m.Kind, m.Path, m.Name)));
        Assert.Equal(At(1), mentions[0].Timestamp);
        Assert.Equal(At(3), mentions[^1].Timestamp);
    }

    [Fact]
    public void ReadMore_FileMentions_EqualFullRead()
    {
        var path = Path.Combine(_dir, Session + ".jsonl");
        File.WriteAllText(path, Assistant(1, Use("t1", "Read", new { file_path = "a/docs/plans/p.md" })) + "\n", Utf8NoBom);
        var (first, resume) = ClaudeChatReader.ReadMore(path, null);
        File.AppendAllText(path, Assistant(2, Use("t2", "Bash", new { command = "cat q.md" })) + "\n", Utf8NoBom);
        var (more, _) = ClaudeChatReader.ReadMore(path, resume);

        Assert.Single(first.FileMentions);   // 앞서 돌려준 결과는 그대로
        Assert.Equal(ClaudeChatReader.Read(path).FileMentions, more.FileMentions);
        Assert.Equal(["p.md", "q.md"], more.FileMentions.Select(m => m.Name));
    }

    [Fact]
    public void Build_MessageKeys_StableUniqueAndKeptWhenMessagesAdded()
    {
        var entries = new List<ChatEntry>
        {
            new(1, At(1), ChatSpeaker.User, "리뷰해 줘", 0),
            new(2, At(2), ChatSpeaker.Conductor, "블록 1", 0),
            new(2, At(2), ChatSpeaker.Conductor, "블록 2", 0), // 한 줄에 text 블록 둘
        };
        var calls = new[] { Call("c1", "r1", 1, 5) };
        var verdicts = new[] { Verdict("r1", "1-F1", Verdicts.Adopted, 20) };

        var first = ChatRoomBuilder.Build(Transcript([.. entries]), ["r1"], calls, [], verdicts, _ => null);
        var again = ChatRoomBuilder.Build(Transcript([.. entries]), ["r1"], calls, [], verdicts, _ => null);
        entries.Add(new ChatEntry(9, At(30), ChatSpeaker.Conductor, "새 말", 0));
        var grown = ChatRoomBuilder.Build(Transcript([.. entries]), ["r1"], calls, [], verdicts, _ => null);

        var keys = first.Messages.Select(m => m.Key).ToList();
        Assert.Equal(keys, again.Messages.Select(m => m.Key));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain("", keys);
        Assert.Contains("s2.0", keys);
        Assert.Contains("s2.1", keys);
        Assert.Equal(keys.Count + 1, grown.Messages.Count);
        Assert.Subset(grown.Messages.Select(m => m.Key).ToHashSet(), keys.ToHashSet());
    }

    [Fact]
    public void Service_StampsChangeWithFiles_AndRoomReadsAppendedLines()
    {
        var root = Path.Combine(_dir, "projects");
        var folder = Path.Combine(root, "F--proj");
        Directory.CreateDirectory(folder);
        var session = Path.Combine(folder, Session + ".jsonl");
        File.WriteAllText(session, User(1, "처음") + "\n", Utf8NoBom);
        var log = Path.Combine(_dir, "data", "calls.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var options = Microsoft.Extensions.Options.Options.Create(new DashboardOptions { LogPath = log, ClaudeProjectsDir = root });
        var sessions = new ClaudeSessionsService(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeSessionsService>.Instance);
        var service = new ChatRoomService(
            new CallLogCache(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<CallLogCache>.Instance),
            new VerdictLogCache(options),
            sessions,
            new SubagentResultsService(options, sessions, Microsoft.Extensions.Logging.Abstractions.NullLogger<SubagentResultsService>.Instance),
            Catalog(),
            options);

        var room1 = service.RoomStamp(Session);
        var list1 = service.ListStamp();
        Assert.NotNull(room1);
        Assert.Null(service.RoomStamp("../" + Session));
        Assert.Null(service.RoomStamp("00000000-0000-0000-0000-000000000000"));
        Assert.Single(service.Room(Session)!.Messages);

        File.AppendAllText(session, Assistant(2, Text("답")) + "\n", Utf8NoBom);
        var room2 = service.RoomStamp(Session);
        Assert.NotEqual(room1, room2);
        Assert.NotEqual(list1, service.ListStamp());
        Assert.Equal(2, service.Room(Session)!.Messages.Count);

        File.WriteAllText(log, "\n", Utf8NoBom); // 호출 기록이 생기거나 바뀌어도
        Assert.NotEqual(room2, service.RoomStamp(Session));
    }

    private static string RoleName(RoomRole role) => role.ToString().ToLowerInvariant();

    [Fact]
    public void RunBubbleSituations_MatchAutoStickerNames()
    {
        // 실행 말풍선 조립을 Shared로 옮기며(2-2 e) 상황 이름이 두 곳에 있다 — 값이 같아야 스티커 설정(Auto)이 그대로 듣는다
        Assert.Equal(AutoSticker.CallNoFindings, RunBubbles.CallNoFindings);
        Assert.Equal(AutoSticker.CallFindings, RunBubbles.CallFindings);
        Assert.Equal(AutoSticker.CallWarning, RunBubbles.CallWarning);
        Assert.Equal(AutoSticker.CallRequests, RunBubbles.CallRequests);
        Assert.Equal(AutoSticker.CallQuota, RunBubbles.CallQuota);
        Assert.Equal(AutoSticker.CallRateLimit, RunBubbles.CallRateLimit);
        Assert.Equal(AutoSticker.CallTimeout, RunBubbles.CallTimeout);
        Assert.Equal(AutoSticker.CallFailed, RunBubbles.CallFailed);
        Assert.Equal(VerdictRecorder.SelfReviewTool, RunBubbles.SelfReviewTool);
        Assert.Equal(FindingFormat.PerfFindings.Name, FindingRenderer.PerfFormatName);
    }
}
