using System;
using UnityEditor;

namespace HBLint.Editor
{
    /// <summary>
    /// rules.json이 가져와지면(저장 · git pull) 생성물을 다시 만든다. 가져오기 도중에는 파일을 쓰지 않고 delayCall로 미룬다.
    /// 생성은 생성물 · 스키마만 다시 가져오므로 rules.json 가져오기가 되풀이되지 않는다.
    /// </summary>
    internal sealed class RulesPostprocessor : AssetPostprocessor
    {
        private static bool _scheduled;

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var settings = HBLintSettings.instance;
            if (!settings.AutoGenerate || _scheduled || Array.IndexOf(importedAssets, settings.RulesPath) < 0)
                return;

            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                LintGenerator.Generate();
            };
        }
    }
}
