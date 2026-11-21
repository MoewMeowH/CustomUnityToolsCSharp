using System.Net.Http.Json;
using System.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HBLint.App.Tests;

/// <summary>시험마다 새 임시 폴더 (끝나면 지움).</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hblint-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>TestServer 위 앱 + 토큰을 붙인 클라이언트.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app, AppOptions options)
    {
        _app = app;
        Options = options;
        Client = app.GetTestClient();
        Client.DefaultRequestHeaders.Add(LocalGuard.TokenHeader, options.Token);
    }

    public AppOptions Options { get; }

    public HttpClient Client { get; }

    public HttpClient Raw() => _app.GetTestClient();

    public static async Task<TestApp> Start(TempDir data, string? devOrigin = null)
    {
        var options = new AppOptions { DataFolder = System.IO.Path.Combine(data.Path, "data"), DevOrigin = devOrigin, OpenBrowser = false };
        var app = AppHost.Build(options, builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new TestApp(app, options);
    }

    public async Task Open(string rules, string output)
    {
        var response = await Client.PostAsJsonAsync("/api/workspace", new { rules, @out = output });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>가짜 UnityEngine.CoreModule DLL · Unity가 만든 모양의 csproj.</summary>
internal static class FakeUnity
{
    public const string Source = """
        namespace UnityEngine
        {
            public class Object { }
            public class Component : Object
            {
                public Transform transform => null;
                public T GetComponent<T>() => default;
                public string tag { get => null; set { } }
            }
            public class Behaviour : Component { }
            public class MonoBehaviour : Behaviour { }
            public class Transform : Component { public Vector3 position { get => default; set { } } }
            public struct Vector3 { public float x; }
            public class Outer { public class Inner { } }
            public class Box<T> { public T Value; }
        }
        """;

    /// <summary>시험 프로세스의 BCL (HBLint.* · 시험 도구 제외) — Unity csproj의 HintPath 자리에 넣는다.</summary>
    public static IReadOnlyList<string> Bcl { get; } =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
        .Where(p =>
        {
            var name = System.IO.Path.GetFileName(p);
            return name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll";
        })
        .ToList();

    public static string EmitUnity(string folder) =>
        Emit(folder, "UnityEngine.CoreModule", Source, []);

    public static string Emit(string folder, string assemblyName, string source, IEnumerable<string> references)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            Bcl.Concat(references).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = System.IO.Path.Combine(folder, assemblyName + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    /// <summary>Sdk 속성 없는 옛 모양 (Unity가 만드는 것). Compile은 csproj 기준 상대 경로.</summary>
    public static string Csproj(TempDir dir, string name, string assemblyName, IEnumerable<string> references, IEnumerable<string> compile, string defines = "UNITY_EDITOR;UNITY_6000")
    {
        var refs = string.Concat(references.Select(r => $"""
                <Reference Include="{SecurityElement.Escape(System.IO.Path.GetFileNameWithoutExtension(r))}">
                  <HintPath>{SecurityElement.Escape(r)}</HintPath>
                </Reference>

            """));
        var sources = string.Concat(compile.Select(c => $"    <Compile Include=\"{SecurityElement.Escape(c)}\" />\n"));
        return dir.Write(name, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <LangVersion>9.0</LangVersion>
                <AssemblyName>{assemblyName}</AssemblyName>
                <DefineConstants>{defines}</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
            {sources}  </ItemGroup>
              <ItemGroup>
            {refs}  </ItemGroup>
            </Project>
            """);
    }

    /// <summary>Unity 프로젝트 폴더 (Assets · ProjectSettings) + Game.cs + csproj.</summary>
    public static string Project(TempDir dir, string game, string assemblyName = "Assembly-CSharp", IEnumerable<string>? extraReferences = null)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Assets"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "ProjectSettings"));
        var unity = EmitUnity(dir.Path);
        dir.Write("Assets/Game.cs", game);
        Csproj(dir, assemblyName + ".csproj", assemblyName, Bcl.Append(unity).Concat(extraReferences ?? []), ["Assets\\Game.cs"]);
        return dir.Path;
    }
}
