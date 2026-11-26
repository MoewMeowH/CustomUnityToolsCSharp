using HBLint.Core.Generation;
using HBLint.Core.Ir;
using HBLint.Generator.Cli;
using RuleSet = HBLint.Core.Generation.RuleSet;

namespace HBLint.Core.Tests;

/// <summary>규칙 폴더 공통 설정(hblint.settings.json) · 규칙 머리 ignorePaths · include… · 생성 합치기.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hblint-settings-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static string RuleText(string id, string head = "") =>
        "{ \"version\": 1, \"id\": \"" + id + "\"" + head + ", \"nodes\": { \"n\": { \"kind\": \"sites.nullOperator\", \"from\": \"scope\" }, \"r\": { \"kind\": \"report\", \"from\": \"n\", \"message\": \"m\" } } }";

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = GeneratorCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void 설정_읽기와_오류()
    {
        var (ok, okErrors) = RuleSettings.Parse("""{ "version": 1, "ignoreAssemblies": ["DOTween*"], "ignorePaths": ["Plugins/**"] }""");
        Assert.Empty(okErrors);
        Assert.Equal(["DOTween*"], ok!.IgnoreAssemblies);
        Assert.Equal(["Plugins/**"], ok.IgnorePaths);

        var (bad, errors) = RuleSettings.Parse("""
            { "version": 2,
              "ignoreAssemblies": ["", "A"],
              "ignorePaths": ["Plugins//x"],
              "extra": 1 }
            """);
        Assert.Null(bad);
        Assert.Contains(errors, e => e.Message.Contains("'version'은 1"));
        Assert.Contains(errors, e => e.Message.Contains("'ignoreAssemblies'에 빈 항목"));
        Assert.Contains(errors, e => e.Message.Contains("빈 폴더 이름") && e.Line == 3);
        Assert.Contains(errors, e => e.Message.Contains("모르는 칸 'extra'"));
    }

    [Fact]
    public void 설정을_쓰고_다시_읽으면_같음()
    {
        var settings = new RuleSettings(["Assembly-CSharp-firstpass"], ["Plugins/Demigiant/**"]);

        var (read, errors) = RuleSettings.Parse(settings.ToJson());

        Assert.Empty(errors);
        Assert.Equal(settings.IgnoreAssemblies, read!.IgnoreAssemblies);
        Assert.Equal(settings.IgnorePaths, read.IgnorePaths);
    }

    [Fact]
    public void 런타임_JSON에_공통을_앞에_합치고_비면_그대로()
    {
        var rule = RuleIr.Parse(RuleText("CLL0001", ", \"ignoreAssemblies\": [\"*Editor*\", \"DOTween*\"], \"includeAssemblies\": [\"DOTween.Modules\"]")).Rule!;

        Assert.Equal(rule.ToRuntimeJson(), rule.ToRuntimeJson([], []));
        var merged = rule.ToRuntimeJson(["DOTween*", "Assembly-CSharp-firstpass"], ["Plugins/**"]);
        Assert.Contains("\"ignoreAssemblies\":[\"DOTween*\",\"Assembly-CSharp-firstpass\",\"*Editor*\"]", merged);
        Assert.Contains("\"ignorePaths\":[\"Plugins/**\"]", merged);
        Assert.Contains("\"includeAssemblies\":[\"DOTween.Modules\"]", merged);
        // 합친 것도 규칙으로 다시 읽힌다 (분석기가 읽는 꼴)
        var runtime = RuleIr.Parse(merged).Rule!;
        Assert.True(runtime.SkipsAssembly("DOTween"));
        Assert.False(runtime.SkipsAssembly("DOTween.Modules"));
        Assert.False(runtime.SkipsAssembly("Assembly-CSharp"));
    }

    [Fact]
    public void 머리_경로_패턴_오류와_같은_글자를_빼고_다시_넣으면_경고()
    {
        var bad = RuleIr.Parse(RuleText("CLL0001", ", \"ignorePaths\": [\"a//b\"]"));
        Assert.False(bad.Success);
        Assert.Contains(bad.Errors, e => e.Message.Contains("ignorePaths: 'a//b'"));

        var both = RuleIr.Parse(RuleText("CLL0001", ", \"ignoreAssemblies\": [\"A\"], \"includeAssemblies\": [\"A\"]"));
        Assert.True(both.Success);
        Assert.Contains(both.Errors, e => e.IsWarning && e.Message.Contains("'A'를 ignoreAssemblies와 includeAssemblies에 함께"));
    }

    [Fact]
    public void 다시_넣을_것이_없는_include는_생성_경고()
    {
        var files = new[] { new RuleFile("a.hblint.json", RuleText("CLL0001", ", \"includePaths\": [\"Plugins/**\"]")) };

        Assert.Contains(RuleSet.Build(files).Warnings, w => w.Contains("includePaths를 적었지만"));
        Assert.DoesNotContain(RuleSet.Build(files, new RuleSettings([], ["Plugins/**"])).Warnings, w => w.Contains("includePaths를 적었지만"));
        // 설정 파일이 깨졌으면 공통 제외를 모르므로 이 경고는 내지 않는다 (설정 오류만)
        var broken = RuleSet.Build(files, null, ["hblint.settings.json(1,1): 깨짐"]);
        Assert.DoesNotContain(broken.Warnings, w => w.Contains("includePaths를 적었지만"));
        Assert.Null(broken.Text);
    }

    [Fact]
    public void 폴더의_설정을_생성물에_합치고_주석_한_줄()
    {
        Directory.CreateDirectory(PathOf("rules"));
        File.WriteAllText(PathOf("rules/a.hblint.json"), RuleText("CLL0001"));
        var without = RuleSet.FromFolder(PathOf("rules")).Text!;
        File.WriteAllText(PathOf("rules/" + RuleSettings.FileName), """{ "version": 1, "ignoreAssemblies": ["Assembly-CSharp-firstpass"] }""");

        var set = RuleSet.FromFolder(PathOf("rules"));

        Assert.Empty(set.Errors);
        Assert.Equal(1, set.Count);
        Assert.Contains("공통 설정 hblint.settings.json", set.Text);
        Assert.Contains("\\\"ignoreAssemblies\\\":[\\\"Assembly-CSharp-firstpass\\\"]", set.Text);
        Assert.DoesNotContain("공통 설정", without);
    }

    [Fact]
    public void CLI_설정_오류면_생성하지_않고_설정을_바꾸면_check가_다름()
    {
        Directory.CreateDirectory(PathOf("rules"));
        File.WriteAllText(PathOf("rules/a.hblint.json"), RuleText("CLL0001"));
        Assert.Equal(0, Run("generate", PathOf("rules"), PathOf("LintRules.g.cs")).Code);

        File.WriteAllText(PathOf("rules/" + RuleSettings.FileName), """{ "version": 1, "ignorePaths": ["a//b"] }""");
        var broken = Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"));
        Assert.Equal(1, broken.Code);
        Assert.Contains(RuleSettings.FileName, broken.Err);

        File.WriteAllText(PathOf("rules/" + RuleSettings.FileName), """{ "version": 1, "ignorePaths": ["Plugins/**"] }""");
        Assert.Equal(1, Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"), "--check").Code);
        Assert.Equal(0, Run("generate", PathOf("rules"), PathOf("LintRules.g.cs")).Code);
        Assert.Equal(0, Run("generate", PathOf("rules"), PathOf("LintRules.g.cs"), "--check").Code);
    }
}
