using System.Text.Json;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>모드 화면의 그래프 편집기가 읽는 자료(페이지 안 JSON)와 스크립트 파일 경로.</summary>
public sealed class ModeEditorDataTests : IDisposable
{
    private const string Modes = """
        {
          "Roles": {
            "R": { "Prompt": "역할 R </script><b>&", "UseGuidelines": false },
            "Reviewer": { "Output": "findings@1" },
            "Spare": { "Prompt": "안 쓰는 역할" }
          },
          "Modes": {
            "seq":   { "Description": "순차", "Custom": 7, "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "Reviewer", "Agent": "a2" } ] },
            "graph": { "Flow": "Graph", "Steps": [ { "Id": "a", "Role": "R", "Agent": "a1" }, { "Role": "Critic", "Agent": "a1", "From": ["input", "a"] } ] },
            "bad":   { "Flow": "Graph", "Steps": [ { "Role": "R", "Agent": "a1", "From": ["x"] } ] }
          }
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("hb-editor-data-").FullName;

    public ModeEditorDataTests()
    {
        File.WriteAllText(Path.Combine(_dir, "modes.json"), Modes);
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            {"Orchestration":{"Providers":{"p":{"Type":"openai-compatible"},"w":{"Type":"openai-responses"}},
             "Agents":[{"Name":"a1","Provider":"p","Model":"m1"},{"Name":"a2","Provider":"w","Model":"m2","Enabled":false}]}}
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CallRecord Call(string mode, string runId, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12], Timestamp = at, Tool = "run_mode", Mode = mode, RunId = runId, Agent = "a", Model = "m", Prompt = "p",
    };

    private JsonElement Build(IReadOnlyList<CallRecord>? calls = null, string? selected = null, EditorDraft? draft = null, bool hasSource = true)
    {
        var snap = new ModesConfigService(Options.Create(new DashboardOptions { McpConfigDir = _dir }), _ => null).Load();
        var json = ModeEditorData.Build(snap, calls ?? [], DateTimeOffset.Now, hasSource, selected, draft);
        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase); // script 블록 안에 실린다
        Assert.DoesNotContain("<b>", json, StringComparison.Ordinal);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static JsonElement Named(JsonElement array, string name) =>
        array.EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

    [Fact]
    public void Modes_carry_the_file_object_as_written_with_recent_run_counts_and_wiring_errors()
    {
        var now = DateTimeOffset.Now;
        var data = Build([
            Call("seq", "r1", now.AddDays(-1)), Call("seq", "r1", now.AddDays(-1)),   // 같은 실행의 두 단계 = 1회
            Call("seq", "r2", now.AddHours(-2)),
            Call("seq", "old", now.AddDays(-8)),                                       // 7일 밖
            Call("graph", "r3", now.AddMinutes(-5)),
        ]);

        var modes = data.GetProperty("modes");
        Assert.Equal(["seq", "graph", "bad"], modes.EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        var seq = Named(modes, "seq");
        Assert.Equal(7, seq.GetProperty("def").GetProperty("Custom").GetInt32());     // 화면이 모르는 키도 그대로
        Assert.Equal(2, seq.GetProperty("runs7d").GetInt32());
        Assert.Equal(JsonValueKind.Null, seq.GetProperty("error").ValueKind);
        Assert.Equal(1, Named(modes, "graph").GetProperty("runs7d").GetInt32());
        Assert.Contains("이 모드에 없는 Id", Named(modes, "bad").GetProperty("error").GetString());
        Assert.Equal(0, Named(modes, "bad").GetProperty("runs7d").GetInt32());
    }

    [Fact]
    public void Roles_list_file_roles_and_built_in_roles_with_their_users()
    {
        var roles = Build().GetProperty("roles");

        Assert.Equal(["R", "Reviewer", "Spare", "Critic"], roles.EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        var r = Named(roles, "R");
        Assert.False(r.GetProperty("builtIn").GetBoolean());
        Assert.Equal("역할 R </script><b>&", r.GetProperty("def").GetProperty("Prompt").GetString()); // 풀면 원래 글자
        Assert.Equal(["seq", "graph", "bad"], r.GetProperty("usedBy").EnumerateArray().Select(m => m.GetString()));

        var reviewer = Named(roles, "Reviewer");                                     // 내장 역할을 파일에서 덮어씀
        Assert.True(reviewer.GetProperty("builtIn").GetBoolean());
        Assert.Equal("findings@1", reviewer.GetProperty("def").GetProperty("Output").GetString());
        Assert.StartsWith("너는 시니어 게임 클라이언트 엔지니어", reviewer.GetProperty("builtInPrompt").GetString());
        Assert.Equal("findings", reviewer.GetProperty("builtInOutput").GetString());

        var critic = Named(roles, "Critic");                                         // 파일에 없는 내장 역할
        Assert.True(critic.GetProperty("builtIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, critic.GetProperty("def").ValueKind);
        Assert.Equal(["graph"], critic.GetProperty("usedBy").EnumerateArray().Select(m => m.GetString()));
        Assert.Empty(Named(roles, "Spare").GetProperty("usedBy").EnumerateArray());
    }

    [Fact]
    public void Top_level_keys_are_found_whatever_their_case()
    {
        // 서버는 키의 대소문자를 무시하고 읽는다 — 화면도 같은 모드 · 역할을 보여야 한다
        File.WriteAllText(Path.Combine(_dir, "modes.json"), Modes.Replace("\"Roles\"", "\"roles\"").Replace("\"Modes\"", "\"modes\""));

        var data = Build();

        Assert.Equal(["seq", "graph", "bad"], data.GetProperty("modes").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Object, Named(data.GetProperty("roles"), "R").GetProperty("def").ValueKind);
        Assert.Equal(JsonValueKind.Object, Named(data.GetProperty("roles"), "Reviewer").GetProperty("def").ValueKind);
    }

    [Fact]
    public void Agents_limits_and_the_returned_draft_are_included()
    {
        var data = Build(selected: "graph", draft: new EditorDraft("mode", "new name", null, """{"Steps":[]}"""), hasSource: false);

        var agents = data.GetProperty("agents");
        Assert.Equal("openai-compatible", Named(agents, "a1").GetProperty("providerType").GetString());
        Assert.False(Named(agents, "a2").GetProperty("enabled").GetBoolean());
        Assert.Equal("openai-responses", Named(agents, "a2").GetProperty("providerType").GetString());

        Assert.Equal(8, data.GetProperty("limits").GetProperty("maxGraphSteps").GetInt32());
        Assert.Equal(5, data.GetProperty("limits").GetProperty("maxRounds").GetInt32());
        Assert.Equal("openai-responses", data.GetProperty("limits").GetProperty("searchProviderType").GetString());
        Assert.Contains("findings", data.GetProperty("outputs").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal("graph", data.GetProperty("selected").GetString());
        Assert.False(data.GetProperty("hasSource").GetBoolean());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("stamp").GetString()));

        var draft = data.GetProperty("draft");
        Assert.Equal(("mode", "new name", """{"Steps":[]}"""),
            (draft.GetProperty("kind").GetString(), draft.GetProperty("name").GetString(), draft.GetProperty("json").GetString()));
        Assert.Equal(JsonValueKind.Null, Build().GetProperty("draft").ValueKind);
    }

    [Theory]
    [InlineData("x6.min.js", 583_499L)]
    [InlineData("mode-graph.js", null)]
    public void Script_files_come_from_the_assembly(string name, long? length)
    {
        var file = LibFiles.Open(name);

        Assert.NotNull(file);
        using var content = file.Value.Content;
        Assert.Equal("text/javascript; charset=utf-8", file.Value.ContentType);
        Assert.True(content.Length > 1000);
        if (length is not null) Assert.Equal(length, content.Length);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("../x6.min.js")]
    [InlineData("X6.MIN.JS")]
    [InlineData("")]
    public void Names_outside_the_list_are_not_served(string name) => Assert.Null(LibFiles.Open(name));
}
