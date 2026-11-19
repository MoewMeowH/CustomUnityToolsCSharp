using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

public class RequireDefineAnalyzerTests
{
    private const string EditorRule =
        """[assembly: HBLint.RequireDefineRule("UNITY_EDITOR", "editor-only", Namespaces = new[] { "UnityEditor" }, DeclaredInDefine = true, IgnoreAssemblies = new[] { "*.Editor", "Assembly-CSharp-Editor*" })]""";

    private static readonly string[] EditorDefines = { "UNITY_EDITOR" };

    /// <summary>Unity 에디터 컴파일처럼 UNITY_EDITOR를 정의하고 규칙 어셈블리를 DLL로 참조해 분석한다.</summary>
    private static Diagnostic[] Run(string source, string assemblyName = "Game", string[]? defines = null, string rule = EditorRule, bool withAttributes = true)
    {
        var rules = Emit(Rules(rule));
        var references = withAttributes ? new[] { Unity, UnityEditor, Attributes, rules } : new[] { Unity, UnityEditor, rules };
        return Analyze(CompileWithDefines(assemblyName, source, defines ?? EditorDefines, references))
            .Where(d => d.Id == "CLL0003")
            .ToArray();
    }

    private const string MainSource = """
        using UnityEditor;
        #if UNITY_EDITOR
        using UnityEditor.Build;
        #endif
        public class Foo
        {
            EditorWindow window;
            void M()
            {
                AssetDatabase.Refresh();
                UnityEditor.Build.BuildHelper.Run();
        #if UNITY_EDITOR
                AssetDatabase.Refresh();
                EditorOnly();
        #endif
                EditorOnly();
                Logged();
                Both();
            }
        #if UNITY_EDITOR
            void EditorOnly() { }
            [System.Diagnostics.Conditional("UNITY_EDITOR")] void Logged() { }
        #endif
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
            void Both() { }
        #endif
        }
        """;

    [Fact]
    public void 디파인_밖의_using_타입_멤버_디파인안_선언_Conditional_메서드는_걸리고_안쪽_조합식은_안_걸림()
    {
        var diagnostics = Run(MainSource);

        Assert.Equal(new[] { 1, 7, 10, 11, 16, 17 }, diagnostics.Select(Line));
        Assert.Equal(new[] { "UnityEditor", "EditorWindow", "AssetDatabase", "BuildHelper", "EditorOnly", "Logged" }, diagnostics.Select(Text));
        Assert.All(diagnostics, d => Assert.Equal("editor-only", d.GetMessage()));
    }

    [Fact]
    public void 부정_조건의_else는_가드로_봄()
    {
        const string source = """
            public class Bar
            {
                void M()
                {
            #if !UNITY_EDITOR
                    Runtime();
            #else
                    UnityEditor.AssetDatabase.Refresh();
            #endif
                }
                void Runtime() { }
            }
            """;

        Assert.Empty(Run(source));
    }

    [Theory]
    [InlineData("UNITY_EDITOR && DEVELOPMENT_BUILD", 0)]
    [InlineData("UNITY_EDITOR || DEVELOPMENT_BUILD", 1)]
    [InlineData("(UNITY_EDITOR)", 0)]
    [InlineData("UNITY_EDITOR == true", 0)]
    [InlineData("!(!UNITY_EDITOR)", 0)]
    [InlineData("true", 1)]
    public void 조건식을_다른_기호의_모든_값으로_따짐(string condition, int expected)
    {
        var source = $$"""
            public class Bar
            {
                void M()
                {
            #if {{condition}}
                    UnityEditor.AssetDatabase.Refresh();
            #endif
                }
            }
            """;

        Assert.Equal(expected, Run(source, defines: new[] { "UNITY_EDITOR", "DEVELOPMENT_BUILD" }).Length);
    }

    [Fact]
    public void 중첩과_elif()
    {
        const string source = """
            public class Bar
            {
                void M()
                {
            #if DEVELOPMENT_BUILD
            #if UNITY_EDITOR
                    UnityEditor.AssetDatabase.Refresh();
            #endif
                    UnityEditor.AssetDatabase.Refresh();
            #endif
            #if UNITY_IOS
            #elif UNITY_EDITOR
                    UnityEditor.AssetDatabase.Refresh();
            #endif
                }
            }
            """;

        var diagnostics = Run(source, defines: new[] { "UNITY_EDITOR", "DEVELOPMENT_BUILD" });

        Assert.Equal(new[] { 9 }, diagnostics.Select(Line));
    }

    [Fact]
    public void partial은_선언이_모두_디파인_안일_때만()
    {
        const string source = """
            public partial class P
            {
                void M() { X(); new OnlyEditor(); Shared(); }
            }
            #if UNITY_EDITOR
            public partial class P
            {
                void X() { }
            }
            public class OnlyEditor { }
            public partial class Q { }
            #endif
            public partial class Q { public static void S() { } }
            public static class Use
            {
                public static void Go() { Q.S(); }
            }
            public partial class P
            {
                void Shared() { }
            }
            """;

        var diagnostics = Run(source);

        Assert.Equal(new[] { "X", "OnlyEditor" }, diagnostics.Select(Text));
    }

    [Theory]
    [InlineData("Game.Editor", 0)]
    [InlineData("Assembly-CSharp-Editor-firstpass", 0)]
    [InlineData("Assembly-CSharp", 4)]
    public void IgnoreAssemblies_패턴에_맞으면_검사하지_않음(string assemblyName, int expected)
    {
        const string source = """
            using UnityEditor;
            public class Foo
            {
                EditorWindow w;
                void M() { AssetDatabase.Refresh(); w.Show(); }
            }
            """;

        Assert.Equal(expected, Run(source, assemblyName).Length);
    }

    [Fact]
    public void 어트리뷰트_어셈블리를_참조하지_않아도_bool_배열_칸을_읽음()
    {
        var diagnostics = Run(MainSource, withAttributes: false);

        Assert.Equal(6, diagnostics.Length);
    }

    [Fact]
    public void else에_같은_이름_대체_선언이_있으면_걸지_않음()
    {
        const string source = """
            public class Bar
            {
                void M() { var a = IsPlaying; Fallback(); Only(); }
            #if UNITY_EDITOR
                static bool IsPlaying => true;
                void Fallback() { }
                void Only() { }
            #else
                static bool IsPlaying => false;
                void @Fallback() { }
                void Other() { }
            #endif
            }
            """;

        Assert.Equal(new[] { "Only" }, Run(source).Select(Text));
    }

    [Fact]
    public void global_using이_다른_파일에_있어도_네임스페이스를_봄()
    {
        var rules = Emit(Rules(EditorRule));
        var files = new[] { ("Usings.cs", "global using UnityEditor;"), ("Foo.cs", "public class Foo { void M() { AssetDatabase.Refresh(); } }") };

        var diagnostics = Analyze(CompileWithDefines("Game", files, EditorDefines, Unity, UnityEditor, Attributes, rules))
            .Where(d => d.Id == "CLL0003");

        Assert.Equal(new[] { "UnityEditor", "AssetDatabase" }, diagnostics.Select(Text));
    }

    [Fact]
    public void 네임스페이스가_없는_파일은_검사하지_않고_선언_규칙도_없으면_비용이_없음()
    {
        const string rule = """[assembly: HBLint.RequireDefineRule("UNITY_EDITOR", "m", Namespaces = new[] { "UnityEditor" })]""";
        const string source = "public class Plain { void M() { System.Console.WriteLine(); } }";

        Assert.Empty(Run(source, rule: rule));
    }
}
