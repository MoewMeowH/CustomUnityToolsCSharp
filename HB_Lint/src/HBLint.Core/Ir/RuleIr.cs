#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HBLint.Core.Json;

namespace HBLint.Core.Ir
{
    /// <summary>
    /// 진단 id 묶음. Roslyn 분석기는 미리 선언한 id로만 보고할 수 있어서 규칙 id는 이 묶음 안에서 고른다.
    /// 지운 규칙의 id는 다시 쓰지 않는다 (남은 #pragma가 새 규칙을 막지 않게).
    /// </summary>
    internal static class RuleIds
    {
        public const string Prefix = "CLL";
        public const int First = 1;
        public const int Last = 200;

        public static string Format(int number) => Prefix + number.ToString("0000", CultureInfo.InvariantCulture);

        public static bool IsRuleId(string id)
        {
            if (id.Length != Prefix.Length + 4 || !id.StartsWith(Prefix, StringComparison.Ordinal))
                return false;
            return int.TryParse(id.Substring(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number >= First && number <= Last;
        }
    }

    internal sealed class IrError
    {
        public IrError(string message, int line, int column, bool isWarning = false, string? node = null)
        {
            Message = message;
            Line = line;
            Column = column;
            IsWarning = isWarning;
            Node = node;
        }

        /// <summary>그 노드에서 난 오류면 노드 이름 (편집 앱이 노드에 표시).</summary>
        public string? Node { get; }

        public string Message { get; }

        public int Line { get; }

        public int Column { get; }

        public bool IsWarning { get; }

        public IrError WithNode(string node) => new IrError(Message, Line, Column, IsWarning, node);

        public override string ToString() => Line > 0 ? $"({Line},{Column}): {Message}" : ": " + Message;
    }

    /// <summary>검증을 통과한 노드 하나. 입력은 노드 이름(또는 "scope")으로 풀어 둔다.</summary>
    internal sealed class IrNode
    {
        private readonly JsonObject _json;

        public IrNode(string name, NodeSpec spec, JsonObject json, Dictionary<string, string[]> inputs)
        {
            Name = name;
            Spec = spec;
            _json = json;
            Inputs = inputs;
        }

        public string Name { get; }

        public NodeSpec Spec { get; }

        public string Kind => Spec.Kind;

        /// <summary>입력 이름 → 받는 노드 이름들.</summary>
        public IReadOnlyDictionary<string, string[]> Inputs { get; }

        public IEnumerable<string> AllInputs => Inputs.Values.SelectMany(v => v);

        public string? GetString(string field) => (_json.Get(field) as JsonString)?.Value;

        public string[] GetStrings(string field) =>
            _json.Get(field) is JsonArray array ? array.Items.OfType<JsonString>().Select(s => s.Value).ToArray() : new string[0];

        public int? GetInt(string field) =>
            _json.Get(field) is JsonNumber number && int.TryParse(number.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : (int?)null;

        public bool? GetBool(string field) => (_json.Get(field) as JsonBool)?.Value;
    }

    internal sealed class RuleParseResult
    {
        public RuleParseResult(RuleIr? rule, IReadOnlyList<IrError> errors, string? id)
        {
            Rule = rule;
            Errors = errors;
            Id = id;
        }

        /// <summary>읽은 id (검증에 실패해도 문자열이면 있음 — 오류 메시지용).</summary>
        public string? Id { get; }

        /// <summary>오류가 없을 때만 있다 (경고는 있어도 됨).</summary>
        public RuleIr? Rule { get; }

        public IReadOnlyList<IrError> Errors { get; }

        public bool Success => Rule != null;
    }

    /// <summary>
    /// 규칙 IR — 규칙 하나 = 그래프 하나. 머리(id · 심각도 · 적용 범위)와 노드들.
    /// 읽기와 검증을 함께 한다: CLI · 앱 · AI가 만든 JSON도 분석기도 이 검사를 거친다.
    /// </summary>
    internal sealed class RuleIr
    {
        public const int CurrentVersion = 1;

        /// <summary>분석기 안에서 그래프를 재귀로 훑으므로 크기를 막는다 (스택 넘침은 컴파일러 · IDE 프로세스를 죽인다).</summary>
        public const int MaxNodes = 256;
        public static readonly string[] Severities = { "error", "warning", "info" };

        /// <summary>편집용 칸 — 분석기에 넘기는 IR에서는 뺀다.</summary>
        private static readonly string[] EditorOnlyKeys = { "$schema", "layout" };

        private static readonly string[] HeadKeys = { "$schema", "version", "id", "title", "severity", "requires", "ignoreAssemblies", "nodes", "layout" };
        private static readonly Regex NodeName = new Regex("^[A-Za-z0-9_.-]+$");

        private readonly JsonObject _json;

        private RuleIr(JsonObject json, string id, string title, string severity, string[] requires, string[] ignoreAssemblies, List<IrNode> nodes)
        {
            _json = json;
            Id = id;
            Title = title;
            Severity = severity;
            Requires = requires;
            IgnoreAssemblies = ignoreAssemblies;
            Nodes = nodes;
            Report = nodes.Single(n => n.Kind == NodeCatalog.ReportKind);
        }

        public string Id { get; }

        public string Title { get; }

        /// <summary>error · warning · info.</summary>
        public string Severity { get; }

        /// <summary>검사 중인 어셈블리가 이것들을 모두 참조할 때만 규칙을 쓴다.</summary>
        public string[] Requires { get; }

        /// <summary>* 와일드카드 어셈블리 이름 패턴 — 맞으면 검사하지 않는다.</summary>
        public string[] IgnoreAssemblies { get; }

        public IReadOnlyList<IrNode> Nodes { get; }

        public IrNode Report { get; }

        public IrNode? Find(string name) => Nodes.FirstOrDefault(n => n.Name == name);

        /// <summary>분석기에 넘기는 한 줄 JSON ($schema · layout 뺌).</summary>
        public string ToRuntimeJson()
        {
            var copy = new JsonObject();
            foreach (var member in _json.Members)
            {
                if (Array.IndexOf(EditorOnlyKeys, member.Key) < 0)
                    copy.Add(member.Key, member.Value);
            }
            return JsonWriter.WriteCompact(copy);
        }

        public static RuleParseResult Parse(string text)
        {
            var errors = new List<IrError>();
            string? id = null;
            JsonValue root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonParseException e)
            {
                errors.Add(new IrError(e.Message, e.Line, e.Column));
                return new RuleParseResult(null, errors, id);
            }

            if (!(root is JsonObject obj))
            {
                errors.Add(Error(root, "규칙은 객체여야 합니다"));
                return new RuleParseResult(null, errors, id);
            }

            foreach (var member in obj.Members)
            {
                if (Array.IndexOf(HeadKeys, member.Key) < 0)
                    errors.Add(Error(member.Value, $"모르는 칸 '{member.Key}'"));
            }

            var version = obj.Get("version");
            if (version == null)
                errors.Add(Error(obj, "'version'이 없습니다"));
            else if (!(version is JsonNumber number) || number.Raw != CurrentVersion.ToString(CultureInfo.InvariantCulture))
                errors.Add(Error(version, $"'version'은 {CurrentVersion}이어야 합니다"));

            id = RequiredString(obj, "id", errors);
            if (id != null && !RuleIds.IsRuleId(id))
                errors.Add(Error(obj.Get("id")!, $"id '{id}'는 {RuleIds.Format(RuleIds.First)}~{RuleIds.Format(RuleIds.Last)} 가운데 하나여야 합니다"));

            var title = OptionalString(obj, "title", errors) ?? "";
            var severity = OptionalString(obj, "severity", errors) ?? "warning";
            if (Array.IndexOf(Severities, severity) < 0)
                errors.Add(Error(obj.Get("severity")!, $"severity는 {string.Join(" · ", Severities)} 가운데 하나여야 합니다"));
            var requires = StringArray(obj, "requires", errors);
            var ignore = StringArray(obj, "ignoreAssemblies", errors);

            var nodes = new List<IrNode>();
            if (!(obj.Get("nodes") is JsonObject nodesJson))
            {
                errors.Add(Error(obj.Get("nodes") ?? obj, "'nodes'(객체)가 없습니다"));
                return new RuleParseResult(null, errors, id);
            }

            if (nodesJson.Members.Count > MaxNodes)
            {
                errors.Add(Error(nodesJson, $"노드가 {nodesJson.Members.Count}개입니다 — 규칙 하나에 {MaxNodes}개까지"));
                return new RuleParseResult(null, errors, id);
            }

            foreach (var member in nodesJson.Members)
            {
                var before = errors.Count;
                var node = ParseNode(member.Key, member.Value, errors);
                for (var i = before; i < errors.Count; i++)
                    errors[i] = errors[i].WithNode(member.Key);
                if (node != null)
                    nodes.Add(node);
            }

            CheckGraph(nodesJson, nodes, errors);

            if (errors.Any(e => !e.IsWarning))
                return new RuleParseResult(null, errors, id);
            return new RuleParseResult(new RuleIr(obj, id!, title, severity, requires, ignore, nodes), errors, id);
        }

        private static IrNode? ParseNode(string name, JsonValue value, List<IrError> errors)
        {
            var at = "nodes." + name;
            if (name == NodeCatalog.ScopeName || !NodeName.IsMatch(name))
            {
                errors.Add(Error(value, $"{at}: 노드 이름은 영문 · 숫자 · . _ -만 쓰고 '{NodeCatalog.ScopeName}'은 쓸 수 없습니다"));
                return null;
            }
            if (!(value is JsonObject json))
            {
                errors.Add(Error(value, $"{at}: 노드는 객체여야 합니다"));
                return null;
            }
            var kind = (json.Get("kind") as JsonString)?.Value;
            if (kind == null)
            {
                errors.Add(Error(json, $"{at}: 'kind'(문자열)가 없습니다"));
                return null;
            }
            var spec = NodeCatalog.Find(kind);
            if (spec == null)
            {
                errors.Add(Error(json.Get("kind")!, $"{at}: 모르는 kind '{kind}'"));
                return null;
            }

            var errorCount = errors.Count;
            foreach (var member in json.Members)
            {
                if (member.Key != "kind" && spec.Inputs.All(i => i.Name != member.Key) && spec.Fields.All(f => f.Name != member.Key))
                    errors.Add(Error(member.Value, $"{at}: {kind}에 없는 칸 '{member.Key}'"));
            }

            var inputs = new Dictionary<string, string[]>();
            foreach (var input in spec.Inputs)
            {
                var raw = json.Get(input.Name);
                if (raw == null)
                {
                    errors.Add(Error(json, $"{at}: 입력 '{input.Name}'이(가) 없습니다"));
                    continue;
                }
                if (input.Many)
                {
                    var names = raw is JsonArray array && array.Items.All(i => i is JsonString)
                        ? array.Items.Cast<JsonString>().Select(s => s.Value).ToArray()
                        : null;
                    if (names == null || names.Length < 2)
                        errors.Add(Error(raw, $"{at}.{input.Name}: 노드 이름 배열(둘 이상)이어야 합니다"));
                    else
                        inputs[input.Name] = names;
                }
                else if (raw is JsonString single)
                {
                    inputs[input.Name] = new[] { single.Value };
                }
                else
                {
                    errors.Add(Error(raw, $"{at}.{input.Name}: 노드 이름(문자열)이어야 합니다"));
                }
            }

            foreach (var field in spec.Fields)
                CheckField(at, field, json.Get(field.Name), json, errors);
            if (spec.RequireOneOf.Length > 0 && spec.RequireOneOf.All(f => json.Get(f) == null))
                errors.Add(Error(json, $"{at}: {string.Join(" · ", spec.RequireOneOf)} 가운데 하나 이상 있어야 합니다"));
            if (errors.Count == errorCount)
                CheckPatterns(at, kind, json, errors);

            return errors.Count == errorCount ? new IrNode(name, spec, json, inputs) : null;
        }

        /// <summary>패턴 칸 — 분석기가 같은 클래스로 읽으므로 여기서 읽히면 분석기에서도 읽힌다.</summary>
        private static void CheckPatterns(string at, string kind, JsonObject json, List<IrError> errors)
        {
            if (kind == "filter.name" && json.Get("pattern") is JsonString pattern)
            {
                NamePattern.ParseRegex(pattern.Value, out var error);
                if (error != null)
                    errors.Add(Error(pattern, $"{at}.pattern: 정규식이 잘못됐습니다 — {error}"));
            }
            if (kind == "filter.location" && json.Get("paths") is JsonArray paths)
            {
                foreach (var path in paths.Items.OfType<JsonString>())
                {
                    PathPattern.Parse(path.Value, out var error);
                    if (error != null)
                        errors.Add(Error(path, $"{at}.paths: '{path.Value}' — {error}"));
                }
            }
        }

        private static void CheckField(string at, FieldSpec field, JsonValue? value, JsonObject owner, List<IrError> errors)
        {
            var where = at + "." + field.Name;
            if (value == null)
            {
                if (field.Required)
                    errors.Add(Error(owner, $"{where}: 필수 칸이 없습니다"));
                return;
            }

            switch (field.Type)
            {
                case FieldType.String:
                    if (!(value is JsonString str))
                        errors.Add(Error(value, $"{where}: 문자열이어야 합니다"));
                    else if (str.Value.Trim().Length == 0)
                        errors.Add(Error(value, $"{where}: 비어 있습니다"));
                    break;
                case FieldType.StringArray:
                    if (!(value is JsonArray array) || array.Items.Any(i => !(i is JsonString)))
                        errors.Add(Error(value, $"{where}: 문자열 배열이어야 합니다"));
                    else if (array.Items.Count == 0 || array.Items.Cast<JsonString>().Any(s => s.Value.Trim().Length == 0))
                        errors.Add(Error(value, $"{where}: 비었거나 빈 항목이 있습니다"));
                    break;
                case FieldType.Int:
                    if (!(value is JsonNumber number) || !int.TryParse(number.Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                        errors.Add(Error(value, $"{where}: 정수여야 합니다"));
                    else if (n < field.Min || n > field.Max)
                        errors.Add(Error(value, $"{where}: {field.Min}~{field.Max} 사이여야 합니다"));
                    break;
                case FieldType.Bool:
                    if (!(value is JsonBool))
                        errors.Add(Error(value, $"{where}: true · false여야 합니다"));
                    break;
                case FieldType.Choice:
                    if (!(value is JsonString choice) || Array.IndexOf(field.Choices, choice.Value) < 0)
                        errors.Add(Error(value, $"{where}: {string.Join(" · ", field.Choices)} 가운데 하나여야 합니다"));
                    break;
                case FieldType.ChoiceArray:
                    if (!(value is JsonArray choices) || choices.Items.Count == 0
                        || choices.Items.Any(i => !(i is JsonString s) || Array.IndexOf(field.Choices, s.Value) < 0))
                        errors.Add(Error(value, $"{where}: {string.Join(" · ", field.Choices)} 가운데 하나 이상의 배열이어야 합니다"));
                    break;
            }
        }

        /// <summary>입력이 가리키는 노드 · 소켓 종류 · 고리 · report 하나 · report에 닿지 않는 노드(경고).</summary>
        private static void CheckGraph(JsonObject nodesJson, List<IrNode> nodes, List<IrError> errors)
        {
            var byName = nodes.ToDictionary(n => n.Name);
            JsonValue Pos(IrNode node) => nodesJson.Get(node.Name)!;

            foreach (var node in nodes)
            {
                foreach (var input in node.Spec.Inputs)
                {
                    if (!node.Inputs.TryGetValue(input.Name, out var sources))
                        continue;
                    foreach (var source in sources)
                    {
                        Socket? socket;
                        if (source == NodeCatalog.ScopeName)
                        {
                            socket = Socket.Scope;
                        }
                        else if (byName.TryGetValue(source, out var target))
                        {
                            socket = target.Spec.Output;
                        }
                        else
                        {
                            if (nodesJson.Get(source) == null)
                                errors.Add(Error(Pos(node), $"nodes.{node.Name}.{input.Name}: 없는 노드 '{source}'", node: node.Name));
                            continue; // 있는데 잘못된 노드는 그 노드에서 이미 오류
                        }
                        if (socket == null || Array.IndexOf(input.Accepts, socket.Value) < 0)
                            errors.Add(Error(Pos(node), $"nodes.{node.Name}.{input.Name}: '{source}'({(socket?.ToString() ?? "끝 노드")})는 이을 수 없습니다 — {string.Join(" · ", input.Accepts)}만 받습니다", node: node.Name));
                    }
                }
            }

            var reports = nodes.Where(n => n.Kind == NodeCatalog.ReportKind).ToList();
            if (reports.Count != 1 && nodes.Count == nodesJson.Members.Count)
                errors.Add(Error(nodesJson, $"report 노드는 하나여야 합니다 (지금 {reports.Count}개)"));

            // 고리: 흰(0) · 회색(1) · 검정(2)
            var color = nodes.ToDictionary(n => n.Name, n => 0);
            bool Cycle(IrNode node)
            {
                color[node.Name] = 1;
                foreach (var source in node.AllInputs)
                {
                    if (!byName.TryGetValue(source, out var next))
                        continue;
                    if (color[next.Name] == 1 || (color[next.Name] == 0 && Cycle(next)))
                        return true;
                }
                color[node.Name] = 2;
                return false;
            }
            foreach (var node in nodes)
            {
                if (color[node.Name] == 0 && Cycle(node))
                {
                    errors.Add(Error(Pos(node), $"nodes.{node.Name}: 입력이 고리를 이룹니다", node: node.Name));
                    return;
                }
            }

            if (reports.Count == 1)
            {
                var reached = new HashSet<string>();
                var stack = new Stack<string>(new[] { reports[0].Name });
                while (stack.Count > 0)
                {
                    var name = stack.Pop();
                    if (!reached.Add(name) || !byName.TryGetValue(name, out var node))
                        continue;
                    foreach (var source in node.AllInputs)
                        stack.Push(source);
                }
                foreach (var node in nodes.Where(n => !reached.Contains(n.Name)))
                    errors.Add(Error(Pos(node), $"nodes.{node.Name}: report에 닿지 않아 쓰이지 않습니다", isWarning: true, node: node.Name));
            }

            // 이름은 선언 사이트에만 있다 — 앞에 선언 노드가 없으면 늘 0건
            foreach (var node in nodes.Where(n => n.Kind == "filter.name"))
            {
                if (!Upstream(node, byName).Any(n => n.Kind == "sites.declaration"))
                    errors.Add(Error(Pos(node), $"nodes.{node.Name}: 이름은 선언(sites.declaration) 사이트에만 있어 이 필터는 늘 0건입니다", isWarning: true, node: node.Name));
            }
        }

        /// <summary>노드로 들어오는 모든 노드 (고리는 앞에서 걸렀다).</summary>
        private static IEnumerable<IrNode> Upstream(IrNode node, Dictionary<string, IrNode> byName)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>(node.AllInputs);
            while (stack.Count > 0)
            {
                var name = stack.Pop();
                if (!seen.Add(name) || !byName.TryGetValue(name, out var upstream))
                    continue;
                yield return upstream;
                foreach (var source in upstream.AllInputs)
                    stack.Push(source);
            }
        }

        private static string? RequiredString(JsonObject obj, string name, List<IrError> errors)
        {
            var value = obj.Get(name);
            if (value == null)
            {
                errors.Add(Error(obj, $"'{name}'이(가) 없습니다"));
                return null;
            }
            return OptionalString(obj, name, errors);
        }

        private static string? OptionalString(JsonObject obj, string name, List<IrError> errors)
        {
            var value = obj.Get(name);
            if (value == null)
                return null;
            if (value is JsonString str)
                return str.Value;
            errors.Add(Error(value, $"'{name}'은(는) 문자열이어야 합니다"));
            return null;
        }

        private static string[] StringArray(JsonObject obj, string name, List<IrError> errors)
        {
            var value = obj.Get(name);
            if (value == null)
                return new string[0];
            if (value is JsonArray array && array.Items.All(i => i is JsonString))
                return array.Items.Cast<JsonString>().Select(s => s.Value).ToArray();
            errors.Add(Error(value, $"'{name}'은(는) 문자열 배열이어야 합니다"));
            return new string[0];
        }

        private static IrError Error(JsonValue at, string message, bool isWarning = false, string? node = null) => new IrError(message, at.Line, at.Column, isWarning, node);
    }
}
