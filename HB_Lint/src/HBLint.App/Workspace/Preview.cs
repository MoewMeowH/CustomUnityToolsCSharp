using System.Collections.Immutable;
using HBLint.Analyzers;
using HBLint.Core.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HBLint.App.Workspace;

internal sealed record PreviewHit(string Id, string Severity, string? File, int Line, int Column, string Message);

internal sealed record PreviewResult(IReadOnlyList<PreviewHit> Hits, int Total, int CompileErrors, long ElapsedMs);

/// <summary>
/// 편집 중인 규칙 하나를 대상 소스에 돌린다 — 대상 컴파일에 [assembly: HBLint.Rule("&lt;IR&gt;")]를 더해 엔진 분석기(GraphRuleAnalyzer)를 그대로 쓴다.
/// 컴파일 이름이 대상 AssemblyName이라 ignoreAssemblies · requires도 실제와 같게 걸린다.
/// 분석기는 같은 id 가운데 먼저 읽은 규칙만 쓰고(자기 어셈블리 → 참조 순, 소스는 트리 순), 대상 소스 · 참조에 이미 생성물 규칙이 있을 수 있으므로
/// 미리 보기 규칙을 트리 맨 앞에 둔다 — 참조는 그대로 둔다 (규칙과 코드가 한 어셈블리일 수 있음).
/// </summary>
internal static class Preview
{
    public const int MaxHits = 500;

    private const string AttributeSource =
        "namespace HBLint { [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)] internal sealed class RuleAttribute : System.Attribute { public RuleAttribute(string ir) { } } }";

    /// <summary>규칙을 찾을 수 없음 — 이 규칙의 것만 보인다. 규칙 오류(CLL9002)는 앞서 읽은 이 규칙이 이겨 밀려난 옛 같은 id 규칙의 것이라 뺀다.</summary>
    private static readonly string TargetNotFoundId = RuleIds.Prefix + "9001";

    public static PreviewResult Run(ProjectSession project, RuleIr rule, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var source = project.Compilation;
        var literal = SymbolDisplay.FormatLiteral(rule.ToRuntimeJson(), quote: true);
        var first = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText($"[assembly: HBLint.Rule({literal})]", project.ParseOptions, "HBLintPreview.Rule.g.cs", cancellationToken: ct),
        };
        // 대상이 HBLint.Attributes를 참조하면(또는 소스에 있으면) 그 어트리뷰트를 쓴다
        if (source.GetTypeByMetadataName("HBLint.RuleAttribute") == null)
            first.Add(CSharpSyntaxTree.ParseText(AttributeSource, project.ParseOptions, "HBLintPreview.Attribute.g.cs", cancellationToken: ct));
        var compilation = CSharpCompilation.Create(source.AssemblyName, first.Concat(source.SyntaxTrees), source.References, source.Options);

        var all = compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new GraphRuleAnalyzer()), new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), null, concurrentAnalysis: true, logAnalyzerExecutionTime: false))
            .GetAllDiagnosticsAsync(ct).GetAwaiter().GetResult();
        var compileErrors = all.Count(d => d.Severity == DiagnosticSeverity.Error && !d.Id.StartsWith(RuleIds.Prefix, StringComparison.Ordinal));
        var diagnostics = all
            .Where(d => d.Id == rule.Id || (d.Id == TargetNotFoundId && d.GetMessage().Contains(rule.Id, StringComparison.Ordinal)))
            .OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ToList();

        var hits = diagnostics.Take(MaxHits).Select(d =>
        {
            var span = d.Location.GetLineSpan();
            return new PreviewHit(
                d.Id,
                d.Severity.ToString().ToLowerInvariant(),
                d.Location.IsInSource ? span.Path : null,
                d.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0,
                d.Location.IsInSource ? span.StartLinePosition.Character + 1 : 0,
                d.GetMessage(System.Globalization.CultureInfo.CurrentUICulture));
        }).ToList();
        return new PreviewResult(hits, diagnostics.Count, compileErrors, watch.ElapsedMilliseconds);
    }
}
