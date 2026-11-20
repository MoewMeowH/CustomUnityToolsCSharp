using HBLint.Core.Generation;
using HBLint.Generator.Cli;
using Microsoft.CodeAnalysis;
using RuleSet = HBLint.Core.Generation.RuleSet;

namespace HBLint.Core.Tests;

public sealed class GenerationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hblint-gen-tests-" + Guid.NewGuid().ToString("N"));

    public GenerationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = GeneratorCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static string RuleText(string id, string message) =>
        "{ \"version\": 1, \"id\": \"" + id + "\", \"nodes\": { \"n\": { \"kind\": \"sites.nullOperator\", \"from\": \"scope\" }, \"r\": { \"kind\": \"report\", \"from\": \"n\", \"message\": "
        + System.Text.Json.JsonSerializer.Serialize(message) + " } } }";

    [Fact]
    public void 문자열_리터럴은_줄을_끊는_글자를_모두_이스케이프()
    {
        var value = "따옴표\" 역슬래시\\ 줄\n 캐리지\r 탭\t 널\0 " + (char)0x85 + (char)0x2028 + (char)0x2029 + " 이모지\U0001F600";

        var literal = RuleSet.CSharpString(value);

        Assert.DoesNotContain(literal, c => c < 0x20 || c == 0x85 || c == 0x2028 || c == 0x2029);
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class C { string s = " + literal + "; }");
        Assert.Empty(tree.GetDiagnostics());
        var token = tree.GetRoot().DescendantTokens().First(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken));
        Assert.Equal(value, token.ValueText);
    }

    [Fact]
    public void 생성물은_id_순이고_컴파일되며_분석기가_원문을_읽음()
    {
        var message = "줄\n바꿈과 \"따옴표\" " + (char)0x2028;
        var set = RuleSet.Build(new[] { new RuleFile("b.hblint.json", RuleText("CLL0009", "b")), new RuleFile("a.hblint.json", RuleText("CLL0002", message)) });

        Assert.Empty(set.Errors);
        Assert.True(set.Text!.IndexOf("CLL0002", StringComparison.Ordinal) < set.Text.IndexOf("CLL0009", StringComparison.Ordinal));
        Assert.DoesNotContain("\r", set.Text);

        var rules = TestCompiler.Emit(TestCompiler.Compile("Game.LintRules", set.Text, TestCompiler.Attributes));
        var diagnostics = TestCompiler.RunAnalyzer(TestCompiler.Compile("Game", "class G { string s; int? M() => s?.Length; }", rules));

        Assert.Equal(new[] { "CLL0002", "CLL0009" }, diagnostics.Select(d => d.Id).OrderBy(i => i));
        Assert.Equal(message, diagnostics.Single(d => d.Id == "CLL0002").GetMessage());
    }

    [Fact]
    public void 같은_id가_두_파일에_있으면_오류()
    {
        var set = RuleSet.Build(new[] { new RuleFile("a.hblint.json", RuleText("CLL0004", "a")), new RuleFile("b.hblint.json", RuleText("CLL0004", "b")) });

        Assert.Null(set.Text);
        Assert.Contains(set.Errors, e => e.Contains("a.hblint.json · b.hblint.json") && e.Contains("CLL0004"));
    }

    [Fact]
    public void 오류는_파일_경로와_줄이_붙음()
    {
        var set = RuleSet.Build(new[] { new RuleFile("x.hblint.json", "{\n  \"version\": 1,\n  \"id\": \"CLL0001\",\n  \"nodes\": 3\n}") });

        Assert.Contains("x.hblint.json(4,", Assert.Single(set.Errors));
    }

    [Fact]
    public void init은_빈_폴더에_예시_스키마_asmdef_생성물을_만들고_다시_돌려도_규칙을_덮지_않음()
    {
        var rules = PathOf("Assets/HBLint");
        var output = PathOf("Assets/HBLint/Rules/LintRules.g.cs");

        var first = Run("init", rules, output, "--asmdef", "CLL.LintRules");
        File.WriteAllText(Path.Combine(rules, "unity-null-operator.hblint.json"), RuleText("CLL0001", "고침"));
        var second = Run("init", rules, output, "--asmdef", "CLL.LintRules");

        Assert.Equal(0, first.Code);
        Assert.Equal(0, second.Code);
        Assert.Equal(Presets.Files().Select(f => f.Key).OrderBy(n => n), Directory.GetFiles(rules, "*.hblint.json").Select(Path.GetFileName).OrderBy(n => n));
        Assert.True(File.Exists(Path.Combine(rules, IrSchema.FileName)));
        Assert.Contains("\"HBLint.Attributes\"", File.ReadAllText(PathOf("Assets/HBLint/Rules/CLL.LintRules.asmdef")));
        Assert.Contains("고침", File.ReadAllText(output));
    }

    [Fact]
    public void init은_생성물_폴더에_다른_asmdef가_있으면_만들지_않음()
    {
        Directory.CreateDirectory(PathOf("out"));
        File.WriteAllText(PathOf("out/Mine.asmdef"), "{}");

        var result = Run("init", PathOf("rules"), PathOf("out/LintRules.g.cs"), "--asmdef", "CLL.LintRules");

        Assert.Equal(0, result.Code);
        Assert.Contains("Mine.asmdef", result.Out);
        Assert.Equal(new[] { "LintRules.g.cs", "Mine.asmdef" }, Directory.GetFiles(PathOf("out")).Select(Path.GetFileName).OrderBy(n => n));
    }

    [Fact]
    public void generate는_만들고_같으면_건드리지_않으며_BOM과_CR이_없음()
    {
        Directory.CreateDirectory(PathOf("rules"));
        File.WriteAllText(PathOf("rules/a.hblint.json"), RuleText("CLL0001", "m"));

        var first = Run("generate", PathOf("rules"), PathOf("out/LintRules.g.cs"));
        var written = File.GetLastWriteTimeUtc(PathOf("out/LintRules.g.cs"));
        File.SetLastWriteTimeUtc(PathOf("out/LintRules.g.cs"), written.AddHours(-1));
        var second = Run("generate", PathOf("rules"), PathOf("out/LintRules.g.cs"));

        Assert.Contains("만들었습니다", first.Out);
        Assert.Contains("바뀐 것 없음", second.Out);
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(PathOf("out/LintRules.g.cs")));
        var bytes = File.ReadAllBytes(PathOf("out/LintRules.g.cs"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM이 없어야 함");
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void 오류면_기존_생성물을_그대로_두고_종료_코드_1()
    {
        Directory.CreateDirectory(PathOf("rules"));
        File.WriteAllText(PathOf("rules/a.hblint.json"), "{ \"version\": 1, \"id\": \"CLL0001\", \"nodes\": {} }");
        File.WriteAllText(PathOf("LintRules.g.cs"), "// 예전 것");

        var generate = Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"));
        var validate = Run("validate", PathOf("rules"));

        Assert.Equal(1, generate.Code);
        Assert.Contains("report 노드는 하나", generate.Err);
        Assert.Contains("바꾸지 않았습니다", generate.Err);
        Assert.Equal("// 예전 것", File.ReadAllText(PathOf("LintRules.g.cs")));
        Assert.Equal(1, validate.Code);
    }

    [Fact]
    public void check는_쓰지_않고_비교만()
    {
        Directory.CreateDirectory(PathOf("rules"));
        File.WriteAllText(PathOf("rules/a.hblint.json"), RuleText("CLL0001", "m"));

        var missing = Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"), "--check");
        Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"));
        var same = Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"), "--check");
        var schema = Run("schema", PathOf("s.json"), "--check");

        Assert.Equal(1, missing.Code);
        Assert.Equal(0, same.Code);
        Assert.Equal(1, schema.Code);
        Assert.False(File.Exists(PathOf("s.json")));
    }

    [Theory]
    [InlineData()]
    [InlineData("generate", "only-one")]
    [InlineData("validate", "a", "--check")]
    [InlineData("unknown", "a", "b")]
    public void 사용법이_틀리면_종료_코드_2(params string[] args)
    {
        Assert.Equal(2, Run(args).Code);
    }

    [Fact]
    public void 폴더가_없으면_종료_코드_1()
    {
        Assert.Equal(1, Run("validate", PathOf("nope")).Code);
    }
}
