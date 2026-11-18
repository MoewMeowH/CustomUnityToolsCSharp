using System.Text;
using System.Text.Json;
using HBLint.Generator.Cli;
using HBLint.Generator.Json;

namespace HBLint.Generator.Tests;

public sealed class CliAndSchemaTests : IDisposable
{
    private const string ValidRules = """{"rules":[{"id":"r","kind":"BanNullOperator","type":"T","message":"m"}]}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hblint-gen-tests-" + Guid.NewGuid().ToString("N"));

    public CliAndSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = GeneratorCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void generate는_만들고_같으면_건드리지_않음()
    {
        File.WriteAllText(PathOf("rules.json"), ValidRules);

        var first = Run("generate", PathOf("rules.json"), PathOf("out/LintRules.g.cs"));
        var written = File.GetLastWriteTimeUtc(PathOf("out/LintRules.g.cs"));
        File.SetLastWriteTimeUtc(PathOf("out/LintRules.g.cs"), written.AddHours(-1));
        var second = Run("generate", PathOf("rules.json"), PathOf("out/LintRules.g.cs"));

        Assert.Equal(0, first.Code);
        Assert.Contains("만들었습니다", first.Out);
        Assert.Equal(0, second.Code);
        Assert.Contains("바뀐 것 없음", second.Out);
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(PathOf("out/LintRules.g.cs")));
        var bytes = File.ReadAllBytes(PathOf("out/LintRules.g.cs"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM이 없어야 함");
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(new[] { "LintRules.g.cs" }, Directory.GetFiles(PathOf("out")).Select(Path.GetFileName));
    }

    [Fact]
    public void 오류면_기존_파일을_그대로_두고_종료_코드_1()
    {
        File.WriteAllText(PathOf("LintRules.g.cs"), "// 예전 것");
        File.WriteAllText(PathOf("rules.json"), """{"rules":[{"id":"r","kind":"BanNullOperator"}]}""");

        var result = Run("generate", PathOf("rules.json"), PathOf("LintRules.g.cs"));

        Assert.Equal(1, result.Code);
        Assert.Contains("'type'", result.Err);
        Assert.Contains("'message'", result.Err);
        Assert.Contains("바꾸지 않았습니다", result.Err);
        Assert.Equal("// 예전 것", File.ReadAllText(PathOf("LintRules.g.cs")));
    }

    [Fact]
    public void check는_비교만_하고_줄바꿈_차이는_무시()
    {
        File.WriteAllText(PathOf("rules.json"), ValidRules);
        var expected = RulesJson.Generate(ValidRules, TestCompiler.RealCatalog).Text!;

        var missing = Run("generate", PathOf("rules.json"), PathOf("LintRules.g.cs"), "--check");
        File.WriteAllText(PathOf("LintRules.g.cs"), expected.Replace("\n", "\r\n"), new UTF8Encoding(true));
        var sameWithCrlfAndBom = Run("generate", PathOf("rules.json"), PathOf("LintRules.g.cs"), "--check");
        File.WriteAllText(PathOf("LintRules.g.cs"), expected + "// 손으로 고침\n");
        var different = Run("generate", PathOf("rules.json"), PathOf("LintRules.g.cs"), "--check");

        Assert.Equal(1, missing.Code);
        Assert.Equal(0, sameWithCrlfAndBom.Code);
        Assert.Equal(1, different.Code);
        Assert.EndsWith("// 손으로 고침\n", File.ReadAllText(PathOf("LintRules.g.cs")));
    }

    [Theory]
    [InlineData]
    [InlineData("generate", "a.json")]
    [InlineData("schema")]
    [InlineData("build", "x", "y")]
    public void 사용법이_틀리면_종료_코드_2(params string[] args)
    {
        Assert.Equal(2, Run(args).Code);
    }

    [Fact]
    public void 출력_경로가_폴더면_예외_없이_종료_코드_1()
    {
        File.WriteAllText(PathOf("rules.json"), ValidRules);
        Directory.CreateDirectory(PathOf("LintRules.g.cs"));

        var result = Run("generate", PathOf("rules.json"), PathOf("LintRules.g.cs"));

        Assert.Equal(1, result.Code);
        Assert.Contains("파일 오류", result.Err);
        Assert.Equal(new[] { "LintRules.g.cs", "rules.json" }, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void 같은_출력으로_동시에_써도_모두_성공하고_임시_파일이_남지_않음()
    {
        var target = PathOf("out.g.cs");
        var texts = Enumerable.Range(0, 8).Select(i => $"// {i}\n").ToArray();

        Parallel.ForEach(texts, text =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    OutputFile.WriteIfChanged(target, text);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    // Windows File.Replace는 대상이 잠시 열려 있으면 실패할 수 있다 — 임시 파일끼리 섞이지 않는 것만 본다
                    Thread.Sleep(10);
                }
            }
        });

        Assert.Contains(File.ReadAllText(target), texts);
        Assert.Equal(new[] { "out.g.cs" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void 없는_rules_json은_종료_코드_1()
    {
        Assert.Equal(1, Run("generate", PathOf("nope.json"), PathOf("out.g.cs")).Code);
    }

    [Fact]
    public void schema를_쓰고_check()
    {
        var write = Run("schema", PathOf("rules.schema.json"));
        var check = Run("schema", PathOf("rules.schema.json"), "--check");

        Assert.Equal(0, write.Code);
        Assert.Equal(0, check.Code);
    }

    [Fact]
    public void 스키마는_두_JSON_읽기_모두에서_읽히고_종류_칸_enum을_담음()
    {
        var text = RulesSchema.Generate(TestCompiler.RealCatalog);

        Assert.Equal(text, RulesSchema.Generate(TestCompiler.RealCatalog));
        JsonReader.Parse(text);
        using var document = JsonDocument.Parse(text);
        var defs = document.RootElement.GetProperty("$defs");
        Assert.Equal("BanNullOperator", defs.GetProperty("rule").GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString());
        var kind = defs.GetProperty("kind-BanNullOperator");
        Assert.Equal(new[] { "type", "message" }, kind.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "Info", "Warning", "Error" },
            kind.GetProperty("properties").GetProperty("severity").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.False(kind.GetProperty("additionalProperties").GetBoolean());
    }
}
