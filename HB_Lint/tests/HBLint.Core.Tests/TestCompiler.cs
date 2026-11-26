using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HBLint.Core.Tests;

/// <summary>생성물을 Roslyn으로 컴파일 · 분석기 실행.</summary>
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
        MetadataReference.CreateFromFile(typeof(HBLint.RuleAttribute).Assembly.Location);

    public static CSharpCompilation Compile(string assemblyName, string source, params MetadataReference[] references)
    {
        return CSharpCompilation.Create(assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source, path: assemblyName + ".cs") },
            Bcl.Concat(references), Options);
    }

    /// <summary>파일 경로 · 전처리 기호를 정해 여러 파일로 (코드 자리 · 디파인 필터 시험).</summary>
    public static CSharpCompilation Compile(string assemblyName, (string Path, string Source)[] files, string[] defines, params MetadataReference[] references)
    {
        var options = new CSharpParseOptions(preprocessorSymbols: defines);
        return CSharpCompilation.Create(assemblyName,
            files.Select(f => CSharpSyntaxTree.ParseText(f.Source, options, path: f.Path)),
            Bcl.Concat(references), Options);
    }

    public static void AssertNoErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "컴파일 에러:\n" + string.Join("\n", errors));
    }

    public static MetadataReference Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, compilation.AssemblyName + ": " + string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    public static ImmutableArray<Diagnostic> RunAnalyzer(CSharpCompilation compilation)
    {
        AssertNoErrors(compilation);
        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HBLint.Analyzers.GraphRuleAnalyzer()))
            .GetAnalyzerDiagnosticsAsync().Result;
    }

    /// <summary>HB_Lint 폴더 (HB_Lint.slnx가 있는 곳).</summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "HB_Lint.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
