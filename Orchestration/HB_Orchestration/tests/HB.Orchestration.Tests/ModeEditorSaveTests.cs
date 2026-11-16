using System.Text.Json.Nodes;
using HB.Orchestration.Agents;
using HB.Orchestration.Dashboard;
using HB.Orchestration.Dashboard.Pages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HB.Orchestration.Tests;

/// <summary>그래프 편집기의 저장: 모드 · 역할 하나 단위, 사용 중 사본과 git 원본에 같은 동작.</summary>
public sealed class ModeEditorSaveTests : IDisposable
{
    private const string Live = """
        {
          "Roles": { "R": { "Prompt": "역할 R" }, "Spare": { "Prompt": "안 쓰는 역할" } },
          "Modes": {
            "first":  { "Description": "첫째", "Steps": [ { "Role": "R", "Agent": "a1" } ] },
            "second": { "Description": "둘째", "Custom": 7, "Steps": [ { "Role": "R", "Agent": "a1" }, { "Role": "R", "Agent": "a2" } ] },
            "third":  { "Steps": [ { "Role": "Reviewer", "Agent": "a1" } ] }
          }
        }
        """;

    // 원본은 사본과 일부러 다르다: second의 설명이 다르고, 원본에만 있는 모드가 있다.
    private const string Source = """
        {
          "Roles": { "R": { "Prompt": "역할 R" }, "Spare": { "Prompt": "안 쓰는 역할" } },
          "Modes": {
            "first":    { "Description": "첫째", "Steps": [ { "Role": "R", "Agent": "a1" } ] },
            "second":   { "Description": "원본의 둘째", "Steps": [ { "Role": "R", "Agent": "a1" } ] },
            "third":    { "Steps": [ { "Role": "Reviewer", "Agent": "a1" } ] },
            "only_src": { "Steps": [ { "Role": "R", "Agent": "zzz" } ] }
          }
        }
        """;

    private const string Graph = """
        { "Description": "새 모드", "Flow": "Graph", "Steps": [
            { "Id": "a", "Role": "R", "Agent": "a1" },
            { "Role": "R", "Agent": "a2", "From": ["input", "a"] } ] }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("hb-mode-editor-").FullName;
    private string LivePath => Path.Combine(_dir, "live", "modes.json");
    private string SourcePath => Path.Combine(_dir, "src", "modes.json");

    public ModeEditorSaveTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "live"));
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(LivePath, Live);
        File.WriteAllText(SourcePath, Source);
        File.WriteAllText(Path.Combine(_dir, "live", "appsettings.json"), """
            {"Orchestration":{"Providers":{"p":{"Type":"openai-compatible"},"w":{"Type":"openai-responses"}},
             "Agents":[{"Name":"a1","Provider":"p","Model":"m"},{"Name":"a2","Provider":"p","Model":"m"},{"Name":"s1","Provider":"w","Model":"m"}]}}
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ModesConfigService Service(string? source = "") => new(Options.Create(new DashboardOptions
    {
        McpConfigDir = Path.Combine(_dir, "live"),
        ModesSourcePath = source == "" ? SourcePath : source,
    }), _ => null);

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    private static List<string> ModeNamesIn(string path) => [.. ((JsonObject)Read(path)["Modes"]!).Select(p => p.Key)];
    private static string Stamp(ModesConfigService service) => service.Load().Stamp;

    // ── 모드 ────────────────────────────────────────────────────────────────

    [Fact]
    public void New_mode_is_written_to_the_live_file_and_the_source_file()
    {
        var service = Service();

        var result = service.SaveMode(Stamp(service), null, "fourth", Graph);

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.StartsWith("git 원본에도 반영했습니다", Assert.Single(result.Notes));
        Assert.Equal(["first", "second", "third", "fourth"], ModeNamesIn(LivePath));
        Assert.Equal(["first", "second", "third", "only_src", "fourth"], ModeNamesIn(SourcePath));
        var saved = service.Load().Typed.Modes["fourth"];
        Assert.Equal(ModeFlow.Graph, saved.Flow);
        Assert.Equal(["input", "a"], saved.Steps[1].From);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "live", "modes-backup")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "src", "modes-backup"))); // 원본은 백업 폴더를 만들지 않는다
    }

    [Fact]
    public void Replacing_a_mode_keeps_its_place_and_the_keys_the_screen_does_not_know()
    {
        var service = Service();
        var edited = """{ "Description": "고친 둘째", "Custom": 7, "Steps": [ { "Role": "R", "Agent": "a2" } ] }""";

        var result = service.SaveMode(Stamp(service), "second", "second", edited);

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.Equal(["first", "second", "third"], ModeNamesIn(LivePath));
        var live = (JsonObject)Read(LivePath)["Modes"]!["second"]!;
        Assert.Equal("고친 둘째", live["Description"]!.GetValue<string>());
        Assert.Equal(7, live["Custom"]!.GetValue<int>());
        // 원본에서도 그 모드만 바뀌고, 원본에만 있는 모드는 남는다
        Assert.Equal("고친 둘째", Read(SourcePath)["Modes"]!["second"]!["Description"]!.GetValue<string>());
        Assert.Equal(["first", "second", "third", "only_src"], ModeNamesIn(SourcePath));
    }

    [Fact]
    public void Renaming_a_mode_keeps_its_place_in_both_files()
    {
        var service = Service();

        var result = service.SaveMode(Stamp(service), "second", "second_new", """{ "Steps": [ { "Role": "R", "Agent": "a1" } ] }""");

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.Equal(["first", "second_new", "third"], ModeNamesIn(LivePath));
        Assert.Equal(["first", "second_new", "third", "only_src"], ModeNamesIn(SourcePath));
    }

    [Theory]
    [InlineData(null, "first", "이미 있습니다")]                    // 새 모드인데 있는 이름
    [InlineData("second", "FIRST", "이미 있습니다")]                // 이름 바꾸기인데 있는 이름 (대소문자 무시)
    [InlineData("missing", "missing", "파일에 없습니다")]           // 화면이 본 모드가 사라짐
    [InlineData(null, "bad name", "영문 · 숫자")]
    [InlineData(null, "review", "예전 모드 이름")]
    public void Mode_name_problems_are_rejected_without_writing(string? original, string name, string expected)
    {
        var service = Service();

        var result = service.SaveMode(Stamp(service), original, name, Graph);

        Assert.Contains(expected, Assert.Single(result.Errors));
        Assert.Equal(Live, File.ReadAllText(LivePath));
        Assert.Equal(Source, File.ReadAllText(SourcePath));
    }

    [Theory]
    [InlineData("""[1, 2]""", "JSON 객체가 아닙니다")]
    [InlineData("""{ "Steps": [""", "JSON 형식 오류")]
    [InlineData("""{ "Steps": [ { "Role": "R", "Agent": "nope" } ] }""", "에이전트 'nope'")]
    [InlineData("""{ "Steps": [ { "Role": "Nobody", "Agent": "a1" } ] }""", "역할 'Nobody'")]
    [InlineData("""{ "Flow": "Graph", "Steps": [ { "Role": "R", "Agent": "a1", "From": ["x"] } ] }""", "이 모드에 없는 Id")]
    [InlineData("""{ "Steps": [] }""", "Steps가 비어 있습니다")]
    public void Wrong_mode_content_is_rejected_without_writing(string json, string expected)
    {
        var service = Service();

        var result = service.SaveMode(Stamp(service), null, "fourth", json);

        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(Live, File.ReadAllText(LivePath));
        Assert.Equal(Source, File.ReadAllText(SourcePath));
    }

    [Fact]
    public void Save_is_rejected_when_the_file_changed_after_the_screen_opened()
    {
        var service = Service();
        var stamp = Stamp(service);
        File.WriteAllText(LivePath, Live.Replace("첫째", "바뀐 첫째"));

        var result = service.SaveMode(stamp, null, "fourth", Graph);

        Assert.Contains("다른 곳에서 바뀌었습니다", Assert.Single(result.Errors));
        Assert.Equal(Source, File.ReadAllText(SourcePath));
    }

    [Fact]
    public void Deleting_a_mode_removes_it_from_both_files()
    {
        var service = Service();

        var result = service.DeleteMode(Stamp(service), "second");

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.Equal(["first", "third"], ModeNamesIn(LivePath));
        Assert.Equal(["first", "third", "only_src"], ModeNamesIn(SourcePath));
        Assert.Contains("파일에 없습니다", Assert.Single(service.DeleteMode(Stamp(service), "second").Errors));
    }

    // ── 역할 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Role_is_added_replaced_and_a_built_in_role_can_be_overridden()
    {
        var service = Service();

        Assert.True(service.SaveRole(Stamp(service), "Fresh", """{ "Prompt": "새 역할\n둘째 줄", "UseGuidelines": false }""").Ok);
        Assert.True(service.SaveRole(Stamp(service), "r", """{ "Prompt": "고친 R" }""").Ok);               // 대소문자가 달라도 같은 역할
        Assert.True(service.SaveRole(Stamp(service), "Reviewer", """{ "Prompt": "덮어쓴 리뷰어" }""").Ok);

        var roles = service.Load().Typed.Roles;
        Assert.Equal("새 역할\n둘째 줄", roles["Fresh"].Prompt);
        Assert.False(roles["Fresh"].UseGuidelines);
        Assert.Equal("고친 R", roles["R"].Prompt);
        Assert.Equal("덮어쓴 리뷰어", roles["Reviewer"].Prompt);
        Assert.Equal(["R", "Spare", "Fresh", "Reviewer"], ((JsonObject)Read(LivePath)["Roles"]!).Select(p => p.Key));
        Assert.Equal("고친 R", Read(SourcePath)["Roles"]!["R"]!["Prompt"]!.GetValue<string>());
    }

    [Fact]
    public void Role_in_use_is_not_deleted_but_an_unused_one_and_a_built_in_override_are()
    {
        var service = Service();
        Assert.True(service.SaveRole(Stamp(service), "Reviewer", """{ "Prompt": "덮어쓴 리뷰어" }""").Ok);

        var inUse = service.DeleteRole(Stamp(service), "R");
        var unused = service.DeleteRole(Stamp(service), "Spare");
        var builtIn = service.DeleteRole(Stamp(service), "Reviewer"); // third가 쓰지만 지우면 내장 프롬프트로 돌아간다

        Assert.Contains("쓰는 모드가 있어 지울 수 없습니다: first, second", Assert.Single(inUse.Errors));
        Assert.True(unused.Ok, string.Join(" / ", unused.Errors));
        Assert.True(builtIn.Ok, string.Join(" / ", builtIn.Errors));
        Assert.Equal(["R"], ((JsonObject)Read(LivePath)["Roles"]!).Select(p => p.Key));
        Assert.Equal(["R"], ((JsonObject)Read(SourcePath)["Roles"]!).Select(p => p.Key));
    }

    [Theory]
    [InlineData("r", "이미 있습니다")]              // 파일에 있는 역할 (대소문자 무시)
    [InlineData("Reviewer", "내장 역할 이름")]
    public void New_role_with_a_taken_name_is_rejected_without_writing(string name, string expected)
    {
        var service = Service();

        var result = service.SaveRole(Stamp(service), name, """{ "Prompt": "새 프롬프트" }""", isNew: true);

        Assert.Contains(expected, Assert.Single(result.Errors));
        Assert.Equal(Live, File.ReadAllText(LivePath));
        Assert.Equal(Source, File.ReadAllText(SourcePath));
    }

    [Theory]
    [InlineData("""{ "Prompt": "  " }""")]
    [InlineData("""{ "UseGuidelines": false }""")]
    public void Role_without_a_prompt_is_rejected_unless_it_overrides_a_built_in_role(string json)
    {
        var service = Service();

        var result = service.SaveRole(Stamp(service), "R", json);

        Assert.Contains("프롬프트가 없습니다", Assert.Single(result.Errors));
        Assert.Equal(Live, File.ReadAllText(LivePath));
        Assert.True(service.SaveRole(Stamp(service), "Reviewer", json).Ok);                                    // 내장: 프롬프트 없이 설정만 덮는다
        Assert.True(service.SaveRole(Stamp(service), "FromFile", """{ "PromptFile": "prompts/x.md" }""", isNew: true).Ok);
    }

    // ── 검증 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{ "Steps": [ { "Role": "R", "Agent": "s1", "WebSearch": { "MaxCalls": 0 } } ] }""", "WebSearch.MaxCalls")]
    [InlineData("""{ "Steps": [ { "Role": "R", "Agent": "a1", "WebSearch": { "MaxCalls": 3 } } ] }""", "제공사 Type이 'openai-responses'")]
    [InlineData("""{ "Steps": [ { "Role": "Reviewer", "Agent": "s1", "WebSearch": { "MaxCalls": 3 } } ] }""", "구조화 출력")]
    public void Web_search_settings_the_server_would_refuse_are_rejected(string json, string expected)
    {
        var service = Service();

        var result = service.SaveMode(Stamp(service), null, "fourth", json);

        Assert.Contains(expected, Assert.Single(result.Errors));
        Assert.Equal(Live, File.ReadAllText(LivePath));
        var searching = """{ "Steps": [ { "Role": "R", "Agent": "s1", "WebSearch": { "MaxCalls": 3, "BlockedDomains": ["example.com"] } } ] }""";
        Assert.True(service.SaveMode(Stamp(service), null, "fourth", searching).Ok);
    }

    [Fact]
    public void An_error_already_in_another_mode_does_not_block_the_save_but_the_saved_mode_must_be_clean()
    {
        // 손으로 고치다 틀린 모드가 이미 있다 (third가 없는 역할을 쓴다)
        File.WriteAllText(LivePath, Live.Replace("\"Reviewer\"", "\"Nobody\""));
        var service = Service();
        var stillBroken = """{ "Steps": [ { "Role": "Nobody", "Agent": "a1" } ] }""";

        var other = service.SaveMode(Stamp(service), null, "fourth", Graph);
        var itself = service.SaveMode(Stamp(service), "third", "third", stillBroken);
        var repaired = service.SaveMode(Stamp(service), "third", "third", """{ "Steps": [ { "Role": "R", "Agent": "a1" } ] }""");

        Assert.True(other.Ok, string.Join(" / ", other.Errors));
        Assert.Contains("역할 'Nobody'", Assert.Single(itself.Errors));
        Assert.True(repaired.Ok, string.Join(" / ", repaired.Errors));
        Assert.Equal(["first", "second", "third", "fourth"], ModeNamesIn(LivePath));
    }

    [Fact]
    public void A_rejected_save_keeps_the_stamp_the_screen_sent()
    {
        var options = Options.Create(new DashboardOptions { McpConfigDir = Path.Combine(_dir, "live"), LogPath = Path.Combine(_dir, "calls.jsonl") });
        var service = new ModesConfigService(options, _ => null);
        var stale = Stamp(service);
        File.WriteAllText(LivePath, Live.Replace("첫째", "바뀐 첫째"));
        var model = new ModesModel(service, new CallLogCache(options, NullLogger<CallLogCache>.Instance));

        model.OnPostMode(stale, null, "fourth", Graph);

        Assert.Contains("다른 곳에서 바뀌었습니다", Assert.Single(model.Errors));
        Assert.Equal(stale, model.EditorStamp);            // 그대로 다시 눌러도 거절된다 — 그 사이 변경을 덮지 않는다
        Assert.NotEqual(stale, model.Snapshot!.Stamp);
        Assert.Contains("fourth", model.EditorJson);       // 고치던 내용은 화면에 남는다
    }

    // ── 원본 반영 ────────────────────────────────────────────────────────────

    [Fact]
    public void Source_is_not_rewritten_when_the_edit_changes_nothing_there()
    {
        var liveOnly = Service(source: null);
        Assert.True(liveOnly.SaveMode(Stamp(liveOnly), null, "fourth", Graph).Ok);
        var service = Service();

        var result = service.DeleteMode(Stamp(service), "fourth"); // 원본에는 없는 모드

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.Contains("바꿀 것이 없었습니다", Assert.Single(result.Notes));
        Assert.Equal(Source, File.ReadAllText(SourcePath));        // 서식도 그대로
    }

    [Fact]
    public void Source_is_left_alone_when_the_edited_file_is_not_the_server_folder_file()
    {
        // 편집 대상을 직접 지정한 경우 (프로젝트 전용 modes.json 등) — 그 수정을 기본 modes.json의 원본에 섞지 않는다
        var service = new ModesConfigService(Options.Create(new DashboardOptions
        {
            McpConfigDir = Path.Combine(_dir, "live"), ModesPath = LivePath, ModesSourcePath = SourcePath,
        }), _ => null);

        var result = service.SaveMode(Stamp(service), null, "fourth", Graph);

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        Assert.Contains("git 원본에는 적용하지 않았습니다", Assert.Single(result.Notes));
        Assert.Equal(Source, File.ReadAllText(SourcePath));
        Assert.False(service.WritesSource(service.Load()));
        var normal = Service();
        Assert.True(normal.WritesSource(normal.Load()));
    }

    // ── 원본 반영 실패 ───────────────────────────────────────────────────────

    [Fact]
    public void Without_a_source_path_only_the_live_file_is_saved()
    {
        var service = Service(source: null);

        var result = service.SaveMode(Stamp(service), null, "fourth", Graph);

        Assert.True(result.Ok);
        Assert.Contains("사용 중 사본만 저장했습니다", Assert.Single(result.Notes));
        Assert.False(service.HasModesSource);
        Assert.Equal(Source, File.ReadAllText(SourcePath));
    }

    [Fact]
    public void Source_failure_keeps_the_live_save_and_says_why()
    {
        var missing = Service(source: Path.Combine(_dir, "nowhere", "modes.json"));
        var wrongName = Service(source: Path.Combine(_dir, "live", "appsettings.json"));
        File.WriteAllText(Path.Combine(_dir, "src", "broken.json"), "{");

        var first = missing.SaveMode(Stamp(missing), null, "fourth", Graph);
        var second = wrongName.SaveMode(Stamp(wrongName), null, "fifth", Graph);

        Assert.True(first.Ok);
        Assert.StartsWith(ModesConfigService.SourceFailedPrefix + ": 파일이 없습니다", Assert.Single(first.Notes));
        Assert.True(second.Ok);
        Assert.StartsWith(ModesConfigService.SourceFailedPrefix + ": 파일 이름이 modes.json이 아닙니다", Assert.Single(second.Notes));
        Assert.Equal(["first", "second", "third", "fourth", "fifth"], ModeNamesIn(LivePath));
    }

    [Fact]
    public void Source_that_would_become_invalid_is_left_alone()
    {
        // 원본에는 역할 R이 없다 → 그 역할을 쓰는 새 모드를 넣으면 원본 검증이 실패한다
        File.WriteAllText(SourcePath, """{ "Roles": {}, "Modes": { "third": { "Steps": [ { "Role": "Reviewer", "Agent": "a1" } ] } } }""");
        var before = File.ReadAllText(SourcePath);
        var service = Service();

        var result = service.SaveMode(Stamp(service), null, "fourth", Graph);

        Assert.True(result.Ok);
        Assert.StartsWith(ModesConfigService.SourceFailedPrefix, Assert.Single(result.Notes));
        Assert.Contains("역할 'R'", result.Notes[0]);
        Assert.Equal(before, File.ReadAllText(SourcePath));
        Assert.Contains("fourth", ModeNamesIn(LivePath));
    }
}
