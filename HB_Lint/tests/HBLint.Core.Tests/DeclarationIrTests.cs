using HBLint.Core.Help;
using HBLint.Core.Ir;

namespace HBLint.Core.Tests;

/// <summary>선언 · 이름 · 코드 자리 노드의 검증 · 경로 패턴 · 노드 설명.</summary>
public class DeclarationIrTests
{
    private static string Rule(string nodes) => "{\"version\": 1, \"id\": \"CLL0005\", \"nodes\": {" + nodes + "}}";

    private static RuleParseResult Parse(string nodes) => RuleIr.Parse(Rule(nodes));

    private static IEnumerable<IrError> Errors(string nodes) => Parse(nodes).Errors.Where(e => !e.IsWarning);

    [Fact]
    public void 선언_노드_칸()
    {
        Assert.True(Parse("""
            "d": { "kind": "sites.declaration", "from": "scope", "kinds": ["method", "enum"], "static": true, "extension": true, "accessibility": ["public"] },
            "r": { "kind": "report", "from": "d", "message": "m" }
            """).Success);
        Assert.Contains(Errors("""
            "d": { "kind": "sites.declaration", "from": "scope" },
            "r": { "kind": "report", "from": "d", "message": "m" }
            """), e => e.Message.Contains("nodes.d.kinds: 필수"));
        Assert.Contains(Errors("""
            "d": { "kind": "sites.declaration", "from": "scope", "kinds": ["record"] },
            "r": { "kind": "report", "from": "d", "message": "m" }
            """), e => e.Message.Contains("nodes.d.kinds:"));
    }

    [Fact]
    public void 이름_필터는_startsWith_pattern_가운데_하나_이상이고_정규식을_검사()
    {
        Assert.Contains(Errors("""
            "d": { "kind": "sites.declaration", "from": "scope", "kinds": ["enum"] },
            "n": { "kind": "filter.name", "from": "d", "keep": "matching" },
            "r": { "kind": "report", "from": "n", "message": "m" }
            """), e => e.Message.Contains("startsWith · pattern 가운데 하나 이상"));
        Assert.Contains(Errors("""
            "d": { "kind": "sites.declaration", "from": "scope", "kinds": ["enum"] },
            "n": { "kind": "filter.name", "from": "d", "pattern": "^E[", "keep": "matching" },
            "r": { "kind": "report", "from": "n", "message": "m" }
            """), e => e.Message.Contains("nodes.n.pattern: 정규식이 잘못됐습니다") && e.Node == "n");
    }

    [Fact]
    public void 선언_없는_이름_필터는_경고()
    {
        var parsed = Parse("""
            "s": { "kind": "sites.new", "from": "scope" },
            "n": { "kind": "filter.name", "from": "s", "startsWith": ["E"], "keep": "matching" },
            "r": { "kind": "report", "from": "n", "message": "m" }
            """);

        Assert.True(parsed.Success);
        Assert.Contains(parsed.Errors, e => e.IsWarning && e.Node == "n" && e.Message.Contains("늘 0건"));

        // 합치기 · 필터를 거쳐도 앞에 선언이 있으면 경고 없음
        var through = Parse("""
            "d": { "kind": "sites.declaration", "from": "scope", "kinds": ["enum"] },
            "s": { "kind": "sites.new", "from": "scope" },
            "u": { "kind": "sites.union", "from": ["d", "s"] },
            "l": { "kind": "filter.location", "from": "u", "paths": ["A.cs"], "where": "inside" },
            "n": { "kind": "filter.name", "from": "l", "startsWith": ["E"], "keep": "matching" },
            "r": { "kind": "report", "from": "n", "message": "m" }
            """);
        Assert.True(through.Success);
        Assert.Empty(through.Errors);
    }

    [Theory]
    [InlineData("", "빈 항목")]
    [InlineData("/", "빈 패턴")]
    [InlineData("Assets//A.cs", "빈 폴더 이름")]
    [InlineData("Assets/***/A.cs", "**는 폴더 이름 하나를 통째로")]
    [InlineData("Ed**/A.cs", "**는 폴더 이름 하나를 통째로")]
    [InlineData("../A.cs", ". · ..")]
    public void 코드_자리_경로_패턴_오류(string pattern, string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(pattern);
        var errors = Errors($$"""
            "s": { "kind": "sites.new", "from": "scope" },
            "l": { "kind": "filter.location", "from": "s", "paths": [{{json}}], "where": "inside" },
            "r": { "kind": "report", "from": "l", "message": "m" }
            """);

        Assert.Contains(errors, e => e.Message.Contains(expected));
    }

    [Fact]
    public void 코드_자리는_paths_types_가운데_하나_이상()
    {
        Assert.Contains(Errors("""
            "s": { "kind": "sites.new", "from": "scope" },
            "l": { "kind": "filter.location", "from": "s", "where": "inside" },
            "r": { "kind": "report", "from": "l", "message": "m" }
            """), e => e.Message.Contains("paths · types 가운데 하나 이상"));
    }

    [Theory]
    [InlineData("CommonEnum.cs", "Assets/Scripts/CommonEnum.cs", true)]
    [InlineData("CommonEnum.cs", "C:\\P\\Assets\\commonenum.cs", true)]
    [InlineData("CommonEnum.cs", "Assets/MyCommonEnum.cs", false)]
    [InlineData("Scripts/CommonEnum.cs", "/abs/Assets/Scripts/CommonEnum.cs", true)]
    [InlineData("Scripts/CommonEnum.cs", "Assets/Other/CommonEnum.cs", false)]
    [InlineData("Editor/**", "Assets/A/Editor/B/C.cs", true)]
    [InlineData("Editor/", "Assets/A/Editor/C.cs", true)]
    [InlineData("Editor/**", "Assets/EditorTools/C.cs", false)]
    [InlineData("Assets/*/A.cs", "Assets/X/A.cs", true)]
    [InlineData("Assets/*/A.cs", "Assets/X/Y/A.cs", false)]
    [InlineData("Assets/**/A.cs", "Assets/A.cs", true)]
    [InlineData("Assets/**/A.cs", "Assets/X/Y/A.cs", true)]
    [InlineData("*Enum*.cs", "Assets/CommonEnums.cs", true)]
    [InlineData("a*a.cs", "a.cs", false)]
    [InlineData("./Assets/A.cs", "Assets/A.cs", true)]
    [InlineData("Editor/**", "D:/Work/Editor/Game/Assets/Scripts/A.cs", false)]
    [InlineData("Editor/**", "D:/Work/Editor/Game/Assets/Editor/A.cs", true)]
    [InlineData("Work/**", "D:/Work/Game/Assets/A.cs", false)]
    [InlineData("Assets/A.cs", "D:/Work/Game/Assets/A.cs", true)]
    [InlineData("Editor/**", "D:/Work/Editor/Game/Packages/com.x/Runtime/A.cs", false)]
    [InlineData("Editor/**", "D:/Work/Editor/Game/Src/A.cs", true)]
    public void 경로_패턴은_끝부분_폴더_경계(string pattern, string path, bool expected)
    {
        var parsed = PathPattern.Parse(pattern, out var error);

        Assert.Null(error);
        Assert.Equal(expected, parsed!.Matches(path));
    }

    [Fact]
    public void 모든_노드_소켓에_쉬운_설명과_예()
    {
        foreach (var spec in NodeCatalog.All)
        {
            var help = NodeHelp.Find(spec.Kind);
            Assert.True(help != null, spec.Kind + ": NodeHelp.Nodes에 설명이 없습니다");
            Assert.False(string.IsNullOrWhiteSpace(help!.Help), spec.Kind);
            Assert.False(string.IsNullOrWhiteSpace(help.Example), spec.Kind);
        }
        Assert.Equal(NodeCatalog.All.Select(n => n.Kind).OrderBy(k => k), NodeHelp.Nodes.Keys.OrderBy(k => k));
        foreach (var socket in Enum.GetValues<Socket>())
            Assert.True(NodeHelp.Sockets.ContainsKey(socket), socket.ToString());
    }
}
