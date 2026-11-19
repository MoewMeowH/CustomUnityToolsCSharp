namespace HBLint.Generator.Tests;

public class TypeNameCheckTests
{
    private static RuleCatalog Catalog => TestCompiler.RealCatalog;

    private static readonly HashSet<string> Types = new() { "UnityEngine.Object" };
    private static readonly HashSet<string> Assemblies = new() { "UnityEngine.CoreModule" };

    private static IReadOnlyList<RulesError> Check(string json) =>
        TypeNameCheck.Check(json, Catalog, Types.Contains, Assemblies.Contains);

    [Fact]
    public void 카탈로그가_TypeName_표시를_읽음()
    {
        var kind = Catalog.Find("BanNullOperator")!;

        Assert.True(kind.Parameters.Single(p => p.JsonName == "type").IsTypeName);
        Assert.False(kind.Parameters.Single(p => p.JsonName == "message").IsTypeName);
    }

    [Fact]
    public void 있는_타입은_통과()
    {
        Assert.Empty(Check("""{"rules":[{"id":"a","kind":"BanNullOperator","type":"UnityEngine.Object","message":"m","requires":["UnityEngine.CoreModule"]}]}"""));
    }

    [Fact]
    public void 없는_타입은_그_값의_줄_칸과_함께_오류()
    {
        const string json = "{\"rules\":[\n  {\"id\":\"typo\",\"kind\":\"BanNullOperator\",\n   \"type\":\"UnityEngine.Objekt\",\"message\":\"m\"}]}";

        var error = Assert.Single(Check(json));

        Assert.Equal("typo", error.RuleId);
        Assert.Contains("UnityEngine.Objekt", error.Message);
        Assert.Equal((3, 11), (error.Line, error.Column));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 빈_타입_이름은_타입을_찾지_않고_오류(string type)
    {
        var asked = false;
        var errors = TypeNameCheck.Check($$"""{"rules":[{"id":"empty","kind":"BanNullOperator","type":"{{type}}","message":"m"}]}""", Catalog,
            _ => asked = true, Assemblies.Contains);

        Assert.Contains("비어 있습니다", Assert.Single(errors).Message);
        Assert.False(asked);
    }

    [Fact]
    public void requires의_어셈블리가_없으면_확인하지_않음()
    {
        Assert.Empty(Check("""{"rules":[{"id":"server","kind":"BanNullOperator","type":"Server.Only","message":"m","requires":["Server.Core"]}]}"""));
    }
}
