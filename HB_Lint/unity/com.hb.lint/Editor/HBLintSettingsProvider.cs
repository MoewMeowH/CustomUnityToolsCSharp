using UnityEditor;
using UnityEngine;

namespace HBLint.Editor
{
    /// <summary>Project Settings > HB Lint.</summary>
    internal static class HBLintSettingsProvider
    {
        public const string Path = "Project/HB Lint";

        [SettingsProvider]
        private static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.Project)
            {
                label = "HB Lint",
                keywords = new[] { "lint", "rules", "analyzer" },
                guiHandler = _ => Draw(),
            };
        }

        private static void Draw()
        {
            var settings = HBLintSettings.instance;
            EditorGUI.BeginChangeCheck();
            settings.RulesPath = EditorGUILayout.TextField(new GUIContent("rules.json", "규칙 원본 (프로젝트 폴더 기준)"), settings.RulesPath);
            settings.OutputPath = EditorGUILayout.TextField(new GUIContent("생성물", "LintRules.g.cs — 같은 폴더에 규칙 asmdef"), settings.OutputPath);
            settings.RulesAssemblyName = EditorGUILayout.TextField(new GUIContent("규칙 asmdef 이름", "이 어셈블리를 참조하는 어셈블리가 검사된다"), settings.RulesAssemblyName);
            settings.AutoGenerate = EditorGUILayout.Toggle(new GUIContent("저장하면 자동 생성"), settings.AutoGenerate);
            if (EditorGUI.EndChangeCheck())
                settings.SaveSettings();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("설치는 없는 것만 만든다: rules.json(예시 규칙) · 규칙 asmdef · 생성물 · rules.schema.json.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("설치"))
                    LintInstaller.Install();
                if (GUILayout.Button("다시 생성"))
                    LintGenerator.Generate();
                if (GUILayout.Button("규칙 편집"))
                    RulesEditorWindow.Open();
            }
        }
    }
}
