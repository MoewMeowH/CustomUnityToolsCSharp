#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HBLint.Core.Json;

namespace HBLint.Core.Generation
{
    /// <summary>
    /// 규칙 폴더 처음 준비 (hblint-gen init · 편집 앱의 "Unity 프로젝트 열기"가 같이 쓴다):
    /// 규칙 파일이 없으면 예시 셋, 스키마는 늘 최신, (asmdef 이름을 주면) 생성물 폴더에 asmdef가 없을 때 규칙 asmdef. 생성은 하지 않는다.
    /// </summary>
    internal static class RuleFolderSetup
    {
        /// <returns>사람에게 보일 줄들 ("경로: 만들었습니다" 등).</returns>
        public static List<string> Prepare(string folder, string outputPath, string? asmdef)
        {
            var lines = new List<string>();
            Directory.CreateDirectory(folder);
            if (Directory.GetFiles(folder, RuleSet.FilePattern, SearchOption.TopDirectoryOnly).Length == 0)
            {
                foreach (var preset in Presets.Files())
                {
                    var path = Path.Combine(folder, preset.Key);
                    OutputFile.WriteIfChanged(path, preset.Value);
                    lines.Add($"{path}: 만들었습니다");
                }
            }
            var schema = Path.Combine(folder, IrSchema.FileName);
            lines.Add(OutputFile.WriteIfChanged(schema, IrSchema.Generate()) ? $"{schema}: 만들었습니다" : $"{schema}: 바뀐 것 없음");

            if (asmdef != null)
            {
                // 한 폴더에 asmdef가 둘이면 Unity가 컴파일을 막는다 — 이미 있으면 그대로 둔다
                var outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
                var existing = Directory.Exists(outputFolder) ? Directory.GetFiles(outputFolder, "*.asmdef") : new string[0];
                if (existing.Length == 0)
                {
                    var path = Path.Combine(outputFolder, asmdef + ".asmdef");
                    OutputFile.WriteIfChanged(path, RulesAsmdef(asmdef));
                    lines.Add($"{path}: 만들었습니다");
                }
                else if (existing.All(p => Path.GetFileNameWithoutExtension(p) != asmdef))
                {
                    lines.Add($"{outputFolder}에 이미 asmdef({Path.GetFileName(existing[0])})가 있어 {asmdef}.asmdef를 만들지 않았습니다");
                }
            }
            return lines;
        }

        /// <summary>규칙 asmdef — HBLint.Attributes 참조, Auto Referenced(그래서 Assembly-CSharp는 자동 검사), 엔진 참조 없음.</summary>
        public static string RulesAsmdef(string name) => JsonWriter.Write(new JsonObject()
            .Add("name", new JsonString(name))
            .Add("references", new JsonArray().Add(new JsonString("HBLint.Attributes")))
            .Add("autoReferenced", new JsonBool(true))
            .Add("noEngineReferences", new JsonBool(true)));
    }
}
