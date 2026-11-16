using System.Reflection;

namespace HB.Orchestration.Dashboard;

/// <summary>
/// 화면이 쓰는 스크립트 파일 /lib/{name}. 대시보드에는 정적 파일 서빙이 없어, 어셈블리에 넣어 둔 파일을 이 경로 하나가 내보낸다
/// (실행 폴더가 어디든 찾는다). 목록에 있는 이름만 내보낸다.
/// </summary>
public static class LibFiles
{
    private const string Script = "text/javascript; charset=utf-8";

    /// <summary>내보내는 파일 이름 → 내용 형식. 내장 리소스 이름은 "lib/{이름}" (csproj의 LogicalName).</summary>
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        ["x6.min.js"] = Script,      // AntV X6 (Lib/x6/README.md)
        ["mode-graph.js"] = Script,  // 모드 화면의 그래프 편집기
    };

    /// <summary>주소에 붙이는 판 표시 — 빌드마다 달라져 브라우저가 옛 파일을 쓰지 않는다.</summary>
    public static string Version { get; } = typeof(LibFiles).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    /// <summary>목록에 없는 이름이면 null.</summary>
    public static (Stream Content, string ContentType)? Open(string name)
    {
        if (!ContentTypes.TryGetValue(name, out var contentType)) return null;
        var stream = typeof(LibFiles).Assembly.GetManifestResourceStream("lib/" + name);
        return stream is null ? null : (stream, contentType);
    }

    public static IEndpointConventionBuilder MapLibFiles(this IEndpointRouteBuilder app) =>
        app.MapGet("/lib/{name}", (string name, HttpResponse response) =>
        {
            if (Open(name) is not { } file) return Results.NotFound();
            response.Headers.CacheControl = "private, max-age=86400"; // 주소의 판 표시(?v=)가 빌드마다 바뀐다
            return Results.Stream(file.Content, file.ContentType);
        });
}
