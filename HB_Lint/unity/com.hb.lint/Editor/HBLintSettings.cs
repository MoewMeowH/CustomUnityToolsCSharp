using UnityEditor;
using UnityEngine;

namespace HBLint.Editor
{
    /// <summary>프로젝트 설정 — ProjectSettings/HBLintSettings.asset (git에 올려 팀이 같이 씀).</summary>
    [FilePath("ProjectSettings/HBLintSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class HBLintSettings : ScriptableSingleton<HBLintSettings>
    {
        public const string DefaultRulesPath = "Assets/HBLint/rules.json";
        public const string DefaultOutputPath = "Assets/HBLint/Rules/LintRules.g.cs";
        public const string DefaultRulesAssemblyName = "CLL.LintRules";

        [SerializeField] private string rulesPath = DefaultRulesPath;
        [SerializeField] private string outputPath = DefaultOutputPath;
        [SerializeField] private string rulesAssemblyName = DefaultRulesAssemblyName;
        [SerializeField] private bool autoGenerate = true;

        /// <summary>규칙 원본 rules.json (프로젝트 폴더 기준).</summary>
        public string RulesPath
        {
            get => NormalizePath(rulesPath);
            set => rulesPath = NormalizePath(value);
        }

        /// <summary>생성물 LintRules.g.cs. 같은 폴더에 규칙 asmdef가 생긴다.</summary>
        public string OutputPath
        {
            get => NormalizePath(outputPath);
            set => outputPath = NormalizePath(value);
        }

        /// <summary>설치가 만드는 규칙 asmdef 이름. 이 어셈블리를 참조하는 어셈블리가 검사된다.</summary>
        public string RulesAssemblyName
        {
            get => rulesAssemblyName;
            set => rulesAssemblyName = value;
        }

        /// <summary>rules.json이 가져와지면(저장하면) 바로 생성.</summary>
        public bool AutoGenerate
        {
            get => autoGenerate;
            set => autoGenerate = value;
        }

        /// <summary>rules.json 옆 rules.schema.json.</summary>
        public string SchemaPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RulesPath) ?? "", "rules.schema.json").Replace('\\', '/');

        public void SaveSettings() => Save(true);

        /// <summary>AssetDatabase 경로 모양으로 (자동 생성이 가져온 에셋 경로와 글자로 비교하므로): '/' 구분 · 앞 "./" 없음 · 앞뒤 공백 없음.</summary>
        internal static string NormalizePath(string path)
        {
            var normalized = (path ?? "").Trim().Replace('\\', '/');
            while (normalized.StartsWith("./", System.StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            return normalized;
        }
    }
}
