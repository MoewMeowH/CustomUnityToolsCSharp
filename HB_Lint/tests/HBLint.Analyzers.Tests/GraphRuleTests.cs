using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

/// <summary>옛 규칙 종류 셋에 없던 그래프 조합 (범위 · 칸 · 필터 방향).</summary>
public class GraphRuleTests
{
    private static Diagnostic[] Run(JsonObject nodes, string source) =>
        AnalyzeGame(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, nodes)), source).Where(d => d.Id == "CLL0010").ToArray();

    [Fact]
    public void 타입_범위에서_받으면_그_계열_타입_안_모든_코드()
    {
        var nodes = new JsonObject
        {
            ["t"] = new JsonObject { ["kind"] = "types.derived", ["from"] = "scope", ["type"] = "UnityEngine.MonoBehaviour" },
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "t" },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "n", ["message"] = "m" },
        };
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                string s;
                int A => s?.Length ?? 0;
                void B() { System.Func<int?> f = () => s?.Length; }
            }
            public class Bar { string s; int? C() => s?.Length; }
            """;

        Assert.Equal(new[] { 5, 5, 6 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 쓰임으로_읽기와_쓰기를_가름()
    {
        JsonObject Nodes(string use) => new()
        {
            ["s"] = new JsonObject { ["kind"] = "sites.member", ["from"] = "scope", ["members"] = new JsonArray("UnityEngine.Component.tag"), ["uses"] = new JsonArray(use) },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "s", ["message"] = use },
        };
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void M()
                {
                    tag = "a";
                    var b = tag == "b";
                }
            }
            """;
        var tagged = UnitySourceWithTag();

        var writes = Analyze(Compile("Game", source, tagged, Attributes, Emit(Rules(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, Nodes("write")))))));
        var reads = Analyze(Compile("Game", source, tagged, Attributes, Emit(Rules(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, Nodes("read")))))));

        Assert.Equal(new[] { 6 }, writes.Select(Line));
        Assert.Equal(new[] { 7 }, reads.Select(Line));
    }

    [Fact]
    public void 연산자를_골라_걸기()
    {
        var nodes = new JsonObject
        {
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "scope", ["operators"] = new JsonArray("??") },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "n", ["message"] = "m" },
        };

        var diagnostics = Run(nodes, "public class C { string s; void M() { var a = s?.Length; var b = s ?? \"\"; if (s is null) { } } }");

        Assert.Equal(new[] { "??" }, diagnostics.Select(Text));
    }

    [Fact]
    public void 디파인_가드_안만_걸기()
    {
        var nodes = new JsonObject
        {
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "scope" },
            ["g"] = new JsonObject { ["kind"] = "filter.define", ["from"] = "n", ["define"] = "UNITY_EDITOR", ["where"] = "inside" },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "g", ["message"] = "m" },
        };
        const string source = """
            public class C
            {
                string s;
                void M()
                {
                    var a = s?.Length;
            #if UNITY_EDITOR
                    var b = s?.Length;
            #endif
                }
            }
            """;
        var rules = Emit(Rules(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, nodes))));

        var diagnostics = Analyze(CompileWithDefines("Game", source, new[] { "UNITY_EDITOR" }, Unity, Attributes, rules));

        Assert.Equal(new[] { 8 }, diagnostics.Select(Line));
    }

    [Fact]
    public void 잘못된_규칙은_빠지고_다른_규칙은_그대로_돔()
    {
        var broken = Presets.Rule("CLL0011", "warning", null, null, new JsonObject
        {
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "missing", ["message"] = "x" },
        });
        var rules = Presets.Attributes(broken, Presets.NullOperator("UnityEngine.Object", "ok"));

        var diagnostics = AnalyzeGame(rules, "public class C { UnityEngine.Transform t; void M() { var a = t?.GetHashCode(); } }");

        Assert.Equal(new[] { "CLL0001", "CLL9002" }, diagnostics.Select(d => d.Id).OrderBy(i => i));
        Assert.Contains("CLL0011", diagnostics.Single(d => d.Id == "CLL9002").GetMessage());
    }

    [Theory]
    [InlineData("write", new[] { 9, 10, 11, 12, 13, 14 })]
    [InlineData("read", new[] { 10, 11, 13, 15 })]
    public void 쓰임은_증감_복합대입_ref를_둘_다로_out_분해를_쓰기로_봄(string use, int[] lines)
    {
        var nodes = new JsonObject
        {
            ["s"] = new JsonObject { ["kind"] = "sites.member", ["from"] = "scope", ["members"] = new JsonArray("Foo.count"), ["uses"] = new JsonArray(use) },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "s", ["message"] = use },
        };
        const string source = """
            public class Foo
            {
                public int count;
                void Set(out int v) { v = 1; }
                void Inc(ref int v) { v++; }
                void Read(int v) { }
                void M()
                {
                    count = 1;
                    count++;
                    count += 2;
                    Set(out count);
                    Inc(ref count);
                    (count, _) = (3, 4);
                    Read(count);
                }
            }
            """;

        Assert.Equal(lines, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 타입_계열에_인터페이스를_적으면_구현_타입()
    {
        var nodes = new JsonObject
        {
            ["t"] = new JsonObject { ["kind"] = "types.derived", ["from"] = "scope", ["type"] = "IThing" },
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "t" },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "n", ["message"] = "m" },
        };
        const string source = """
            public interface IThing { }
            public class Base : IThing { }
            public class A : Base { string s; int? M() => s?.Length; }
            public class B { string s; int? M() => s?.Length; }
            """;

        Assert.Equal(new[] { 3 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 같은_id를_두_규칙이_쓰면_먼저_읽은_것만_쓰고_CLL9002()
    {
        var rules = Presets.Attributes(Presets.NullOperator("UnityEngine.Object", "first", id: "CLL0005"), Presets.NullOperator("UnityEngine.Object", "second", id: "CLL0005"));

        var diagnostics = AnalyzeGame(rules, "public class C { UnityEngine.Transform t; void M() { var a = t?.GetHashCode(); } }");

        Assert.Equal("first", Assert.Single(diagnostics, d => d.Id == "CLL0005").GetMessage());
        Assert.Contains("같은 id", Assert.Single(diagnostics, d => d.Id == "CLL9002").GetMessage());
    }

    [Fact]
    public void report에_닿지_않는_노드의_타입_오타는_규칙을_빼지_않음()
    {
        var nodes = new JsonObject
        {
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "scope" },
            ["lonely"] = new JsonObject { ["kind"] = "filter.targetType", ["from"] = "n", ["type"] = "No.Such.Type" },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "n", ["message"] = "m" },
        };

        var diagnostics = AnalyzeGame(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, nodes)), "public class C { string s; int? M() => s?.Length; }");

        Assert.Equal(new[] { "CLL0010" }, diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void 갈라졌다_합쳐지는_그래프는_어느_길이든_통과하면_한_번만()
    {
        // n → (디파인 안) · (string 대상) → 합치기 → report
        var nodes = new JsonObject
        {
            ["n"] = new JsonObject { ["kind"] = "sites.nullOperator", ["from"] = "scope", ["operators"] = new JsonArray("?.") },
            ["inEditor"] = new JsonObject { ["kind"] = "filter.define", ["from"] = "n", ["define"] = "UNITY_EDITOR", ["where"] = "inside" },
            ["strings"] = new JsonObject { ["kind"] = "filter.targetType", ["from"] = "n", ["type"] = "System.String" },
            ["u"] = new JsonObject { ["kind"] = "sites.union", ["from"] = new JsonArray("inEditor", "strings") },
            ["r"] = new JsonObject { ["kind"] = "report", ["from"] = "u", ["message"] = "m" },
        };
        const string source = """
            public class C
            {
                string s; object o;
                void M()
                {
                    var a = s?.Length;
                    var b = o?.ToString();
            #if UNITY_EDITOR
                    var c = o?.ToString();
                    var d = s?.Length;
            #endif
                }
            }
            """;
        var rules = Emit(Rules(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, nodes))));

        var diagnostics = Analyze(CompileWithDefines("Game", source, new[] { "UNITY_EDITOR" }, Unity, Attributes, rules));

        Assert.Equal(new[] { 6, 9, 10 }, diagnostics.Select(Line));
    }

    /// <summary>tag 속성이 있는 가짜 UnityEngine (기본 하네스에는 없음).</summary>
    private static MetadataReference UnitySourceWithTag() => Emit(Compile("UnityEngine.CoreModule", """
        namespace UnityEngine
        {
            public class Object { }
            public class Component : Object { public string tag { get; set; } }
            public class Behaviour : Component { }
            public class MonoBehaviour : Behaviour { }
        }
        """));
}
