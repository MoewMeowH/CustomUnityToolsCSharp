#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HBLint.Generator;
using UnityEditor;
using UnityEngine;

namespace HBLint.Editor
{
    /// <summary>
    /// rules.json → LintRules.g.cs · rules.schema.json. 오류가 있으면 콘솔에 줄 · 칸과 함께 남기고 생성물은 그대로 둔다.
    /// 경로는 프로젝트 폴더 기준이고, Assets/ · Packages/ 아래 파일만 AssetDatabase로 다시 가져온다.
    /// </summary>
    public static class LintGenerator
    {
        public const string AttributesAssemblyName = "HBLint.Attributes";

        /// <summary>불려 있는 HBLint.Attributes에서 규칙 종류를 읽는다. 실패하면 콘솔에 남기고 null.</summary>
        public static RuleCatalog? LoadCatalog()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AttributesAssemblyName);
            if (assembly == null)
            {
                Debug.LogError($"[HB Lint] {AttributesAssemblyName} 어셈블리가 불려 있지 않습니다 — com.hb.lint 패키지의 Runtime이 컴파일됐는지 확인하세요.");
                return null;
            }
            try
            {
                return RuleCatalog.FromAssembly(assembly);
            }
            catch (RuleCatalogException e)
            {
                Debug.LogError("[HB Lint] 규칙 어트리뷰트 오류: " + e.Message);
                return null;
            }
        }

        /// <summary>설정의 경로로 생성. 오류가 하나라도 있으면 false.</summary>
        public static bool Generate()
        {
            var settings = HBLintSettings.instance;
            return Generate(settings.RulesPath, settings.OutputPath, settings.SchemaPath);
        }

        public static bool Generate(string rulesPath, string outputPath, string schemaPath)
        {
            var catalog = LoadCatalog();
            if (catalog == null)
                return false;
            if (!File.Exists(rulesPath))
            {
                Debug.LogWarning($"[HB Lint] {rulesPath}가 없습니다 — Tools/HB Lint/설치로 만드세요.");
                return false;
            }

            var json = File.ReadAllText(rulesPath);
            var result = RulesJson.Generate(json, catalog);
            var errors = result.Success ? CheckTypeNames(json, catalog) : result.Errors;
            if (errors.Count > 0)
            {
                LogErrors(rulesPath, errors);
                Debug.LogError($"[HB Lint] 오류 {errors.Count}개 — {outputPath}는 바꾸지 않았습니다.");
                return false;
            }

            WriteAndImport(outputPath, result.Text!);
            WriteAndImport(schemaPath, RulesSchema.Generate(catalog));
            return true;
        }

        /// <summary>[TypeName] · [MemberName] 칸의 타입 · 멤버가 불려 있는 어셈블리에 있는가 (requires의 어셈블리가 불려 있을 때만 본다).</summary>
        internal static IReadOnlyList<RulesError> CheckTypeNames(string json, RuleCatalog catalog)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var names = new HashSet<string>(assemblies.Select(a => a.GetName().Name), StringComparer.Ordinal);
            return TypeNameCheck.Check(json, catalog,
                typeName => FindType(assemblies, typeName) != null,
                names.Contains,
                (typeName, memberName) => MemberOwner(FindType(assemblies, typeName), memberName));
        }

        private const System.Reflection.BindingFlags DeclaredMembers =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.DeclaredOnly;

        /// <summary>
        /// 그 이름의 메서드 · 속성(인덱서 제외) · 필드를 선언한 타입 — 그 타입 자신, 없으면 가장 가까운 부모 (분석기는 선언된 타입에서만 찾는다).
        /// </summary>
        private static string? MemberOwner(Type? type, string memberName)
        {
            const System.Reflection.MemberTypes kinds =
                System.Reflection.MemberTypes.Method | System.Reflection.MemberTypes.Property | System.Reflection.MemberTypes.Field;
            for (var current = type; current != null; current = current.BaseType)
            {
                var declared = current.GetMember(memberName, kinds, DeclaredMembers)
                    .Any(m => !(m is System.Reflection.PropertyInfo property && property.GetIndexParameters().Length > 0));
                if (declared)
                    return current.IsGenericType ? current.GetGenericTypeDefinition().FullName : current.FullName;
            }
            return null;
        }

        private static Type? FindType(System.Reflection.Assembly[] assemblies, string typeName)
        {
            foreach (var assembly in assemblies)
            {
                var type = GetTypeOrNull(assembly, typeName);
                if (type != null)
                    return type;
            }
            return null;
        }

        /// <summary>이름 문법이 틀리면 throwOnError: false여도 런타임에 따라 ArgumentException이 난다 — "없음"으로 본다 (rules.json 입력).</summary>
        private static Type? GetTypeOrNull(System.Reflection.Assembly assembly, string typeName)
        {
            try
            {
                return assembly.GetType(typeName, throwOnError: false);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>오류마다 한 줄. 경로 부분은 누르면 그 줄로 가는 콘솔 링크.</summary>
        public static void LogErrors(string rulesPath, IEnumerable<RulesError> errors)
        {
            foreach (var e in errors)
            {
                var id = e.RuleId == null ? "" : $" [{e.RuleId}]";
                Debug.LogError($"[HB Lint] <a href=\"{rulesPath}\" line=\"{e.Line}\">{rulesPath}({e.Line},{e.Column})</a>{id} {e.Message}");
            }
        }

        /// <summary>내용이 바뀌었을 때만 쓰고, 에셋이면 다시 가져온다.</summary>
        internal static void WriteAndImport(string path, string text)
        {
            if (OutputFile.WriteIfChanged(path, text) && IsAssetPath(path))
                AssetDatabase.ImportAsset(path);
        }

        internal static bool IsAssetPath(string path) =>
            path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal);
    }
}
