using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HBLint.App.Workspace;
using HBLint.Core.Ir;

namespace HBLint.App.Tests;

public class SidecarTests
{
    private const string Game = """
        using UnityEngine;

        public class Player : MonoBehaviour
        {
            void Update()
            {
                var t = GetComponent<Transform>();
            }

            void Start() { var u = GetComponent<Transform>(); }
        }
        """;

    private static string Rule(string type = "UnityEngine.MonoBehaviour", string member = "UnityEngine.Component.GetComponent", string head = "") => $$"""
        {
          "version": 1,
          "id": "CLL0002",
          "requires": ["UnityEngine.CoreModule"],{{head}}
          "nodes": {
            "t": { "kind": "types.derived", "from": "scope", "type": "{{type}}" },
            "m": { "kind": "methods.named", "from": "t", "names": ["Update"] },
            "s": { "kind": "sites.member", "from": "m", "members": ["{{member}}"] },
            "r": { "kind": "report", "from": "s", "message": "GetComponent in Update" }
          }
        }
        """;

    private static ProjectSession Load(string folder, string? csproj = null) =>
        ProjectSession.Load(ProjectLoader.ResolveCsproj(folder, csproj), CancellationToken.None);

    private static RuleIr Parse(string text)
    {
        var parsed = RuleIr.Parse(text);
        Assert.True(parsed.Success, string.Join("\n", parsed.Errors));
        return parsed.Rule!;
    }

    [Fact]
    public void Unity_csproj를_읽어_타입을_찾음()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));

        Assert.Equal("unity", project.Info.Kind);
        Assert.Equal("Assembly-CSharp", project.Info.AssemblyName);
        Assert.Contains("UNITY_EDITOR", project.Info.Defines);
        Assert.Equal("UnityEngine.MonoBehaviour", project.SearchTypes("Mono")[0].MetadataName);
        Assert.Contains(project.SearchTypes("Inner"), t => t.MetadataName == "UnityEngine.Outer+Inner");
        Assert.Contains(project.SearchTypes("Box"), t => t.MetadataName == "UnityEngine.Box`1");
        Assert.Contains(project.SearchTypes("Player"), t => t.MetadataName == "Player" && t.Assembly == "Assembly-CSharp");
        Assert.All(project.SearchTypes("e"), t => Assert.DoesNotContain("<", t.MetadataName));
    }

    [Fact]
    public void 자동_완성_이름은_분석기가_찾는_이름()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));

        foreach (var type in project.SearchTypes("e"))
            Assert.NotNull(project.Compilation.GetTypeByMetadataName(type.MetadataName));
    }

    [Fact]
    public void 멤버_목록과_네임스페이스()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));

        Assert.Contains(project.Members("UnityEngine.Component", "GetC")!, m => m.Name == "GetComponent" && m.Kind == "method");
        Assert.Contains(project.Members("UnityEngine.Component", "")!, m => m.Name == "tag" && m.Kind == "property");
        Assert.Contains(project.Members("UnityEngine.Box`1", "")!, m => m.Name == "Value" && m.Kind == "field");
        Assert.Null(project.Members("UnityEngine.Nope", ""));
        Assert.Contains("UnityEngine", project.SearchNamespaces("unity"));
        Assert.True(project.HasNamespace("UnityEngine"));
        Assert.False(project.HasNamespace("Unity"));
    }

    [Fact]
    public void 이름_확인은_노드와_칸을_알림()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));

        Assert.Empty(NameCheck.Run(project, Rule()));
        Assert.Empty(NameCheck.Run(project, Rule(member: "UnityEngine.Component.*")));

        var problems = NameCheck.Run(project, Rule(type: "UnityEngine.MonoBehavior", member: "UnityEngine.Component.GetComponnt", head: "\n  \"severity\": \"info\","));
        Assert.Contains(problems, p => p is { Node: "t", Field: "type", Value: "UnityEngine.MonoBehavior" });
        Assert.Contains(problems, p => p is { Node: "s", Field: "members", Value: "UnityEngine.Component.GetComponnt" });

        var requires = NameCheck.Run(project, Rule().Replace("\"UnityEngine.CoreModule\"", "\"UnityEngine.PhysicsModule\""));
        Assert.Contains(requires, p => p is { Node: "scope", Field: "requires", Value: "UnityEngine.PhysicsModule" });
    }

    [Theory]
    [InlineData("UnityEngine.MonoBehavior", "UnityEngine.Component.GetComponent")]
    [InlineData("UnityEngine.MonoBehaviour", "UnityEngine.Component.GetComponnt")]
    [InlineData("UnityEngine.MonoBehaviour", "UnityEngine.Componen.GetComponent")]
    [InlineData("UnityEngine.MonoBehaviour", "UnityEngine.Outer.Inner")]
    public void 이름_확인은_분석기_CLL9001과_같은_이름을_잡음(string type, string member)
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));
        var text = Rule(type, member);

        var ours = NameCheck.Run(project, text).Select(p => p.Value).ToList();
        var analyzer = Preview.Run(project, Parse(text), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None).Hits.Where(h => h.Id == "CLL9001").ToList();

        Assert.Single(ours);
        Assert.Single(analyzer);
        Assert.Contains(ours[0], analyzer[0].Message);
    }

    [Fact]
    public void 미리_보기는_대상_소스의_위치를_냄()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));

        var result = Preview.Run(project, Parse(Rule()), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("CLL0002", hit.Id);
        Assert.Equal(7, hit.Line);
        Assert.EndsWith("Game.cs", hit.File);
        Assert.Equal(0, result.CompileErrors);
    }

    [Fact]
    public void 미리_보기는_어셈블리_이름으로_ignoreAssemblies를_건다()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game, assemblyName: "Game.Editor"), "Game.Editor.csproj");

        Assert.Single(Preview.Run(project, Parse(Rule()), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None).Hits);
        Assert.Empty(Preview.Run(project, Parse(Rule(head: "\n  \"ignoreAssemblies\": [\"*.Editor\"],")), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None).Hits);
    }

    [Fact]
    public void 대상이_규칙_어셈블리를_참조해도_같은_id가_겹치지_않음()
    {
        using var dir = new TempDir();
        var attributes = typeof(HBLint.RuleAttribute).Assembly.Location;
        var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(Parse(Rule()).ToRuntimeJson(), quote: true);
        var rules = FakeUnity.Emit(dir.Path, "CLL.LintRules", $"[assembly: HBLint.Rule({literal})]", [attributes]);
        var project = Load(FakeUnity.Project(dir, Game, extraReferences: [attributes, rules]));

        var result = Preview.Run(project, Parse(Rule()), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None);

        Assert.Single(result.Hits);
        Assert.Equal("CLL0002", result.Hits[0].Id);
    }

    [Fact]
    public void 대상_소스에_같은_id의_생성물이_있어도_편집_중인_규칙을_돌림()
    {
        using var dir = new TempDir();
        var attributes = typeof(HBLint.RuleAttribute).Assembly.Location;
        // 저장된 옛 규칙: 같은 id, Start만 본다
        var old = Parse(Rule().Replace("[\"Update\"]", "[\"Start\"]"));
        var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(old.ToRuntimeJson(), quote: true);
        FakeUnity.Project(dir, Game, extraReferences: [attributes]);
        dir.Write("Assets/LintRules.g.cs", $"[assembly: HBLint.Rule({literal})]");
        var csproj = Path.Combine(dir.Path, "Assembly-CSharp.csproj");
        File.WriteAllText(csproj, File.ReadAllText(csproj).Replace("<Compile Include=\"Assets\\Game.cs\" />", "<Compile Include=\"Assets\\LintRules.g.cs\" />\n    <Compile Include=\"Assets\\Game.cs\" />"));
        var project = Load(dir.Path);

        var result = Preview.Run(project, Parse(Rule()), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None);

        Assert.Equal(7, Assert.Single(result.Hits).Line); // Update 쪽 (옛 규칙이면 Start = 10줄)
        Assert.Equal(0, result.CompileErrors);
    }

    [Fact]
    public void 규칙과_코드가_한_어셈블리인_참조를_빼지_않음()
    {
        using var dir = new TempDir();
        var attributes = typeof(HBLint.RuleAttribute).Assembly.Location;
        var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(Parse(Rule()).ToRuntimeJson(), quote: true);
        var unity = FakeUnity.EmitUnity(dir.Path);
        var shared = FakeUnity.Emit(dir.Path, "Game.Shared", $"[assembly: HBLint.Rule({literal})]\nnamespace Shared {{ public class Base : UnityEngine.MonoBehaviour {{ }} }}", [attributes, unity]);
        var game = Game.Replace("public class Player : MonoBehaviour", "public class Player : Shared.Base");
        var project = Load(FakeUnity.Project(dir, game, extraReferences: [attributes, shared]));

        var result = Preview.Run(project, Parse(Rule()), HBLint.Core.Generation.RuleSettings.Empty, CancellationToken.None);

        Assert.Equal(0, result.CompileErrors);
        Assert.Equal(7, Assert.Single(result.Hits).Line);
    }

    [Fact]
    public void requires_확인은_분석기처럼_대소문자를_가리고_자기_어셈블리는_아님()
    {
        using var dir = new TempDir();
        var project = Load(FakeUnity.Project(dir, Game));

        Assert.Contains(NameCheck.Run(project, Rule().Replace("\"UnityEngine.CoreModule\"", "\"unityengine.coremodule\"")), p => p.Field == "requires");
        Assert.Contains(NameCheck.Run(project, Rule().Replace("\"UnityEngine.CoreModule\"", "\"Assembly-CSharp\"")), p => p.Field == "requires");
    }

    [Fact]
    public void Unity_폴더_안_csproj는_Sdk_속성이_있어도_msbuild를_돌리지_않음()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Assets"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ProjectSettings"));
        // msbuild로 돌면 이 타깃이 표시 파일을 만든다
        dir.Write("Assembly-CSharp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="Mark" BeforeTargets="FindReferenceAssembliesForReferences;Restore"><WriteLinesToFile File="ran.txt" Lines="ran" /></Target>
            </Project>
            """);

        var info = ProjectLoader.Load(ProjectLoader.ResolveCsproj(dir.Path, null), CancellationToken.None);

        Assert.Equal("unity", info.Kind);
        Assert.False(File.Exists(Path.Combine(dir.Path, "ran.txt")));
    }

    [Fact]
    public void Unity_csproj의_UNC_경로는_열지_않고_없는_참조로()
    {
        using var dir = new TempDir();
        FakeUnity.Project(dir, Game);
        var csproj = Path.Combine(dir.Path, "Assembly-CSharp.csproj");
        File.WriteAllText(csproj, File.ReadAllText(csproj)
            .Replace("<ItemGroup>\n    <Compile", "<ItemGroup>\n    <Compile Include=\"\\\\unreachable.invalid\\s\\x.cs\" />\n    <Compile")
            .Replace("</Project>", "  <ItemGroup><Reference Include=\"X\"><HintPath>\\\\unreachable.invalid\\s\\a.dll</HintPath></Reference></ItemGroup>\n</Project>"));

        var info = ProjectLoader.Load(csproj, CancellationToken.None);

        Assert.Contains(@"\\unreachable.invalid\s\a.dll", info.MissingReferences);
        Assert.Contains(@"\\unreachable.invalid\s\x.cs", info.MissingReferences);
        Assert.DoesNotContain(info.Sources, s => s.StartsWith(@"\\", StringComparison.Ordinal));
        Assert.Throws<ProjectLoadException>(() => ProjectLoader.ResolveCsproj(@"\\unreachable.invalid\s\p.csproj", null));
    }

    [Fact]
    public void Assembly_CSharp가_없으면_후보를_알려줌()
    {
        using var dir = new TempDir();
        FakeUnity.Project(dir, Game, assemblyName: "Game.Runtime");
        dir.Write("Game.Editor.csproj", "<Project />");

        var e = Assert.Throws<ProjectLoadException>(() => ProjectLoader.ResolveCsproj(dir.Path, null));

        Assert.Equal(["Game.Editor.csproj", "Game.Runtime.csproj"], e.Candidates);
    }

    [Fact]
    public void csproj가_없는_Unity_폴더는_만드는_법을_안내()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Assets"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "ProjectSettings"));

        var e = Assert.Throws<ProjectLoadException>(() => ProjectLoader.ResolveCsproj(dir.Path, null));

        Assert.Contains("Regenerate project files", e.Message);
    }

    [Fact]
    public void SDK_프로젝트는_msbuild로_읽고_빌드_안_한_참조_프로젝트는_소스로()
    {
        using var dir = new TempDir();
        const string sdk = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>$(DefineConstants);MY_DEFINE</DefineConstants></PropertyGroup>
            {0}</Project>
            """;
        dir.Write("Lib/Lib.csproj", string.Format(sdk, ""));
        dir.Write("Lib/Helper.cs", "namespace Lib { public class Helper { public void Run() { } } }");
        var app = dir.Write("App/App.csproj", string.Format(sdk, "  <ItemGroup><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup>\n"));
        dir.Write("App/Program.cs", "namespace App { public class Program { public Lib.Helper H; } }");

        var project = ProjectSession.Load(app, CancellationToken.None);

        Assert.Equal("sdk", project.Info.Kind);
        Assert.Equal("App", project.Info.AssemblyName);
        Assert.Contains(project.Info.Sources, s => s.EndsWith("Program.cs", StringComparison.Ordinal));
        Assert.Contains("MY_DEFINE", project.Info.Defines);
        Assert.Empty(project.Info.MissingReferences);
        Assert.Equal("Lib", Assert.Single(project.Info.SourceReferences).AssemblyName);
        Assert.Contains(project.SearchTypes("Helper"), t => t.MetadataName == "Lib.Helper");
        Assert.Contains(project.Members("Lib.Helper", "")!, m => m.Name == "Run");
        Assert.Empty(project.Compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void SDK_여러_TFM_실행_파일_다이아몬드_참조()
    {
        using var dir = new TempDir();
        static string Project(string tfm, string body = "") =>
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>{tfm}</PropertyGroup>\n{body}</Project>\n";
        static string Ref(params string[] names) =>
            "  <ItemGroup>" + string.Concat(names.Select(n => $"<ProjectReference Include=\"..\\{n}\\{n}.csproj\" />")) + "</ItemGroup>\n";
        // D는 여러 TFM, B · C가 D를, App(실행 파일 · 최상위 문)이 B · C를 참조 — 모두 빌드 전
        dir.Write("D/D.csproj", Project("<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>"));
        dir.Write("D/Dx.cs", "namespace D { public class Dx { public int Value; } }");
        dir.Write("B/B.csproj", Project("<TargetFramework>net10.0</TargetFramework>", Ref("D")));
        dir.Write("B/Bx.cs", "namespace B { public class Bx { public D.Dx D = new D.Dx(); } }");
        dir.Write("C/C.csproj", Project("<TargetFramework>net10.0</TargetFramework>", Ref("D")));
        dir.Write("C/Cx.cs", "namespace C { public class Cx { public D.Dx D = new D.Dx(); } }");
        var app = dir.Write("App/App.csproj", Project("<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>", Ref("B", "C")));
        dir.Write("App/Program.cs", "D.Dx shared = new B.Bx().D;\nshared = new C.Cx().D;\nSystem.Console.WriteLine(shared.Value);\n");

        var project = ProjectSession.Load(app, CancellationToken.None);

        Assert.True(project.Info.Executable);
        Assert.Empty(project.Info.MissingReferences);
        Assert.Empty(project.Compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Same(project.Info.SourceReferences[0].SourceReferences[0], project.Info.SourceReferences[1].SourceReferences[0]);
    }

    [Fact]
    public async Task API로_프로젝트를_읽고_자동_완성_검증_미리_보기()
    {
        using var dir = new TempDir();
        using var unityDir = new TempDir();
        FakeUnity.Project(unityDir, Game);
        var rules = Path.Combine(dir.Path, "rules");
        Directory.CreateDirectory(rules);
        await using var app = await TestApp.Start(dir);
        await app.Open(rules, Path.Combine(dir.Path, "LintRules.g.cs"));

        var state = await app.Client.PostAsJsonAsync("/api/project", new { path = unityDir.Path });
        Assert.True(state.IsSuccessStatusCode, await state.Content.ReadAsStringAsync());
        var project = JsonDocument.Parse(await state.Content.ReadAsStringAsync()).RootElement.GetProperty("project");
        Assert.Equal("Assembly-CSharp", project.GetProperty("assemblyName").GetString());
        Assert.Contains("Assembly-CSharp.csproj", project.GetProperty("candidates").EnumerateArray().Select(c => c.GetString()));

        var types = JsonDocument.Parse(await app.Client.GetStringAsync("/api/symbols/types?q=Mono")).RootElement;
        Assert.Equal("UnityEngine.MonoBehaviour", types[0].GetProperty("metadataName").GetString());

        var validation = JsonDocument.Parse(await (await app.Client.PostAsync("/api/validate", new StringContent(Rule(type: "UnityEngine.MonoBehavor"), Encoding.UTF8))).Content.ReadAsStringAsync()).RootElement;
        Assert.True(validation.GetProperty("ok").GetBoolean());
        Assert.Equal("t", validation.GetProperty("names")[0].GetProperty("node").GetString());

        var preview = JsonDocument.Parse(await (await app.Client.PostAsync("/api/preview", new StringContent(Rule(), Encoding.UTF8))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, preview.GetProperty("total").GetInt32());
        Assert.Equal(7, preview.GetProperty("hits")[0].GetProperty("line").GetInt32());

        var broken = await app.Client.PostAsync("/api/preview", new StringContent("{}", Encoding.UTF8));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, broken.StatusCode);
    }
}
