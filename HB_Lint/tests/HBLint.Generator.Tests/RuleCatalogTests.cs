namespace HBLint.Generator.Tests;

public class RuleCatalogTests
{
    [Fact]
    public void 실제_어트리뷰트의_규칙_종류_셋()
    {
        Assert.Equal(new[] { "BanCallInMethod", "BanNullOperator", "RequireDefine" }, TestCompiler.RealCatalog.Kinds.Select(k => k.Name));
    }

    [Fact]
    public void 실제_어트리뷰트에서_BanCallInMethod_RequireDefine_칸을_읽음()
    {
        var banCall = TestCompiler.RealCatalog.Find("BanCallInMethod")!;
        var requireDefine = TestCompiler.RealCatalog.Find("RequireDefine")!;

        Assert.Equal(new[] { "methods", "members", "ownerType", "message" }, banCall.Parameters.Select(p => p.JsonName));
        Assert.Equal(new[] { RuleValueKind.StringArray, RuleValueKind.StringArray, RuleValueKind.String, RuleValueKind.String }, banCall.Parameters.Select(p => p.Kind));
        Assert.Equal(new[] { "followDepth", "requires", "severity" }, banCall.Properties.Select(p => p.JsonName));
        Assert.Equal(RuleValueKind.Int, banCall.Properties[0].Kind);

        Assert.Equal(new[] { "define", "message" }, requireDefine.Parameters.Select(p => p.JsonName));
        Assert.Equal(new[] { "declaredInDefine", "ignoreAssemblies", "namespaces", "requires", "severity" }, requireDefine.Properties.Select(p => p.JsonName));
        Assert.Equal(RuleValueKind.Bool, requireDefine.Properties[0].Kind);
    }

    [Fact]
    public void 실제_어트리뷰트에서_BanNullOperator를_읽음()
    {
        var kind = TestCompiler.RealCatalog.Find("BanNullOperator")!;

        Assert.Equal("BanNullOperator", kind.Name);
        Assert.Equal("HBLint.BanNullOperatorRuleAttribute", kind.AttributeFullName);
        Assert.Equal(new[] { "type", "message" }, kind.Parameters.Select(p => p.JsonName));
        Assert.All(kind.Parameters, p => Assert.Equal(RuleValueKind.String, p.Kind));
        Assert.Equal(new[] { "requires", "severity" }, kind.Properties.Select(p => p.JsonName));
        Assert.Equal(RuleValueKind.StringArray, kind.Properties[0].Kind);
        var severity = kind.Properties[1];
        Assert.Equal(RuleValueKind.Enum, severity.Kind);
        Assert.Equal("HBLint.LintSeverity", severity.EnumTypeName);
        Assert.Equal(new[] { "Info", "Warning", "Error" }, severity.EnumNames);
    }

    [Fact]
    public void 새_규칙_종류는_생성기를_고치지_않고_읽힘()
    {
        var assembly = TestCompiler.LoadAttributes("""
            using System;
            namespace HBLint
            {
                public enum Mode { Loose = 0, Strict = 1 }
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class CountLimitRuleAttribute : Attribute
                {
                    public CountLimitRuleAttribute(string target, int limit) { }
                    public bool Enabled { get; set; }
                    public Mode Mode { get; set; }
                    public string ReadOnly => "";
                }
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class NotAssemblyRuleAttribute : Attribute { }
                public sealed class HelperAttribute : Attribute { }
            }
            """);

        var catalog = RuleCatalog.FromAssembly(assembly);

        var kind = Assert.Single(catalog.Kinds);
        Assert.Equal("CountLimit", kind.Name);
        Assert.Equal(new[] { RuleValueKind.String, RuleValueKind.Int }, kind.Parameters.Select(p => p.Kind));
        Assert.Equal(new[] { "enabled", "mode" }, kind.Properties.Select(p => p.JsonName));
        Assert.Equal(new[] { RuleValueKind.Bool, RuleValueKind.Enum }, kind.Properties.Select(p => p.Kind));
    }

    [Theory]
    [InlineData("public XRuleAttribute(double x) { }", "다루지 않는 타입")]
    [InlineData("public XRuleAttribute(string id) { }", "예약 칸")]
    [InlineData("public XRuleAttribute(string a) { } public XRuleAttribute() { }", "생성자가 하나")]
    [InlineData("public XRuleAttribute(string value) { } public string Value { get; set; }", "겹칩니다")]
    public void 생성기가_다룰_수_없는_어트리뷰트는_오류(string members, string expected)
    {
        var assembly = TestCompiler.LoadAttributes($$"""
            using System;
            namespace HBLint
            {
                [AttributeUsage(AttributeTargets.Assembly)]
                public sealed class XRuleAttribute : Attribute { {{members}} }
            }
            """);

        var e = Assert.Throws<RuleCatalogException>(() => RuleCatalog.FromAssembly(assembly));

        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void 규칙_어트리뷰트가_없으면_오류()
    {
        var assembly = TestCompiler.LoadAttributes("namespace HBLint { public class Nothing { } }");

        Assert.Throws<RuleCatalogException>(() => RuleCatalog.FromAssembly(assembly));
    }
}
