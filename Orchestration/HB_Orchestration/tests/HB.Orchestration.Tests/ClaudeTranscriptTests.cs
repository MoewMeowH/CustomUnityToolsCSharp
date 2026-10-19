using System.Text;
using System.Text.Json;
using HB.Orchestration.Shared;

namespace HB.Orchestration.Tests;

/// <summary>
/// 5장 C: Claude Code 세션 기록 읽기 (합성 데이터 — 실제 세션 기록에는 대화 전체가 있어 커밋하지 않는다).
/// 지침 목록 · run_mode 연결(확정 / 추정) · 깨진 줄 · 읽기 전용 · UTF-8 · 원본 대조.
/// </summary>
public sealed class ClaudeTranscriptTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private const string Cwd = @"F:\AI\ToolCreate";
    private readonly string _dir = Directory.CreateTempSubdirectory("hb-orch-transcript-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Line(object o) => JsonSerializer.Serialize(o);

    private static string Ts(int second) => $"2026-10-16T07:37:{second:00}.000Z";

    private static string Instructions(int second, params (string Path, string Type, string Content)[] files) => Line(new
    {
        type = "attachment", sessionId = Session, timestamp = Ts(second), version = "2.1.301", cwd = Cwd,
        attachment = new { type = "instructions", files = files.Select(f => new { path = f.Path, type = f.Type, content = f.Content }) },
    });

    private static string RunModeUse(int second, string id, string mode) => Line(new
    {
        type = "assistant", sessionId = Session, timestamp = Ts(second), cwd = Cwd,
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name = "mcp__hb-orchestration__run_mode", input = new { mode, input = "본문은 읽지 않는다" } } } },
    });

    private static string ToolUse(int second, string id, string name) => Line(new
    {
        type = "assistant", sessionId = Session, timestamp = Ts(second),
        message = new { role = "assistant", content = new object[] { new { type = "tool_use", id, name, input = new { command = "cat calls.jsonl" } } } },
    });

    private static string ToolResult(int second, string useId, string text) => Line(new
    {
        type = "user", sessionId = Session, timestamp = Ts(second),
        message = new { role = "user", content = new object[] { new { tool_use_id = useId, type = "tool_result", content = new object[] { new { type = "text", text } } } } },
    });

    private static string UserText(int second, string text) => Line(new
    {
        type = "user", sessionId = Session, timestamp = Ts(second), message = new { role = "user", content = text },
    });

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Session + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    // ── 지침 목록 ─────────────────────────────────────────────────────────

    [Fact]
    public void Instructions_PathTypeSizeHash_FromUtf8Content()
    {
        const string korean = "# 리뷰 지침\n## Unity 공식 문서 참조\n- 한글 규칙";
        var path = Write(
            Line(new { type = "mode", mode = "normal", sessionId = Session }), // timestamp 없는 첫 줄
            Instructions(3, (@"F:\AI\ToolCreate\CLAUDE.md", "Project", "# 프로젝트 지침\n@REVIEW_GUIDELINES.md"),
                            (@"F:\AI\ToolCreate\REVIEW_GUIDELINES.md", "Project", korean),
                            (@"C:\memory\MEMORY.md", "AutoMem", "- 메모")));

        var s = ClaudeTranscriptReader.Read(path);

        Assert.Null(s.FormatError);
        Assert.Equal(Session, s.SessionId);
        Assert.Equal("2.1.301", s.Version);
        Assert.Equal(Cwd, s.Cwd);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T07:37:03Z"), s.StartedAt);

        var files = s.Instructions.Single().Files;
        Assert.Equal([@"F:\AI\ToolCreate\CLAUDE.md", @"F:\AI\ToolCreate\REVIEW_GUIDELINES.md", @"C:\memory\MEMORY.md"], files.Select(f => f.Path));
        Assert.Equal(["Project", "Project", "AutoMem"], files.Select(f => f.Type));

        var bytes = new UTF8Encoding(false).GetBytes(korean);
        Assert.Equal(bytes.Length, files[1].Bytes);
        Assert.Equal(BlobStore.Sha256Hex(bytes), files[1].Sha256);
        Assert.NotEqual(Encoding.Latin1.GetByteCount(korean), files[1].Bytes); // 바이트 기준이 문자 수가 아님
    }

    // ── run_mode 연결 ────────────────────────────────────────────────────

    [Fact]
    public void RunId_FromSameLineResult_BackgroundNotification_AndPersistedPreview()
    {
        var path = Write(
            RunModeUse(1, "toolu_a", "review"),
            ToolResult(2, "toolu_a", "runId: aaaaaaaaaa\n### 1. Reviewer · gpt (입력 1 / 출력 1, 1.0초)\n본문"),
            RunModeUse(3, "toolu_b", "cross_p3"),
            ToolResult(4, "toolu_b", "MCP tool \"hb-orchestration/run_mode\" is still running after 120s. It was moved to the background as task kbg1 and keeps running"),
            // 실제 모양 (320760ac 113줄): <result> 다음 줄부터 결과
            UserText(9, "<task-notification>\n<task-id>kbg1</task-id>\n<status>completed</status>\n<summary>MCP task kbg1 (hb-orchestration/run_mode) completed.</summary>\n<result>\nrunId: bbbbbbbbbb\n### 1. …\n</result>\n</task-notification>"),
            RunModeUse(10, "toolu_c", "panel"),
            ToolResult(11, "toolu_c", "<persisted-output>\nOutput too large (30.7KB). Full output saved to: C:\\x\\tool-results\\a.txt\n\nPreview (first 2KB):\nrunId: cccccccccc\n### 1. …"));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;

        Assert.Equal(["review", "cross_p3", "panel"], calls.Select(c => c.Mode));
        Assert.Equal(["aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc"], calls.Select(c => c.RunId));
        Assert.Equal(["result", "task-notification", "persisted"], calls.Select(c => c.ResultSource));
    }

    [Fact]
    public void RunIdInOtherToolResults_IsIgnored()
    {
        // 4장 오탐 사례: Bash로 calls.jsonl을 읽은 결과에 다른 실행의 runId가 있다.
        var path = Write(
            ToolUse(1, "toolu_bash", "Bash"),
            ToolResult(2, "toolu_bash", "runId: 563ad7726f\n{\"runId\":\"563ad7726f\"}"),
            RunModeUse(3, "toolu_x", "review"),
            ToolResult(4, "toolu_x", "[reason 필요] … 본문 중간 runId: 1234567890 언급"));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;
        Assert.Single(calls);
        Assert.Null(calls[0].RunId); // 머리말 모양(줄 전체)이 아니면 읽지 않는다
        Assert.Equal(ClaudeTranscriptReader.RejectedSource, calls[0].ResultSource); // '['로 시작 = 실행 전 거절
    }

    // ── review 4942156eb5 반영 ───────────────────────────────────────────

    [Fact]
    public void RunIdLineInsideResultBody_IsNotHeader()
    {
        // 에이전트 응답 본문에 "runId: …"만 있는 줄 (작업 로그를 리뷰받는 경우 등)
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", "### 1. Reviewer · gpt\n본문\nrunId: 9999999999\n끝"),
            RunModeUse(3, "u2", "cross_p3"),
            ToolResult(4, "u2", "MCP tool \"hb-orchestration/run_mode\" was moved to the background as task kbg2 so that a message that arrived while it was running can reach you"),
            UserText(9, "<task-notification>\n<task-id>kbg2</task-id>\n<summary>runId: 8888888888</summary>\n<result>\n### 1. …\nrunId: 7777777777\n</result>\n</task-notification>"),
            RunModeUse(10, "u3", "panel"),
            ToolResult(11, "u3", "<persisted-output>\nrunId: 6666666666\nPreview (first 2KB):\n### 1. …\nrunId: 5555555555\n"));

        Assert.All(ClaudeTranscriptReader.Read(path).RunCalls, c => Assert.Null(c.RunId));
    }

    // ── 백그라운드 판별 (계획서 2026-10-19_세션연결-백그라운드-판별) ─────────────

    private const string NewBackground = "MCP tool \"hb-orchestration/run_mode\" was moved to the background as task kxu568pwf so that a message that arrived while it was running can reach you; it was not interrupted and keeps running";

    private static string Notification(string task, string runId) =>
        $"<task-notification>\n<task-id>{task}</task-id>\n<status>completed</status>\n<summary>MCP task {task} (hb-orchestration/run_mode) completed.</summary>\n<result>\nrunId: {runId}\n### 1. …\n</result>\n</task-notification>";

    [Fact]
    public void Background_phrase_inside_result_body_is_not_background()
    {
        // 리뷰 응답 본문이 이 안내 문구를 인용 (세션 연결 코드를 리뷰받는 경우 등)
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", "runId: 1111111111\n### 1. Reviewer · sol\n- 결과에 \"moved to the background as task abc\" 문구가 있으면 …"),
            RunModeUse(3, "u2", "review"),
            ToolResult(4, "u2", "### 1. Reviewer · sol\n본문 중간에 MCP tool \"hb-orchestration/run_mode\" was moved to the background as task zzz"));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;
        Assert.Equal("1111111111", calls[0].RunId);
        Assert.Equal("result", calls[0].ResultSource);
        Assert.Null(calls[1].RunId);
        Assert.Equal("result", calls[1].ResultSource); // 백그라운드로 등록되지 않음
    }

    [Fact]
    public void Both_real_background_phrasings_are_recognized()
    {
        var path = Write(
            RunModeUse(1, "u1", "panel"),
            ToolResult(2, "u1", NewBackground),                               // 2026-10-19 세션 기록의 실제 문구
            RunModeUse(3, "u2", "cross_p3"),
            ToolResult(4, "u2", "  MCP tool \"hb-orchestration/run_mode\" is still running after 120s. It was moved to the background as task kold and keeps running"),
            UserText(9, Notification("kxu568pwf", "2222222222")),
            UserText(10, Notification("kold", "3333333333")));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;
        Assert.Equal(["2222222222", "3333333333"], calls.Select(c => c.RunId));
        Assert.All(calls, c => Assert.Equal("task-notification", c.ResultSource));
    }

    [Fact]
    public void Two_notifications_in_one_string_each_resolve_their_own_run()
    {
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            RunModeUse(3, "u2", "perf_sol"),
            ToolResult(4, "u2", NewBackground.Replace("kxu568pwf", "kb")),
            UserText(9, Notification("ka", "aaaaaaaaaa") + "\n" + Notification("kb", "bbbbbbbbbb")));

        Assert.Equal(["aaaaaaaaaa", "bbbbbbbbbb"], ClaudeTranscriptReader.Read(path).RunCalls.Select(c => c.RunId));
    }

    [Fact]
    public void Notification_without_result_does_not_borrow_next_blocks_result()
    {
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            UserText(9, "<task-notification>\n<task-id>ka</task-id>\n<status>failed</status>\n</task-notification>\n" + Notification("kother", "cccccccccc")));

        var call = Assert.Single(ClaudeTranscriptReader.Read(path).RunCalls);
        Assert.Null(call.RunId);
    }

    [Fact]
    public void Notification_text_inside_tool_result_is_ignored()
    {
        // 2026-10-19 세션 기록: grep 출력 같은 도구 결과에 알림 문자열이 그대로 섞여 들어옴
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            ToolUse(3, "toolu_bash", "Bash"),
            ToolResult(4, "toolu_bash", "grep 결과: " + Notification("ka", "dddddddddd")));

        var call = Assert.Single(ClaudeTranscriptReader.Read(path).RunCalls);
        Assert.Null(call.RunId);
    }

    // ── /cross-review 2e59bbb0db 반영 (sol 1-F1 · self-reviewer C1·C3 · 지휘자 K1·K2·K3) ─────

    [Fact]
    public void Notification_quoted_inside_result_does_not_link_other_run()
    {
        // ka의 실제 알림 결과 본문이 kb의 알림 문자열을 인용한다. kb는 나중에 진짜 알림으로 연결돼야 한다.
        var quoted = Notification("kb", "eeeeeeeeee");
        var kaNotification = "<task-notification>\n<task-id>ka</task-id>\n<status>completed</status>\n<result>\nrunId: aaaaaaaaaa\n### 1. Reviewer\n인용:\n" + quoted + "\n끝\n</result>\n</task-notification>";
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            RunModeUse(3, "u2", "review"),
            ToolResult(4, "u2", NewBackground.Replace("kxu568pwf", "kb")),
            UserText(9, kaNotification),
            UserText(10, Notification("kb", "bbbbbbbbbb")));

        Assert.Equal(["aaaaaaaaaa", "bbbbbbbbbb"], ClaudeTranscriptReader.Read(path).RunCalls.Select(c => c.RunId));
    }

    [Fact]
    public void Quoted_closing_tag_inside_result_does_not_end_block()
    {
        // 결과 본문이 닫는 태그와 다른 task-id를 인용 (뒤에 본문이 더 이어짐) → 블록이 거기서 끝나지 않고, 인용된 task-id를 쓰지 않는다
        var text = "<task-notification>\n<task-id>ka</task-id>\n<result>\nrunId: aaaaaaaaaa\n본문 </task-notification> <task-id>kb</task-id> 계속\n</result>\n</task-notification>";
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            RunModeUse(3, "u2", "review"),
            ToolResult(4, "u2", NewBackground.Replace("kxu568pwf", "kb")),
            UserText(9, text));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;
        Assert.Equal("aaaaaaaaaa", calls[0].RunId);
        Assert.Null(calls[1].RunId);
    }

    [Fact]
    public void Notification_text_inside_run_mode_own_result_is_ignored()
    {
        // run_mode 자신의 (백그라운드가 아닌) 결과 본문에 대기 중인 작업 ka의 알림 문자열이 있어도 ka를 연결하지 않는다
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            RunModeUse(3, "u2", "critique"),
            ToolResult(4, "u2", "runId: 1212121212\n### 1. Critic\n" + Notification("ka", "ffffffffff")));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;
        Assert.Null(calls[0].RunId);
        Assert.Equal("1212121212", calls[1].RunId);
    }

    [Fact]
    public void Notification_without_closing_tag_reads_to_end()
    {
        var path = Write(
            RunModeUse(1, "u1", "review"),
            ToolResult(2, "u1", NewBackground.Replace("kxu568pwf", "ka")),
            UserText(9, "<task-notification>\n<task-id>ka</task-id>\n<result>\nrunId: abababababab\n"),
            RunModeUse(10, "u2", "review"),
            ToolResult(11, "u2", NewBackground.Replace("kxu568pwf", "kc")),
            UserText(12, "<task-notification>\n<task-id>kc</task-id>\n<result>\nrunId: cdcdcdcdcd\n### 1."));

        var calls = ClaudeTranscriptReader.Read(path).RunCalls;
        Assert.Null(calls[0].RunId);                 // 12자리라 머리말 모양이 아님
        Assert.Equal("cdcdcdcdcd", calls[1].RunId);  // 닫는 태그 없이 끝까지 한 블록
    }

    [Fact]
    public void RejectedCall_IsNotEstimated()
    {
        var s = ClaudeTranscriptReader.Read(Write(RunModeUse(0, "u1", "review"), ToolResult(1, "u1", "[ownReview 필요] …")));
        var at = DateTimeOffset.Parse("2026-10-16T07:37:00Z");

        var link = RunLinker.LinkAll([s], [Rec("1111111111", "review", at.AddSeconds(0.5))])[s].Single();

        Assert.Equal(RunLinkKind.None, link.Kind); // 같은 시각 다른 세션의 실행이 있어도 붙이지 않는다
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    public void ExtremeTimestamps_DoNotThrow(string ts)
    {
        var path = Write(Line(new
        {
            type = "assistant", sessionId = Session, timestamp = ts, cwd = Cwd,
            message = new { content = new object[] { new { type = "tool_use", id = "u1", name = "mcp__hb-orchestration__run_mode", input = new { mode = "review" } } } },
        }));
        var s = ClaudeTranscriptReader.Read(path);

        // 예외 없이 계산되고, 각 극단 시각에 맞는 실행만 고른다.
        var links = RunLinker.LinkAll([s], [Rec("1111111111", "review", DateTimeOffset.MinValue), Rec("2222222222", "review", DateTimeOffset.MaxValue)]);
        Assert.Equal(ts.StartsWith("0001") ? "1111111111" : "2222222222", links[s].Single().RunId);
    }

    [Fact]
    public void KnownTypeWithWrongMessageShape_IsUnknownLine()
    {
        var s = ClaudeTranscriptReader.Read(Write(
            Line(new { type = "assistant", sessionId = Session, timestamp = Ts(1), message = new { content = 3 } }),
            Line(new { type = "user", sessionId = Session, timestamp = Ts(1) })));
        Assert.Equal(2, s.UnknownLines);
    }

    [Fact]
    public void InvalidUtf8Line_IsUnknown_OtherLinesRead()
    {
        var path = Path.Combine(_dir, Session + ".jsonl");
        var good = new UTF8Encoding(false).GetBytes(Instructions(1, (@"F:\A.md", "Project", "한글")) + "\n");
        // 따옴표 안의 잘못된 바이트(0xFF): 대체 문자로 바꾸면 유효한 JSON이 되어 조용히 틀린 해시가 나온다
        var bad = new UTF8Encoding(false).GetBytes(Instructions(2, (@"F:\B.md", "Project", "X")) + "\n");
        var x = Array.IndexOf(bad, (byte)'X');
        bad[x] = 0xFF;
        File.WriteAllBytes(path, [.. good, .. bad]);

        var s = ClaudeTranscriptReader.Read(path);

        Assert.Equal(1, s.UnknownLines);
        Assert.Equal(@"F:\A.md", s.Instructions.Single().Files.Single().Path);
    }

    [Fact]
    public void LoneSurrogateContent_IsUnknown()
    {
        var raw = "{\"type\":\"attachment\",\"sessionId\":\"" + Session + "\",\"timestamp\":\"" + Ts(1) + "\"," +
                  "\"attachment\":{\"type\":\"instructions\",\"files\":[{\"path\":\"F:\\\\A.md\",\"content\":\"\\uD800\"},{\"path\":\"F:\\\\B.md\",\"content\":\"ok\"}]}}";
        var s = ClaudeTranscriptReader.Read(Write(raw));

        Assert.Equal(1, s.UnknownLines);
        Assert.Equal(@"F:\B.md", s.Instructions.Single().Files.Single().Path);
    }

    private static CallRecord Rec(string runId, string mode, DateTimeOffset at, string project = Cwd, int step = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = at, Tool = mode, Mode = mode, RunId = runId, Step = step,
        Model = "m", Prompt = "p", ProjectDir = project,
    };

    [Fact]
    public void Linker_ConfirmedEstimatedAmbiguousNone()
    {
        var path = Write(
            RunModeUse(0, "u1", "cross_p1"),   // 머리말 없음 → 추정
            ToolResult(1, "u1", "### 1. Cross_P1_A …"),
            RunModeUse(10, "u2", "review"),    // 확정
            ToolResult(11, "u2", "runId: 2222222222\n### 1."),
            RunModeUse(20, "u3", "cross_p2"),  // 후보 2개 → 연결 안 함
            RunModeUse(30, "u4", "debate"));   // 후보 없음 (창 밖 · 다른 프로젝트)
        var s = ClaudeTranscriptReader.Read(path);
        var at = DateTimeOffset.Parse("2026-10-16T07:37:00Z");

        var calls = new List<CallRecord>
        {
            Rec("1111111111", "cross_p1", at.AddSeconds(0.7)),
            Rec("1111111111", "cross_p1", at.AddSeconds(0.71), step: 2),
            Rec("2222222222", "review", at.AddSeconds(10.5)),
            Rec("3333333333", "cross_p2", at.AddSeconds(20.4)),
            Rec("4444444444", "cross_p2", at.AddSeconds(21)),
            Rec("5555555555", "debate", at.AddSeconds(30 + 61)),                          // +61초 → 창 밖
            Rec("6666666666", "debate", at.AddSeconds(30.5), project: @"F:\Other"),       // 다른 프로젝트
            Rec("7777777777", "review", at.AddSeconds(10.6)),                             // review 후보지만 u2는 확정
        };

        var links = RunLinker.LinkAll([s], calls)[s];

        Assert.Equal((RunLinkKind.Estimated, "1111111111"), (links[0].Kind, links[0].RunId));
        Assert.Equal((RunLinkKind.Confirmed, "2222222222"), (links[1].Kind, links[1].RunId));
        Assert.Equal((RunLinkKind.Ambiguous, 2), (links[2].Kind, links[2].Candidates));
        Assert.Equal(RunLinkKind.None, links[3].Kind);
    }

    [Fact]
    public void Linker_ExcludesRunsConfirmedByAnotherSession()
    {
        var at = DateTimeOffset.Parse("2026-10-16T07:37:00Z");
        var a = ClaudeTranscriptReader.Read(Write(RunModeUse(0, "u1", "review"), ToolResult(1, "u1", "### 머리말 없음")));
        var bPath = Path.Combine(_dir, "99999999-2222-3333-4444-555555555555.jsonl");
        File.WriteAllText(bPath, RunModeUse(0, "u9", "review") + "\n" + ToolResult(1, "u9", "runId: 8888888888\n"));
        var b = ClaudeTranscriptReader.Read(bPath);

        var links = RunLinker.LinkAll([a, b], [Rec("8888888888", "review", at.AddSeconds(0.5))]);

        Assert.Equal(RunLinkKind.None, links[a].Single().Kind);     // b가 확정한 실행은 a의 추정 후보가 아니다
        Assert.Equal(RunLinkKind.Confirmed, links[b].Single().Kind);
    }

    // ── 알 수 없는 형식 ───────────────────────────────────────────────────

    [Fact]
    public void BrokenLines_AreCountedAndSkipped()
    {
        var path = Write(
            "{ 깨진 json",
            "[1, 2, 3]",
            Line(new { type = "attachment", sessionId = Session, timestamp = Ts(1), attachment = new { type = "instructions", files = "배열 아님" } }),
            Line(new { type = "attachment", sessionId = Session, timestamp = Ts(1), attachment = new { type = "instructions", files = new object[] { new { path = 3 } } } }),
            Line(new { type = "assistant", sessionId = Session, message = "문자열 메시지" }),
            Instructions(2, (@"F:\A.md", "Project", "정상")),
            "",
            "\0\0\0");

        var s = ClaudeTranscriptReader.Read(path);

        Assert.Null(s.FormatError);
        Assert.Equal(6, s.UnknownLines); // 깨진 json · 배열 루트 · files 문자열 · path 숫자 항목 · message가 문자열 · 널 문자 줄
        Assert.Equal(@"F:\A.md", s.Instructions.Last().Files.Single().Path);
    }

    [Fact]
    public void FileWithNoRecognizableLines_IsUnknownFormat()
    {
        var s = ClaudeTranscriptReader.Read(Write("이건 jsonl이 아니다", "{\"a\":1}"));
        Assert.NotNull(s.FormatError);
        Assert.Empty(s.Instructions);
    }

    [Fact]
    public void MissingFile_IsUnknownFormat_NoThrow()
    {
        var s = ClaudeTranscriptReader.Read(Path.Combine(_dir, "없는파일.jsonl"));
        Assert.NotNull(s.FormatError);
    }

    // ── 읽기 전용 · UTF-8 ────────────────────────────────────────────────

    [Fact]
    public void Read_DoesNotModifyFile()
    {
        var path = Write(Instructions(1, (@"F:\A.md", "Project", "내용")), RunModeUse(2, "u", "review"));
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

        ClaudeTranscriptReader.Read(path);

        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Read_IsUtf8Explicit()
    {
        // 직렬화기가 한글을 \u 이스케이프로 쓰지 않도록 직접 만든 줄 (실제 세션 기록처럼 UTF-8 원문 바이트)
        var raw = "{\"type\":\"attachment\",\"sessionId\":\"" + Session + "\",\"timestamp\":\"" + Ts(1) + "\"," +
                  "\"attachment\":{\"type\":\"instructions\",\"files\":[{\"path\":\"F:\\\\가.md\",\"type\":\"Project\",\"content\":\"## Unity 공식 문서 참조\"}]}}";
        var s = ClaudeTranscriptReader.Read(Write(raw));

        var f = s.Instructions.Single().Files.Single();
        Assert.Equal(@"F:\가.md", f.Path);
        Assert.Equal(BlobStore.Sha256Hex(new UTF8Encoding(false).GetBytes("## Unity 공식 문서 참조")), f.Sha256);
    }

    // ── 원본 대조 (결정 4: content + LF 정확히 1개만 "끝 줄바꿈만 다름") ─────────

    [Theory]
    [InlineData("내용\n", "내용\n", InstructionDiskMatch.Same)]
    [InlineData("내용\n", "내용", InstructionDiskMatch.TrailingNewlineOnly)]
    [InlineData("내용\r\n", "내용", InstructionDiskMatch.Different)]
    [InlineData("내용\n\n", "내용", InstructionDiskMatch.Different)]
    [InlineData("바뀐 내용\n", "내용", InstructionDiskMatch.Different)]
    public void DiskCompare(string disk, string content, InstructionDiskMatch expected)
    {
        var file = Path.Combine(_dir, "g.md");
        File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes(disk));
        var bytes = new UTF8Encoding(false).GetBytes(content);

        Assert.Equal(expected, InstructionDiskCompare.Compare(new TranscriptInstructionFile(file, "Project", bytes.Length, BlobStore.Sha256Hex(bytes))));
    }

    [Fact]
    public void DiskCompare_Missing() =>
        Assert.Equal(InstructionDiskMatch.Missing,
            InstructionDiskCompare.Compare(new TranscriptInstructionFile(Path.Combine(_dir, "없음.md"), null, 0, new string('0', 64))));

    // ── 파일 이름 · 경로 인코딩 ──────────────────────────────────────────

    [Theory]
    [InlineData("320760ac-ee61-44fe-b7e4-9b5e9a218a47.jsonl", true)]
    [InlineData("320760ac-ee61-44fe-b7e4-9b5e9a218a47.orphaned-1792144695126-284d6068.jsonl", false)]
    [InlineData("notes.jsonl", false)]
    public void SessionFileName(string name, bool expected) => Assert.Equal(expected, ClaudeTranscriptReader.IsSessionFileName(name));

    [Theory]
    [InlineData(@"F:\AI\ToolCreate", "F--AI-ToolCreate")]
    [InlineData(@"F:\AI\ToolWithCS\Orchestration\HB_Orchestration", "F--AI-ToolWithCS-Orchestration-HB-Orchestration")]
    public void EncodeProjectDir(string path, string expected) => Assert.Equal(expected, ClaudeTranscriptReader.EncodeProjectDir(path));
}
