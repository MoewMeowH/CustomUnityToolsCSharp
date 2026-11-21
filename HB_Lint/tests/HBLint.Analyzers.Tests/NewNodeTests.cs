using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

/// <summary>새 규칙 9개용 노드 — 사이트(new · 박싱 · 문자열 · 람다) · 멤버 "타입.*" · 비교 · 코루틴 · 맥락 · 참조/값 · 묶음.</summary>
public class NewNodeTests
{
    private static Diagnostic[] Run(JsonObject nodes, string source) =>
        AnalyzeGame(Presets.Attributes(Presets.Rule("CLL0010", "warning", null, null, nodes)), source).Where(d => d.Id == "CLL0010").ToArray();

    private static JsonObject Node(string kind, string from, JsonObject? fields = null)
    {
        var node = new JsonObject { ["kind"] = kind, ["from"] = from };
        foreach (var field in fields ?? new JsonObject())
            node[field.Key] = field.Value?.DeepClone();
        return node;
    }

    private static JsonObject Report(string from, string message = "m") => Node("report", from, new JsonObject { ["message"] = message });

    private static JsonObject MonoBehaviourMethods(params string[] names) => new()
    {
        ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
        ["m"] = Node("methods.named", "t", new JsonObject { ["names"] = new JsonArray(names.Select(n => (JsonNode?)n).ToArray()) }),
    };

    private static int[] AdditionalLines(Diagnostic diagnostic) =>
        diagnostic.AdditionalLocations.Select(l => l.GetLineSpan().StartLinePosition.Line + 1).ToArray();

    [Fact]
    public void Update_안_new는_참조_형식만()
    {
        var nodes = MonoBehaviourMethods("Update");
        nodes["n"] = Node("sites.new", "m");
        nodes["c"] = Node("filter.targetType", "n", new JsonObject { ["category"] = "reference" });
        nodes["r"] = Report("c");
        const string source = """
            using System.Collections.Generic;
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Log(params object[] xs) { }
                void Update()
                {
                    var a = new List<int>();
                    var b = new Vector3(1, 2, 3);
                    List<int> c = new();
                    var d = new int[3];
                    int[] e = { 1, 2 };
                    var f = new { A = 1 };
                    Log(1, 2);
                    var g = new[] { 1 };
                    System.Action h = new System.Action(Other);
                    System.Action k = Other;
                }
                void Other() { var h = new List<int>(); }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(new[] { 8, 10, 11, 12, 13, 15, 16 }, diagnostics.Select(Line));
        Assert.Equal(new[] { "new List<int>", "new", "new int[3]", "{", "new", "new[]", "new System.Action" }, diagnostics.Select(Text));
    }

    [Theory]
    [InlineData("reference", 3)]
    [InlineData("value", 4)]
    public void 타입_매개변수의_참조_값은_제약으로(string category, int line)
    {
        var nodes = new JsonObject
        {
            ["n"] = Node("sites.new", "scope"),
            ["c"] = Node("filter.targetType", "n", new JsonObject { ["category"] = category }),
            ["r"] = Report("c"),
        };
        const string source = """
            public class C
            {
                T A<T>() where T : class, new() => new T();
                T B<T>() where T : struct => new T();
                T D<T>() where T : new() => new T();
            }
            """;

        Assert.Equal(new[] { line }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 박싱은_변환으로_보이는_것만()
    {
        var nodes = new JsonObject { ["b"] = Node("sites.boxing", "scope"), ["r"] = Report("b") };
        const string source = """
            using System;
            using System.Collections.Generic;
            public struct S { public int v; }
            public enum E { A = 1 }
            public class C
            {
                int i; float f; S s; E e; object o; IComparable c; List<int> list = new List<int>();
                void Log(object x) { }
                void LogMany(params object[] xs) { }
                void M()
                {
                    o = i;
                    Log(i);
                    LogMany(i, f);
                    c = i;
                    var a = "a" + i;
                    var b = $"{i}";
                    var t = s.ToString();
                    var hf = e.HasFlag(E.A);
                    object n = (int?)i;
                    object k = 3;
                    foreach (object x in list) { }
                    var cast = (object)f;
                    var str = "x" + "y";
                }
            }
            """;

        var diagnostics = Run(nodes, source);

        // 16줄 "a" + i는 연산 트리엔 박싱 변환이 있지만 컴파일러가 i.ToString()으로 바꿔 박싱하지 않는다
        Assert.Equal(new[] { 12, 13, 14, 14, 15, 19, 20, 21, 23 }, diagnostics.Select(Line));
        Assert.Equal(new[] { "i", "i", "i", "f", "i", "E.A", "(int?)i", "3", "f" }, diagnostics.Select(Text));
    }

    [Fact]
    public void 문자열_연결은_바깥_식_하나만_상수는_빼고()
    {
        var nodes = new JsonObject { ["s"] = Node("sites.stringConcat", "scope"), ["r"] = Report("s") };
        const string source = """
            public class C
            {
                string str; int i; const string K = "k";
                void M()
                {
                    var s1 = "a" + "b";
                    var s2 = K + "b";
                    var s3 = str + "b" + str;
                    str += "x";
                    var s5 = $"x{str}";
                    var s6 = $"x{K}";
                    var s7 = 'c' + str;
                    var s8 = str + i;
                    var s9 = string.Concat(str, str);
                    var s10 = $"{str}" + str;
                    var s11 = 1 + 2 + str;
                    str += $"{i}";
                }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(new[] { 8, 9, 10, 12, 13, 15, 16, 17 }, diagnostics.Select(Line));
        Assert.Equal("str + \"b\" + str", Text(diagnostics[0]));
    }

    [Theory]
    [InlineData(true, new[] { 9, 10, 11, 14, 15 })]
    [InlineData(false, new[] { 9, 10, 11, 12, 13, 14, 15, 15, 16 })]
    public void 람다는_캡처하는_것만(bool capturingOnly, int[] lines)
    {
        var nodes = new JsonObject
        {
            ["l"] = Node("sites.lambda", "scope", new JsonObject { ["capturingOnly"] = capturingOnly }),
            ["r"] = Report("l"),
        };
        const string source = """
            using System;
            public class C
            {
                int i; static int si;
                void Log(object o) { }
                void M(int p)
                {
                    int local = 1;
                    Action l1 = () => Console.WriteLine(local);
                    Action l2 = () => Console.WriteLine(p);
                    Action l3 = () => Log(i);
                    Action l4 = () => Console.WriteLine(si);
                    Func<int, int> l6 = x => x + 1;
                    Action l7 = delegate { local++; };
                    Action l8 = () => { int y = 2; Action z = () => Console.WriteLine(y); };
                    Action l11 = static () => Console.WriteLine(si);
                }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(lines, diagnostics.Select(Line));
        Assert.Equal("() =>", Text(diagnostics[0]));
        Assert.Contains(diagnostics, d => Text(d) == "delegate");
    }

    [Fact]
    public void 람다를_만드는_자리는_감싸는_메서드에_속함()
    {
        var nodes = MonoBehaviourMethods("Update");
        nodes["l"] = Node("sites.lambda", "m");
        nodes["r"] = Report("l");
        const string source = """
            using System;
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                int hp;
                void Update() { Action a = () => hp++; }
                void Other() { Action a = () => hp++; }
            }
            """;

        Assert.Equal(new[] { 6 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 멤버에_타입_별표면_그_타입의_멤버_전부_쿼리_식_포함()
    {
        var nodes = MonoBehaviourMethods("Update");
        nodes["s"] = Node("sites.member", "m", new JsonObject { ["members"] = new JsonArray("System.Linq.Enumerable.*") });
        nodes["r"] = Report("s");
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                List<int> list = new List<int>();
                void Update()
                {
                    var a = list.Where(x => x > 0).ToList();
                    var b = from x in list where x > 0 select x * 2;
                    var c = list.Count;
                    var d = list.Count();
                }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(new[] { 9, 9, 10, 10, 12 }, diagnostics.Select(Line));
        Assert.Equal(new[] { "Where", "ToList", "where x > 0", "select x * 2", "Count" }, diagnostics.Select(Text));
    }

    [Fact]
    public void 비교는_같다_다르다의_한쪽만()
    {
        var nodes = new JsonObject
        {
            ["s"] = Node("sites.member", "scope", new JsonObject
            {
                ["members"] = new JsonArray("UnityEngine.Component.tag", "UnityEngine.GameObject.tag"),
                ["uses"] = new JsonArray("compare"),
            }),
            ["r"] = Report("s"),
        };
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void M(Component other, GameObject go)
                {
                    if (tag == "Player") { }
                    if ("Player" == tag) { }
                    if (tag != "Enemy") { }
                    if (other.tag == "P") { }
                    if (go.tag == "P") { }
                    if (tag.Equals("P")) { }
                    switch (tag) { case "P": break; }
                    if (tag is "P") { }
                    object ob = null; if (ob == (object)tag) { }
                    var x = tag;
                    tag = "a";
                }
            }
            """;

        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 비교는_암시적_변환을_지나감()
    {
        var nodes = new JsonObject
        {
            ["s"] = Node("sites.member", "scope", new JsonObject { ["members"] = new JsonArray("Foo.count"), ["uses"] = new JsonArray("compare") }),
            ["r"] = Report("s"),
        };
        const string source = """
            public class Foo
            {
                public int count;
                void M(int? maybe, long wide)
                {
                    if (count == maybe) { }
                    if (count != wide) { }
                    var x = count + 1;
                }
            }
            """;

        Assert.Equal(new[] { 6, 7 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 코루틴_루프_안_WaitForSeconds()
    {
        var nodes = new JsonObject
        {
            ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
            ["co"] = Node("methods.coroutine", "t"),
            ["n"] = Node("sites.new", "co"),
            ["w"] = Node("filter.targetType", "n", new JsonObject { ["type"] = "UnityEngine.WaitForSeconds" }),
            ["loop"] = Node("filter.context", "w", new JsonObject { ["context"] = "loop", ["where"] = "inside" }),
            ["r"] = Report("loop"),
        };
        const string source = """
            using System.Collections;
            using System.Collections.Generic;
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                IEnumerator Co()
                {
                    var once = new WaitForSeconds(1);
                    while (true) { yield return new WaitForSeconds(1); }
                }
                IEnumerator Co2()
                {
                    for (var w = new WaitForSeconds(1); ; ) { yield return new WaitForSeconds(2); }
                }
                IEnumerator Co3()
                {
                    foreach (var w in new List<WaitForSeconds> { new WaitForSeconds(1) }) { yield return w; }
                    do { yield return new WaitForSeconds(1); } while (false);
                }
                IEnumerator<object> Co4() { while (true) { yield return new WaitForSeconds(1); } }
                void NotCoroutine() { while (true) { var w = new WaitForSeconds(1); } }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(new[] { 9, 13, 18, 20 }, diagnostics.Select(Line));
        // 13줄은 for 초기화가 아니라 본문의 new
        var line13 = source.Split('\n')[12];
        Assert.Equal(line13.IndexOf("new WaitForSeconds(2)", StringComparison.Ordinal), diagnostics[1].Location.GetLineSpan().StartLinePosition.Character);
    }

    [Theory]
    [InlineData("inside", new[] { 8, 9 })]
    [InlineData("outside", new[] { 7 })]
    public void 람다_안_밖(string where, int[] lines)
    {
        var nodes = new JsonObject
        {
            ["n"] = Node("sites.new", "scope"),
            ["c"] = Node("filter.context", "n", new JsonObject { ["context"] = "lambda", ["where"] = where }),
            ["r"] = Report("c"),
        };
        const string source = """
            using System;
            using System.Collections.Generic;
            public class C
            {
                void M()
                {
                    var a = new List<int>();
                    Action f = () => { var b = new List<int>(); };
                    Func<object> g = () => new object();
                }
            }
            """;

        Assert.Equal(lines, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 짝_묶기는_같은_메서드_같은_대상에_둘_다_있을_때_한_번()
    {
        var nodes = new JsonObject
        {
            ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
            ["pos"] = Node("sites.member", "t", new JsonObject { ["members"] = new JsonArray("UnityEngine.Transform.position"), ["uses"] = new JsonArray("write") }),
            ["rot"] = Node("sites.member", "t", new JsonObject { ["members"] = new JsonArray("UnityEngine.Transform.rotation"), ["uses"] = new JsonArray("write") }),
            ["p"] = new JsonObject { ["kind"] = "groups.pair", ["a"] = "pos", ["b"] = "rot", ["key"] = "methodAndTarget" },
            ["r"] = Report("p"),
        };
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                Transform cached;
                void Update()
                {
                    transform.position = Vector3.zero;
                    transform.rotation = Quaternion.identity;
                }
                void A(Transform t)
                {
                    transform.position = Vector3.zero;
                    t.rotation = Quaternion.identity;
                }
                void B()
                {
                    this.transform.rotation = Quaternion.identity;
                    var p = transform.position;
                    transform.position = p;
                }
                void C()
                {
                    GetComponent<Transform>().position = Vector3.zero;
                    GetComponent<Transform>().rotation = Quaternion.identity;
                    cached.position = Vector3.zero;
                }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(new[] { 7, 17 }, diagnostics.Select(Line));
        Assert.Equal(new[] { 8 }, AdditionalLines(diagnostics[0]));
        Assert.Equal(new[] { 19 }, AdditionalLines(diagnostics[1]));
    }

    [Theory]
    [InlineData("methodAndTarget", new[] { 6 })]
    [InlineData("method", new[] { 6, 13 })]
    public void 묶기_개수는_묶음마다_한_번(string key, int[] lines)
    {
        var nodes = MonoBehaviourMethods("Update", "LateUpdate");
        nodes["s"] = Node("sites.member", "m", new JsonObject
        {
            ["members"] = new JsonArray("UnityEngine.Component.transform", "UnityEngine.GameObject.transform"),
            ["uses"] = new JsonArray("read"),
        });
        nodes["g"] = Node("groups.by", "s", new JsonObject { ["key"] = key });
        nodes["c"] = Node("groups.count", "g", new JsonObject { ["min"] = 4 });
        nodes["r"] = Report("c");
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update()
                {
                    var a = transform.position;
                    var b = this.transform.rotation;
                    transform.position = a;
                    transform.rotation = b;
                }
                void LateUpdate()
                {
                    var a = transform.position;
                    transform.position = a;
                    var g = gameObject.transform;
                    var h = gameObject.transform;
                }
            }
            """;

        var diagnostics = Run(nodes, source);

        Assert.Equal(lines, diagnostics.Select(Line));
        Assert.Equal(new[] { 7, 8, 9 }, AdditionalLines(diagnostics[0]));
    }

    [Fact]
    public void 짝은_서로_다른_사이트_둘_이상()
    {
        // a · b가 같은 노드라 사이트 하나가 둘 다에 걸림
        var nodes = new JsonObject
        {
            ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
            ["pos"] = Node("sites.member", "t", new JsonObject { ["members"] = new JsonArray("UnityEngine.Transform.position"), ["uses"] = new JsonArray("write") }),
            ["p"] = new JsonObject { ["kind"] = "groups.pair", ["a"] = "pos", ["b"] = "pos", ["key"] = "method" },
            ["r"] = Report("p"),
        };
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update() { transform.position = Vector3.zero; }
                void LateUpdate() { transform.position = Vector3.zero; transform.position = Vector3.zero; }
            }
            """;

        Assert.Equal(new[] { 5 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 짝_뒤_개수는_묶음_전체_사이트_수()
    {
        var nodes = new JsonObject
        {
            ["t"] = Node("types.derived", "scope", new JsonObject { ["type"] = "UnityEngine.MonoBehaviour" }),
            ["pos"] = Node("sites.member", "t", new JsonObject { ["members"] = new JsonArray("UnityEngine.Transform.position"), ["uses"] = new JsonArray("write") }),
            ["rot"] = Node("sites.member", "t", new JsonObject { ["members"] = new JsonArray("UnityEngine.Transform.rotation"), ["uses"] = new JsonArray("write") }),
            ["p"] = new JsonObject { ["kind"] = "groups.pair", ["a"] = "pos", ["b"] = "rot", ["key"] = "methodAndTarget" },
            ["c"] = Node("groups.count", "p", new JsonObject { ["min"] = 3 }),
            ["r"] = Report("c"),
        };
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update() { transform.position = Vector3.zero; transform.rotation = Quaternion.identity; }
                void LateUpdate() { transform.position = Vector3.zero; transform.rotation = Quaternion.identity; transform.position = Vector3.zero; }
            }
            """;

        var diagnostic = Assert.Single(Run(nodes, source));

        Assert.Equal(5, Line(diagnostic));
        Assert.Equal(2, diagnostic.AdditionalLocations.Count);
    }

    [Fact]
    public void 합치기로_같은_사이트가_두_번_와도_묶음에선_하나()
    {
        var nodes = MonoBehaviourMethods("Update", "LateUpdate");
        nodes["s1"] = Node("sites.member", "m", new JsonObject { ["members"] = new JsonArray("UnityEngine.Component.transform") });
        nodes["s2"] = Node("sites.member", "m", new JsonObject { ["members"] = new JsonArray("UnityEngine.Component.transform") });
        nodes["u"] = new JsonObject { ["kind"] = "sites.union", ["from"] = new JsonArray("s1", "s2") };
        nodes["g"] = Node("groups.by", "u", new JsonObject { ["key"] = "method" });
        nodes["c"] = Node("groups.count", "g", new JsonObject { ["min"] = 2 });
        nodes["r"] = Report("c");
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update() { var a = transform; }
                void LateUpdate() { var a = transform; var b = transform; }
            }
            """;

        var diagnostic = Assert.Single(Run(nodes, source));

        Assert.Equal(5, Line(diagnostic));
        Assert.Single(diagnostic.AdditionalLocations);
    }

    [Fact]
    public void 전체_범위_사이트도_타입마다_묶음()
    {
        var nodes = new JsonObject
        {
            ["s"] = Node("sites.member", "scope", new JsonObject { ["members"] = new JsonArray("Foo.v"), ["uses"] = new JsonArray("read") }),
            ["g"] = Node("groups.by", "s", new JsonObject { ["key"] = "methodAndTarget" }),
            ["c"] = Node("groups.count", "g", new JsonObject { ["min"] = 2 }),
            ["r"] = Report("c"),
        };
        const string source = """
            public class Foo
            {
                public int v;
                void M() { var a = v; var b = v; }
                void N() { var a = v; }
                void O(Foo o) { var a = o.v; var b = v; }
            }
            public class Bar
            {
                void P(Foo f) { var a = f.v; var b = f.v; }
            }
            """;

        Assert.Equal(new[] { 4, 10 }, Run(nodes, source).Select(Line));
    }

    [Fact]
    public void 묶음도_따라간_경로를_붙임()
    {
        var nodes = MonoBehaviourMethods("Update");
        nodes["f"] = Node("methods.follow", "m", new JsonObject { ["depth"] = 1 });
        nodes["s"] = Node("sites.member", "f", new JsonObject { ["members"] = new JsonArray("UnityEngine.Component.transform") });
        nodes["g"] = Node("groups.by", "s", new JsonObject { ["key"] = "method" });
        nodes["c"] = Node("groups.count", "g", new JsonObject { ["min"] = 2 });
        nodes["r"] = Report("c");
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update() { Helper(); }
                void Helper()
                {
                    var a = transform; var b = transform;
                }
                void Other() { var a = transform; var b = transform; }
            }
            """;

        var diagnostic = Assert.Single(Run(nodes, source));

        Assert.Equal(7, Line(diagnostic));
        Assert.Equal("m (Update → Helper)", diagnostic.GetMessage());
    }
}
