using System.Runtime.InteropServices;
using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

/// <summary>
/// 8.3 짧은 이름(CREDEN~1.JSO)으로 DenyPatterns를 피할 수 있다는 지적(골든 sol, 2026-10-20)을 확인한 고정 테스트.
/// 실제로는 SafePath의 Path.GetFullPath가 짧은 이름을 긴 이름으로 풀어 우회되지 않았다 — .NET 동작이 바뀌면 여기서 드러난다.
/// 짧은 이름은 볼륨 설정에 따라 생기지 않을 수 있어, 만들어지지 않았으면 그 검사는 건너뛴다.
/// </summary>
public sealed class ShortNameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-orch-8dot3-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (string? Full, string? Error) Resolve(string path, SafePath.Access access) =>
        SafePath.Resolve(path, _root, ContextOptions.BuiltInDenyPatterns, access);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);

    /// <summary>루트 기준 짧은 이름 상대 경로. 짧은 이름이 없으면(8.3 꺼짐) null.</summary>
    private string? ShortRelative(string relative)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new char[1024];
        var length = GetShortPathNameW(Path.Combine(_root, relative), buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length) return null;
        var shortFull = new string(buffer, 0, (int)length);
        var rootShort = new char[1024];
        var rootLength = GetShortPathNameW(_root, rootShort, (uint)rootShort.Length);
        // 문자열로 자른다: Path.GetRelativePath는 GetFullPath를 거치며 짧은 이름을 긴 이름으로 풀어 버린다 (실측 2026-10-23)
        var prefix = new string(rootShort, 0, (int)rootLength).TrimEnd('\\') + "\\";
        if (!shortFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var shortRelative = shortFull[prefix.Length..].Replace('\\', '/');
        return string.Equals(shortRelative, relative, StringComparison.OrdinalIgnoreCase) ? null : shortRelative;
    }

    [Fact]
    public void Short_name_of_denied_file_is_rejected()
    {
        File.WriteAllText(Path.Combine(_root, "credentials.json"), "secret");
        if (ShortRelative("credentials.json") is not { } alias) return; // 8.3 꺼진 볼륨

        Assert.Contains("~", alias);
        foreach (var access in new[] { SafePath.Access.Read, SafePath.Access.Write })
        {
            var (full, error) = Resolve(alias, access);
            Assert.Null(full);
            Assert.Contains("credentials", error);
        }
    }

    [Fact]
    public void Short_name_folder_on_the_way_is_resolved_before_matching()
    {
        // '/'가 든 패턴(폴더 경로)도 긴 이름으로 비교해야 한다
        var dir = Path.Combine(_root, "secretsfolder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        if (ShortRelative("secretsfolder") is not { } aliasDir) return;

        var deny = new[] { "secretsfolder/**" };
        var (full, error) = SafePath.Resolve($"{aliasDir}/a.txt", _root, deny, SafePath.Access.Read);
        Assert.Null(full);
        Assert.Contains("secretsfolder/**", error);

        // 쓰기: 아직 없는 꼬리는 요청한 이름 그대로, 앞의 짧은 이름 폴더는 긴 이름으로
        (full, error) = SafePath.Resolve($"{aliasDir}/new.txt", _root, deny, SafePath.Access.Write, writeDenyPatterns: []);
        Assert.Null(full);
        Assert.Contains("secretsfolder/**", error);
    }

    [Fact]
    public void Real_names_with_tilde_are_allowed()
    {
        File.WriteAllText(Path.Combine(_root, "Copy~1.md"), "x");
        Assert.Null(Resolve("Copy~1.md", SafePath.Access.Read).Error);
        Assert.Null(Resolve("Notes~2.md", SafePath.Access.Write).Error); // 아직 없는 이름
    }

    [Fact]
    public void Allowed_file_by_short_name_resolves()
    {
        File.WriteAllText(Path.Combine(_root, "longdocumentname.md"), "x");
        if (ShortRelative("longdocumentname.md") is not { } alias) return;

        var (full, error) = Resolve(alias, SafePath.Access.Read);
        Assert.Null(error);
        Assert.NotNull(full);
    }
}
