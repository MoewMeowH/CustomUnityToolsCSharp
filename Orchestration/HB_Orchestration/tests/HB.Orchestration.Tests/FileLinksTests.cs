using HB.Orchestration.Dashboard;

namespace HB.Orchestration.Tests;

/// <summary>
/// Rider 링크: jetbrains:// 템플릿은 가장 깊은 프로젝트 폴더 기준 상대 경로(조각마다 인코딩, '/' 유지)와 프로젝트 이름을 넣고,
/// 어느 프로젝트에도 속하지 않으면 링크가 없다. {project}가 없는 예전 템플릿은 절대 경로 전체 인코딩 그대로.
/// recentSolutions.xml 파싱 (솔루션 파일 → 그 폴더, $USER_HOME$, 이름 우선순위).
/// (2026-10-19 Rider 2026.2.3에서 사용자와 확인: 상대 경로·한글 원문·한글만 인코딩 = 열림 / 절대 경로·'%2F'·솔루션 밖 = 안 열림)
/// </summary>
public sealed class FileLinksTests
{
    private const string Jb = DashboardOptions.DefaultFileLinkTemplate;

    private static readonly string Ws = Path.Combine(Path.GetTempPath(), "ws");
    private static readonly string Orch = Path.Combine(Ws, "Orchestration", "HB_Orchestration");

    private static readonly RiderProject[] Projects =
    [
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Ws)), "WS"),
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Orch)), "HB_Orchestration"),
    ];

    [Fact]
    public void Jetbrains_DeepestProject_RelativePath()
    {
        var link = FileLinks.Build("Orchestration/HB_Orchestration/src/A.cs:42", Ws, Jb, projects: Projects);

        Assert.Equal("jetbrains://rider/navigate/reference?project=HB_Orchestration&path=src/A.cs:42", link.Url);
    }

    [Fact]
    public void Jetbrains_KoreanSegmentsEncoded_SlashKept()
    {
        var link = FileLinks.Build("Orchestration/HB_Orchestration/docs/plans/이미지 첨부.md", Ws, Jb, projects: Projects);

        Assert.Equal("jetbrains://rider/navigate/reference?project=HB_Orchestration&path=docs/plans/"
                     + Uri.EscapeDataString("이미지 첨부.md") + ":1", link.Url);
        Assert.DoesNotContain("%2F", link.Url);
    }

    [Fact]
    public void Jetbrains_OutsideAnyProject_NoUrl()
    {
        var link = FileLinks.Build(Path.Combine(Path.GetTempPath(), "other", "B.cs"), null, Jb, projects: Projects);

        Assert.Null(link.Url);
    }

    [Fact]
    public void Jetbrains_SiblingFolderWithSamePrefix_NotMatched()
    {
        var link = FileLinks.Build(Path.Combine(Path.GetTempPath(), "ws2", "B.cs"), null, Jb, projects: Projects);

        Assert.Null(link.Url);
    }

    [Fact]
    public void Jetbrains_ExtraRoot_ResolvedThenMatched()
    {
        var roots = new Dictionary<string, string> { ["orch"] = Orch };

        var link = FileLinks.Build("@orch/src/A.cs:3-9", Ws, Jb, roots, Projects);

        Assert.Equal("jetbrains://rider/navigate/reference?project=HB_Orchestration&path=src/A.cs:3", link.Url);
    }

    [Fact]
    public void LegacyTemplate_AbsolutePathEncoded()
    {
        var link = FileLinks.Build("src/A.cs:7", Orch, "http://localhost:63342/api/file?file={path}&line={line}");

        var full = Path.GetFullPath(Path.Combine(Orch, "src/A.cs")).Replace('\\', '/');
        Assert.Equal($"http://localhost:63342/api/file?file={Uri.EscapeDataString(full)}&line=7", link.Url);
    }

    [Fact]
    public void ParseRecentSolutions()
    {
        const string xml = """
            <application><component name="RiderRecentProjectsManager"><option name="additionalInfo"><map>
              <entry key="F:/AI/ToolWithCS/Orchestration/HB_Orchestration/HB_Orchestration.slnx"><value>
                <RecentProjectMetaInfo displayName="Shown" opened="true"><option name="customProjectName" value="HB_Orchestration" /></RecentProjectMetaInfo>
              </value></entry>
              <entry key="$USER_HOME$/Downloads/PC/PC/Assembly-CSharp.csproj"><value><RecentProjectMetaInfo displayName="PC" /></value></entry>
              <entry key="F:/AI/ToolWithCS/EString/Script"><value><RecentProjectMetaInfo /></value></entry>
              <entry key="F:/broken" />
            </map></option></component></application>
            """;

        var list = RiderProjects.Parse(xml, @"C:\Users\me");

        Assert.Collection(list,
            p => { Assert.Equal("HB_Orchestration", p.Name); Assert.Equal(Path.GetFullPath("F:/AI/ToolWithCS/Orchestration/HB_Orchestration"), p.BaseDir); },
            p => { Assert.Equal("PC", p.Name); Assert.Equal(Path.GetFullPath("C:/Users/me/Downloads/PC/PC"), p.BaseDir); },
            p => { Assert.Equal("Script", p.Name); Assert.Equal(Path.GetFullPath("F:/AI/ToolWithCS/EString/Script"), p.BaseDir); });
    }
}
