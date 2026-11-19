using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HBLint.Generator.Tests;

/// <summary>생성물을 Roslyn으로 컴파일 · 분석기 실행 · 어셈블리로 불러오기.</summary>
internal static class TestCompiler
{
    private static readonly CSharpCompilationOptions Options = new(OutputKind.DynamicallyLinkedLibrary);

    // 시험 프로세스의 어셈블리 목록에서 HBLint.* 는 뺀다 — 필요한 곳에서만 Attributes를 명시해 넘긴다
    private static readonly IReadOnlyList<MetadataReference> Bcl =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Where(p => !Path.GetFileName(p).StartsWith("HBLint.", StringComparison.Ordinal))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToList();

    public static readonly MetadataReference Attributes =
        MetadataReference.CreateFromFile(typeof(HBLint.LintSeverity).Assembly.Location);

    public static RuleCatalog RealCatalog => RuleCatalog.FromAssembly(typeof(HBLint.LintSeverity).Assembly);

    public static CSharpCompilation Compile(string assemblyName, string source, params MetadataReference[] references)
    {
        return CSharpCompilation.Create(assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source, path: assemblyName + ".cs") },
            Bcl.Concat(references), Options);
    }

    public static void AssertNoErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "컴파일 에러:\n" + string.Join("\n", errors));
    }

    public static byte[] EmitBytes(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, compilation.AssemblyName + ": " + string.Join("\n", result.Diagnostics));
        return stream.ToArray();
    }

    public static MetadataReference Emit(CSharpCompilation compilation) => MetadataReference.CreateFromImage(EmitBytes(compilation));

    /// <summary>소스로 만든 어트리뷰트 어셈블리를 불러온다 (새 규칙 종류 · 잘못된 어트리뷰트 시험용).</summary>
    public static Assembly LoadAttributes(string source)
    {
        return Assembly.Load(EmitBytes(Compile("HBLint.Attributes.Test" + Guid.NewGuid().ToString("N"), source)));
    }

    public static ImmutableArray<Diagnostic> RunAnalyzer(CSharpCompilation compilation)
    {
        AssertNoErrors(compilation);
        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HBLint.Analyzers.BanNullOperatorAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().Result;
    }

    /// <summary>assembly-level 어트리뷰트를 (이름, 생성자 인자, 이름 붙은 인자)로.</summary>
    public static List<AttributeData> AssemblyAttributes(Compilation compilation, string attributeFullName)
    {
        return compilation.Assembly.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == attributeFullName)
            .ToList();
    }
}
