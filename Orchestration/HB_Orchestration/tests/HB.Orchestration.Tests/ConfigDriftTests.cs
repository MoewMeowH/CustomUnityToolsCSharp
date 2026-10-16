using System.Text;
using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

/// <summary>
/// ConfigDrift: 원본에만 있는 경로와 자료형 차이만 알리고, 사용 중 값은 보지 않는다.
/// ConfigDriftReport: publish 뒤 bat이 부르는 --config-drift 출력 · 종료 코드.
/// </summary>
public sealed class ConfigDriftTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hb-config-drift-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static IReadOnlyList<ConfigDriftItem> Diff(string source, string live) =>
        ConfigDrift.Compare(ConfigDrift.Parse(source), ConfigDrift.Parse(live));

    private static string[] Lines(IReadOnlyList<ConfigDriftItem> items) =>
        items.Select(i => $"{i.Kind} {i.Path}").ToArray();

    // ── 비교 규칙 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Same_NoItems()
    {
        var json = """{ "Modes": { "review": { "Steps": [ { "Role": "R", "Agent": "a" } ] } } }""";
        Assert.Empty(Diff(json, json));
    }

    [Fact]
    public void OnlyInSource_ModeRoleField_ReportedOnceEach()
    {
        var source = """
            { "Roles": { "R": {}, "WorkOrderCheck": { "Prompt": "p" } },
              "Modes": { "review": { "RequireOwnReview": true, "Description": "d" },
                         "work_order": { "Steps": [], "Flow": "Concurrent" } } }
            """;
        var live = """{ "Roles": { "R": {} }, "Modes": { "review": { "Description": "바꾼 값" } } }""";

        Assert.Equal(
            ["OnlyInSource Roles.WorkOrderCheck", "OnlyInSource Modes.review.RequireOwnReview", "OnlyInSource Modes.work_order"],
            Lines(Diff(source, live)));
    }

    [Fact]
    public void LiveOnlyKeysAndChangedValues_NotReported()
    {
        var source = """{ "Budget": { "DailyTokenLimit": 300000 } }""";
        var live = """{ "Budget": { "DailyTokenLimit": 5 }, "Context": { "ExtraRoots": { "orch": "F:/x" } }, "Debug": {} }""";
        Assert.Empty(Diff(source, live));
    }

    [Fact]
    public void Keys_CaseInsensitive()
    {
        Assert.Empty(Diff("""{ "Modes": { "Review": { "MaxRounds": 2 } } }""", """{ "modes": { "review": { "maxrounds": 3 } } }"""));
    }

    [Fact]
    public void CaseCollision_NotComparable_SkipsInside()
    {
        var items = Diff("""{ "Modes": { "a": { "X": 1 }, "A": { "Y": 1 } } }""", """{ "Modes": {} }""");
        var item = Assert.Single(items);
        Assert.Equal(ConfigDriftKind.NotComparable, item.Kind);
        Assert.Equal("Modes", item.Path);
    }

    [Theory]
    [InlineData("""{ "X": { "a": 1 } }""", """{ "X": null }""", "object", "null")]
    [InlineData("""{ "X": [1] }""", """{ "X": { } }""", "array", "object")]
    [InlineData("""{ "X": "1" }""", """{ "X": 1 }""", "string", "number")]
    [InlineData("""{ "X": true }""", """{ "X": "true" }""", "bool", "string")]
    public void KindMismatch_Reported(string source, string live, string sourceKind, string liveKind)
    {
        var item = Assert.Single(Diff(source, live));
        Assert.Equal(ConfigDriftKind.KindMismatch, item.Kind);
        Assert.Equal("X", item.Path);
        Assert.Equal($"원본 {sourceKind}, 사용 중 {liveKind}", item.Detail);
    }

    [Fact]
    public void SameKindValueChange_NotReported()
    {
        Assert.Empty(Diff("""{ "X": true, "Y": null }""", """{ "X": false, "Y": null }"""));
    }

    [Fact]
    public void RootKindMismatch_ShownAsTopLevel()
    {
        var item = Assert.Single(Diff("{}", "[]"));
        Assert.Equal("(최상위)", item.Path);
    }

    // ── 배열 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NamedArray_MatchedByName_CaseInsensitive()
    {
        var source = """{ "Agents": [ { "Name": "a", "Model": "m", "MaxOutputTokens": 1 }, { "Name": "new_one", "Model": "m" } ] }""";
        var live = """{ "Agents": [ { "name": "A", "Model": "다른 모델" } ] }""";
        Assert.Equal(
            ["OnlyInSource Agents[Name=a].MaxOutputTokens", "OnlyInSource Agents[Name=new_one]"],
            Lines(Diff(source, live)));
    }

    [Fact]
    public void NamedArray_LiveEmpty_AllSourceNamesReported()
    {
        Assert.Equal(["OnlyInSource Agents[Name=a]"], Lines(Diff("""{ "Agents": [ { "Name": "a" } ] }""", """{ "Agents": [] }""")));
    }

    [Fact]
    public void SourceEmptyArray_NotCompared()
    {
        Assert.Empty(Diff("""{ "Agents": [] }""", """{ "Agents": [ { "Name": "a" } ] }"""));
    }

    [Theory]
    [InlineData("""[ { "Name": "a" }, { "Name": "A" } ]""")]   // 겹침 (대소문자 무시)
    [InlineData("""[ { "Name": "a" }, { "Model": "m" } ]""")]  // 일부 Name 없음
    [InlineData("""[ { "Name": "" } ]""")]                     // 빈 Name
    [InlineData("""[ { "Name": 3 } ]""")]                      // 문자열 아님
    [InlineData("""[ { "Name": null } ]""")]                   // null (키는 있음)
    [InlineData("""[ { "Name": "A", "name": "B" } ]""")]       // 대소문자만 다른 Name 키 둘
    public void BrokenNamesInSource_NotComparable(string array)
    {
        var item = Assert.Single(Diff($$"""{ "Agents": {{array}} }""", """{ "Agents": [] }"""));
        Assert.Equal(ConfigDriftKind.NotComparable, item.Kind);
        Assert.Equal("Agents", item.Path);
    }

    [Fact]
    public void NamedSource_PlainLive_NotComparable()
    {
        var item = Assert.Single(Diff("""{ "Agents": [ { "Name": "a" } ] }""", """{ "Agents": [ "a" ] }"""));
        Assert.Equal(ConfigDriftKind.NotComparable, item.Kind);
    }

    [Fact]
    public void PlainArrays_InsideNotCompared()
    {
        // Steps 원소에 새 필드가 생겨도 알리지 않는다 (의도된 제외 범위).
        var source = """{ "Steps": [ { "Role": "R", "Agent": "a", "Sees": "All" } ], "FreeProviders": [ "local", "x" ] }""";
        var live = """{ "Steps": [ { "Role": "R", "Agent": "b" } ], "FreeProviders": [] }""";
        Assert.Empty(Diff(source, live));
    }

    // ── 경로 표기 · 파싱 ──────────────────────────────────────────────────────

    [Fact]
    public void SpecialCharacterNames_Quoted()
    {
        var source = """{ "Pricing": { "Models": { "gpt-6.1-sol": {}, "a b": {} } }, "Agents": [ { "Name": "x.y" } ] }""";
        var live = """{ "Pricing": { "Models": {} }, "Agents": [] }""";
        Assert.Equal(
            ["OnlyInSource Pricing.Models[\"gpt-6.1-sol\"]", "OnlyInSource Pricing.Models[\"a b\"]", "OnlyInSource Agents[Name=\"x.y\"]"],
            Lines(Diff(source, live)));
    }

    [Fact]
    public void CommentsAndTrailingCommas_Accepted()
    {
        var source = """
            {
              // 주석
              "Modes": { "a": {}, "b": {}, },
            }
            """;
        Assert.Equal(["OnlyInSource Modes.b"], Lines(Diff(source, """{ "Modes": { "a": {} } }""")));
    }

    // ── --config-drift ───────────────────────────────────────────────────────

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = ConfigDriftReport.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private (string Source, string Live) Folders()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var live = Directory.CreateDirectory(Path.Combine(_root, "publish")).FullName;
        return (source, live);
    }

    [Theory]
    [InlineData]
    [InlineData("--config-drift")]
    [InlineData("--config-drift", "a")]
    [InlineData("--config-drift", "a", "b", "c")]
    [InlineData("--config-drift", "a", " ")]
    [InlineData("--other", "a", "b")]
    public void Usage_ExitCode2(params string[] args)
    {
        var (code, output, error) = Run(args);
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("사용법", error);
    }

    [Fact]
    public void BothFiles_ReportsAndExit0()
    {
        var (source, live) = Folders();
        File.WriteAllText(Path.Combine(source, "modes.json"), """{ "Modes": { "a": {}, "work_order": {} } }""");
        File.WriteAllText(Path.Combine(live, "modes.json"), """{ "Modes": { "a": {} } }""");
        // BOM이 있어도 서버와 같이 읽는다.
        File.WriteAllText(Path.Combine(source, "appsettings.json"), """{ "Orchestration": {} }""", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(live, "appsettings.json"), """{ "Orchestration": { "Debug": {} } }""");

        var (code, output, _) = Run("--config-drift", source, live);

        Assert.Equal(0, code);
        Assert.Contains("[알림] modes.json: 원본에만 있음 Modes.work_order", output);
        Assert.Contains("appsettings.json: 원본에만 있는 경로 없음", output);
        Assert.DoesNotContain("[경고]", output);
    }

    [Fact]
    public void OneFileMissing_OtherStillCompared_Exit1()
    {
        var (source, live) = Folders();
        File.WriteAllText(Path.Combine(source, "modes.json"), """{ "Modes": {} }""");
        File.WriteAllText(Path.Combine(source, "appsettings.json"), """{ "A": 1 }""");
        File.WriteAllText(Path.Combine(live, "appsettings.json"), """{ }""");

        var (code, output, _) = Run("--config-drift", source, live);

        Assert.Equal(1, code);
        Assert.Contains("[경고] modes.json: 비교하지 못했습니다", output);
        Assert.DoesNotContain("modes.json: 원본에만 있는 경로 없음", output);
        Assert.Contains("[알림] appsettings.json: 원본에만 있음 A", output);
    }

    [Fact]
    public void BrokenJson_Warning_Exit1()
    {
        var (source, live) = Folders();
        foreach (var f in ConfigDriftReport.Files)
        {
            File.WriteAllText(Path.Combine(source, f), "{ }");
            File.WriteAllText(Path.Combine(live, f), "{ }");
        }
        File.WriteAllText(Path.Combine(live, "modes.json"), """{ "Modes": """);

        var (code, output, _) = Run("--config-drift", source, live);

        Assert.Equal(1, code);
        Assert.Contains("[경고] modes.json", output);
        Assert.Contains("appsettings.json: 원본에만 있는 경로 없음", output);
    }
}
