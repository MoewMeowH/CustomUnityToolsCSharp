#nullable enable
using System.Collections.Generic;
using HBLint.Core.Json;

namespace HBLint.Core.Generation
{
    /// <summary>예시 규칙 셋 — hblint-gen init이 빈 규칙 폴더에 넣고, samples/의 규칙 파일도 이것과 같다.</summary>
    internal static class Presets
    {
        public static IReadOnlyList<KeyValuePair<string, string>> Files()
        {
            return new[]
            {
                File("unity-null-operator", Rule("CLL0001",
                    "Unity 오브젝트에 null 연산자 금지 — 파괴된 Unity 오브젝트는 C# null이 아니라서 ?. · ?? · ??= · is null이 통과시킨다.",
                    Strings("UnityEngine.CoreModule"), null,
                    new JsonObject()
                        .Add("nullOperators", new JsonObject().Add("kind", S("sites.nullOperator")).Add("from", S("scope")))
                        .Add("unityObject", new JsonObject().Add("kind", S("filter.targetType")).Add("from", S("nullOperators")).Add("type", S("UnityEngine.Object")))
                        .Add("report", new JsonObject().Add("kind", S("report")).Add("from", S("unityObject"))
                            .Add("message", S("Unity 오브젝트는 ?. · ?? · ??= · is null이 파괴된 오브젝트를 null로 보지 않습니다. '== null' 또는 'if (obj)'로 확인하세요."))))),
                File("no-getcomponent-in-update", Rule("CLL0002",
                    "Update 계열에서 GetComponent 금지 — 매 프레임 GetComponent는 비싸다. 같은 클래스 헬퍼도 한 단계 따라간다.",
                    Strings("UnityEngine.CoreModule"), null,
                    new JsonObject()
                        .Add("monoBehaviour", new JsonObject().Add("kind", S("types.derived")).Add("from", S("scope")).Add("type", S("UnityEngine.MonoBehaviour")))
                        .Add("updates", new JsonObject().Add("kind", S("methods.named")).Add("from", S("monoBehaviour")).Add("names", Strings("Update", "LateUpdate", "FixedUpdate")))
                        .Add("helpers", new JsonObject().Add("kind", S("methods.follow")).Add("from", S("updates")).Add("depth", new JsonNumber("1")))
                        .Add("getComponent", new JsonObject().Add("kind", S("sites.member")).Add("from", S("helpers"))
                            .Add("members", Strings("UnityEngine.Component.GetComponent", "UnityEngine.GameObject.GetComponent")))
                        .Add("report", new JsonObject().Add("kind", S("report")).Add("from", S("getComponent"))
                            .Add("message", S("Update 계열에서 GetComponent를 부르지 마세요 — Awake에서 필드에 캐싱하세요."))))),
                File("editor-only-code", Rule("CLL0003",
                    "에디터 전용 코드 — UnityEditor API와 #if UNITY_EDITOR 안에 선언된 코드는 플레이어 빌드에 없다.",
                    null, Strings("Assembly-CSharp-Editor*", "*.Editor", "*.Editor.*", "*.Tests", "*.Tests.*"),
                    new JsonObject()
                        .Add("unityEditor", new JsonObject().Add("kind", S("sites.namespace")).Add("from", S("scope")).Add("namespaces", Strings("UnityEditor")))
                        .Add("editorOnly", new JsonObject().Add("kind", S("sites.declaredInDefine")).Add("from", S("scope")).Add("define", S("UNITY_EDITOR")))
                        .Add("both", new JsonObject().Add("kind", S("sites.union")).Add("from", Strings("unityEditor", "editorOnly")))
                        .Add("outsideGuard", new JsonObject().Add("kind", S("filter.define")).Add("from", S("both")).Add("define", S("UNITY_EDITOR")).Add("where", S("outside")))
                        .Add("report", new JsonObject().Add("kind", S("report")).Add("from", S("outsideGuard"))
                            .Add("message", S("에디터 전용 코드입니다 — #if UNITY_EDITOR 안에서만 쓰세요. 플레이어 빌드가 깨집니다."))))),
            };
        }

        private static KeyValuePair<string, string> File(string name, JsonObject rule) =>
            new KeyValuePair<string, string>(name + ".hblint.json", JsonWriter.Write(rule));

        private static JsonObject Rule(string id, string title, JsonArray? requires, JsonArray? ignoreAssemblies, JsonObject nodes)
        {
            var rule = new JsonObject()
                .Add("$schema", S("./" + IrSchema.FileName))
                .Add("version", new JsonNumber("1"))
                .Add("id", S(id))
                .Add("title", S(title))
                .Add("severity", S("warning"));
            if (requires != null)
                rule.Add("requires", requires);
            if (ignoreAssemblies != null)
                rule.Add("ignoreAssemblies", ignoreAssemblies);
            return rule.Add("nodes", nodes);
        }

        private static JsonArray Strings(params string[] values)
        {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add(S(value));
            return array;
        }

        private static JsonString S(string value) => new JsonString(value);
    }
}
