namespace HBLint.Generator.Tests;

/// <summary>rules.json → 생성 → 규칙 어셈블리 컴파일 → 분석기가 대상 코드에서 진단을 낸다.</summary>
public class EndToEndTests
{
    private const string Unity = """
        namespace UnityEngine
        {
            public class Object { }
            public class Component : Object { }
            public class Transform : Component { }
        }
        """;

    private const string Game = """
        public class Foo
        {
            UnityEngine.Transform t;
            void M() { var a = t?.GetHashCode(); }
        }
        """;

    [Fact]
    public void 예시_rules_json으로_만든_규칙이_분석기에_적용됨()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "samples", "rules.json"));
        var result = RulesJson.Generate(json, TestCompiler.RealCatalog);
        Assert.True(result.Success, string.Join("\n", result.Errors));

        var unity = TestCompiler.Emit(TestCompiler.Compile("UnityEngine.CoreModule", Unity));
        var rules = TestCompiler.Emit(TestCompiler.Compile("Game.LintRules", result.Text!, TestCompiler.Attributes));
        var diagnostics = TestCompiler.RunAnalyzer(TestCompiler.Compile("Game", Game, unity, TestCompiler.Attributes, rules));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CLL0001", diagnostic.Id);
        Assert.Contains("'== null'", diagnostic.GetMessage());
    }

    [Fact]
    public void 저장소의_생성물이_최신()
    {
        var root = RepoRoot();
        var catalog = TestCompiler.RealCatalog;

        var rules = RulesJson.Generate(File.ReadAllText(Path.Combine(root, "samples", "rules.json")), catalog);
        Assert.True(OutputFile.Matches(Path.Combine(root, "samples", "LintRules.g.cs"), rules.Text!),
            "samples/LintRules.g.cs가 samples/rules.json과 다릅니다 — hblint-gen generate로 다시 만드세요");
        Assert.True(OutputFile.Matches(Path.Combine(root, "rules.schema.json"), RulesSchema.Generate(catalog)),
            "rules.schema.json이 어트리뷰트와 다릅니다 — hblint-gen schema로 다시 만드세요");
    }

    /// <summary>HB_Lint 폴더 (HB_Lint.slnx가 있는 곳).</summary>
    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "HB_Lint.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
