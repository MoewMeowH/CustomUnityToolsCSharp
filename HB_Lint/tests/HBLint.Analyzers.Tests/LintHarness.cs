using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HBLint.Analyzers.Tests;

/// <summary>
/// 가짜 UnityEngine · 실제 HBLint.Attributes DLL · 규칙 어셈블리 · 대상 어셈블리를 차례로 컴파일해 분석기를 돌린다.
/// 규칙 어셈블리를 DLL(메타데이터 참조, Unity 방식) 또는 컴파일 참조(IDE 방식)로 넘길 수 있다.
/// </summary>
internal static class LintHarness
{
    private const string UnitySource = """
        namespace UnityEngine
        {
            public class Object { }
            public class Component : Object
            {
                public Transform transform => null;
                public GameObject gameObject => null;
                public T GetComponent<T>() => default;
                public Component GetComponent(System.Type type) => null;
            }
            public class GameObject : Object { public T GetComponent<T>() => default; }
            public class Behaviour : Component { }
            public class MonoBehaviour : Behaviour { }
            public class Transform : Component { }
            public class ScriptableObject : Object { }
            public class Camera : Behaviour { public static Camera main => null; }
        }
        """;

    private static readonly CSharpCompilationOptions Options = new(OutputKind.DynamicallyLinkedLibrary);

    // 시험 프로세스의 어셈블리 목록에는 시험 프로젝트가 참조한 HBLint.* 가 섞여 있다 — 빼야 "참조 안 함" 시험이 정말 참조 없이 돈다
    private static readonly IReadOnlyList<MetadataReference> Bcl =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Where(p => !Path.GetFileName(p).StartsWith("HBLint.", StringComparison.Ordinal))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToList();

    public static readonly MetadataReference Attributes =
        MetadataReference.CreateFromFile(typeof(HBLint.LintSeverity).Assembly.Location);

    public static readonly MetadataReference Unity = Emit(Compile("UnityEngine.CoreModule", UnitySource));

    private const string UnityEditorSource = """
        namespace UnityEditor
        {
            public static class AssetDatabase { public static void Refresh() { } }
            public class EditorWindow { public void Show() { } }
        }
        namespace UnityEditor.Build
        {
            public static class BuildHelper { public static void Run() { } }
        }
        """;

    public static readonly MetadataReference UnityEditor = Emit(Compile("UnityEditor", UnityEditorSource));

    /// <summary>전처리 기호를 넣어 컴파일 (Unity 에디터 컴파일은 UNITY_EDITOR가 정의돼 있다).</summary>
    public static CSharpCompilation CompileWithDefines(string assemblyName, string source, string[] defines, params MetadataReference[] references)
    {
        return CompileWithDefines(assemblyName, new[] { (assemblyName + ".cs", source) }, defines, references);
    }

    public static CSharpCompilation CompileWithDefines(string assemblyName, (string Path, string Source)[] files, string[] defines, params MetadataReference[] references)
    {
        var options = new CSharpParseOptions(preprocessorSymbols: defines);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, options, path: f.Path));
        return CSharpCompilation.Create(assemblyName, trees, Bcl.Concat(references), Options);
    }

    public static CSharpCompilation Compile(string assemblyName, string source, params MetadataReference[] references)
    {
        return Compile(assemblyName, new[] { (assemblyName + ".cs", source) }, references);
    }

    public static CSharpCompilation Compile(string assemblyName, (string Path, string Source)[] files, params MetadataReference[] references)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path));
        return CSharpCompilation.Create(assemblyName, trees, Bcl.Concat(references), Options);
    }

    /// <summary>규칙 어셈블리 (UnityEngine은 참조하지 않는다 — 대상 타입은 문자열).</summary>
    public static CSharpCompilation Rules(string ruleAttributes)
    {
        return Compile("Game.LintRules", ruleAttributes, Attributes);
    }

    public static MetadataReference Emit(CSharpCompilation compilation)
    {
        var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, compilation.AssemblyName + ": " + string.Join("\n", result.Diagnostics));
        stream.Position = 0;
        return MetadataReference.CreateFromStream(stream);
    }

    public static ImmutableArray<Diagnostic> Analyze(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "컴파일 에러: " + string.Join("\n", errors));
        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new BanNullOperatorAnalyzer(), new BanCallInMethodAnalyzer(), new RequireDefineAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().Result
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToImmutableArray();
    }

    /// <summary>규칙 어셈블리를 DLL로 참조하는 대상 어셈블리 "Game"을 분석한다.</summary>
    public static ImmutableArray<Diagnostic> AnalyzeGame(string ruleAttributes, string gameSource)
    {
        var rules = Emit(Rules(ruleAttributes));
        return Analyze(Compile("Game", gameSource, Unity, Attributes, rules));
    }

    public static int Line(Diagnostic diagnostic) => diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;

    public static string Text(Diagnostic diagnostic) =>
        diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
}
