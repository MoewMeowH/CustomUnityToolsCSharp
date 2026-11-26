using HBLint.Core.Generation;

namespace HBLint.Core.Tests;

/// <summary>samples/ 규칙 파일 → 생성 → 규칙 어셈블리 컴파일 → 분석기가 대상 코드에서 진단을 낸다.</summary>
public class EndToEndTests
{
    private const string Unity = """
        namespace UnityEngine
        {
            public class Object { }
            public class Component : Object { public T GetComponent<T>() => default; }
            public class GameObject : Object { public T GetComponent<T>() => default; }
            public class Behaviour : Component { }
            public class MonoBehaviour : Behaviour { }
            public class Transform : Component { }
        }
        """;

    private const string Game = """
        public class Foo : UnityEngine.MonoBehaviour
        {
            UnityEngine.Transform t;
            void Update() { var a = t?.GetHashCode(); Helper(); }
            void Helper() { GetComponent<UnityEngine.Transform>(); }
        }
        """;

    private static string Samples => Path.Combine(TestCompiler.RepoRoot(), "samples");

    [Fact]
    public void 예시_규칙으로_만든_생성물이_분석기에_적용됨()
    {
        var set = RuleSet.Build(RuleSet.ReadFolder(Samples));
        Assert.Empty(set.Errors);

        var unity = TestCompiler.Emit(TestCompiler.Compile("UnityEngine.CoreModule", Unity));
        var rules = TestCompiler.Emit(TestCompiler.Compile("Game.LintRules", set.Text!, TestCompiler.Attributes));
        var diagnostics = TestCompiler.RunAnalyzer(TestCompiler.Compile("Game", Game, unity, TestCompiler.Attributes, rules));

        Assert.Equal(new[] { "CLL0001", "CLL0002" }, diagnostics.Select(d => d.Id).OrderBy(i => i));
        Assert.Contains("'== null'", diagnostics.Single(d => d.Id == "CLL0001").GetMessage());
        Assert.EndsWith("(Update → Helper)", diagnostics.Single(d => d.Id == "CLL0002").GetMessage());
    }

    private const string MoreUnity = """
        namespace UnityEngine
        {
            public class Object { }
            public class Component : Object { public Transform transform => null; public string tag { get => null; set { } } }
            public class GameObject : Object { public Transform transform => null; public string tag { get => null; set { } } }
            public class Behaviour : Component { }
            public class MonoBehaviour : Behaviour { }
            public class Transform : Component { public Vector3 position { get => default; set { } } public Quaternion rotation { get => default; set { } } }
            public struct Vector3 { public static Vector3 zero => default; }
            public struct Quaternion { public static Quaternion identity => default; }
            public class YieldInstruction { }
            public sealed class WaitForSeconds : YieldInstruction { public WaitForSeconds(float seconds) { } }
        }
        """;

    private const string MoreGame = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;
        using UnityEngine;
        public class Foo : MonoBehaviour
        {
            List<int> list = new List<int>();
            int hp; string label;
            void Log(object o) { }
            void Update()
            {
                var a = new List<int>();
                Log(hp);
                var b = list.Any();
                label = "hp " + hp;
                Action c = () => hp++;
                if (tag == "Player") { }
                transform.position = Vector3.zero;
                transform.rotation = Quaternion.identity;
                var d = transform; var e = transform;
            }
            IEnumerator Co()
            {
                while (true) { yield return new WaitForSeconds(1); }
            }
        }
        """;

    [Fact]
    public void 새_규칙_예시_셋이_각각_걸림()
    {
        var set = RuleSet.Build(RuleSet.ReadFolder(Path.Combine(Samples, "more")));
        Assert.Empty(set.Errors);
        Assert.Empty(set.Warnings);

        var unity = TestCompiler.Emit(TestCompiler.Compile("UnityEngine.CoreModule", MoreUnity));
        var rules = TestCompiler.Emit(TestCompiler.Compile("Game.LintRules", set.Text!, TestCompiler.Attributes));
        var diagnostics = TestCompiler.RunAnalyzer(TestCompiler.Compile("Game", MoreGame, unity, TestCompiler.Attributes, rules));

        var found = diagnostics.Select(d => d.Id + "@" + (d.Location.GetLineSpan().StartLinePosition.Line + 1)).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            "CLL0004@13", "CLL0005@14", "CLL0006@15", "CLL0007@16", "CLL0008@17",
            "CLL0009@25", "CLL0010@18", "CLL0011@19", "CLL0012@19",
        }, found);
    }

    private const string ConventionUnity = """
        namespace UnityEngine
        {
            public class Object { }
            public static class Debug
            {
                public static void Log(object message) { }
                public static void LogWarning(object message) { }
                public static void LogError(object message) { }
                public static void LogException(System.Exception exception) { }
                public static void LogFormat(string format, params object[] args) { }
                public static void LogWarningFormat(string format, params object[] args) { }
                public static void LogErrorFormat(string format, params object[] args) { }
                public static void LogAssertion(object message) { }
                public static void LogAssertionFormat(string format, params object[] args) { }
                public static void DrawLine(int a, int b) { }
            }
        }
        """;

    [Fact]
    public void 클라_규약_예시_셋이_각각_걸림()
    {
        // 코드 규약 규칙은 각 프로젝트 규칙 폴더에 두고, 여기에는 시험용 사본만 둔다
        var set = RuleSet.Build(RuleSet.ReadFolder(Path.Combine(TestCompiler.RepoRoot(), "tests", "HBLint.Core.Tests", "TestData", "conventions")));
        Assert.Empty(set.Errors);
        Assert.Empty(set.Warnings);

        var unity = TestCompiler.Emit(TestCompiler.Compile("UnityEngine.CoreModule", ConventionUnity));
        var rules = TestCompiler.Emit(TestCompiler.Compile("Game.LintRules", set.Text!, TestCompiler.Attributes));
        var files = new (string Path, string Source)[]
        {
            ("Assets/Scripts/Common/CommonEnum.cs", """
                public class CommonEnum
                {
                    public enum EState { A }
                }
                """),
            ("Assets/Scripts/Player.cs", """
                using UnityEngine;
                public static class PlayerExt
                {
                    public static int E_Twice(this int x) => x * 2;
                    public static int Twice(this int x) => x * 2;
                    public static int Helper(int x) => x;
                }
                public class Player
                {
                    public enum EMove { A }
                    private enum Inner { A }
                #if UNITY_EDITOR
                    public enum EEditorOnly { A }
                #endif
                    void M()
                    {
                        Debug.Log("x");
                        Debug.LogErrorFormat("{0}", 1);
                        Debug.DrawLine(0, 1);
                    }
                }
                """),
            ("Assets/Editor/Tool.cs", "public enum ETool { A }"),
        };
        // 에디터 컴파일처럼 UNITY_EDITOR를 넣는다 — 없으면 #if UNITY_EDITOR 안 선언은 파싱되지도 않아 디파인 예외를 시험하지 못한다
        var diagnostics = TestCompiler.RunAnalyzer(TestCompiler.Compile("Game", files, new[] { "UNITY_EDITOR" }, unity, TestCompiler.Attributes, rules));

        var found = diagnostics
            .Select(d => d.Id + " " + d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))
            .OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            "CLL0013 Log", "CLL0013 LogErrorFormat",
            "CLL0014 Twice",
            "CLL0015 Inner",
            "CLL0016 EMove",
        }, found);
    }

    [Fact]
    public void 저장소의_예시가_프리셋_생성물_스키마와_같음()
    {
        foreach (var preset in Presets.Files())
        {
            Assert.True(OutputFile.Matches(Path.Combine(Samples, preset.Key), preset.Value),
                $"samples/{preset.Key}가 프리셋과 다릅니다 — hblint-gen init으로 빈 폴더에 만들어 비교하세요");
        }
        Assert.Equal(Presets.Files().Count, Directory.GetFiles(Samples, RuleSet.FilePattern).Length);

        var set = RuleSet.Build(RuleSet.ReadFolder(Samples));
        Assert.True(OutputFile.Matches(Path.Combine(Samples, "LintRules.g.cs"), set.Text!),
            "samples/LintRules.g.cs가 규칙 파일과 다릅니다 — hblint-gen generate samples samples/LintRules.g.cs로 다시 만드세요");
        Assert.True(OutputFile.Matches(Path.Combine(Samples, IrSchema.FileName), IrSchema.Generate()),
            "samples/hblint.schema.json이 노드 카탈로그와 다릅니다 — hblint-gen schema로 다시 만드세요");
    }
}
