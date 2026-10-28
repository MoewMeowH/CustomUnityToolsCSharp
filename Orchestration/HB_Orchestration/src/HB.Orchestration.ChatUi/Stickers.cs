using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HB.Orchestration.ChatUi;

/// <summary>
/// 채팅방 스티커 설정 (계획서 2026-10-27_에이전트-채팅방). appsettings.json의 Dashboard:Stickers에 기본값,
/// 개인 값(폴더 경로)은 appsettings.local.json(git 제외)에 둔다. 두 파일 모두 고치면 다시 켜지 않아도 반영된다.
/// </summary>
public sealed class StickerOptions
{
    /// <summary>그림 폴더 (절대 경로). 비우면 그림 없이 글자 배지로 보인다.</summary>
    public string? Dir { get; set; }

    /// <summary>말풍선 안 스티커 크기 (CSS 픽셀).</summary>
    public int SizePx { get; set; } = 96;

    /// <summary>태그 → 폴더 안 파일 이름. 이 목록에 있는 태그만 그림으로 바꾼다.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>의미 태그 → Files의 태그 (예: 완료 → 해냈다). 지휘자는 의미 태그나 Files 태그 아무거나 쓴다.</summary>
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 상황 → 태그 (외부 에이전트 · 서브에이전트 · 판정 말풍선에 화면이 붙인다). 값을 비우면 그 상황은 붙이지 않는다.
    /// 키는 <see cref="AutoSticker"/>의 상수.
    /// </summary>
    public Dictionary<string, string> Auto { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Dir에 그 파일이 없을 때 볼 폴더 (HB_Chat 4차 D2 스티커 팩 — 팩 폴더에 빠진 그림은 기본 폴더 것으로). 비우면 없음 — 대시보드는 쓰지 않는다.
    /// </summary>
    public string? FallbackDir { get; set; }

    /// <summary>그림 주소 앞부분 (태그가 뒤에 붙음). 기본 /sticker/ — HB_Chat 팩은 /sticker-pack/{팩}/.</summary>
    public string UrlBase { get; set; } = "/sticker/";
}

/// <summary>자동 스티커 상황 이름 (StickerOptions.Auto의 키).</summary>
public static class AutoSticker
{
    public const string CallNoFindings = "CallNoFindings";
    public const string CallFindings = "CallFindings";
    public const string CallWarning = "CallWarning";
    public const string CallRequests = "CallRequests";
    public const string CallQuota = "CallQuota";
    public const string CallRateLimit = "CallRateLimit";
    public const string CallTimeout = "CallTimeout";
    public const string CallFailed = "CallFailed";
    public const string SubagentResult = "SubagentResult";
    public const string VerdictMostlyAdopted = "VerdictMostlyAdopted";
    public const string VerdictMostlyRejected = "VerdictMostlyRejected";
    public const string VerdictMixed = "VerdictMixed";
}

/// <summary>
/// 태그 → 그림 파일. options는 부를 때마다 현재 설정을 준다 (쓰는 쪽이 IOptionsMonitor의 CurrentValue를 넘긴다) —
/// 설정을 고치면 다음 요청부터 반영된다.
/// </summary>
public sealed class StickerCatalog(Func<StickerOptions> options)
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".gif"] = "image/gif", [".webp"] = "image/webp", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
    };

    public StickerOptions Options => options();

    /// <summary>의미 태그면 Files 태그로 바꾼다. 모르는 태그는 null.</summary>
    public string? Resolve(string tag)
    {
        var o = Options;
        if (o.Aliases.TryGetValue(tag, out var target)) tag = target;
        return o.Files.ContainsKey(tag) ? tag : null;
    }

    /// <summary>상황에 붙일 태그 (Files에 있는 태그로 바꾼 값). 설정이 없거나 비었으면 null.</summary>
    public string? ForSituation(string situation) =>
        Options.Auto.TryGetValue(situation, out var tag) && !string.IsNullOrWhiteSpace(tag) ? Resolve(tag.Trim()) : null;

    /// <summary>
    /// 태그의 그림 파일 (폴더 안의 실제 파일, 허용 확장자). 폴더가 없거나 파일 이름이 폴더 밖을 가리키면 null.
    /// Dir에 없으면 FallbackDir에서 찾는다 (HB_Chat 4차 D2).
    /// </summary>
    public (string Path, string ContentType)? FileFor(string tag)
    {
        var o = Options;
        if (Resolve(tag) is not { } resolved) return null;
        var name = o.Files[resolved];
        if (string.IsNullOrWhiteSpace(name) || !ContentTypes.TryGetValue(Path.GetExtension(name), out var type)) return null;
        return (Find(o.Dir, name) ?? Find(o.FallbackDir, name)) is { } path ? (path, type) : null;
    }

    /// <summary>폴더 안의 그 파일 (폴더 밖을 가리키거나 없으면 null).</summary>
    private static string? Find(string? folder, string name)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string dir, full;
        try
        {
            dir = Path.GetFullPath(folder.Trim());
            full = Path.GetFullPath(Path.Combine(dir, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var prefix = Path.EndsInDirectorySeparator(dir) ? dir : dir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }
}

/// <summary>본문 HTML의 [[icon:태그]]를 스티커로 바꾼다.</summary>
public static partial class StickerRenderer
{
    /// <summary>
    /// Markdig로 만든 HTML에서 &lt;pre&gt; · &lt;code&gt; 밖의 [[icon:태그]]만 바꾼다. 모르는 태그는 글자 그대로 둔다.
    /// 태그는 HTML 인코딩된 채 들어오므로 풀어서 찾고, 내보낼 때 다시 인코딩한다.
    /// </summary>
    public static string Replace(string html, StickerCatalog catalog)
    {
        if (!html.Contains("[[icon:", StringComparison.Ordinal)) return html;
        var sb = new StringBuilder(html.Length);
        var pos = 0;
        foreach (Match code in CodeRegex().Matches(html))
        {
            sb.Append(ReplaceOutsideCode(html[pos..code.Index], catalog));
            sb.Append(code.Value);
            pos = code.Index + code.Length;
        }
        sb.Append(ReplaceOutsideCode(html[pos..], catalog));
        return sb.ToString();
    }

    private static string ReplaceOutsideCode(string html, StickerCatalog catalog) =>
        TagRegex().Replace(html, m => catalog.Resolve(WebUtility.HtmlDecode(m.Groups[1].Value)) is { } tag ? Html(tag, catalog) : m.Value);

    /// <summary>스티커 하나. 그림 파일이 있으면 img, 없으면 글자 배지.</summary>
    public static string Html(string tag, StickerCatalog catalog)
    {
        var enc = WebUtility.HtmlEncode(tag);
        if (catalog.FileFor(tag) is null) return $"<span class=\"sticker-text\">{enc}</span>";
        var size = Math.Clamp(catalog.Options.SizePx, 24, 512);
        return $"<img class=\"sticker\" src=\"{WebUtility.HtmlEncode(catalog.Options.UrlBase)}{Uri.EscapeDataString(tag)}\" alt=\"{enc}\" title=\"{enc}\" width=\"{size}\" height=\"{size}\" loading=\"lazy\" />";
    }

    [GeneratedRegex(@"<pre[\s>].*?</pre>|<code[\s>].*?</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeRegex();

    /// <summary>태그 이름: 대괄호 · 꺾쇠 · 따옴표 · 공백 없이 40자까지.</summary>
    [GeneratedRegex(@"\[\[icon:([^\[\]<>""\s]{1,40})\]\]")]
    private static partial Regex TagRegex();
}

public static class StickerEndpoints
{
    /// <summary>
    /// 채팅방 스티커 그림 /sticker/{tag}. 설정 목록(Files)에 있는 태그만, 스티커 폴더 안의 파일만 내보낸다.
    /// 그림이 커서(2026-10-27 기본 팩: 1254px, 장당 약 1.4MB) 하루 동안 브라우저 캐시에 둔다.
    /// </summary>
    public static IEndpointConventionBuilder MapStickerImages(this IEndpointRouteBuilder app) =>
        app.MapGet("/sticker/{tag}", (string tag, StickerCatalog catalog, HttpResponse response) =>
        {
            if (catalog.FileFor(tag) is not { } file) return Results.NotFound();
            response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(file.Path, file.ContentType, lastModified: File.GetLastWriteTimeUtc(file.Path));
        });
}
