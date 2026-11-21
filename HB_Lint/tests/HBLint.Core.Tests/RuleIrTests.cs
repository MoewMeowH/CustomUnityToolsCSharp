using HBLint.Core.Generation;
using HBLint.Core.Ir;

namespace HBLint.Core.Tests;

public class RuleIrTests
{
    private static string Rule(string nodes, string head = "\"version\": 1, \"id\": \"CLL0005\"") => "{" + head + ", \"nodes\": {" + nodes + "}}";

    private const string Valid = """
        "n": { "kind": "sites.nullOperator", "from": "scope" },
        "r": { "kind": "report", "from": "n", "message": "m" }
        """;

    private static IReadOnlyList<IrError> Errors(string text)
    {
        var parsed = RuleIr.Parse(text);
        Assert.False(parsed.Success);
        return parsed.Errors.Where(e => !e.IsWarning).ToList();
    }

    [Fact]
    public void 프리셋_규칙은_모두_오류_없이_읽힘()
    {
        foreach (var preset in Presets.Files())
        {
            var parsed = RuleIr.Parse(preset.Value);
            Assert.True(parsed.Success, preset.Key + ": " + string.Join("\n", parsed.Errors));
            Assert.Empty(parsed.Errors);
        }
    }

    [Fact]
    public void 머리_칸과_기본값()
    {
        var rule = RuleIr.Parse(Rule(Valid)).Rule!;

        Assert.Equal("CLL0005", rule.Id);
        Assert.Equal("warning", rule.Severity);
        Assert.Empty(rule.Requires);
        Assert.Equal("r", rule.Report.Name);
    }

    [Theory]
    [InlineData("\"version\": 1, \"id\": \"CLL0000\"", "CLL0001~CLL0200")]
    [InlineData("\"version\": 1, \"id\": \"CLL0201\"", "CLL0001~CLL0200")]
    [InlineData("\"version\": 1, \"id\": \"HBL0001\"", "CLL0001~CLL0200")]
    [InlineData("\"version\": 2, \"id\": \"CLL0001\"", "'version'")]
    [InlineData("\"id\": \"CLL0001\"", "'version'")]
    [InlineData("\"version\": 1", "'id'")]
    [InlineData("\"version\": 1, \"id\": \"CLL0001\", \"severity\": \"fatal\"", "severity")]
    [InlineData("\"version\": 1, \"id\": \"CLL0001\", \"sevrity\": \"error\"", "모르는 칸 'sevrity'")]
    public void 머리_오류(string head, string expected)
    {
        Assert.Contains(Errors(Rule(Valid, head)), e => e.Message.Contains(expected));
    }

    [Fact]
    public void 노드_칸_오류를_모두_보여줌()
    {
        var errors = Errors(Rule("""
            "a": { "kind": "sites.magic", "from": "scope" },
            "b": { "kind": "methods.follow", "from": "c", "depth": 3 },
            "c": { "kind": "types.derived", "from": "scope" },
            "d": { "kind": "sites.nullOperator", "from": "scope", "operators": ["?."], "extra": 1 },
            "r": { "kind": "report", "from": "d", "message": "" }
            """));

        Assert.Contains(errors, e => e.Message.Contains("모르는 kind 'sites.magic'"));
        Assert.Contains(errors, e => e.Message.Contains("nodes.b.depth: 0~2"));
        Assert.Contains(errors, e => e.Message.Contains("nodes.c.type: 필수"));
        Assert.Contains(errors, e => e.Message.Contains("없는 칸 'extra'"));
        Assert.Contains(errors, e => e.Message.Contains("nodes.r.message: 비어"));
    }

    [Fact]
    public void 소켓이_다르면_이을_수_없음()
    {
        var errors = Errors(Rule("""
            "m": { "kind": "methods.named", "from": "scope", "names": ["Update"] },
            "r": { "kind": "report", "from": "m", "message": "x" }
            """));

        Assert.Contains(errors, e => e.Message.Contains("nodes.m.from: 'scope'(Scope)는 이을 수 없습니다"));
        Assert.Contains(errors, e => e.Message.Contains("nodes.r.from: 'm'(Methods)"));
    }

    [Fact]
    public void 대상_타입은_type_category_가운데_하나_이상()
    {
        Assert.Contains(Errors(Rule("""
            "n": { "kind": "sites.new", "from": "scope" },
            "t": { "kind": "filter.targetType", "from": "n" },
            "r": { "kind": "report", "from": "t", "message": "x" }
            """)), e => e.Message.Contains("nodes.t: type · category 가운데 하나 이상"));
        Assert.Contains(Errors(Rule("""
            "n": { "kind": "sites.new", "from": "scope" },
            "t": { "kind": "filter.targetType", "from": "n", "category": "struct" },
            "r": { "kind": "report", "from": "t", "message": "x" }
            """)), e => e.Message.Contains("nodes.t.category: reference · value"));

        Assert.True(RuleIr.Parse(Rule("""
            "n": { "kind": "sites.new", "from": "scope" },
            "t": { "kind": "filter.targetType", "from": "n", "category": "reference" },
            "r": { "kind": "report", "from": "t", "message": "x" }
            """)).Success);
    }

    [Fact]
    public void 묶음_소켓()
    {
        var parsed = RuleIr.Parse(Rule("""
            "a": { "kind": "sites.member", "from": "scope", "members": ["T.a"], "uses": ["write"] },
            "b": { "kind": "sites.member", "from": "scope", "members": ["T.b"], "uses": ["write"] },
            "p": { "kind": "groups.pair", "a": "a", "b": "b", "key": "methodAndTarget" },
            "c": { "kind": "groups.count", "from": "p", "min": 3 },
            "r": { "kind": "report", "from": "c", "message": "x" }
            """));
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors));

        var sockets = Errors(Rule("""
            "n": { "kind": "sites.new", "from": "scope" },
            "c": { "kind": "groups.count", "from": "n", "min": 2 },
            "g": { "kind": "groups.by", "from": "c", "key": "method" },
            "r": { "kind": "report", "from": "g", "message": "x" }
            """));
        Assert.Contains(sockets, e => e.Message.Contains("nodes.c.from: 'n'(Sites)는 이을 수 없습니다"));
        Assert.Contains(sockets, e => e.Message.Contains("nodes.g.from: 'c'(Groups)는 이을 수 없습니다"));

        var fields = Errors(Rule("""
            "n": { "kind": "sites.new", "from": "scope" },
            "g": { "kind": "groups.by", "from": "n", "key": "type" },
            "c": { "kind": "groups.count", "from": "g", "min": 1 },
            "r": { "kind": "report", "from": "c", "message": "x" }
            """));
        Assert.Contains(fields, e => e.Message.Contains("nodes.c.min: 2~100"));
        Assert.Contains(fields, e => e.Message.Contains("nodes.g.key: method · methodAndTarget"));
    }

    [Fact]
    public void 없는_노드_고리_report_수()
    {
        Assert.Contains(Errors(Rule("""
            "r": { "kind": "report", "from": "missing", "message": "x" }
            """)), e => e.Message.Contains("없는 노드 'missing'"));

        Assert.Contains(Errors(Rule("""
            "a": { "kind": "filter.define", "from": "b", "define": "D", "where": "inside" },
            "b": { "kind": "filter.define", "from": "a", "define": "D", "where": "inside" },
            "r": { "kind": "report", "from": "a", "message": "x" }
            """)), e => e.Message.Contains("고리"));

        Assert.Contains(Errors(Rule("""
            "n": { "kind": "sites.nullOperator", "from": "scope" },
            "r1": { "kind": "report", "from": "n", "message": "x" },
            "r2": { "kind": "report", "from": "n", "message": "y" }
            """)), e => e.Message.Contains("report 노드는 하나"));

        Assert.Contains(Errors(Rule("""
            "n": { "kind": "sites.nullOperator", "from": "scope" },
            "u": { "kind": "sites.union", "from": ["n"] },
            "r": { "kind": "report", "from": "u", "message": "x" }
            """)), e => e.Message.Contains("둘 이상"));
    }

    [Fact]
    public void 노드는_256개까지()
    {
        var chain = string.Concat(Enumerable.Range(0, RuleIr.MaxNodes).Select(i =>
            $"\"f{i}\": {{ \"kind\": \"filter.define\", \"from\": \"{(i == 0 ? "n" : "f" + (i - 1))}\", \"define\": \"D\", \"where\": \"inside\" }}, "));
        var text = Rule("\"n\": { \"kind\": \"sites.nullOperator\", \"from\": \"scope\" }, " + chain + "\"r\": { \"kind\": \"report\", \"from\": \"n\", \"message\": \"x\" }");

        Assert.Contains(Errors(text), e => e.Message.Contains("256개까지"));
    }

    [Fact]
    public void report에_닿지_않는_노드는_경고만()
    {
        var parsed = RuleIr.Parse(Rule(Valid + """
            , "lonely": { "kind": "sites.nullOperator", "from": "scope" }
            """));

        Assert.True(parsed.Success);
        var warning = Assert.Single(parsed.Errors);
        Assert.True(warning.IsWarning);
        Assert.Contains("nodes.lonely", warning.Message);
    }

    [Fact]
    public void 오류에_줄_칸이_붙음()
    {
        var error = Errors("{\n  \"version\": 1,\n  \"id\": \"CLL0001\",\n  \"nodes\": {\n    \"r\": { \"kind\": \"report\", \"from\": \"x\", \"message\": 1 }\n  }\n}")
            .First(e => e.Message.Contains("message"));

        Assert.Equal(5, error.Line);
    }

    [Fact]
    public void 분석기에_넘기는_JSON은_편집용_칸을_뺌()
    {
        var rule = RuleIr.Parse(Rule(Valid, "\"$schema\": \"./hblint.schema.json\", \"version\": 1, \"id\": \"CLL0005\", \"layout\": { \"n\": [10, 20] }")).Rule!;

        var json = rule.ToRuntimeJson();

        Assert.DoesNotContain("$schema", json);
        Assert.DoesNotContain("layout", json);
        Assert.DoesNotContain("\n", json);
        Assert.True(RuleIr.Parse(json).Success);
    }
}
