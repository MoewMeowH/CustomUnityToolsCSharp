using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using HB.Orchestration.Agents;
using HB.Orchestration.Shared;
using HB.Orchestration.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace HB.Orchestration.Tests;

/// <summary>review 계열 구조화 출력 (계획서 2026-10-19_리뷰-구조화-출력).</summary>
public sealed class StructuredOutputTests
{
    private static string Finding(string severity = "major", string? file = "src/A.cs", object? line = null, string problem = "문제",
        string confidence = "high", string extra = "") =>
        $$"""{"severity":"{{severity}}","file":{{(file is null ? "null" : $"\"{file}\"")}},"line":{{JsonSerializer.Serialize(line)}},"problem":"{{problem}}","detail":"상세","suggestion":"제안","confidence":"{{confidence}}"{{extra}}}""";

    private static StructuredOutputRecord Parse(string json, FindingFormat? format = null, ChatFinishReason? finish = null, int step = 1) =>
        FindingParser.Parse(format ?? FindingFormat.Findings, step, json, finish ?? ChatFinishReason.Stop, "prompt");

    // ── 해석 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Valid_json_is_parsed_sorted_by_severity_and_numbered()
    {
        var s = Parse($$"""{"findings":[{{Finding("minor", line: 10, problem: "작은 것")}},{{Finding("critical", line: 3, problem: "큰 것")}}],"needsInfo":[]}""");

        Assert.Equal(OutputStates.Complete, s.State);
        Assert.Equal(["1-F1", "1-F2"], s.Findings.Select(f => f.Id));
        Assert.Equal("큰 것", s.Findings[0].Problem);
        Assert.Equal("src/A.cs:3", s.Findings[0].Location);
    }

    [Fact]
    public void Fenced_json_with_prose_is_parsed()
    {
        var s = Parse($$"""
            결과입니다.
            ```json
            {"findings":[{{Finding()}}],"needsInfo":[]}
            ```
            """);
        Assert.True(s.Parsed);
        Assert.Single(s.Findings);
    }

    [Theory]
    [InlineData("그냥 텍스트", "JSON 객체가 아님")]
    [InlineData("""{"needsInfo":[]}""", "findings 배열이 없음")]
    [InlineData("""{"findings":[{"severity":"huge","problem":"p","confidence":"high"}]}""", "severity 'huge'")]
    [InlineData("""{"findings":[{"severity":"major","problem":"","confidence":"high"}]}""", "problem 비어 있음")]
    [InlineData("""{"findings":[{"severity":"major","problem":"p","confidence":"high","line":-3}]}""", "line은 1 이상")]
    [InlineData("""{"findings":[{"severity":"major","problem":"p","confidence":"sure"}]}""", "confidence 'sure'")]
    [InlineData("""{"findings":[],"needsInfo":"문자열"}""", "needsInfo 배열이 아님")]
    [InlineData("""[{"findings":[],"needsInfo":[]}]""", "최상위가 JSON 객체가 아님")]
    [InlineData("""{"findings":[{"severity":"major","problem":"p","confidence":"high","detail":{"a":1}}]}""", "detail이(가) 문자열이 아님")]
    [InlineData("""{"findings":[{"severity":"major","problem":"p","confidence":"high","suggestion":123}]}""", "suggestion이(가) 문자열이 아님")]
    [InlineData("""{"findings":[{"severity":"major","problem":"p","confidence":"high","file":[]}]}""", "file이(가) 문자열이 아님")]
    public void Invalid_output_fails_with_reason(string json, string reason)
    {
        var s = Parse(json);
        Assert.Equal(OutputStates.Failed, s.State);
        Assert.Contains(reason, s.Error);
    }

    [Fact]
    public void Truncated_json_fails_with_cut_reason()
    {
        var s = Parse("""{"findings":[{"severity":"major","pro""", finish: ChatFinishReason.Length);
        Assert.Equal(OutputStates.Failed, s.State);
        Assert.StartsWith("출력 상한에서 잘림", s.Error);
    }

    [Fact]
    public void Complete_json_with_length_finish_is_incomplete_not_no_problem()
    {
        var s = Parse("""{"findings":[],"needsInfo":[]}""", finish: ChatFinishReason.Length);
        Assert.Equal(OutputStates.Incomplete, s.State);
        Assert.DoesNotContain("문제 없음", FindingRenderer.Render(s));
    }

    [Fact]
    public void Empty_findings_with_needs_info_is_not_no_problem()
    {
        var s = Parse("""{"findings":[],"needsInfo":["호출부 코드"]}""");
        Assert.Equal(OutputStates.NeedsInfo, s.State);
        var text = FindingRenderer.Render(s);
        Assert.Contains("자료 부족: 호출부 코드", text);
        Assert.Contains("검토 불완전: 자료 부족 1건", text);
        Assert.DoesNotContain("문제 없음", text);
    }

    [Fact]
    public void Needs_info_prefix_is_not_doubled()
    {
        var s = Parse("""{"findings":[],"needsInfo":["자료 부족: 자료 부족: 호출부","자료 부족 — 설정 파일"]}""");
        Assert.Equal(["호출부", "— 설정 파일"], s.NeedsInfo);
        Assert.DoesNotContain("자료 부족: 자료 부족", FindingRenderer.Render(s));
    }

    [Fact]
    public void Empty_complete_is_no_problem()
    {
        Assert.Equal("문제 없음", FindingRenderer.Render(Parse("""{"findings":[],"needsInfo":[]}""")));
    }

    [Fact]
    public void String_line_takes_leading_number()
    {
        Assert.Equal(42, Parse($$"""{"findings":[{{Finding(line: "42-50")}}]}""").Findings[0].Line);
    }

    // ── 그리기 ───────────────────────────────────────────────────────────

    [Fact]
    public void Render_findings_line_and_summary()
    {
        var text = FindingRenderer.Render(Parse($$"""{"findings":[{{Finding("major", line: 42)}},{{Finding("nit", file: null)}}],"needsInfo":[]}"""));
        Assert.Contains("- 1-F1 [major] src/A.cs:42 — 문제 / 제안 / 확신도(high)\n  상세: 상세\n", text);
        Assert.Contains("- 1-F2 [nit] 위치 없음 — 문제", text);
        Assert.EndsWith("총 2건(critical 0 / major 1 / minor 0 / nit 1)", text);
    }

    [Fact]
    public void Render_perf_includes_all_fields_and_sections()
    {
        var json = $$"""
            {"findings":[{{Finding("high", line: 5, extra: ",\"frequency\":\"매 프레임\",\"scaling\":\"O(N)\",\"measurement\":\"Profiler GC Alloc\"")}}],
             "needsInfo":[],"specDependent":["N 규모"],"fixDirection":["캐싱"],"measurePriority":["GC Alloc"]}
            """;
        var text = FindingRenderer.Render(Parse(json, FindingFormat.PerfFindings));
        Assert.Contains("  빈도: 매 프레임 · 증가: O(N) · 측정: Profiler GC Alloc\n", text);
        Assert.Contains("사양 의존:\n  - N 규모\n", text);
        Assert.Contains("수정 방향:\n  - 캐싱\n", text);
        Assert.Contains("측정 우선순위:\n  - GC Alloc\n", text);
        Assert.EndsWith("총 1건(높음 1 / 중간 0 / 낮음 0)", text);
    }

    [Fact]
    public void Perf_rejects_review_severity()
    {
        Assert.Equal(OutputStates.Failed, Parse($$"""{"findings":[{{Finding("major")}}]}""", FindingFormat.PerfFindings).State);
    }

    // ── 겹침 후보 ─────────────────────────────────────────────────────────

    private static FindingRecord F(string id, string? file, int? line) =>
        new() { Id = id, Severity = "major", File = file, Line = line, Problem = "p", Confidence = "high" };

    [Fact]
    public void Overlap_groups_cross_step_within_window()
    {
        var lines = FindingOverlap.Lines([
            F("1-F1", "src/A.cs", 40), F("2-F1", "src\\A.cs", 44), F("2-F2", "src/A.cs", 46), // 46은 그룹 첫 줄(40)에서 6줄 → 새 그룹
            F("1-F2", "src/A.cs", 100), F("1-F3", "src/A.cs", 101),                        // 같은 단계뿐 → 표시 안 함
            F("1-F4", null, null), F("2-F3", "src/A.cs", null),                           // 위치 없음 → 비교 안 함
        ]);
        Assert.Equal(["겹침 후보: src/A.cs:40-44 — 1-F1, 2-F1"], lines);
    }

    [Fact]
    public void Overlap_caps_lines()
    {
        var many = Enumerable.Range(0, 12).SelectMany(i => new[] { F($"1-F{i + 1}", "a.cs", i * 100 + 1), F($"2-F{i + 1}", "a.cs", i * 100 + 2) });
        var lines = FindingOverlap.Lines(many);
        Assert.Equal(11, lines.Count);
        Assert.StartsWith("겹침 후보 외 2그룹", lines[^1]);
    }

    // ── 요청: Schema면 strict json_schema가 실제 HTTP 본문에 실린다 ───────────

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            const string reply = """
                {"id":"c","object":"chat.completion","created":1,"model":"m",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"{\"findings\":[],\"needsInfo\":[]}"}}],
                 "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Schema_request_sends_strict_json_schema_and_instructions()
    {
        var handler = new CaptureHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1/"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        var client = new OpenAIClient(new ApiKeyCredential("k"), options).GetChatClient("m").AsIChatClient();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "a", ChatOptions = StructuredRequest.Options("역할 지시", FindingFormat.Findings),
        });

        await agent.RunAsync("요청");

        using var body = JsonDocument.Parse(handler.Body!);
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("findings", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Contains("역할 지시", handler.Body);
    }

    [Fact]
    public void Schema_follows_strict_rules()
    {
        foreach (var format in new[] { FindingFormat.Findings, FindingFormat.PerfFindings })
            AssertStrict(format.Schema());

        static void AssertStrict(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("properties", out var props))
            {
                Assert.False(node.GetProperty("additionalProperties").GetBoolean());
                var required = node.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                foreach (var p in props.EnumerateObject())
                {
                    Assert.Contains(p.Name, required);
                    AssertStrict(p.Value);
                }
            }
            if (node.TryGetProperty("items", out var items)) AssertStrict(items);
        }
    }

    // ── 실행 경로 (ModeRunner + AgentRunner + 기록) ───────────────────────────

    private const string Modes = """
        {
          "Roles": { "Rev": { "Prompt": "역할", "Output": "findings" }, "Bad": { "Prompt": "역할", "Output": "nope" } },
          "Modes": {
            "r":    { "Steps": [ { "Role": "Rev", "Agent": "a1" } ] },
            "pan":  { "Flow": "Concurrent", "Steps": [ { "Role": "Rev", "Agent": "a1" }, { "Role": "Rev", "Agent": "a1" } ] },
            "bad":  { "Steps": [ { "Role": "Bad", "Agent": "a1" } ] }
          }
        }
        """;

    [Fact]
    public async Task Run_renders_for_conductor_and_records_raw_and_structured()
    {
        using var h = new ModeRunnerHarness(Modes);
        var json = $$"""{"findings":[{{Finding("major", line: 42)}}],"needsInfo":[]}""";
        h.Client.Script = [ScriptedChatClient.Reply(json)];

        var result = await h.Run("r");

        Assert.Contains("- 1-F1 [major] src/A.cs:42", result);
        Assert.DoesNotContain("\"findings\"", result);
        var rec = Assert.Single(h.Records());
        Assert.Equal(json, rec.Response);
        Assert.Equal("findings@1", rec.Structured!.Format);
        Assert.Equal("prompt", rec.Structured.RequestedBy);
        Assert.Equal("1-F1", rec.Structured.Findings[0].Id);
        Assert.Contains("## 출력 형식 (JSON)", ScriptedChatClient.SystemText(h.Client.Received[0]));
        Assert.Null(h.Client.Received[0].Options?.RawRepresentationFactory);
    }

    [Fact]
    public async Task Run_parse_failure_passes_raw_with_warning()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply("자유 텍스트 리뷰")];

        var result = await h.Run("r");

        Assert.Contains("자유 텍스트 리뷰", result);
        Assert.Contains("[경고] 구조화 출력 해석 실패 (JSON 객체가 아님)", result);
        var rec = Assert.Single(h.Records());
        Assert.True(rec.Success);
        Assert.Equal(OutputStates.Failed, rec.Structured!.State);
    }

    [Fact]
    public async Task Run_concurrent_reports_overlap()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply($$"""{"findings":[{{Finding(line: 10)}}],"needsInfo":[]}""")];

        var result = await h.Run("pan");

        Assert.Contains("겹침 후보: src/A.cs:10 — 1-F1, 2-F1", result);
    }

    [Fact]
    public async Task Schema_agent_sets_raw_representation()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Options.Agents[0].StructuredOutput = StructuredOutputMode.Schema;
        h.Client.Script = [ScriptedChatClient.Reply("""{"findings":[],"needsInfo":[]}""")];

        await h.Run("r");

        Assert.NotNull(h.Client.Received[0].Options?.RawRepresentationFactory);
        Assert.Equal("schema", h.Records()[0].Structured!.RequestedBy);
    }

    [Fact]
    public async Task Unknown_output_name_is_config_error()
    {
        using var h = new ModeRunnerHarness(Modes);
        Assert.Contains("Output 'nope'을(를) 모릅니다", await h.Run("bad"));
        Assert.Empty(h.Records());
    }

    [Fact]
    public async Task Save_result_saves_rendered_text()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply($$"""{"findings":[{{Finding(line: 7)}}],"needsInfo":[]}""")];
        var result = await h.Run("r");
        var runId = result.Split('\n')[0]["runId: ".Length..].Trim();
        var project = Directory.CreateTempSubdirectory("hb-orch-save-").FullName;
        try
        {
            var record = h.Records()[0] with { ProjectDir = project };
            File.WriteAllText(h.LogPath, "");
            await CallLog.AppendAsync(record, h.LogPath);

            var saved = ResultSaver.Save(runId, 1, "out.md", null, false, ContextOptions.BuiltInDenyPatterns, h.LogPath);
            Assert.StartsWith("저장:", saved);
            Assert.StartsWith("- 1-F1 [major] src/A.cs:7", File.ReadAllText(Path.Combine(project, "out.md")));
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    // ── record_verdicts findingIds ─────────────────────────────────────────

    [Fact]
    public async Task Verdicts_fill_from_finding_ids()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply($$"""{"findings":[{{Finding("minor", line: 10, problem: "작은 것")}},{{Finding("critical", line: 3, problem: "큰 것")}}],"needsInfo":[]}""")];
        var runId = (await h.Run("pan")).Split('\n')[0]["runId: ".Length..].Trim();

        var result = VerdictRecorder.Record(runId, [
            new VerdictItem("1-F1", null, "adopted", FindingIds: ["1-F1", "2-F1"]),
            new VerdictItem("x", null, "adopted", FindingIds: ["9-F9"]),
        ], 500, h.LogPath);
        Assert.Contains("이 실행에 없는 지적 ID '9-F9'", result);

        result = VerdictRecorder.Record(runId, [new VerdictItem("1-F1", null, "adopted", FindingIds: ["1-F1", "2-F1"])], 500, h.LogPath);
        Assert.Contains("지적 ID 연결 1", result);
        var v = Assert.Single(VerdictLog.ReadAll(VerdictLog.PathFor(h.LogPath)));
        Assert.Equal([1, 2], v.Steps);
        Assert.Equal("큰 것", v.Summary);
        Assert.Equal("src/A.cs:3", v.File);
        Assert.Equal("high", v.Severity);
        Assert.Equal(["1-F1", "2-F1"], v.FindingIds);
    }

    [Fact]
    public async Task Verdict_steps_must_match_finding_ids()
    {
        using var h = new ModeRunnerHarness(Modes);
        h.Client.Script = [ScriptedChatClient.Reply($$"""{"findings":[{{Finding()}}],"needsInfo":[]}""")];
        var runId = (await h.Run("pan")).Split('\n')[0]["runId: ".Length..].Trim();

        var result = VerdictRecorder.Record(runId, [new VerdictItem("k", null, "adopted", Steps: [2], FindingIds: ["1-F1"])], 500, h.LogPath);
        Assert.Contains("steps(2)가 findingIds의 단계(1)와 다릅니다", result);
    }
}
