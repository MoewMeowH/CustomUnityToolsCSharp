using System.Text.Json;
using Microsoft.AspNetCore.Html;

namespace HB.Chat;

/// <summary>
/// 화면 아이콘 (4차 D3 — 노션 "설정, 휴지통등 text 클릭형식을 UI/UX 화"): 24×24 선 그림 SVG를 앱 안에 둔다 (외부 글꼴 · 스크립트 없이).
/// 모양은 이 파일에서 직접 그린 단순한 경로다. 화면 스크립트(chat.js)는 _Layout body의 data-icons로 같은 모음을 받는다.
/// 아이콘은 꾸밈(aria-hidden) — 단추 이름은 곁의 글이나 aria-label · title이 맡는다.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["plus"] = "M12 5v14M5 12h14",
        ["trash"] = "M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6",
        ["settings"] = "M4 6h9M17 6h3M15 4v4M4 12h3M11 12h9M9 10v4M4 18h11M19 18h1M17 16v4",
        ["back"] = "M15 5l-7 7 7 7",
        ["more"] = "M5 12h.01M12 12h.01M19 12h.01",
        ["panel"] = "M4 5h16v14H4zM15 5v14",
        ["reply"] = "M9 7l-5 5 5 5M4 12h10a6 6 0 0 1 6 6v1",
        ["external"] = "M14 4h6v6M20 4l-9 9M18 14v5H5V6h5",
        ["send"] = "M12 19V5M6 11l6-6 6 6",
        ["interrupt"] = "M13 3L5 14h6l-1 7 8-11h-6z",
        ["stop"] = "M7 7h10v10H7z",
        ["attach"] = "M20 11l-8 8a5 5 0 0 1-7-7l9-9a3 3 0 0 1 4 4l-9 9a1 1 0 0 1-1.5-1.5L15 7",
        ["x"] = "M6 6l12 12M18 6L6 18",
        ["restore"] = "M4 4v6h6M4 10a8 8 0 1 1 2 7",
        ["check"] = "M5 12l5 5 9-10",
        ["pencil"] = "M4 20h4L19 9l-4-4L4 16zM14 6l4 4",
        ["download"] = "M12 4v11M7 10l5 5 5-5M5 20h14",
        ["dashboard"] = "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z",
        ["branch"] = "M7 4v16M7 14c0-4 10-3 10-8M17 4v2",
        ["open"] = "M5 12h14M13 6l6 6-6 6",
        ["minus"] = "M5 12h14",
    };

    /// <summary>아이콘 SVG (모르는 이름이면 빈 글 — 화면이 깨지지 않게).</summary>
    public static HtmlString Svg(string name) =>
        Paths.TryGetValue(name, out var d)
            ? new HtmlString($"<svg class=\"icon icon-{name}\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\"><path d=\"{d}\"/></svg>")
            : HtmlString.Empty;

    /// <summary>화면 스크립트용 (이름 → 경로).</summary>
    public static string Json() => JsonSerializer.Serialize(Paths);

    public static IReadOnlyCollection<string> Names => Paths.Keys;
}
