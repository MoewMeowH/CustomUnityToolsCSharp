using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

public class BanNullOperatorAnalyzerTests
{
    private const string Message = "Unity 오브젝트는 '== null'로 확인하세요";

    private static readonly string UnityRule =
        Presets.Attributes(Presets.NullOperator("UnityEngine.Object", Message, new[] { "UnityEngine.CoreModule" }));

    // 줄 7~11이 위반 다섯 개
    private const string FiveViolations = """
        using UnityEngine;
        public class Foo : MonoBehaviour
        {
            Transform t;
            void M()
            {
                var a = t?.GetHashCode();
                var b = t ?? null;
                t ??= null;
                if (t is null) { }
                if (t is not null) { }
            }
        }
        """;

    [Fact]
    public void 다섯_연산자_모두_진단하고_메시지는_어트리뷰트_그대로()
    {
        var diagnostics = AnalyzeGame(UnityRule, FiveViolations);

        Assert.Equal(new[] { "?.", "??", "??=", "is null", "is not null" }, diagnostics.Select(Text));
        Assert.Equal(new[] { 7, 8, 9, 10, 11 }, diagnostics.Select(Line));
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("CLL0001", d.Id);
            Assert.Equal(Message, d.GetMessage());
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        });
    }

    [Fact]
    public void 복합_패턴_안의_null도_진단()
    {
        const string source = """
            using UnityEngine;
            public class Foo
            {
                Transform t;
                void M()
                {
                    if (t is not null and var alive) { }
                    if (t is null or Transform) { }
                    if (t is (not null)) { }
                    if (t is Transform x) { }
                }
            }
            """;

        var diagnostics = AnalyzeGame(UnityRule, source);

        Assert.Equal(new[] { 7, 8, 9 }, diagnostics.Select(Line));
    }

    [Fact]
    public void 자식_타입은_진단하고_string_object_인터페이스는_안_함()
    {
        const string source = """
            using UnityEngine;
            public interface IThing { }
            public class Mine : MonoBehaviour, IThing { }
            public class Foo
            {
                Mine mine; ScriptableObject so; Component c; string s; object o; IThing thing;
                void M()
                {
                    var a = mine?.GetHashCode();
                    var b = so ?? null;
                    var d = c?.transform?.GetHashCode();
                    var e = s?.Length;
                    var f = o ?? s;
                    var g = thing?.GetHashCode();
                    if (s is null) { }
                }
            }
            """;

        var diagnostics = AnalyzeGame(UnityRule, source);

        // 11줄: c?. 과 .transform?. 둘 다 Unity 오브젝트
        Assert.Equal(new[] { 9, 10, 11, 11 }, diagnostics.Select(Line));
    }

    [Fact]
    public void 타입_매개변수_제약이_대상_계열이면_진단()
    {
        const string source = """
            public static class Ext
            {
                public static int A<T>(T x) where T : UnityEngine.Component => x?.GetHashCode() ?? 0;
                public static int B<T, U>(T x) where T : U where U : UnityEngine.Object => x is null ? 0 : 1;
                public static int C<T>(T x) where T : class => x?.GetHashCode() ?? 0;
            }
            """;

        var diagnostics = AnalyzeGame(UnityRule, source);

        Assert.Equal(new[] { "?.", "is null" }, diagnostics.Select(Text));
    }

    [Fact]
    public void 규칙_어셈블리를_컴파일_참조로_넘겨도_동작()
    {
        var rules = Rules(UnityRule).ToMetadataReference();

        var diagnostics = Analyze(Compile("Game", FiveViolations, Unity, Attributes, rules));

        Assert.Equal(5, diagnostics.Length);
    }

    [Fact]
    public void 규칙_어셈블리를_참조하지_않으면_검사하지_않음()
    {
        var diagnostics = Analyze(Compile("Game", FiveViolations, Unity, Attributes));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void 대상_어셈블리가_어트리뷰트_어셈블리를_직접_참조하지_않아도_동작()
    {
        // Unity asmdef는 참조한 asmdef의 의존 어셈블리를 컴파일 참조로 받지 않을 수 있다
        var rules = Emit(Rules(UnityRule));

        var diagnostics = Analyze(Compile("Game", FiveViolations, Unity, rules));

        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal(Message, d.GetMessage()));
    }

    [Fact]
    public void 어트리뷰트_어셈블리를_참조하지_않아도_심각도가_든_규칙이_동작()
    {
        // Unity에서 규칙 asmdef만 참조한 asmdef는 HBLint.Attributes를 받지 못한다
        var rule = Presets.Attributes(Presets.NullOperator("UnityEngine.Object", "m", new[] { "UnityEngine.CoreModule" }, "error"));
        var rules = Emit(Rules(rule));

        var diagnostics = Analyze(Compile("Game", FiveViolations, Unity, rules));

        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void 규칙_어셈블리가_컴파일_참조이고_어트리뷰트_어셈블리를_참조하지_않아도_동작()
    {
        // IDE에서 Unity asmdef 참조는 프로젝트(컴파일) 참조로 들어온다
        var rules = Rules(UnityRule + "\n" + Presets.Attributes(Presets.NullOperator("UnityEngine.Component", "c", severity: "error", id: "CLL0004"))).ToMetadataReference();

        var diagnostics = Analyze(Compile("Game", FiveViolations, Unity, rules));

        Assert.Equal(10, diagnostics.Length);
        Assert.Equal(5, diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void 같은_어셈블리_안의_규칙도_동작()
    {
        var compilation = Compile("Game", new[] { ("LintRules.cs", UnityRule), ("Foo.cs", FiveViolations) }, Unity, Attributes);

        var diagnostics = Analyze(compilation);

        Assert.Equal(5, diagnostics.Length);
    }

    [Fact]
    public void Requires가_없으면_조용히_건너뜀()
    {
        const string server = "public class S { string s; void M() { var a = s?.Length; } }";
        var rules = Emit(Rules(UnityRule));

        var diagnostics = Analyze(Compile("Server", server, Attributes, rules));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Requires를_채웠는데_타입이_없으면_CLL9001()
    {
        var typo = Presets.Attributes(Presets.NullOperator("UnityEngine.Objekt", "x", new[] { "UnityEngine.CoreModule" }));

        var diagnostics = AnalyzeGame(typo, FiveViolations);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CLL9001", diagnostic.Id);
        Assert.Contains("UnityEngine.Objekt", diagnostic.GetMessage());
        Assert.Contains("Game.LintRules", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("error", DiagnosticSeverity.Error)]
    [InlineData("info", DiagnosticSeverity.Info)]
    [InlineData("warning", DiagnosticSeverity.Warning)]
    public void Severity가_진단_심각도에_반영(string severity, DiagnosticSeverity expected)
    {
        var rule = Presets.Attributes(Presets.NullOperator("UnityEngine.Object", "m", severity: severity));

        var diagnostics = AnalyzeGame(rule, FiveViolations);

        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal(expected, d.Severity));
    }

    [Fact]
    public void 메시지가_비면_규칙을_빼고_CLL9002()
    {
        var diagnostics = AnalyzeGame(Presets.Attributes(Presets.NullOperator("UnityEngine.Object", "")), FiveViolations);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CLL9002", diagnostic.Id);
        Assert.Contains("message", diagnostic.GetMessage());
    }

    [Fact]
    public void 규칙이_둘이면_각각_진단()
    {
        var rules = Presets.Attributes(Presets.NullOperator("UnityEngine.Object", "A"), Presets.NullOperator("UnityEngine.Component", "B", id: "CLL0004"));
        const string source = """
            public class Foo
            {
                UnityEngine.ScriptableObject so; UnityEngine.Transform t;
                void M() { var a = so?.GetHashCode(); var b = t?.GetHashCode(); }
            }
            """;

        var diagnostics = AnalyzeGame(rules, source);

        Assert.Equal(new[] { "A", "A", "B" }, diagnostics.Select(d => d.GetMessage()).OrderBy(m => m));
    }

    [Fact]
    public void 생성된_코드는_검사하지_않음()
    {
        var generated = "// <auto-generated/>\n" + FiveViolations;
        var rules = Emit(Rules(UnityRule));

        var diagnostics = Analyze(Compile("Game", new[] { ("Foo.g.cs", generated) }, Unity, Attributes, rules));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void 어트리뷰트_이름이_분석기가_찾는_이름과_같음()
    {
        Assert.Equal(LintRuleReader.AttributesAssemblyName, typeof(HBLint.RuleAttribute).Assembly.GetName().Name);
        Assert.Equal(GraphRuleAnalyzer.AttributeFullName, typeof(HBLint.RuleAttribute).FullName);
    }
}
