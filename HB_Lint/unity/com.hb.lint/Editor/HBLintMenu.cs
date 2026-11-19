using UnityEditor;

namespace HBLint.Editor
{
    internal static class HBLintMenu
    {
        private const string Root = "Tools/HB Lint/";

        [MenuItem(Root + "규칙 편집", priority = 0)]
        private static void OpenEditor() => RulesEditorWindow.Open();

        [MenuItem(Root + "다시 생성", priority = 20)]
        private static void Generate()
        {
            if (LintGenerator.Generate())
                UnityEngine.Debug.Log("[HB Lint] 생성했습니다: " + HBLintSettings.instance.OutputPath);
        }

        [MenuItem(Root + "설치", priority = 40)]
        private static void Install() => LintInstaller.Install();

        [MenuItem(Root + "설정", priority = 41)]
        private static void OpenSettings() => SettingsService.OpenProjectSettings(HBLintSettingsProvider.Path);
    }
}
