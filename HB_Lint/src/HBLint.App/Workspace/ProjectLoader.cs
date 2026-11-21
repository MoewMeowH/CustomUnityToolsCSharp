using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace HBLint.App.Workspace;

/// <summary>대상 프로젝트에서 읽은 것 — 컴파일을 만들 재료.</summary>
internal sealed record ProjectInfo(
    string Csproj,
    string Kind,
    string AssemblyName,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Defines,
    string? LangVersion,
    IReadOnlyList<ProjectInfo> SourceReferences,
    IReadOnlyList<string> MissingReferences,
    bool Executable = false);

/// <summary>읽기 실패. Candidates = 고를 수 있는 csproj 이름 (화면이 고르기 칸을 띄운다).</summary>
internal sealed class ProjectLoadException(string message, IReadOnlyList<string>? candidates = null) : Exception(message)
{
    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];
}

/// <summary>
/// 대상 프로젝트 읽기. 읽기만 한다 — 단 .NET SDK 프로젝트는 dotnet msbuild가 그 프로젝트의 MSBuild 코드를 돌리고 restore로 obj/를 쓴다 (IDE가 여는 것과 같음).
/// <list type="bullet">
/// <item>csproj가 Unity 프로젝트 폴더(Assets · ProjectSettings)에 있으면 Unity가 만든 csproj로 보고 XML만 읽는다 (HintPath · ProjectReference → Library/ScriptAssemblies · Compile · DefineConstants · LangVersion)
/// — Sdk 속성이 붙어 있어도 msbuild를 돌리지 않는다. UNC · 장치 경로(\\ 로 시작)는 열지 않고 없는 참조로 둔다 (열기만 해도 SMB 인증이 나간다).</item>
/// <item>그 밖의 <c>&lt;Project Sdk=…&gt;</c> → <c>dotnet msbuild -getItem · -getProperty</c> (디자인 타임, 참조 프로젝트는 빌드하지 않음).
/// 여러 TFM이면 첫 TFM으로. 참조 프로젝트 DLL이 아직 없으면 그 프로젝트도 같은 질의로 읽어 소스 컴파일 참조로 쓴다.</item>
/// </list>
/// </summary>
internal static class ProjectLoader
{
    private const int MaxDepth = 8;
    private static readonly TimeSpan MsbuildTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Unity 프로젝트 폴더면 그 안의 csproj 이름들, 아니면 빈 목록.</summary>
    public static IReadOnlyList<string> UnityCsprojs(string folder) =>
        IsUnityFolder(folder) ? CsprojNames(folder) : [];

    private static IReadOnlyList<string> CsprojNames(string folder) =>
        Directory.GetFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsUnityFolder(string folder) =>
        Directory.Exists(Path.Combine(folder, "Assets")) && Directory.Exists(Path.Combine(folder, "ProjectSettings"));

    /// <summary>\\server\share · \\?\ · \\.\ (그리고 // 꼴) — 파일 시스템에 묻지 않는다.</summary>
    public static bool IsRemoteOrDevice(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>csproj 경로 또는 폴더(Unity 프로젝트 · csproj가 하나인 폴더) → csproj 경로.</summary>
    public static string ResolveCsproj(string path, string? csprojName)
    {
        if (IsRemoteOrDevice(path))
            throw new ProjectLoadException($"{path}: 네트워크 · 장치 경로는 읽지 않습니다");
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            if (!full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new ProjectLoadException($"{full}: .csproj 파일이 아닙니다");
            return full;
        }
        if (!Directory.Exists(full))
            throw new ProjectLoadException($"{full}: 없습니다");

        var candidates = Directory.GetFiles(full, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojName != null)
        {
            var chosen = candidates.FirstOrDefault(c => string.Equals(Path.GetFileName(c), csprojName, StringComparison.OrdinalIgnoreCase));
            return chosen ?? throw new ProjectLoadException($"{full}에 {csprojName}이(가) 없습니다", CsprojNames(full));
        }
        if (IsUnityFolder(full))
        {
            if (candidates.Length == 0)
                throw new ProjectLoadException($"{full}: csproj가 없습니다 — Unity에서 Preferences > External Tools > Regenerate project files로 만드세요");
            var main = candidates.FirstOrDefault(c => string.Equals(Path.GetFileName(c), "Assembly-CSharp.csproj", StringComparison.OrdinalIgnoreCase));
            return main ?? throw new ProjectLoadException($"{full}: Assembly-CSharp.csproj가 없습니다 — 읽을 csproj를 고르세요", CsprojNames(full));
        }
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new ProjectLoadException($"{full}: csproj가 없습니다"),
            _ => throw new ProjectLoadException($"{full}: csproj가 여럿입니다 — 하나를 고르세요", CsprojNames(full)),
        };
    }

    public static ProjectInfo Load(string csproj, CancellationToken ct) =>
        Load(Path.GetFullPath(csproj), 0, new Dictionary<string, ProjectInfo?>(StringComparer.OrdinalIgnoreCase), ct);

    /// <param name="loaded">읽은 프로젝트 (null = 읽는 중 — 고리). 다이아몬드 참조(A→B→D, A→C→D)의 D를 한 번만 읽는다.</param>
    private static ProjectInfo Load(string csproj, int depth, Dictionary<string, ProjectInfo?> loaded, CancellationToken ct)
    {
        if (loaded.TryGetValue(csproj, out var known))
            return known ?? throw new ProjectLoadException($"{csproj}: 프로젝트 참조가 고리를 이룹니다");
        if (!File.Exists(csproj))
            throw new ProjectLoadException($"{csproj}: 없습니다");
        XDocument xml;
        try
        {
            xml = XDocument.Load(csproj);
        }
        catch (System.Xml.XmlException e)
        {
            throw new ProjectLoadException($"{csproj}: XML을 읽을 수 없습니다 — {e.Message}");
        }

        loaded[csproj] = null;
        var info = xml.Root?.Attribute("Sdk") != null && !IsUnityFolder(Path.GetDirectoryName(csproj)!)
            ? LoadSdk(csproj, depth, loaded, ct)
            : LoadUnity(csproj, xml);
        loaded[csproj] = info;
        return info;
    }

    private static ProjectInfo LoadUnity(string csproj, XDocument xml)
    {
        var folder = Path.GetDirectoryName(csproj)!;
        var ns = xml.Root!.Name.Namespace;
        string? Property(string name) => xml.Descendants(ns + name).FirstOrDefault()?.Value.Trim();
        var skipped = new List<string>();
        IEnumerable<string> Local(IEnumerable<string?> paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;
                if (IsRemoteOrDevice(path))
                    skipped.Add(path);
                else
                    yield return Path.GetFullPath(path, folder);
            }
        }

        var references = Local(xml.Descendants(ns + "Reference").Select(r => r.Element(ns + "HintPath")?.Value.Trim())).ToList();
        // Unity csproj의 ProjectReference = 다른 asmdef → Unity가 컴파일한 Library/ScriptAssemblies/<이름>.dll
        references.AddRange(xml.Descendants(ns + "ProjectReference")
            .Select(r => r.Attribute("Include")?.Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.Combine(folder, "Library", "ScriptAssemblies", Path.GetFileNameWithoutExtension(p!) + ".dll")));
        var sources = Local(xml.Descendants(ns + "Compile").Select(c => c.Attribute("Include")?.Value)).ToList();
        var distinct = references.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ProjectInfo(
            csproj,
            "unity",
            Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(csproj),
            distinct.Where(File.Exists).ToList(),
            sources,
            Split(Property("DefineConstants")),
            Property("LangVersion"),
            [],
            distinct.Where(p => !File.Exists(p)).Concat(skipped).ToList());
    }

    private static readonly string[] Query =
    [
        "-restore", "-p:DesignTimeBuild=true", "-p:BuildProjectReferences=false",
        "-t:FindReferenceAssembliesForReferences",
        "-getItem:ReferencePathWithRefAssemblies", "-getItem:Compile", "-getItem:ProjectReference",
        "-getProperty:AssemblyName", "-getProperty:DefineConstants", "-getProperty:LangVersion", "-getProperty:OutputType",
    ];

    private static ProjectInfo LoadSdk(string csproj, int depth, Dictionary<string, ProjectInfo?> loaded, CancellationToken ct)
    {
        var run = RunMsbuild(csproj, Query, ct);
        // 여러 TFM(<TargetFrameworks>)은 바깥 빌드에 그 타깃이 없다(MSB4057) — 첫 TFM의 안쪽 빌드로 다시 묻는다
        if (run.ExitCode != 0 && (run.Output + run.Error).Contains("MSB4057", StringComparison.Ordinal))
        {
            var frameworks = RunMsbuild(csproj, ["-getProperty:TargetFrameworks"], ct);
            var first = frameworks.ExitCode == 0 ? Split(frameworks.Output.Trim()).FirstOrDefault() : null;
            if (first != null)
                run = RunMsbuild(csproj, [.. Query, "-p:TargetFramework=" + first], ct);
        }
        if (run.ExitCode != 0)
        {
            // msbuild 오류는 stdout에 나온다
            var message = string.Join("\n", (run.Output + "\n" + run.Error).Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(5));
            throw new ProjectLoadException($"{csproj}: dotnet msbuild 실패 (종료 코드 {run.ExitCode})\n{message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(run.Output);
            var root = doc.RootElement;
            var properties = root.GetProperty("Properties");
            var items = root.GetProperty("Items");
            string? Property(string name) => properties.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s ? s : null;
            IEnumerable<JsonElement> Items(string name) => items.TryGetProperty(name, out var v) ? v.EnumerateArray() : [];

            var references = Items("ReferencePathWithRefAssemblies").Select(i => i.GetProperty("FullPath").GetString()!).ToList();
            var sources = Items("Compile").Select(i => i.GetProperty("FullPath").GetString()!).ToList();
            var missing = references.Where(r => !File.Exists(r)).ToList();

            // 빌드 전이라 DLL이 없는 참조 프로젝트 → 소스로 읽는다 (DLL 이름 = 그 프로젝트의 AssemblyName)
            var sourceReferences = new List<ProjectInfo>();
            if (missing.Count > 0 && depth < MaxDepth)
            {
                foreach (var reference in Items("ProjectReference").Select(i => i.GetProperty("FullPath").GetString()!))
                {
                    if (!File.Exists(reference))
                        continue;
                    var project = Load(Path.GetFullPath(reference), depth + 1, loaded, ct);
                    if (missing.RemoveAll(m => string.Equals(Path.GetFileNameWithoutExtension(m), project.AssemblyName, StringComparison.OrdinalIgnoreCase)) > 0)
                        sourceReferences.Add(project);
                }
            }

            var outputType = Property("OutputType");
            return new ProjectInfo(
                csproj,
                "sdk",
                Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(csproj),
                references.Where(File.Exists).ToList(),
                sources,
                Split(Property("DefineConstants")),
                Property("LangVersion"),
                sourceReferences,
                missing,
                string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is JsonException || e is KeyNotFoundException || e is InvalidOperationException)
        {
            throw new ProjectLoadException($"{csproj}: dotnet msbuild 출력을 읽을 수 없습니다 — {e.Message}");
        }
    }

    /// <returns>Output = stdout (질의 JSON · 값, msbuild 오류도 여기), Error = stderr.</returns>
    private static (int ExitCode, string Output, string Error) RunMsbuild(string csproj, IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(csproj)!,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(csproj);
        start.ArgumentList.Add("-nologo");
        // 재사용 노드는 빌드가 끝나도 살아 남아 물려받은 stdout 파이프를 쥔다 — 그러면 출력 읽기가 끝나지 않는다
        start.ArgumentList.Add("-nodeReuse:false");
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ProjectLoadException("dotnet을 실행할 수 없습니다");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ProjectLoadException("dotnet을 실행할 수 없습니다 — .NET SDK가 PATH에 있어야 합니다 (" + e.Message + ")");
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MsbuildTimeout);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                ct.ThrowIfCancellationRequested(); // 요청이 끊김 — 시간 초과가 아니다
                throw new ProjectLoadException($"{csproj}: dotnet msbuild가 {MsbuildTimeout.TotalMinutes}분 안에 끝나지 않았습니다");
            }
            // 자식이 파이프를 쥐고 남아도 묶이지 않게 — 끝난 뒤 출력은 곧 다 온다
            if (!Task.WhenAll(output, error).Wait(TimeSpan.FromSeconds(30), ct))
                throw new ProjectLoadException($"{csproj}: dotnet msbuild는 끝났는데 출력이 닫히지 않았습니다");
            return (process.ExitCode, output.Result, error.Result);
        }
    }

    private static string[] Split(string? defines) =>
        (defines ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
