using HBLint.Generator.Json;

namespace HBLint.Generator.Tests;

public class RulesDocumentTests
{
    private static RuleCatalog Catalog => TestCompiler.RealCatalog;

    [Fact]
    public void 쓸_때_id_순_칸은_예약칸_다음_카탈로그_순()
    {
        const string json = """
            {
              "rules": [
                { "severity": "Error", "kind": "BanNullOperator", "id": "z", "message": "m", "type": "T", "description": "d" },
                { "message": "m2", "type": "T2", "id": "a", "kind": "BanNullOperator", "requires": ["X"] }
              ],
              "$schema": "../rules.schema.json"
            }
            """;

        var document = RulesDocument.Load(json, Catalog, out var errors);

        Assert.Empty(errors);
        Assert.Equal(
            "{\n" +
            "  \"$schema\": \"../rules.schema.json\",\n" +
            "  \"rules\": [\n" +
            "    {\n" +
            "      \"id\": \"a\",\n" +
            "      \"kind\": \"BanNullOperator\",\n" +
            "      \"type\": \"T2\",\n" +
            "      \"message\": \"m2\",\n" +
            "      \"requires\": [\n" +
            "        \"X\"\n" +
            "      ]\n" +
            "    },\n" +
            "    {\n" +
            "      \"id\": \"z\",\n" +
            "      \"kind\": \"BanNullOperator\",\n" +
            "      \"description\": \"d\",\n" +
            "      \"type\": \"T\",\n" +
            "      \"message\": \"m\",\n" +
            "      \"severity\": \"Error\"\n" +
            "    }\n" +
            "  ]\n" +
            "}\n",
            document!.ToJson(Catalog));
    }

    [Fact]
    public void 예시_rules_json을_다시_써도_생성물이_같고_두_번째부터는_글자도_같음()
    {
        var original = File.ReadAllText(Path.Combine(EndToEndTests.RepoRoot(), "samples", "rules.json"));

        var first = RulesDocument.Load(original, Catalog, out _)!.ToJson(Catalog);
        var second = RulesDocument.Load(first, Catalog, out _)!.ToJson(Catalog);

        Assert.Equal(RulesJson.Generate(original, Catalog).Text, RulesJson.Generate(first, Catalog).Text);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("{\"rules\": [", "JSON 형식 오류")]
    [InlineData("[]", "맨 바깥은 객체")]
    [InlineData("{}", "배열이 없습니다")]
    [InlineData("{\"rules\": [], \"extra\": 1}", "모르는 칸 'extra'")]
    [InlineData("{\"rules\": [], \"$schema\": 1}", "문자열이어야")]
    [InlineData("{\"rules\": [1]}", "규칙은 객체")]
    [InlineData("{\"rules\": [{\"kind\": \"BanNullOperator\"}]}", "'id' · 'kind'")]
    [InlineData("{\"rules\": [{\"id\": \"a\", \"kind\": \"Nope\"}]}", "모르는 kind 'Nope'")]
    [InlineData("{\"rules\": [{\"id\": \"a\", \"kind\": \"BanNullOperator\", \"Type\": \"T\"}]}", "없는 칸 'Type'")]
    [InlineData("{\"rules\": [{\"id\": \"a\", \"kind\": \"BanNullOperator\", \"description\": 1}]}", "'description'")]
    public void 구조가_틀리면_열지_않음(string json, string expected)
    {
        var document = RulesDocument.Load(json, Catalog, out var errors);

        Assert.Null(document);
        Assert.Contains(errors, e => e.Message.Contains(expected));
    }

    [Fact]
    public void 값_종류_오류는_열리고_Validate가_알려줌()
    {
        var document = RulesDocument.Load("""{"rules":[{"id":"a","kind":"BanNullOperator","type":1,"message":"m"}]}""", Catalog, out var errors);

        Assert.Empty(errors);
        var result = document!.Validate(Catalog);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.RuleId == "a" && e.Message.Contains("'type'"));
    }

    [Fact]
    public void 새_규칙은_필수_칸을_빈_값으로_채우고_고치면_생성됨()
    {
        var kind = Catalog.Find("BanNullOperator")!;
        var document = new RulesDocument();
        var entry = RulesDocument.NewRule("new-rule", kind);
        document.Rules.Add(entry);

        Assert.Equal(new[] { "type", "message" }, entry.Values.Keys);

        entry.Values["type"] = new JsonString("UnityEngine.Object");
        entry.Values["message"] = new JsonString("m");
        entry.Values["severity"] = RulesDocument.EmptyValue(kind.Properties.Single(p => p.JsonName == "severity"));
        var result = document.Validate(Catalog);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("Severity = global::HBLint.LintSeverity.Info", result.Text);
    }

    [Fact]
    public void 빈_값은_칸_종류를_따름()
    {
        var kind = Catalog.Find("BanNullOperator")!;

        Assert.IsType<JsonString>(RulesDocument.EmptyValue(kind.Parameters[0]));
        Assert.IsType<JsonArray>(RulesDocument.EmptyValue(kind.Properties.Single(p => p.JsonName == "requires")));
        Assert.Equal("Info", ((JsonString)RulesDocument.EmptyValue(kind.Properties.Single(p => p.JsonName == "severity"))).Value);
    }
}
