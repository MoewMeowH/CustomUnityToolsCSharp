#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HBLint.Core.Ir;
using HBLint.Core.Json;

namespace HBLint.Core.Generation
{
    /// <summary>
    /// 규칙 폴더 공통 설정 hblint.settings.json — 모든 규칙에 더하는 제외(ignoreAssemblies · ignorePaths).
    /// 분석기는 생성물만 읽으므로 생성 때 규칙마다 런타임 JSON에 합친다 (RuleIr.ToRuntimeJson). 규칙은 includeAssemblies · includePaths로 일부를 다시 넣는다.
    /// 파일 이름이 *.hblint.json이 아니라 규칙으로 읽히지 않는다.
    /// </summary>
    internal sealed class RuleSettings
    {
        public const string FileName = "hblint.settings.json";

        private static readonly string[] Keys = { "$schema", "version", "ignoreAssemblies", "ignorePaths" };

        public static readonly RuleSettings Empty = new RuleSettings(new string[0], new string[0]);

        public RuleSettings(IReadOnlyList<string> ignoreAssemblies, IReadOnlyList<string> ignorePaths)
        {
            IgnoreAssemblies = ignoreAssemblies;
            IgnorePaths = ignorePaths;
        }

        public IReadOnlyList<string> IgnoreAssemblies { get; }

        public IReadOnlyList<string> IgnorePaths { get; }

        public bool IsEmpty => IgnoreAssemblies.Count == 0 && IgnorePaths.Count == 0;

        /// <summary>규칙 폴더의 설정 — 없으면 Empty. 오류는 "경로(줄,칸): 내용" (오류가 있으면 Settings는 null).</summary>
        public static (RuleSettings? Settings, IReadOnlyList<string> Errors) ReadFolder(string folder)
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
                return (Empty, new string[0]);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // 다른 편집기 · 백신이 잠갔거나 권한 — 설정 오류로 보이고 생성은 막힌다
                return (null, new[] { $"{path}: 읽을 수 없습니다 — {e.Message}" });
            }
            var (settings, errors) = Parse(text);
            return (settings, errors.Select(e => path + e).ToList());
        }

        /// <summary>읽기 · 검증 (오류 문자열은 "(줄,칸): 내용").</summary>
        public static (RuleSettings? Settings, IReadOnlyList<IrError> Errors) Parse(string text)
        {
            var errors = new List<IrError>();
            JsonValue root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonParseException e)
            {
                errors.Add(new IrError(e.Message, e.Line, e.Column));
                return (null, errors);
            }
            if (!(root is JsonObject obj))
            {
                errors.Add(Error(root, "설정은 객체여야 합니다"));
                return (null, errors);
            }
            foreach (var member in obj.Members)
            {
                if (Array.IndexOf(Keys, member.Key) < 0)
                    errors.Add(Error(member.Value, $"모르는 칸 '{member.Key}' — {string.Join(" · ", Keys.Skip(1))}만 씁니다"));
            }
            var version = obj.Get("version");
            if (version == null)
                errors.Add(Error(obj, "'version'이 없습니다"));
            else if (!(version is JsonNumber number) || number.Raw != RuleIr.CurrentVersion.ToString(CultureInfo.InvariantCulture))
                errors.Add(Error(version, $"'version'은 {RuleIr.CurrentVersion}이어야 합니다"));

            var assemblies = Strings(obj, "ignoreAssemblies", errors);
            var paths = Strings(obj, "ignorePaths", errors);
            if (obj.Get("ignorePaths") is JsonArray array)
            {
                foreach (var item in array.Items.OfType<JsonString>())
                {
                    PathPattern.Parse(item.Value, out var error);
                    if (error != null)
                        errors.Add(Error(item, $"ignorePaths: '{item.Value}' — {error}"));
                }
            }
            return errors.Count > 0 ? (null, errors) : (new RuleSettings(assemblies, paths), errors);
        }

        /// <summary>CLI · 앱이 같은 모양으로 쓴다 (version · ignoreAssemblies · ignorePaths 순, LF).</summary>
        public string ToJson()
        {
            // $schema는 넣지 않는다 — hblint.schema.json은 규칙 파일 모양이라 설정 파일에 대면 오류로 보인다
            var obj = new JsonObject()
                .Add("version", new JsonNumber(RuleIr.CurrentVersion.ToString(CultureInfo.InvariantCulture)))
                .Add("ignoreAssemblies", JsonStrings(IgnoreAssemblies))
                .Add("ignorePaths", JsonStrings(IgnorePaths));
            return JsonWriter.Write(obj);
        }

        /// <summary>
        /// 규칙이 다시 넣기(include…)를 썼는데 그 종류 제외가 공통 · 규칙 어디에도 없으면 경고 — 다시 넣을 것이 없다.
        /// </summary>
        public IEnumerable<string> Warnings(RuleIr rule)
        {
            if (rule.IncludeAssemblies.Length > 0 && IgnoreAssemblies.Count == 0 && rule.IgnoreAssemblies.Length == 0)
                yield return "includeAssemblies를 적었지만 공통 설정 · 이 규칙 어디에도 ignoreAssemblies가 없어 다시 넣을 것이 없습니다";
            if (rule.IncludePaths.Length > 0 && IgnorePaths.Count == 0 && rule.IgnorePaths.Length == 0)
                yield return "includePaths를 적었지만 공통 설정 · 이 규칙 어디에도 ignorePaths가 없어 다시 넣을 것이 없습니다";
        }

        private static string[] Strings(JsonObject obj, string name, List<IrError> errors)
        {
            var value = obj.Get(name);
            if (value == null)
                return new string[0];
            if (!(value is JsonArray array) || array.Items.Any(i => !(i is JsonString)))
            {
                errors.Add(Error(value, $"'{name}'은(는) 문자열 배열이어야 합니다"));
                return new string[0];
            }
            var items = array.Items.Cast<JsonString>().Select(s => s.Value).ToArray();
            if (items.Any(s => s.Trim().Length == 0))
                errors.Add(Error(value, $"'{name}'에 빈 항목이 있습니다"));
            return items;
        }

        private static JsonArray JsonStrings(IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (var value in values)
                array.Add(new JsonString(value));
            return array;
        }

        private static IrError Error(JsonValue at, string message) => new IrError(message, at.Line, at.Column);
    }
}
