#nullable enable
using System.IO;
using System.Linq;
using HBLint.Generator;
using HBLint.Generator.Json;
using UnityEditor;
using UnityEngine;

namespace HBLint.Editor
{
    /// <summary>처음 설치: 없는 것만 만든다 — rules.json(예시 규칙) · 규칙 asmdef · 생성물 · 스키마. 있는 파일은 건드리지 않는다.</summary>
    public static class LintInstaller
    {
        public const string ExampleRuleId = "unity-null-operator";

        public static void Install()
        {
            var settings = HBLintSettings.instance;
            // 생성물이 asmdef보다 먼저 가져와져 Assembly-CSharp로 한 번 컴파일되지 않게 가져오기를 묶는다
            AssetDatabase.StartAssetEditing();
            try
            {
                Install(settings.RulesPath, settings.OutputPath, settings.SchemaPath, settings.RulesAssemblyName);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();
        }

        public static void Install(string rulesPath, string outputPath, string schemaPath, string rulesAssemblyName)
        {
            var catalog = LintGenerator.LoadCatalog();
            if (catalog == null)
                return;

            if (!File.Exists(rulesPath))
                WriteNew(rulesPath, ExampleRules().ToJson(catalog));

            // 한 폴더에 asmdef가 둘이면 Unity가 컴파일을 막는다 — 이름을 바꿨어도 이미 있는 asmdef를 그대로 둔다
            var folder = Path.GetDirectoryName(outputPath) ?? "";
            var existing = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.asmdef") : new string[0];
            if (existing.Length == 0)
                WriteNew(Path.Combine(folder, rulesAssemblyName + ".asmdef").Replace('\\', '/'), RulesAsmdef(rulesAssemblyName));
            else if (existing.All(p => Path.GetFileNameWithoutExtension(p) != rulesAssemblyName))
                Debug.LogWarning($"[HB Lint] {folder}에 이미 asmdef({Path.GetFileName(existing[0])})가 있어 {rulesAssemblyName}.asmdef를 만들지 않았습니다. 이름을 바꾸려면 그 asmdef를 직접 고치세요.");

            LintGenerator.Generate(rulesPath, outputPath, schemaPath);
        }

        private const string EditorOnlyRuleId = "editor-only-code";

        /// <summary>예시 규칙 둘 — Unity 오브젝트에 null 연산자 금지 · UNITY_EDITOR 밖 에디터 전용 코드 금지.</summary>
        internal static RulesDocument ExampleRules()
        {
            var document = new RulesDocument { Schema = "./rules.schema.json" };
            var entry = new RuleEntry(ExampleRuleId, "BanNullOperator")
            {
                Description = "파괴된 Unity 오브젝트는 C# null이 아니라서 ?. · ?? · ??= · is null이 통과시킨다.",
            };
            entry.Values["type"] = new JsonString("UnityEngine.Object");
            entry.Values["message"] = new JsonString("Unity 오브젝트는 ?. · ?? · ??= · is null이 파괴된 오브젝트를 null로 보지 않습니다. '== null' 또는 'if (obj)'로 확인하세요.");
            entry.Values["requires"] = new JsonArray().Add(new JsonString("UnityEngine.CoreModule"));
            entry.Values["severity"] = new JsonString("Warning");
            document.Rules.Add(entry);

            var editorOnly = new RuleEntry(EditorOnlyRuleId, "RequireDefine")
            {
                Description = "UnityEditor API와 #if UNITY_EDITOR 안에 선언된 코드는 플레이어 빌드에 없다.",
            };
            editorOnly.Values["define"] = new JsonString("UNITY_EDITOR");
            editorOnly.Values["message"] = new JsonString("에디터 전용 코드입니다 — #if UNITY_EDITOR 안에서만 쓰세요. 플레이어 빌드가 깨집니다.");
            editorOnly.Values["namespaces"] = new JsonArray().Add(new JsonString("UnityEditor"));
            editorOnly.Values["declaredInDefine"] = new JsonBool(true);
            var ignore = new JsonArray();
            foreach (var pattern in new[] { "Assembly-CSharp-Editor*", "*.Editor", "*.Editor.*", "*.Tests", "*.Tests.*" })
                ignore.Add(new JsonString(pattern));
            editorOnly.Values["ignoreAssemblies"] = ignore;
            document.Rules.Add(editorOnly);
            return document;
        }

        /// <summary>규칙 asmdef — HBLint.Attributes 참조, Auto Referenced(그래서 Assembly-CSharp는 자동 검사), 엔진 참조 없음.</summary>
        internal static string RulesAsmdef(string name)
        {
            var asmdef = new JsonObject()
                .Add("name", new JsonString(name))
                .Add("references", new JsonArray().Add(new JsonString(LintGenerator.AttributesAssemblyName)))
                .Add("autoReferenced", new JsonBool(true))
                .Add("noEngineReferences", new JsonBool(true));
            return JsonWriter.Write(asmdef);
        }

        private static void WriteNew(string path, string text)
        {
            OutputFile.WriteIfChanged(path, text);
            Debug.Log($"[HB Lint] 만들었습니다: {path}");
        }
    }
}
