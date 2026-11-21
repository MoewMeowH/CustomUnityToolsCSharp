using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HBLint.App.Workspace;

/// <summary>읽은 대상 프로젝트 하나 — 컴파일(참조 + 소스) · 타입 색인. 다시 읽기 전까지 그대로 쓴다 (컴파일은 바뀌지 않는 값).</summary>
internal sealed class ProjectSession
{
    private const int MaxResults = 50;

    private readonly List<TypeEntry> _types;
    private readonly List<string> _namespaces;

    private ProjectSession(ProjectInfo info, CSharpParseOptions parseOptions, CSharpCompilation compilation, List<TypeEntry> types, List<string> namespaces, long loadMs)
    {
        Info = info;
        ParseOptions = parseOptions;
        Compilation = compilation;
        _types = types;
        _namespaces = namespaces;
        LoadMs = loadMs;
    }

    public ProjectInfo Info { get; }

    public CSharpParseOptions ParseOptions { get; }

    /// <summary>대상 소스 + 참조. 미리 보기는 여기에 규칙 트리를 더한다.</summary>
    public CSharpCompilation Compilation { get; }

    public int TypeCount => _types.Count;

    public long LoadMs { get; }

    public static ProjectSession Load(string csproj, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var info = ProjectLoader.Load(csproj, ct);
        var (parseOptions, compilation) = Compile(info, new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase), ct);
        var (types, namespaces) = Index(compilation);
        return new ProjectSession(info, parseOptions, compilation, types, namespaces, watch.ElapsedMilliseconds);
    }

    /// <param name="built">소스로 읽은 참조 프로젝트 컴파일 (csproj별 한 번 — 다이아몬드 참조가 같은 이름 어셈블리 둘이 되지 않게).</param>
    private static (CSharpParseOptions, CSharpCompilation) Compile(ProjectInfo info, Dictionary<string, CSharpCompilation> built, CancellationToken ct)
    {
        var version = info.LangVersion != null && LanguageVersionFacts.TryParse(info.LangVersion, out var parsed) ? parsed : LanguageVersion.Default;
        var parseOptions = new CSharpParseOptions(version, preprocessorSymbols: info.Defines);
        var trees = new List<SyntaxTree>();
        foreach (var source in info.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(source))
                trees.Add(CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(source), System.Text.Encoding.UTF8), parseOptions, source, ct));
        }
        var references = info.References.Select(r => (MetadataReference)MetadataReference.CreateFromFile(r)).ToList();
        foreach (var project in info.SourceReferences)
        {
            if (!built.TryGetValue(project.Csproj, out var reference))
                built[project.Csproj] = reference = Compile(project, built, ct).Item2;
            references.Add(reference.ToMetadataReference());
        }
        // 최상위 문은 실행 파일에서만 된다 (DLL이면 CS8805)
        var options = new CSharpCompilationOptions(info.Executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);
        return (parseOptions, CSharpCompilation.Create(info.AssemblyName, trees, references, options));
    }

    private static (List<TypeEntry>, List<string>) Index(CSharpCompilation compilation)
    {
        var types = new List<TypeEntry>();
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        var own = compilation.Assembly;

        void Walk(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
                WalkType(type, null);
            foreach (var child in ns.GetNamespaceMembers())
                Walk(child);
        }

        void WalkType(INamedTypeSymbol type, string? outer)
        {
            var visible = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, own) || type.DeclaredAccessibility == Accessibility.Public;
            if (!visible || type.MetadataName.Contains('<'))
                return;
            var name = outer != null ? outer + "+" + type.MetadataName : MetadataName(type);
            types.Add(new TypeEntry(name, type.Name, KindOf(type), type.ContainingAssembly?.Name ?? ""));
            if (!type.ContainingNamespace.IsGlobalNamespace)
                namespaces.Add(type.ContainingNamespace.ToDisplayString());
            foreach (var nested in type.GetTypeMembers())
                WalkType(nested, name);
        }

        Walk(compilation.GlobalNamespace);
        return (types, namespaces.ToList());
    }

    /// <summary>분석기가 GetTypeByMetadataName으로 찾는 이름 — 네임스페이스.이름`n, 중첩은 Outer+Inner.</summary>
    public static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType != null)
            return MetadataName(type.ContainingType) + "+" + type.MetadataName;
        return type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
    }

    private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct => "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => "class",
    };

    /// <summary>짧은 이름이 q로 시작 → 이름 어딘가에 q 순. 대소문자 무시.</summary>
    public IReadOnlyList<TypeEntry> SearchTypes(string q)
    {
        if (q.Length == 0)
            return [];
        return _types
            .Select(t => (Type: t, Rank: t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : t.MetadataName.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2))
            .Where(x => x.Rank < 2)
            .OrderBy(x => x.Rank).ThenBy(x => x.Type.Name.Length).ThenBy(x => x.Type.MetadataName, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(x => x.Type)
            .ToList();
    }

    public IReadOnlyList<string> SearchNamespaces(string q) =>
        _namespaces.Where(n => n.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n.Length)
            .Take(MaxResults).ToList();

    public bool HasNamespace(string name) => _namespaces.BinarySearch(name, StringComparer.Ordinal) >= 0
        || _namespaces.Any(n => n.StartsWith(name + ".", StringComparison.Ordinal));

    /// <summary>그 타입에 선언된 메서드 · 속성 · 필드 이름 (분석기가 찾는 것과 같은 종류). 타입을 못 찾으면 null.</summary>
    public IReadOnlyList<MemberEntry>? Members(string typeName, string q)
    {
        var type = Compilation.GetTypeByMetadataName(typeName);
        if (type == null)
            return null;
        return type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName && (m is IMethodSymbol { MethodKind: MethodKind.Ordinary } || m is IPropertySymbol || m is IFieldSymbol))
            .Where(m => m.DeclaredAccessibility != Accessibility.Private || SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, Compilation.Assembly))
            .GroupBy(m => m.Name)
            .Select(g => new MemberEntry(g.Key, g.First() switch { IMethodSymbol => "method", IPropertySymbol => "property", _ => "field" }, g.Count()))
            .Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(MaxResults)
            .ToList();
    }

    public IEnumerable<string> ReferencedAssemblyNames() =>
        Compilation.ReferencedAssemblyNames.Select(a => a.Name);
}

internal sealed record TypeEntry(string MetadataName, string Name, string Kind, string Assembly);

internal sealed record MemberEntry(string Name, string Kind, int Overloads);
