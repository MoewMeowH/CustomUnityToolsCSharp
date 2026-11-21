using HBLint.Core.Ir;
using HBLint.Core.Json;
using Microsoft.CodeAnalysis;

namespace HBLint.App.Workspace;

/// <summary>못 찾은 이름 하나. node가 scope면 머리 칸.</summary>
internal sealed record NameProblem(string Node, string Field, string Value, string Message);

/// <summary>
/// 규칙의 타입 · 멤버 · 네임스페이스 칸과 requires를 대상 프로젝트 컴파일에서 찾아본다 (분석기는 못 찾으면 위치 없는 CLL9001로만 알린다).
/// 타입은 GetTypeByMetadataName, 멤버는 분석기 Sites.Resolve와 같은 규칙 — 마지막 '.' 앞이 타입, 뒤가 이름 또는 *, 메서드 · 속성 · 필드.
/// 검증 오류가 있는 규칙도 읽을 수 있는 칸은 본다.
/// </summary>
internal static class NameCheck
{
    public static IReadOnlyList<NameProblem> Run(ProjectSession project, string ruleText)
    {
        var problems = new List<NameProblem>();
        JsonObject root;
        try
        {
            if (JsonReader.Parse(ruleText) is not JsonObject obj)
                return problems;
            root = obj;
        }
        catch (JsonParseException)
        {
            return problems;
        }

        var compilation = project.Compilation;
        // 분석기(GraphRuleAnalyzer)와 같게: 참조한 어셈블리 이름만(자기 자신은 아님), 대소문자 가림
        var referenced = new HashSet<string>(project.ReferencedAssemblyNames(), StringComparer.Ordinal);
        foreach (var assembly in Strings(root.Get("requires")))
        {
            if (!referenced.Contains(assembly))
                problems.Add(new NameProblem(NodeCatalog.ScopeName, "requires", assembly, $"이 프로젝트({project.Info.AssemblyName})는 '{assembly}'를 참조하지 않습니다 — 이 어셈블리에서는 규칙이 돌지 않습니다"));
        }

        if (root.Get("nodes") is not JsonObject nodes)
            return problems;
        foreach (var member in nodes.Members)
        {
            if (member.Value is not JsonObject node || node.Get("kind") is not JsonString kind || NodeCatalog.Find(kind.Value) is not { } spec)
                continue;
            foreach (var field in spec.Fields)
            {
                foreach (var value in Strings(node.Get(field.Name)))
                {
                    var message = field.Hint switch
                    {
                        FieldHint.Type => compilation.GetTypeByMetadataName(value) == null ? $"타입 '{value}'을(를) 찾을 수 없습니다 (메타데이터 이름: 네임스페이스 포함 · 중첩은 Outer+Inner · 제네릭은 List`1)" : null,
                        FieldHint.Member => CheckMember(compilation, value),
                        FieldHint.Namespace => project.HasNamespace(value) ? null : $"네임스페이스 '{value}'이(가) 이 프로젝트에 없습니다",
                        _ => null,
                    };
                    if (message != null)
                        problems.Add(new NameProblem(member.Key, field.Name, value, message));
                }
            }
        }
        return problems;
    }

    private static string? CheckMember(Compilation compilation, string value)
    {
        var separator = value.LastIndexOf('.');
        if (separator <= 0 || separator == value.Length - 1)
            return $"'{value}'는 \"타입.멤버\" 꼴이어야 합니다";
        var typeName = value.Substring(0, separator);
        var type = compilation.GetTypeByMetadataName(typeName);
        if (type == null)
            return $"타입 '{typeName}'을(를) 찾을 수 없습니다";
        var name = value.Substring(separator + 1);
        var found = (name == NodeCatalog.AllMembers ? type.GetMembers() : type.GetMembers(name))
            .Any(m => m is IMethodSymbol || m is IPropertySymbol || m is IFieldSymbol);
        return found ? null : name == NodeCatalog.AllMembers
            ? $"'{typeName}'에 메서드 · 속성 · 필드가 없습니다"
            : $"'{typeName}'에 '{name}' 메서드 · 속성 · 필드가 없습니다 (멤버는 선언된 타입으로 — 예: UnityEngine.Component.GetComponent)";
    }

    private static IEnumerable<string> Strings(Core.Json.JsonValue? value) => value switch
    {
        JsonString s when s.Value.Trim().Length > 0 => [s.Value],
        JsonArray a => a.Items.OfType<JsonString>().Select(s => s.Value).Where(v => v.Trim().Length > 0),
        _ => [],
    };
}
