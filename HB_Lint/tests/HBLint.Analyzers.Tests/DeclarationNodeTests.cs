using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

/// <summary>선언 노드 · 이름 필터 · 코드 자리 필터.</summary>
public class DeclarationNodeTests
{
    private const string Id = "CLL0020";

    // Presets.Rule이 nodes를 자식으로 붙이므로 사본을 넘긴다 (같은 nodes로 두 번 돌릴 수 있게)
    private static string RuleText(JsonObject nodes, string[]? ignoreAssemblies = null) =>
        Presets.Attributes(Presets.Rule(Id, "warning", null, ignoreAssemblies, (JsonObject)nodes.DeepClone()));

    private static Diagnostic[] Run(JsonObject nodes, string source) =>
        AnalyzeGame(RuleText(nodes), source).Where(d => d.Id == Id).ToArray();

    /// <summary>파일 경로를 정해 여러 파일로.</summary>
    private static Diagnostic[] RunFiles(JsonObject nodes, params (string Path, string Source)[] files) =>
        RunFilesWithDefines(nodes, new string[0], files);

    /// <summary>전처리 기호를 넣어 (Unity 에디터 · IDE 컴파일은 UNITY_EDITOR가 있다 — 없으면 #if 안 선언은 파싱되지도 않는다).</summary>
    private static Diagnostic[] RunFilesWithDefines(JsonObject nodes, string[] defines, params (string Path, string Source)[] files)
    {
        var rules = Emit(Rules(RuleText(nodes)));
        return Analyze(CompileWithDefines("Game", files, defines, Unity, Attributes, rules)).Where(d => d.Id == Id).ToArray();
    }

    private static JsonObject Node(string kind, string from, JsonObject? fields = null)
    {
        var node = new JsonObject { ["kind"] = kind, ["from"] = from };
        foreach (var field in fields ?? new JsonObject())
            node[field.Key] = field.Value?.DeepClone();
        return node;
    }

    private static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject Declarations(JsonObject fields, string last = "d")
    {
        var nodes = new JsonObject { ["d"] = Node("sites.declaration", "scope", fields) };
        nodes["r"] = Node("report", last, new JsonObject { ["message"] = "m" });
        return nodes;
    }

    private static string[] Texts(IEnumerable<Diagnostic> diagnostics) => diagnostics.Select(Text).ToArray();

    [Fact]
    public void 종류마다_선언_이름_자리()
    {
        const string source = """
            using System;
            public class A
            {
                public int f1, f2;
                public int P { get; set; }
                public int this[int i] => i;
                public event Action E1, E2;
                public event Action E3 { add { } remove { } }
                public void M() { void Local() { } }
                public A() { }
                public static A operator +(A a, A b) => a;
                public enum Inner { X }
            }
            public struct S { }
            public interface I { void N(); }
            public delegate void D();
            public record R(int X);
            public record struct RS(int X);
            public class Impl : I { void I.N() { } }
            """;
        string[] Found(params string[] kinds) => Texts(Run(Declarations(new JsonObject { ["kinds"] = Strings(kinds) }), source));

        Assert.Equal(new[] { "A", "R", "Impl" }, Found("class"));
        Assert.Equal(new[] { "S", "RS" }, Found("struct"));
        Assert.Equal(new[] { "I" }, Found("interface"));
        Assert.Equal(new[] { "Inner" }, Found("enum"));
        Assert.Equal(new[] { "D" }, Found("delegate"));
        // 로컬 함수 · 생성자 · 연산자 · 명시적 인터페이스 구현은 빠진다
        Assert.Equal(new[] { "M", "N" }, Found("method"));
        // 인덱서는 빠지고, record 위치 매개변수 속성은 선언 구문이 따로 없다
        Assert.Equal(new[] { "P" }, Found("property"));
        Assert.Equal(new[] { "f1", "f2" }, Found("field"));
        Assert.Equal(new[] { "E1", "E2", "E3" }, Found("event"));
    }

    [Fact]
    public void static과_확장_메서드()
    {
        const string source = """
            public static class Ext
            {
                public static int E_Twice(this int x) => x * 2;
                public static int Twice(this int x) => x * 2;
                public static int Helper(int x) => x;
                public const int K = 1;
            }
            public class C { public void Instance() { } public static readonly int R = 0; }
            """;

        Assert.Equal(new[] { "E_Twice", "Twice" }, Texts(Run(Declarations(new JsonObject { ["kinds"] = Strings("method"), ["extension"] = true }), source)));
        Assert.Equal(new[] { "Helper", "Instance" }, Texts(Run(Declarations(new JsonObject { ["kinds"] = Strings("method"), ["extension"] = false }), source)));
        Assert.Equal(new[] { "Ext", "E_Twice", "Twice", "Helper", "K", "R" }, Texts(Run(Declarations(new JsonObject { ["kinds"] = Strings("class", "method", "field"), ["static"] = true }), source)));
        Assert.Equal(new[] { "C", "Instance" }, Texts(Run(Declarations(new JsonObject { ["kinds"] = Strings("class", "method", "field"), ["static"] = false }), source)));
    }

    [Fact]
    public void 접근_수준은_기본값과_복합을_나눠_본다()
    {
        const string source = """
            enum TopDefault { A }
            public enum TopPublic { A }
            public class Holder
            {
                enum NestedDefault { A }
                private enum NestedPrivate { A }
                protected enum NestedProtected { A }
                internal enum NestedInternal { A }
                protected internal enum NestedProtectedInternal { A }
                private protected enum NestedPrivateProtected { A }
                public enum NestedPublic { A }
            }
            """;
        string[] Found(params string[] access) =>
            Texts(Run(Declarations(new JsonObject { ["kinds"] = Strings("enum"), ["accessibility"] = Strings(access) }), source));

        Assert.Equal(new[] { "NestedDefault", "NestedPrivate", "NestedPrivateProtected" }, Found("private"));
        Assert.Equal(new[] { "TopDefault", "NestedInternal", "NestedProtectedInternal" }, Found("internal"));
        Assert.Equal(new[] { "NestedProtected", "NestedProtectedInternal", "NestedPrivateProtected" }, Found("protected"));
        Assert.Equal(new[] { "TopDefault", "TopPublic", "NestedProtected", "NestedInternal", "NestedProtectedInternal", "NestedPrivateProtected", "NestedPublic" }, Found("public", "internal", "protected"));
    }

    [Fact]
    public void partial_타입은_조각마다()
    {
        const string source = """
            public partial class P { }
            public partial class P { }
            """;

        Assert.Equal(new[] { 1, 2 }, Run(Declarations(new JsonObject { ["kinds"] = Strings("class") }), source).Select(Line));
    }

    [Fact]
    public void Types에서_받으면_그_계열과_그_안_멤버만()
    {
        var nodes = new JsonObject
        {
            ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
            ["d"] = Node("sites.declaration", "t", new JsonObject { ["kinds"] = Strings("class", "method") }),
            ["r"] = Node("report", "d", new JsonObject { ["message"] = "m" }),
        };
        const string source = """
            using UnityEngine;
            public class Player : MonoBehaviour { void Update() { } }
            public class Plain { void Update() { } }
            """;

        Assert.Equal(new[] { "Player", "Update" }, Texts(Run(nodes, source)));
        Assert.Equal(2, Run(nodes, source)[1].Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Theory]
    [InlineData("matching", new[] { "E_Twice" })]
    [InlineData("notMatching", new[] { "Twice", "e_Lower" })]
    public void 이름_startsWith(string keep, string[] expected)
    {
        var nodes = Declarations(new JsonObject { ["kinds"] = Strings("method"), ["extension"] = true }, "n");
        nodes["n"] = Node("filter.name", "d", new JsonObject { ["startsWith"] = Strings("E_"), ["keep"] = keep });
        const string source = """
            public static class Ext
            {
                public static int E_Twice(this int x) => x * 2;
                public static int Twice(this int x) => x * 2;
                public static int e_Lower(this int x) => x;
            }
            """;

        Assert.Equal(expected, Texts(Run(nodes, source)));
    }

    [Fact]
    public void 이름_정규식과_startsWith는_둘_다_맞아야()
    {
        var nodes = Declarations(new JsonObject { ["kinds"] = Strings("enum") }, "n");
        nodes["n"] = Node("filter.name", "d", new JsonObject { ["pattern"] = "^E[A-Z]", ["keep"] = "notMatching" });
        const string source = """
            public enum EState { A }
            public enum Enemy { A }
            public enum State { A }
            public enum E { A }
            """;

        Assert.Equal(new[] { "Enemy", "State", "E" }, Texts(Run(nodes, source)));

        nodes["n"] = Node("filter.name", "d", new JsonObject { ["startsWith"] = Strings("E"), ["pattern"] = "State$", ["keep"] = "matching" });
        Assert.Equal(new[] { "EState" }, Texts(Run(nodes, source)));
    }

    [Fact]
    public void 이름_필터는_선언_아닌_사이트를_늘_뺀다()
    {
        var nodes = new JsonObject
        {
            ["s"] = Node("sites.new", "scope"),
            ["n"] = Node("filter.name", "s", new JsonObject { ["startsWith"] = Strings("Z"), ["keep"] = "notMatching" }),
            ["r"] = Node("report", "n", new JsonObject { ["message"] = "m" }),
        };

        Assert.Empty(Run(nodes, "public class C { object o = new object(); }"));
    }

    private static JsonObject EnumLocation(JsonObject location)
    {
        var nodes = Declarations(new JsonObject { ["kinds"] = Strings("enum") }, "l");
        location["where"] ??= "outside";
        nodes["l"] = Node("filter.location", "d", location);
        return nodes;
    }

    [Theory]
    [InlineData("Assets/Scripts/Common/CommonEnum.cs")]
    [InlineData("C:\\Project\\Assets\\Scripts\\Common\\CommonEnum.cs")]
    [InlineData("/home/user/Project/Assets/Scripts/Common/commonenum.CS")]
    public void 경로는_끝부분_대소문자_구분자_무시(string commonPath)
    {
        var nodes = EnumLocation(new JsonObject { ["paths"] = Strings("CommonEnum.cs") });

        var diagnostics = RunFiles(nodes,
            (commonPath, "public enum EInCommon { A }"),
            ("Assets/Scripts/Player.cs", "public enum EOutside { A }"),
            ("Assets/Scripts/MyCommonEnum.cs", "public enum EBoundary { A }"));

        Assert.Equal(new[] { "EOutside", "EBoundary" }, Texts(diagnostics).OrderBy(t => t == "EBoundary").ToArray());
    }

    [Theory]
    [InlineData("Editor/**", new[] { "ERuntime" })]
    [InlineData("Editor/", new[] { "ERuntime" })]
    [InlineData("Assets/*/Editor/*.cs", new[] { "ERuntime", "EDeep" })]
    [InlineData("Assets/**/Editor/*.cs", new[] { "ERuntime" })]
    [InlineData("*Editor*.cs", new[] { "ERuntime", "EDeep", "ETool" })]
    public void 별표_패턴(string pattern, string[] outside)
    {
        var nodes = EnumLocation(new JsonObject { ["paths"] = Strings(pattern) });

        var diagnostics = RunFiles(nodes,
            ("Assets/Scripts/Runtime.cs", "public enum ERuntime { A }"),
            ("Assets/Game/Editor/Tool.cs", "public enum ETool { A }"),
            ("Assets/Game/Deep/Editor/Deep.cs", "public enum EDeep { A }"));

        Assert.Equal(outside.OrderBy(x => x), Texts(diagnostics).OrderBy(x => x));
    }

    [Fact]
    public void 감싸는_클래스는_바깥_어느_단계든_자기_자신은_아님()
    {
        const string source = """
            namespace Game
            {
                public class CommonEnum
                {
                    public enum EInClass { A }
                    public class Group { public enum EInGroup { A } }
                }
                public enum CommonEnumTop { A }
                public class Other { public enum EInOther { A } }
            }
            """;

        var byName = Run(EnumLocation(new JsonObject { ["types"] = Strings("CommonEnum") }), source);
        Assert.Equal(new[] { "CommonEnumTop", "EInOther" }, Texts(byName));

        var byFullName = Run(EnumLocation(new JsonObject { ["types"] = Strings("Game.CommonEnum+Group") }), source);
        Assert.Equal(new[] { "EInClass", "CommonEnumTop", "EInOther" }, Texts(byFullName));
    }

    [Fact]
    public void 파일과_클래스를_둘_다_쓰면_둘_다_맞아야_안()
    {
        var nodes = EnumLocation(new JsonObject { ["paths"] = Strings("CommonEnum.cs"), ["types"] = Strings("CommonEnum") });

        var diagnostics = RunFiles(nodes,
            ("Assets/CommonEnum.cs", "public partial class CommonEnum { public enum EGood { A } }\npublic enum ETopInFile { A }"),
            ("Assets/Other.cs", "public partial class CommonEnum { public enum EWrongFile { A } }"));

        Assert.Equal(new[] { "ETopInFile", "EWrongFile" }, Texts(diagnostics).OrderBy(x => x));
    }

    [Fact]
    public void 코드_자리는_쓰임_사이트에도()
    {
        var nodes = new JsonObject
        {
            ["s"] = Node("sites.new", "scope"),
            ["l"] = Node("filter.location", "s", new JsonObject { ["types"] = Strings("Logger"), ["where"] = "outside" }),
            ["r"] = Node("report", "l", new JsonObject { ["message"] = "m" }),
        };
        const string source = """
            public class Logger { object a = new object(); void M() { var b = new object(); } }
            public class Game { void M() { var c = new object(); } }
            """;

        Assert.Equal(new[] { 2 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 정규식이_시간을_넘기면_그_필터를_끄고_CLL9002로_알림()
    {
        var nodes = Declarations(new JsonObject { ["kinds"] = Strings("class") }, "n");
        nodes["n"] = Node("filter.name", "d", new JsonObject { ["pattern"] = "^(a+)+$", ["keep"] = "notMatching" });
        var names = Enumerable.Range(0, 5).Select(i => new string('a', 40) + "b" + i).ToArray();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var diagnostics = AnalyzeGame(RuleText(nodes), string.Join("\n", names.Select(n => $"public class {n} {{ }}")));
        started.Stop();

        Assert.DoesNotContain(diagnostics, d => d.Id == Id);
        Assert.Contains(diagnostics, d => d.Id == "CLL9002" && d.GetMessage().Contains("CLL0020") && d.GetMessage().Contains("정규식"));
        // 꺼진 뒤로는 돌리지 않으므로 이름마다 100ms씩 쌓이지 않는다 (동시 실행이라 처음 몇 개는 겹칠 수 있음)
        Assert.True(started.ElapsedMilliseconds < 5 * 100 + 2000, started.ElapsedMilliseconds + "ms");
    }

    [Fact]
    public void 정규식이_시간_안이면_CLL9002_없음()
    {
        var nodes = Declarations(new JsonObject { ["kinds"] = Strings("class") }, "n");
        nodes["n"] = Node("filter.name", "d", new JsonObject { ["pattern"] = "^E", ["keep"] = "notMatching" });

        var diagnostics = AnalyzeGame(RuleText(nodes), "public class Ok { }");

        Assert.DoesNotContain(diagnostics, d => d.Id == "CLL9002");
        Assert.Contains(diagnostics, d => d.Id == Id);
    }

    [Theory]
    [InlineData("D:/Work/Editor/MyGame/Assets/Scripts/Player.cs", true)]
    [InlineData("D:\\Work\\Editor\\MyGame\\Assets\\Editor\\Tool.cs", false)]
    [InlineData("/home/u/Client/Packages/com.game/Editor/Tool.cs", false)]
    [InlineData("C:/Repo/Editor/Library/PackageCache/com.x@1.0/Runtime/A.cs", true)]
    public void 프로젝트_바깥_상위_폴더에는_경로_패턴이_맞지_않음(string path, bool reported)
    {
        var nodes = EnumLocation(new JsonObject { ["paths"] = Strings("Editor/**") });

        var diagnostics = RunFiles(nodes, (path, "public enum EX { A }"));

        Assert.Equal(reported, diagnostics.Length == 1);
    }

    [Fact]
    public void Types_범위에서_중첩_delegate도_중첩_enum처럼_자기_자신으로_판정()
    {
        var nodes = new JsonObject
        {
            ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
            ["d"] = Node("sites.declaration", "t", new JsonObject { ["kinds"] = Strings("enum", "delegate", "method") }),
            ["r"] = Node("report", "d", new JsonObject { ["message"] = "m" }),
        };
        const string source = """
            using UnityEngine;
            public class P : MonoBehaviour
            {
                public enum E { A }
                public delegate void D();
                void Update() { }
            }
            """;

        Assert.Equal(new[] { "Update" }, Texts(Run(nodes, source)));
    }

    [Fact]
    public void 클라_규약_enum_자리와_예외()
    {
        // ignoreAssemblies는 머리 칸이라 여기서는 자리 · 디파인 예외만
        var nodes = new JsonObject
        {
            ["d"] = Node("sites.declaration", "scope", new JsonObject { ["kinds"] = Strings("enum"), ["accessibility"] = Strings("public", "internal", "protected") }),
            ["g"] = Node("filter.define", "d", new JsonObject { ["define"] = "UNITY_EDITOR", ["where"] = "outside" }),
            ["e"] = Node("filter.location", "g", new JsonObject { ["paths"] = Strings("Editor/**"), ["where"] = "outside" }),
            ["c"] = Node("filter.location", "e", new JsonObject { ["paths"] = Strings("CommonEnum.cs"), ["types"] = Strings("CommonEnum"), ["where"] = "outside" }),
            ["r"] = Node("report", "c", new JsonObject { ["message"] = "m" }),
        };

        var files = new (string Path, string Source)[]
        {
            ("Assets/Scripts/Common/CommonEnum.cs", "public class CommonEnum { public enum EState { A } }"),
            ("Assets/Scripts/Player.cs", """
                public class Player
                {
                    public enum EMove { A }
                    private enum EInner { A }
                #if UNITY_EDITOR
                    public enum EEditorOnly { A }
                #endif
                }
                """),
            ("Assets/Editor/Tool.cs", "public enum ETool { A }"),
        };

        // 에디터 컴파일(UNITY_EDITOR 있음)에서도 #if UNITY_EDITOR 안 enum은 빠진다
        Assert.Equal(new[] { "EMove" }, Texts(RunFilesWithDefines(nodes, new[] { "UNITY_EDITOR" }, files)));

        // 디파인 필터가 실제로 거르는지 — 빼면 EEditorOnly가 걸린다
        nodes["e"]!["from"] = "d";
        nodes.Remove("g");
        Assert.Equal(new[] { "EMove", "EEditorOnly" }, Texts(RunFilesWithDefines(nodes, new[] { "UNITY_EDITOR" }, files)));
    }

    [Fact]
    public void 클라_규약_enum_자리_Editor_어셈블리는_검사하지_않음()
    {
        var nodes = EnumLocation(new JsonObject { ["paths"] = Strings("CommonEnum.cs") });
        var rules = Emit(Rules(RuleText(nodes, ignoreAssemblies: new[] { "*Editor*" })));

        var editor = Analyze(Compile("Assembly-CSharp-Editor", "public enum ETool { A }", Unity, Attributes, rules));
        var game = Analyze(Compile("Assembly-CSharp", "public enum ETool { A }", Unity, Attributes, rules));

        Assert.DoesNotContain(editor, d => d.Id == Id);
        Assert.Contains(game, d => d.Id == Id);
    }
}
