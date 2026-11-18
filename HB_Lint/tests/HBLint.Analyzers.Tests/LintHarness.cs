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
            public class Component : Object { public Transform transform => null; }
            public class Behaviour : Component { }
            public class MonoBehaviour : Behaviour { }
            public class Transform : Component { }
            public class ScriptableObject : Object { }
        }
        """;

    private static readonly CSharpCompilationOptions Options = new(OutputKind.DynamicallyLinkedLibrary);

    private static readonly IReadOnlyList<MetadataReference> Bcl =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToList();

    public static readonly MetadataReference Attributes =
        MetadataReference.CreateFromFile(typeof(HBLint.LintSeverity).Assembly.Location);

    public static readonly MetadataReference Unity = Emit(Compile("UnityEngine.CoreModule", UnitySource));

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
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new BanNullOperatorAnalyzer()))
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
