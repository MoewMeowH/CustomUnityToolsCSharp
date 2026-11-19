namespace HBLint.Generator.Tests;

public class TypeNameCheckTests
{
    private static RuleCatalog Catalog => TestCompiler.RealCatalog;

    private static readonly HashSet<string> Types = new() { "UnityEngine.Object", "UnityEngine.Component", "UnityEngine.MonoBehaviour" };
    private static readonly HashSet<string> Assemblies = new() { "UnityEngine.CoreModule" };
    private static readonly HashSet<string> Members = new() { "UnityEngine.Component.GetComponent" };

    /// <summary>GetComponent는 Component에 선언, MonoBehaviour는 물려받음.</summary>
    private static string? Owner(string type, string member)
    {
        if (Members.Contains(type + "." + member))
            return type;
        return type == "UnityEngine.MonoBehaviour" && member == "GetComponent" ? "UnityEngine.Component" : null;
    }

    private static IReadOnlyList<RulesError> Check(string json) =>
        TypeNameCheck.Check(json, Catalog, Types.Contains, Assemblies.Contains, Owner);

    [Fact]
    public void 카탈로그가_TypeName_MemberName_표시를_읽음()
    {
        var nullOperator = Catalog.Find("BanNullOperator")!;
        var banCall = Catalog.Find("BanCallInMethod")!;

        Assert.True(nullOperator.Parameters.Single(p => p.JsonName == "type").IsTypeName);
        Assert.False(nullOperator.Parameters.Single(p => p.JsonName == "message").IsTypeName);
        Assert.True(banCall.Parameters.Single(p => p.JsonName == "ownerType").IsTypeName);
        Assert.True(banCall.Parameters.Single(p => p.JsonName == "members").IsMemberName);
        Assert.False(banCall.Parameters.Single(p => p.JsonName == "methods").IsMemberName);
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
            _ => asked = true, Assemblies.Contains, (type, _) => type);

        Assert.Contains("비어 있습니다", Assert.Single(errors).Message);
        Assert.False(asked);
    }

    [Fact]
    public void requires의_어셈블리가_없으면_확인하지_않음()
    {
        Assert.Empty(Check("""{"rules":[{"id":"server","kind":"BanNullOperator","type":"Server.Only","message":"m","requires":["Server.Core"]}]}"""));
    }

    [Fact]
    public void 멤버_배열은_항목마다_확인()
    {
        const string json = """
            {"rules":[{"id":"b","kind":"BanCallInMethod","methods":["Update"],"ownerType":"UnityEngine.MonoBehaviour","message":"m",
              "members":["UnityEngine.Component.GetComponent","UnityEngine.Component.GetComponnent","UnityEngine.Camra.main","NoDot"]}]}
            """;

        var messages = Check(json).Select(e => e.Message).ToList();

        Assert.Equal(3, messages.Count);
        Assert.Contains(messages, m => m.Contains("GetComponnent") && m.Contains("멤버"));
        Assert.Contains(messages, m => m.Contains("UnityEngine.Camra") && m.Contains("타입"));
        Assert.Contains(messages, m => m.Contains("NoDot") && m.Contains("'타입.멤버'"));
    }

    [Fact]
    public void 부모에_선언된_멤버를_자식_이름으로_적으면_선언_타입을_알려_줌()
    {
        const string json = """
            {"rules":[{"id":"b","kind":"BanCallInMethod","methods":["Update"],"ownerType":"UnityEngine.MonoBehaviour","message":"m",
              "members":["UnityEngine.MonoBehaviour.GetComponent"]}]}
            """;

        var error = Assert.Single(Check(json));

        Assert.Contains("'UnityEngine.Component.GetComponent'(으)로 적으세요", error.Message);
    }
}
