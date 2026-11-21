using HBLint.Core.Generation;

namespace HBLint.Generator.Cli;

/// <summary>
/// hblint-gen — 규칙 파일(*.hblint.json, 규칙 하나 = 파일 하나) → LintRules.g.cs. Unity · 일반 .NET 공통.
/// 종료 코드: 0 성공 · 1 오류 또는 --check 다름 · 2 사용법.
/// </summary>
internal static class GeneratorCli
{
    private const string Usage = """
        사용법:
          hblint-gen init <규칙 폴더> <LintRules.g.cs> [--asmdef <규칙 어셈블리 이름>]
          hblint-gen validate <규칙 폴더>
          hblint-gen generate <규칙 폴더> <LintRules.g.cs> [--check]
          hblint-gen schema <hblint.schema.json> [--check]

        init      규칙 폴더가 비었으면 예시 규칙, (--asmdef) 생성물 폴더에 asmdef가 없으면 Unity 규칙 asmdef를 만들고,
                  규칙 폴더의 스키마는 늘 최신으로 쓴다. 그 뒤 generate.
        규칙 폴더  그 폴더 바로 아래의 *.hblint.json (하위 폴더는 보지 않음)
        --check   쓰지 않고 기존 파일과 비교만 한다 (다르면 종료 코드 1)
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var check = args.Contains("--check");
        string? asmdef = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--check")
                continue;
            if (args[i] == "--asmdef" && i + 1 < args.Length)
            {
                asmdef = args[++i];
                continue;
            }
            positional.Add(args[i]);
        }

        try
        {
            switch (positional.Count > 0 ? positional[0] : "")
            {
                case "init" when positional.Count == 3 && !check:
                    return Init(positional[1], positional[2], asmdef, output, error);
                case "validate" when positional.Count == 2 && !check && asmdef == null:
                    return Validate(positional[1], output, error);
                case "generate" when positional.Count == 3 && asmdef == null:
                    return Generate(positional[1], positional[2], check, output, error);
                case "schema" when positional.Count == 2 && asmdef == null:
                    return Finish(positional[1], IrSchema.Generate(), check, output, error);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            error.WriteLine("파일 오류: " + e.Message);
            return 1;
        }

        error.WriteLine(Usage);
        return 2;
    }

    private static int Init(string folder, string outputPath, string? asmdef, TextWriter output, TextWriter error)
    {
        foreach (var line in RuleFolderSetup.Prepare(folder, outputPath, asmdef))
            output.WriteLine(line);
        return Generate(folder, outputPath, false, output, error);
    }

    private static int Validate(string folder, TextWriter output, TextWriter error)
    {
        if (!TryRead(folder, error, out var set))
            return 1;
        foreach (var warning in set.Warnings)
            output.WriteLine("경고 " + warning);
        foreach (var e in set.Errors)
            error.WriteLine(e);
        if (set.Errors.Count > 0)
        {
            error.WriteLine($"오류 {set.Errors.Count}개");
            return 1;
        }
        output.WriteLine($"규칙 {set.Count}개: 오류 없음");
        return 0;
    }

    private static int Generate(string folder, string outputPath, bool check, TextWriter output, TextWriter error)
    {
        if (!TryRead(folder, error, out var set))
            return 1;
        foreach (var warning in set.Warnings)
            output.WriteLine("경고 " + warning);
        if (set.Text == null)
        {
            foreach (var e in set.Errors)
                error.WriteLine(e);
            error.WriteLine($"오류 {set.Errors.Count}개 — {outputPath}는 바꾸지 않았습니다");
            return 1;
        }
        return Finish(outputPath, set.Text, check, output, error);
    }

    private static bool TryRead(string folder, TextWriter error, out RuleSet set)
    {
        if (!Directory.Exists(folder))
        {
            error.WriteLine($"{folder}: 폴더가 없습니다");
            set = null!;
            return false;
        }
        set = RuleSet.Build(RuleSet.ReadFolder(folder));
        return true;
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
}
