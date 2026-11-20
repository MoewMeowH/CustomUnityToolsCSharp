using Microsoft.CodeAnalysis;
using static HBLint.Analyzers.Tests.LintHarness;

namespace HBLint.Analyzers.Tests;

public class BanCallInMethodAnalyzerTests
{
    private static readonly string GetComponentRule = Presets.Attributes(Presets.CallInMethod(
        new[] { "Update", "LateUpdate" }, new[] { "UnityEngine.Component.GetComponent", "UnityEngine.GameObject.GetComponent", "UnityEngine.Camera.main" }, "UnityEngine.MonoBehaviour", "no-getcomponent", requires: new[] { "UnityEngine.CoreModule" }));

    private static Diagnostic[] Cll0002(IEnumerable<Diagnostic> diagnostics) => diagnostics.Where(d => d.Id == "CLL0002").ToArray();

    [Fact]
    public void Update_본문_호출_속성_로컬함수는_걸리고_람다_다른메서드_다른계열은_안_걸림()
    {
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update()
                {
                    var a = GetComponent<Transform>();
                    var b = gameObject.GetComponent<Transform>();
                    var c = GetComponent(typeof(Transform));
                    var d = Camera.main;
                    void Local() { var e = GetComponent<Transform>(); }
                    System.Func<Transform> f = () => GetComponent<Transform>();
                    Local();
                }
                void Start() { var x = GetComponent<Transform>(); }
            }
            public class NotMono
            {
                Component c;
                void Update() { c.GetComponent<Transform>(); }
            }
            """;

        var diagnostics = Cll0002(AnalyzeGame(GetComponentRule, source));

        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, diagnostics.Select(Line));
        Assert.Equal("GetComponent<Transform>", Text(diagnostics[0]));
        Assert.Equal("main", Text(diagnostics[3]));
        Assert.All(diagnostics, d => Assert.Equal("no-getcomponent", d.GetMessage()));
    }

    private const string UiSource = """
        using UnityEngine;
        namespace MyGame.UI
        {
            public class UIBase : MonoBehaviour { public virtual void RefreshUI() { } public void Plain() { } }
            public class ShopUI : UIBase { public override void RefreshUI() { } }
            public class HiddenUI : UIBase { public new void Plain() { } }
            public class Panel : UIBase
            {
                ShopUI shop; HiddenUI hidden;
                void Update() { RefreshUI(); shop.RefreshUI(); hidden.Plain(); }
            }
            public class Manager : MonoBehaviour
            {
                ShopUI shop;
                void Update() { shop.RefreshUI(); }
            }
        }
        """;

    [Fact]
    public void 프로젝트_메서드와_오버라이드를_잡고_new로_가린_것은_안_잡음()
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "MyGame.UI.UIBase.RefreshUI", "MyGame.UI.UIBase.Plain" }, "MyGame.UI.UIBase", "no-refresh"));

        var diagnostics = Cll0002(AnalyzeGame(rule, UiSource));

        Assert.Equal(new[] { "RefreshUI", "RefreshUI" }, diagnostics.Select(Text));
        Assert.All(diagnostics, d => Assert.Equal(10, Line(d)));
    }

    [Fact]
    public void ownerType을_MonoBehaviour로_두면_다른_클래스에서_부르는_것도_잡음()
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "MyGame.UI.UIBase.RefreshUI" }, "UnityEngine.MonoBehaviour", "no-refresh"));

        var diagnostics = Cll0002(AnalyzeGame(rule, UiSource));

        Assert.Equal(new[] { 10, 10, 15 }, diagnostics.Select(Line));
    }

    [Theory]
    [InlineData(0, new int[0], "")]
    [InlineData(1, new[] { 6 }, "no (Update → A)")]
    [InlineData(2, new[] { 6, 9 }, "no (Update → A)|no (Update → A → B)")]
    public void FollowDepth만큼_같은_클래스_헬퍼를_따라가고_경로를_붙임(int depth, int[] lines, string messages)
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "UnityEngine.Component.GetComponent" }, "UnityEngine.MonoBehaviour", "no", depth));
        const string source = """
            using UnityEngine;
            public class Foo : MonoBehaviour
            {
                void Update() { A(); }
                void A()
                { GetComponent<Transform>(); B(); }
                void B()
                {
                    GetComponent<Transform>();
                }
                void C() { GetComponent<Transform>(); }
            }
            """;

        var diagnostics = Cll0002(AnalyzeGame(rule, source)).OrderBy(d => d.Location.SourceSpan.Start).ToArray();

        Assert.Equal(lines, diagnostics.Select(Line));
        Assert.Equal(messages, string.Join("|", diagnostics.Select(d => d.GetMessage())));
    }

    [Fact]
    public void 따라가기_깊이가_범위를_넘으면_규칙을_빼고_CLL9002()
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "UnityEngine.Component.GetComponent" }, "UnityEngine.MonoBehaviour", "no", 5));
        const string source = "using UnityEngine; public class Foo : MonoBehaviour { void Update() { GetComponent<Transform>(); } }";

        var diagnostic = Assert.Single(AnalyzeGame(rule, source));

        Assert.Equal("CLL9002", diagnostic.Id);
        Assert.Contains("depth", diagnostic.GetMessage());
    }

    [Fact]
    public void 없는_멤버는_CLL9001이고_나머지_멤버는_그대로_동작()
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "UnityEngine.Component.GetComponnent", "UnityEngine.Component.GetComponent" }, "UnityEngine.MonoBehaviour", "no"));
        const string source = "using UnityEngine; public class Foo : MonoBehaviour { void Update() { GetComponent<Transform>(); } }";

        var diagnostics = AnalyzeGame(rule, source);

        Assert.Single(Cll0002(diagnostics));
        Assert.Contains(diagnostics, d => d.Id == "CLL9001" && d.GetMessage().Contains("GetComponnent"));
    }

    [Fact]
    public void 어트리뷰트_어셈블리를_참조하지_않아도_배열_정수_칸을_읽음()
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "UnityEngine.Component.GetComponent" }, "UnityEngine.MonoBehaviour", "no", 1, "error"));
        const string source = "using UnityEngine; public class Foo : MonoBehaviour { void Update() { A(); } void A() { GetComponent<Transform>(); } }";
        var rules = Emit(Rules(rule));

        var diagnostics = Cll0002(Analyze(Compile("Game", source, Unity, rules)));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("no (Update → A)", diagnostic.GetMessage());
    }

    [Fact]
    public void partial_메서드_구현부도_보고_따라감()
    {
        var rule = Presets.Attributes(Presets.CallInMethod(new[] { "Update" }, new[] { "UnityEngine.Component.GetComponent" }, "UnityEngine.MonoBehaviour", "no", 1));
        const string source = """
            using UnityEngine;
            public partial class Foo : MonoBehaviour
            {
                partial void Update();
                partial void Helper();
            }
            public partial class Foo
            {
                partial void Update() { GetComponent<Transform>(); Helper(); }
                partial void Helper() { GetComponent<Transform>(); }
            }
            """;

        var diagnostics = Cll0002(AnalyzeGame(rule, source));

        Assert.Equal(new[] { "no", "no (Update → Helper)" }, diagnostics.Select(d => d.GetMessage()));
    }
}
