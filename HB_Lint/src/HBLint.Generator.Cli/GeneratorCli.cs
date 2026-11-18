namespace HBLint.Generator.Cli;

/// <summary>
/// hblint-gen generate &lt;rules.json&gt; &lt;LintRules.g.cs&gt; [--check]
/// hblint-gen schema &lt;rules.schema.json&gt; [--check]
/// 종료 코드: 0 성공 · 1 오류 또는 --check 다름 · 2 사용법.
/// </summary>
internal static class GeneratorCli
{
    private const string Usage = """
        사용법:
          hblint-gen generate <rules.json> <LintRules.g.cs> [--check]
          hblint-gen schema <rules.schema.json> [--check]

        --check  쓰지 않고 기존 파일과 비교만 한다 (다르면 종료 코드 1)
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var check = args.Contains("--check");
        var positional = args.Where(a => a != "--check").ToArray();

        try
        {
            if (positional.Length == 3 && positional[0] == "generate")
                return Generate(positional[1], positional[2], check, output, error);
            if (positional.Length == 2 && positional[0] == "schema")
                return Schema(positional[1], check, output, error);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            error.WriteLine("파일 오류: " + e.Message);
            return 1;
        }

        error.WriteLine(Usage);
        return 2;
    }

    private static int Generate(string rulesPath, string outputPath, bool check, TextWriter output, TextWriter error)
    {
        if (!File.Exists(rulesPath))
        {
            error.WriteLine($"{rulesPath}: 파일이 없습니다");
            return 1;
        }
        if (!TryCatalog(error, out var catalog))
            return 1;

        var result = RulesJson.Generate(File.ReadAllText(rulesPath), catalog);
        if (!result.Success)
        {
            foreach (var e in result.Errors)
                error.WriteLine(rulesPath + e);
            error.WriteLine($"오류 {result.Errors.Count}개 — {outputPath}는 바꾸지 않았습니다");
            return 1;
        }
        return Finish(outputPath, result.Text!, check, output, error);
    }

    private static int Schema(string outputPath, bool check, TextWriter output, TextWriter error)
    {
        if (!TryCatalog(error, out var catalog))
            return 1;
        return Finish(outputPath, RulesSchema.Generate(catalog), check, output, error);
    }

    private static int Finish(string outputPath, string text, bool check, TextWriter output, TextWriter error)
    {
        if (check)
        {
            if (OutputFile.Matches(outputPath, text))
            {
                output.WriteLine($"{outputPath}: 최신");
                return 0;
            }
            error.WriteLine($"{outputPath}: 생성 결과와 다릅니다 — hblint-gen을 --check 없이 돌려 다시 만드세요");
            return 1;
        }
        output.WriteLine(OutputFile.WriteIfChanged(outputPath, text) ? $"{outputPath}: 만들었습니다" : $"{outputPath}: 바뀐 것 없음");
        return 0;
    }

    private static bool TryCatalog(TextWriter error, out RuleCatalog catalog)
    {
        try
        {
            catalog = RuleCatalog.FromAssembly(typeof(HBLint.LintSeverity).Assembly);
            return true;
        }
        catch (RuleCatalogException e)
        {
            error.WriteLine("규칙 어트리뷰트 오류: " + e.Message);
            catalog = null!;
            return false;
        }
    }
}
