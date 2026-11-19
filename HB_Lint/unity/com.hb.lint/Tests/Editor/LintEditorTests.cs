#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using HBLint.Generator;
using HBLint.Generator.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HBLint.Editor.Tests
{
    /// <summary>파일은 프로젝트 Temp/ 아래에만 쓴다 — Assets/가 아니라 가져오기 · 컴파일이 일어나지 않는다.</summary>
    public sealed class LintEditorTests
    {
        private string _dir = "";

        private string RulesPath => _dir + "/rules.json";
        private string OutputPath => _dir + "/Rules/LintRules.g.cs";
        private string SchemaPath => _dir + "/rules.schema.json";
        private string AsmdefPath => _dir + "/Rules/CLL.Test.asmdef";

        [SetUp]
        public void SetUp()
        {
            _dir = "Temp/HBLintTests/" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        [Test]
        public void 불려있는_HBLint_Attributes에서_규칙_종류를_읽음()
        {
            var catalog = LintGenerator.LoadCatalog();

            Assert.IsNotNull(catalog);
            Assert.IsNotNull(catalog!.Find("BanNullOperator"));
        }

        [Test]
        public void 설치는_없는_것만_만들고_다시_눌러도_rules_json을_덮지_않음()
        {
            LintInstaller.Install(RulesPath, OutputPath, SchemaPath, "CLL.Test");

            Assert.IsTrue(File.Exists(RulesPath));
            Assert.IsTrue(File.Exists(AsmdefPath));
            Assert.IsTrue(File.Exists(SchemaPath));
            StringAssert.Contains("global::HBLint.BanNullOperatorRule(\"UnityEngine.Object\"", File.ReadAllText(OutputPath));

            var edited = File.ReadAllText(RulesPath).Replace("\"Warning\"", "\"Error\"");
            File.WriteAllText(RulesPath, edited);
            LintInstaller.Install(RulesPath, OutputPath, SchemaPath, "CLL.Test");

            Assert.AreEqual(edited, File.ReadAllText(RulesPath));
            StringAssert.Contains("global::HBLint.LintSeverity.Error", File.ReadAllText(OutputPath));
        }

        [Test]
        public void 오류면_생성물을_그대로_두고_콘솔에_줄과_함께_남김()
        {
            LintInstaller.Install(RulesPath, OutputPath, SchemaPath, "CLL.Test");
            var before = File.ReadAllText(OutputPath);
            File.WriteAllText(RulesPath, "{\"rules\":[{\"id\":\"broken\",\"kind\":\"BanNullOperator\",\"type\":\"T\"}]}");

            LogAssert.Expect(LogType.Error, new Regex(@"\[HB Lint\] <a href=.*\(1,\d+\)</a> \[broken\] .*'message'"));
            LogAssert.Expect(LogType.Error, new Regex(@"\[HB Lint\] 오류 1개"));
            var ok = LintGenerator.Generate(RulesPath, OutputPath, SchemaPath);

            Assert.IsFalse(ok);
            Assert.AreEqual(before, File.ReadAllText(OutputPath));
        }

        [Test]
        public void 규칙의_타입이_없으면_생성하지_않고_줄과_함께_남김()
        {
            LintInstaller.Install(RulesPath, OutputPath, SchemaPath, "CLL.Test");
            var before = File.ReadAllText(OutputPath);
            File.WriteAllText(RulesPath, File.ReadAllText(RulesPath).Replace("\"UnityEngine.Object\"", "\"UnityEngine.Objekt\""));

            LogAssert.Expect(LogType.Error, new Regex(@"\[HB Lint\] .*\[unity-null-operator\] .*UnityEngine\.Objekt"));
            LogAssert.Expect(LogType.Error, new Regex(@"\[HB Lint\] 오류 1개"));
            var ok = LintGenerator.Generate(RulesPath, OutputPath, SchemaPath);

            Assert.IsFalse(ok);
            Assert.AreEqual(before, File.ReadAllText(OutputPath));
        }

        [Test]
        public void asmdef_이름을_바꿔_다시_설치해도_asmdef가_둘이_되지_않음()
        {
            LintInstaller.Install(RulesPath, OutputPath, SchemaPath, "CLL.Test");

            LogAssert.Expect(LogType.Warning, new Regex(@"\[HB Lint\] .*CLL\.Test\.asmdef.*Game\.Renamed\.asmdef를 만들지 않았습니다"));
            LintInstaller.Install(RulesPath, OutputPath, SchemaPath, "Game.Renamed");

            Assert.AreEqual(1, Directory.GetFiles(_dir + "/Rules", "*.asmdef").Length);
        }

        [Test]
        public void 규칙_asmdef는_어트리뷰트를_참조하고_자동_참조됨()
        {
            var asmdef = (JsonObject)JsonReader.Parse(LintInstaller.RulesAsmdef("CLL.LintRules"));

            Assert.AreEqual("CLL.LintRules", ((JsonString)asmdef.Get("name")!).Value);
            Assert.AreEqual("HBLint.Attributes", ((JsonString)((JsonArray)asmdef.Get("references")!).Items[0]).Value);
            Assert.IsTrue(((JsonBool)asmdef.Get("autoReferenced")!).Value);
        }

        [Test]
        public void 예시_규칙은_생성기를_오류_없이_통과()
        {
            var catalog = LintGenerator.LoadCatalog()!;

            var result = RulesJson.Generate(LintInstaller.ExampleRules().ToJson(catalog), catalog);

            Assert.IsTrue(result.Success, string.Join("\n", result.Errors));
        }
    }
}
