using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

/// <summary>규칙 머리의 제외 · 다시 넣기 — ignoreAssemblies + includeAssemblies, ignorePaths + includePaths (공통 설정은 생성 때 여기에 합쳐진다).</summary>
public class ScopeTests
{
    private const string Id = "CLL0030";

    private static string Rule(JsonObject head, bool grouped = false)
    {
        var rule = new JsonObject { ["version"] = 1, ["id"] = Id };
        foreach (var field in head)
            rule[field.Key] = field.Value?.DeepClone();
        rule["nodes"] = grouped
            ? new JsonObject
            {
                ["s"] = new JsonObject { ["kind"] = "sites.new", ["from"] = "scope" },
                ["g"] = new JsonObject { ["kind"] = "groups.by", ["from"] = "s", ["key"] = "method" },
                ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "g", ["message"] = "m" },
            }
            : new JsonObject
            {
                ["s"] = new JsonObject { ["kind"] = "sites.new", ["from"] = "scope" },
                ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "s", ["message"] = "m" },
            };
        return Presets.Attributes(rule.ToJsonString());
    }

    private static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static int Hits(string rule, string assembly, params (string Path, string Source)[] files)
    {
        var rules = Emit(Rules(rule));
        return Analyze(Compile(assembly, files, Unity, Attributes, rules)).Count(d => d.Id == Id);
    }

    private const string Code = "public class C { object o = new object(); }";

    [Theory]
    [InlineData("DOTween", 0)]
    [InlineData("DOTween.Modules", 1)]
    [InlineData("Assembly-CSharp-firstpass", 0)]
    [InlineData("Assembly-CSharp", 1)]
    public void 어셈블리_제외와_다시_넣기(string assembly, int expected)
    {
        var rule = Rule(new JsonObject
        {
            ["ignoreAssemblies"] = Strings("Assembly-CSharp-firstpass", "DOTween*"),
            ["includeAssemblies"] = Strings("DOTween.Modules"),
        });

        Assert.Equal(expected, Hits(rule, assembly, ("Assets/A.cs", Code)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 경로_제외와_다시_넣기(bool grouped)
    {
        var rule = Rule(new JsonObject
        {
            ["ignorePaths"] = Strings("Plugins/**"),
            ["includePaths"] = Strings("Plugins/Mine/**"),
        }, grouped);

        var hits = Hits(rule, "Game",
            ("Assets/Plugins/Demigiant/DOTween/Modules/M.cs", "public class M { object o = new object(); }"),
            ("C:\\Project\\Assets\\Plugins\\Mine\\Keep.cs", "public class K { object o = new object(); }"),
            ("D:/Work/Plugins/Game/Assets/Scripts/Player.cs", "public class P { object o = new object(); }"));

        // 제외된 DOTween만 빠진다 — 다시 넣은 Mine, 프로젝트 바깥 상위 폴더가 Plugins인 Player는 남음
        Assert.Equal(2, hits);
    }

    [Fact]
    public void 먼저_읽은_규칙이_이_어셈블리를_건너뛰어도_뒤의_같은_id_규칙이_대신_돌지_않음()
    {
        // 편집 앱 미리 보기: 자기 어셈블리의 새 규칙(Game을 뺌)이 참조한 옛 생성물의 같은 id 규칙보다 먼저 읽힌다
        var old = Emit(Rules(Rule(new JsonObject())));
        var fresh = Rule(new JsonObject { ["ignoreAssemblies"] = Strings("Game") });

        var diagnostics = Analyze(Compile("Game", [("Assets/Rule.g.cs", fresh), ("Assets/A.cs", Code)], Unity, Attributes, old));

        Assert.DoesNotContain(diagnostics, d => d.Id == Id);
    }

    [Fact]
    public void 제외가_없으면_그대로()
    {
        Assert.Equal(1, Hits(Rule(new JsonObject()), "Game", ("Assets/Plugins/A.cs", Code)));
    }
}
