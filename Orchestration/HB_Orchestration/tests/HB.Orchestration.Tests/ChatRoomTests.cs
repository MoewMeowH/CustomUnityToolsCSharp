using System.Text;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>
/// 채팅방 (계획서 2026-10-27_에이전트-채팅방): 세션 기록 본문 읽기 · 스티커 치환과 파일 범위 · 말풍선 엮기.
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
    public void Reader_RecordVerdicts_LinksInputRunIdAndCreatedRunId()
    {
        var path = Write(
            Assistant(1, Use("v1", "mcp__hb-orchestration__record_verdicts", new { runId = "aaaaaaaaaa", items = Array.Empty<object>() })),
            User(2, new object[] { Result("v1", "판정 1건 기록") }),
            Assistant(3, Use("v2", "mcp__hb-orchestration__record_verdicts", new { selfReview = new { review = "x" } })),
            User(4, new object[] { Result("v2", "self-reviewer 기록 runId: bbbbbbbbbb\n판정 2건 기록") }),
            // 다른 도구 결과의 같은 문구는 연결하지 않는다.
            Assistant(5, Use("g1", "Grep", new { pattern = "runId" })),
            User(6, new object[] { Result("g1", "기록 runId: cccccccccc") }));

        Assert.Equal(["aaaaaaaaaa", "bbbbbbbbbb"], ClaudeChatReader.Read(path).VerdictRunIds);
    }

    [Fact]
    public void Reader_MissingFile_ReturnsFormatError()
    {
        var chat = ClaudeChatReader.Read(Path.Combine(_dir, "없음.jsonl"));
        Assert.NotNull(chat.FormatError);
        Assert.Empty(chat.Entries);
    }

    // ── 스티커 ──

    private sealed class Monitor(DashboardOptions value) : IOptionsMonitor<DashboardOptions>
    {
        public DashboardOptions CurrentValue => value;
        public DashboardOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DashboardOptions, string?> listener) => null;
    }

    private StickerCatalog Catalog(bool withDir = true)
    {
        var stickers = Path.Combine(_dir, "stickers");
        Directory.CreateDirectory(stickers);
        File.WriteAllBytes(Path.Combine(stickers, "06-done.png"), [0x89, 0x50]);
        File.WriteAllBytes(Path.Combine(_dir, "outside.png"), [0x89, 0x50]);
        return new StickerCatalog(new Monitor(new DashboardOptions
        {
            Stickers = new StickerOptions
            {
                Dir = withDir ? stickers : null,
                Files = new(StringComparer.Ordinal) { ["해냈다"] = "06-done.png", ["밖"] = "../outside.png", ["실행"] = "run.exe", ["없음"] = "99-none.png" },
                Aliases = new(StringComparer.Ordinal) { ["완료"] = "해냈다" },
                Auto = new(StringComparer.Ordinal) { [AutoSticker.CallNoFindings] = "완료", [AutoSticker.CallFailed] = "" },
            },
        }));
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

    private static string RoleName(RoomRole role) => role.ToString().ToLowerInvariant();
}
