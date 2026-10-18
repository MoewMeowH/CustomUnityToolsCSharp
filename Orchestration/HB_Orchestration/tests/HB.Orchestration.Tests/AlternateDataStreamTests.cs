using HB.Orchestration.Agents;

namespace HB.Orchestration.Tests;

/// <summary>
/// Windows 경로 우회 (self-reviewer 시범에서 ADS 발견: ".env::$DATA"로 비밀 내용이 읽혔음. 사용자 공유 피드백으로 끝 점·공백, 장치 이름, 접두사·UNC 추가).
/// 쓰기 모드에서 CON·COM1.txt·aux.md·lpt1·CONIN$가 장치 경로로 통과했음 (실측 2026-10-19).
/// </summary>
public sealed class AlternateDataStreamTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hb-orch-ads-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly SafePath.Access[] Both = [SafePath.Access.Read, SafePath.Access.Write];

    private (string? Full, string? Error) Resolve(string path, SafePath.Access access) =>
        SafePath.Resolve(path, _root, ContextOptions.BuiltInDenyPatterns, access);

    [Theory]
    [InlineData(".env::$DATA")]
    [InlineData(".env:s")]
    [InlineData("credentials.json::$DATA")]
    [InlineData("docs/a.md:zone")]
    public void Colon_in_relative_path_is_rejected(string path)
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "secret");
        foreach (var access in Both)
            Assert.Equal((null, "경로에 ':'를 쓸 수 없습니다 (대체 데이터 스트림)."), Resolve(path, access));
    }

    [Theory]
    [InlineData(".env.")]
    [InlineData(".env ")]
    [InlineData(".env. .")]
    [InlineData("sub/../.env.")]
    [InlineData("notes.md ")]
    [InlineData("docs./a.md")]
    public void Trailing_dot_or_space_is_rejected(string path)
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "secret");
        foreach (var access in Both)
        {
            var (full, error) = Resolve(path, access);
            Assert.Null(full);
            Assert.Contains("점이나 공백으로 끝납니다", error);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1.txt")]
    [InlineData("aux.md")]
    [InlineData("sub/CON")]
    [InlineData("lpt1")]
    [InlineData("CONIN$")]
    [InlineData("conout$.log")]
    [InlineData("PRN.tar.gz")]
    public void Device_names_are_rejected(string path)
    {
        foreach (var access in Both)
        {
            var (full, error) = Resolve(path, access);
            Assert.Null(full);
            Assert.Contains("Windows 장치 이름", error);
        }
    }

    [Theory]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\C:\x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("//server/share/x")]
    public void Prefixed_and_unc_paths_are_rejected(string path)
    {
        foreach (var access in Both)
            Assert.Equal((null, "프로젝트 루트 기준 상대 경로만 받습니다."), Resolve(path, access));
    }

    [Theory]
    [InlineData("console.md")]
    [InlineData("COM10.txt")]
    [InlineData("connection/a.md")]
    [InlineData("docs/aux_notes.md")]
    public void Names_that_only_look_like_devices_are_allowed(string path)
    {
        Assert.Null(Resolve(path, SafePath.Access.Write).Error);
    }
}
