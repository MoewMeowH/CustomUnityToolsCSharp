using System.Text;
using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

/// <summary>
/// ConfigDrift.Compare: 원본에만 있는 경로와 자료형 차이만 알리고, 사용 중 값은 보지 않는다.
/// ConfigDrift.CompareValues: 기준과 견주어 원본에서 바뀐 값을 알리고, 일부러 바꾼 값은 넘어간다.
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

    // ── 값 비교 (기준 있음 · 없음) ────────────────────────────────────────────

    private static IReadOnlyList<ConfigDriftItem> Values(string source, string live, string baseline) =>
        ConfigDrift.CompareValues(ConfigDrift.Parse(source), ConfigDrift.Parse(live), hasBaseline: true, ConfigDrift.Parse(baseline));

    [Fact]
    public void Values_SourceChanged_LiveStillOld_Reported()
    {
        var item = Assert.Single(Values("""{ "X": 3 }""", """{ "X": 1 }""", """{ "X": 1 }"""));
        Assert.Equal(ConfigDriftKind.SourceValueChanged, item.Kind);
        Assert.Equal("X", item.Path);
        Assert.Equal("사용 중 1 → 원본 3", item.Detail);
    }

    [Theory]
    [InlineData("""{ "X": 1 }""", """{ "X": 9 }""", """{ "X": 1 }""")] // 사용자만 바꿈
    [InlineData("""{ "X": 3 }""", """{ "X": 3 }""", """{ "X": 1 }""")] // 사용 중에 이미 옮김
    [InlineData("""{ "X": 1 }""", """{ "X": 1 }""", """{ "X": 1 }""")] // 셋 다 같음
    [InlineData("""{ "X": 1 }""", """{ "X": 1 }""", """{ }""")]        // 새 키, 사용 중에도 같은 값
    public void Values_NothingToReview_Silent(string source, string live, string baseline)
    {
        Assert.Empty(Values(source, live, baseline));
    }

    [Theory]
    [InlineData("""{ "X": 3 }""", """{ "X": 9 }""", """{ "X": 1 }""")]   // 셋 다 다름
    [InlineData("""{ "X": 3 }""", """{ "X": 9 }""", """{ }""")]          // 기준에 없는 키
    [InlineData("""{ "X": 3 }""", """{ "X": 9 }""", """{ "X": "1" }""")] // 기준의 자료형이 다름
    public void Values_BothChanged(string source, string live, string baseline)
    {
        Assert.Equal(ConfigDriftKind.BothChanged, Assert.Single(Values(source, live, baseline)).Kind);
    }

    [Fact]
    public void Values_BaselineParentHasOtherKind_ChildIsBothChanged()
    {
        var item = Assert.Single(Values("""{ "A": { "X": 3 } }""", """{ "A": { "X": 9 } }""", """{ "A": "옛 값" }"""));
        Assert.Equal(ConfigDriftKind.BothChanged, item.Kind);
        Assert.Equal("A.X", item.Path);
    }

    [Fact]
    public void Values_NamedArray_MatchedByName()
    {
        var source = """{ "Agents": [ { "Name": "b", "MaxAttempts": 2 }, { "Name": "a", "MaxAttempts": 3 } ] }""";
        var live = """{ "Agents": [ { "Name": "A", "MaxAttempts": 1 }, { "Name": "b", "MaxAttempts": 7 } ] }""";
        var baseline = """{ "Agents": [ { "name": "a", "MaxAttempts": 1 }, { "Name": "b", "MaxAttempts": 2 } ] }""";

        var item = Assert.Single(Values(source, live, baseline));
        Assert.Equal(ConfigDriftKind.SourceValueChanged, item.Kind);
        Assert.Equal("Agents[Name=a].MaxAttempts", item.Path);
    }

    [Fact]
    public void Values_PlainArrays_ComparedWhole()
    {
        var source = """{ "Steps": [ { "Role": "R", "Agent": "new" } ], "FreeProviders": [ "local", "codex" ], "Same": [ 1 ] }""";
        var live = """{ "Steps": [ { "Role": "R", "Agent": "old" } ], "FreeProviders": [ "local" ], "Same": [ 1 ] }""";

        Assert.Equal(
            [("Steps", "내용 다름"), ("FreeProviders", "내용 다름")],
            Values(source, live, live).Select(i => (i.Path, i.Detail)));
    }

    [Fact]
    public void Values_ArrayItemKeyCase_IsSameValue()
    {
        var source = """{ "Steps": [ { "Role": "R" } ] }""";
        Assert.Empty(Values(source, """{ "Steps": [ { "role": "R" } ] }""", """{ "Steps": [] }"""));
    }

    [Fact]
    public void Values_ArrayOrderChange_Reported()
    {
        Assert.Single(Values("""{ "F": [ "a", "b" ] }""", """{ "F": [ "b", "a" ] }""", """{ "F": [ "b", "a" ] }"""));
    }

    [Fact]
    public void Values_SourceEmptyArray_ComparedAsValue()
    {
        Assert.Single(Values("""{ "F": [] }""", """{ "F": [ "a" ] }""", """{ "F": [ "a" ] }"""));
    }

    [Fact]
    public void Values_OnlyInSourceAndKindMismatch_StillReported()
    {
        Assert.Equal(
            ["OnlyInSource New", "KindMismatch K"],
            Lines(Values("""{ "New": 1, "K": "1" }""", """{ "K": 1 }""", """{ "K": 1 }""")));
    }

    [Fact]
    public void Values_NoBaseline_AllDifferencesReported()
    {
        var items = ConfigDrift.CompareValues(
            ConfigDrift.Parse("""{ "X": 1, "Y": true, "Z": "같음" }"""),
            ConfigDrift.Parse("""{ "X": 2, "Y": false, "Z": "같음" }"""),
            hasBaseline: false, baseline: null);

        Assert.Equal(["ValueDiffers X", "ValueDiffers Y"], Lines(items));
        Assert.Equal("사용 중 false → 원본 true", items[1].Detail);
    }

    [Fact]
    public void Values_LongOrMultilineText_ShownAsLength()
    {
        var longText = new string('가', 41);
        var source = $$"""{ "Long": "{{longText}}", "Line": "a\nb", "Short": "{{new string('나', 40)}}", "Empty": "" }""";
        var live = """{ "Long": "짧음", "Line": "a", "Short": "다른 값", "Empty": "x" }""";

        Assert.Equal(
            ["사용 중 짧음 → 원본 41자", "사용 중 a → 원본 3자", $"사용 중 다른 값 → 원본 {new string('나', 40)}", "사용 중 x → 원본 (빈 글자)"],
            Values(source, live, live).Select(i => i.Detail));
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
    [InlineData("--config-drift", "a", "b", "c", "d")]
    [InlineData("--config-drift", "a", " ")]
    [InlineData("--config-drift", "a", "b", " ")]
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

    private (string Source, string Live, string Baseline) FoldersWithBaseline(string source, string live, string? baseline)
    {
        var (sourceDir, liveDir) = Folders();
        var baselineDir = Path.Combine(_root, "baseline");
        if (baseline is not null) Directory.CreateDirectory(baselineDir);
        foreach (var f in ConfigDriftReport.Files)
        {
            File.WriteAllText(Path.Combine(sourceDir, f), f == "modes.json" ? source : "{ }");
            File.WriteAllText(Path.Combine(liveDir, f), f == "modes.json" ? live : "{ }");
            if (baseline is not null) File.WriteAllText(Path.Combine(baselineDir, f), f == "modes.json" ? baseline : "{ }");
        }
        return (sourceDir, liveDir, baselineDir);
    }

    [Fact]
    public void Baseline_SourceValueChanged_Exit3()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 1 }""", """{ "X": 1 }""");

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(3, code);
        Assert.Contains("[알림] modes.json: 원본 값 바뀜 X (사용 중 1 → 원본 3)", output);
        Assert.Contains("appsettings.json: 알릴 것 없음", output);
    }

    [Fact]
    public void Baseline_BothChanged_Exit3()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 9 }""", """{ "X": 1 }""");

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(3, code);
        Assert.Contains("[알림] modes.json: 원본과 사용 중이 둘 다 바뀜 X (사용 중 9 → 원본 3)", output);
    }

    [Fact]
    public void Baseline_OnlyDeliberateChange_Exit0()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 1 }""", """{ "X": 9 }""", """{ "X": 1 }""");

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(0, code);
        Assert.Contains("modes.json: 알릴 것 없음", output);
    }

    [Fact]
    public void Baseline_OnlyInSource_Exit0()
    {
        // 원본에만 있는 경로는 기준과 무관하게 매번 보이므로 확인 대상이 아니다.
        var (source, live, baseline) = FoldersWithBaseline("""{ "New": 1 }""", """{ }""", """{ }""");

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(0, code);
        Assert.Contains("[알림] modes.json: 원본에만 있음 New", output);
    }

    [Fact]
    public void Baseline_FolderMissing_ValueDiffersAsNote_Exit3()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 1 }""", null);

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(3, code);
        Assert.Contains("[참고] modes.json: 값 다름 X (사용 중 1 → 원본 3)", output);
        Assert.DoesNotContain("[경고]", output);
        Assert.False(Directory.Exists(baseline));
    }

    [Fact]
    public void Baseline_OneFileMissing_OnlyThatFileIsFirstRun()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 1 }""", """{ "X": 1 }""");
        File.WriteAllText(Path.Combine(source, "appsettings.json"), """{ "Y": 2 }""");
        File.WriteAllText(Path.Combine(live, "appsettings.json"), """{ "Y": 1 }""");
        File.Delete(Path.Combine(baseline, "appsettings.json"));

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(3, code);
        Assert.Contains("[알림] modes.json: 원본 값 바뀜 X", output);
        Assert.Contains("[참고] appsettings.json: 값 다름 Y", output);
    }

    [Fact]
    public void Baseline_Broken_WarnsAndTreatsAsFirstRun()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 1 }""", """{ "X": """);

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(3, code);
        Assert.Contains("[경고] modes.json: 기준 파일을 읽지 못해", output);
        Assert.Contains("[참고] modes.json: 값 다름 X", output);
    }

    [Fact]
    public void Baseline_LiveUnreadable_Exit1EvenWithValuesToReview()
    {
        var (source, live, baseline) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 1 }""", """{ "X": 1 }""");
        File.WriteAllText(Path.Combine(source, "appsettings.json"), """{ "Y": 2 }""");
        File.Delete(Path.Combine(live, "appsettings.json"));

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(1, code);
        Assert.Contains("[알림] modes.json: 원본 값 바뀜 X", output);
        Assert.Contains("[경고] appsettings.json: 비교하지 못했습니다", output);
    }

    [Fact]
    public void Baseline_NotComparable_Exit1SoBaselineStays()
    {
        var (source, live, baseline) = FoldersWithBaseline(
            """{ "Agents": [ { "Name": "a", "MaxAttempts": 3 } ] }""",
            """{ "Agents": [ { "Name": "a", "MaxAttempts": 1 }, { "Name": "A", "MaxAttempts": 1 } ] }""",
            """{ "Agents": [ { "Name": "a", "MaxAttempts": 1 } ] }""");

        var (code, output, _) = Run("--config-drift", source, live, baseline);

        Assert.Equal(1, code);
        Assert.Contains("[알림] modes.json: 비교 못 함 Agents", output);
    }

    [Fact]
    public void Baseline_NotComparableWithValuesToReview_Exit1()
    {
        var (source, live, baseline) = FoldersWithBaseline(
            """{ "X": 3, "Agents": [ { "Name": "a" } ] }""",
            """{ "X": 1, "Agents": [ "a" ] }""",
            """{ "X": 1, "Agents": [ { "Name": "a" } ] }""");

        Assert.Equal(1, Run("--config-drift", source, live, baseline).Code);
    }

    [Fact]
    public void WithoutBaselineArgument_NotComparable_StaysExit0()
    {
        var (source, live, _) = FoldersWithBaseline(
            """{ "Agents": [ { "Name": "a" } ] }""", """{ "Agents": [ "a" ] }""", null);

        Assert.Equal(0, Run("--config-drift", source, live).Code);
    }

    [Fact]
    public void WithoutBaselineArgument_ValueChangesStayQuiet()
    {
        var (source, live, _) = FoldersWithBaseline("""{ "X": 3 }""", """{ "X": 1 }""", null);

        var (code, output, _) = Run("--config-drift", source, live);

        Assert.Equal(0, code);
        Assert.Contains("modes.json: 원본에만 있는 경로 없음", output);
    }
}
