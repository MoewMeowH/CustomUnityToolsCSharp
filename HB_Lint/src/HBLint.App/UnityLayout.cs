using HBLint.App.Workspace;
using HBLint.Core.Generation;

namespace HBLint.App;

/// <summary>Unity 프로젝트 하나에서 앱이 쓸 자리 — 규칙 폴더 · 생성물. NeedsSetup이면 규칙 폴더가 없거나 비었다(규칙 파일 · 스키마 없음).</summary>
internal sealed record UnityLayout(string Project, string Rules, string Output, bool NeedsSetup, bool HasPackage);

/// <summary>
/// "Unity 프로젝트 열기": 프로젝트 폴더만 받아 규칙 폴더 · 생성물 자리를 찾는다 (hblint-gen init이 만드는 자리가 기준).
/// 규칙 폴더 = Assets/HBLint, 없으면 Assets 아래 hblint.schema.json · *.hblint.json이 있는 가장 얕은 폴더, 그것도 없으면 Assets/HBLint(준비 필요).
/// 생성물 = 규칙 폴더 아래 LintRules.g.cs, 없으면 Assets 아래 hblint-gen 생성물, 그것도 없으면 &lt;규칙 폴더&gt;/Rules/LintRules.g.cs.
/// 훑을 때 링크 · 정션은 따라가지 않는다 (프로젝트 밖 · 네트워크 · 고리로 끌려가지 않게).
/// </summary>
internal static class UnityProject
{
    public const string RulesAssembly = "CLL.LintRules";
    private const string OutputName = "LintRules.g.cs";

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>Unity 프로젝트 폴더가 아니면 null. 네트워크 · 장치 경로, 링크로 된 규칙 폴더는 ArgumentException.</summary>
    public static UnityLayout? Find(string path)
    {
        if (ProjectLoader.IsRemoteOrDevice(path))
            throw new ArgumentException($"{path}: 네트워크 · 장치 경로는 열지 않습니다");
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!ProjectLoader.IsUnityFolder(project))
            return null;
        var assets = Path.Combine(project, "Assets");
        var rules = Path.Combine(assets, "HBLint");
        if (!Directory.Exists(rules))
            rules = Shallowest(Directory.EnumerateFiles(assets, IrSchema.FileName, Walk).Concat(Directory.EnumerateFiles(assets, RuleSet.FilePattern, Walk))
                .Select(Path.GetDirectoryName).OfType<string>()) ?? rules;
        else if (new DirectoryInfo(rules).LinkTarget != null)
            throw new ArgumentException($"{rules}: 링크로 된 규칙 폴더는 열지 않습니다 (실제 폴더를 고르세요)");

        var output = (Directory.Exists(rules) ? Generated(rules) : null) ?? Generated(assets) ?? Path.Combine(rules, "Rules", OutputName);
        return new UnityLayout(project, rules, output, NeedsSetup(rules), HasPackage(project));
    }

    /// <summary>폴더가 없거나 규칙 파일도 스키마도 없음 (빈 폴더 · 0.1 시절 rules.json만 있는 폴더).</summary>
    private static bool NeedsSetup(string rules) =>
        !Directory.Exists(rules)
        || (Directory.GetFiles(rules, RuleSet.FilePattern, SearchOption.TopDirectoryOnly).Length == 0 && !File.Exists(Path.Combine(rules, IrSchema.FileName)));

    /// <summary>그 아래에서 가장 얕은 hblint-gen 생성물 (머리가 다른 같은 이름 파일은 뺀다).</summary>
    private static string? Generated(string folder) =>
        Shallowest(Directory.EnumerateFiles(folder, OutputName, Walk).Where(RuleFolder.IsGeneratedFile));

    /// <summary>폴더 깊이(구분자 수)가 가장 작은 것, 같으면 짧은 것.</summary>
    private static string? Shallowest(IEnumerable<string> paths) =>
        paths.OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>com.hb.lint가 manifest에 있거나 Packages/ 아래에 있으면 (없으면 규칙 asmdef가 HBLint.Attributes를 못 찾아 Unity 컴파일이 깨진다).</summary>
    private static bool HasPackage(string project)
    {
        var manifest = Path.Combine(project, "Packages", "manifest.json");
        return Directory.Exists(Path.Combine(project, "Packages", "com.hb.lint"))
            || (File.Exists(manifest) && File.ReadAllText(manifest).Contains("\"com.hb.lint\"", StringComparison.Ordinal));
    }

    /// <summary>처음 준비 — hblint-gen init과 같다 (규칙 파일이 없으면 예시 3개 · 스키마 · 생성물 폴더에 asmdef가 없으면 규칙 asmdef) + 생성.</summary>
    public static List<string> Setup(UnityLayout layout)
    {
        var lines = RuleFolderSetup.Prepare(layout.Rules, layout.Output, RulesAssembly);
        var (set, refused) = new RuleFolder(layout.Rules, layout.Output).Generate();
        lines.Add(refused ?? (set.Text != null ? $"{layout.Output}: 만들었습니다" : $"생성 안 함 — 오류 {set.Errors.Count}개"));
        return lines;
    }
}
