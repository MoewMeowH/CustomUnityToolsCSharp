using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace HB.Chat.Desktop;

/// <summary>화면 테마 (5차 a · b): 채팅 서버 설정 화면에서 고른 값. `/api/appearance`의 theme와 같은 이름.</summary>
public enum AppTheme { System, Light, Discord }

/// <summary>
/// 창 틀 색 (5차 b): 맨 위 줄(Back · 글 · 누름 · 고름) + 화면이 뜨기 전 바탕(Page — 웹 화면의 --bg와 같아 흰 번쩍임이 없게).
/// 디코 다크는 HB_Chat _Layout의 디코 색, 밝게는 그 밝은 색과 맞춘다.
/// </summary>
public sealed record FrameColors(Color Back, Color Text, Color Muted, Color Hover, Color Selected, Color Accent, Color Page);

/// <summary>
/// 테마 → WebView2 색 설정 · 맨 위 줄 색 (5차 b). 화면 없는 로직 — 시험에서 부른다.
/// 테마를 아직 모르면(서버 준비 전 · 읽기 실패) 서버 기본값과 같은 디코 다크로 둔다.
/// </summary>
public static class Appearance
{
    public const AppTheme Default = AppTheme.Discord;

    public static readonly FrameColors Dark = new(
        Back: Color.FromArgb(0x1e, 0x1f, 0x22), Text: Color.FromArgb(0xdb, 0xde, 0xe1), Muted: Color.FromArgb(0x94, 0x9b, 0xa4),
        Hover: Color.FromArgb(0x2e, 0x30, 0x35), Selected: Color.FromArgb(0x40, 0x42, 0x49), Accent: Color.FromArgb(0x58, 0x65, 0xf2),
        Page: Color.FromArgb(0x31, 0x33, 0x38));

    public static readonly FrameColors Light = new(
        Back: Color.FromArgb(0xe9, 0xeb, 0xef), Text: Color.FromArgb(0x1d, 0x23, 0x30), Muted: Color.FromArgb(0x6b, 0x73, 0x85),
        Hover: Color.FromArgb(0xdc, 0xdf, 0xe5), Selected: Color.FromArgb(0xcf, 0xd3, 0xdb), Accent: Color.FromArgb(0x3b, 0x6e, 0xf5),
        Page: Color.FromArgb(0xf6, 0xf7, 0xf9));

    /// <summary>`/api/appearance` 응답 `{ "theme": "discord" }` → 테마. 모양이 틀리거나 모르는 이름이면 null (앞 값을 그대로 둔다).</summary>
    public static AppTheme? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("theme", out var t) || t.ValueKind != JsonValueKind.String)
                return null;
            return t.GetString() switch
            {
                "system" => AppTheme.System,
                "light" => AppTheme.Light,
                "discord" => AppTheme.Discord,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>`/api/appearance`의 notifyBody (알림에 답 앞부분 보이기). 없거나 모양이 틀리면 false — 본문을 띄우지 않는 쪽.</summary>
    public static bool NotifyBody(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("notifyBody", out var v)
                && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>서버 · 저장 파일의 이름 (Parse의 반대).</summary>
    public static string Name(AppTheme theme) => theme switch
    {
        AppTheme.System => "system",
        AppTheme.Light => "light",
        _ => "discord",
    };

    /// <summary>
    /// 지난번에 읽은 테마 (데이터 폴더 desktop-theme.json, `/api/appearance`와 같은 모양) — 켤 때 서버를 읽기 전 처음 값.
    /// 밝게를 고른 사용자가 켤 때마다 어두웠다 밝아지지 않게 (/self-review 993ef3b338 C5). 없거나 못 읽으면 null.
    /// </summary>
    public static AppTheme? LoadLast(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>읽은 테마를 남긴다. 못 쓰면 그대로 (다음에 켤 때 기본값으로 시작할 뿐).</summary>
    public static void SaveLast(string path, AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { theme = Name(theme) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>WebView2 색 설정 — 대시보드처럼 테마 설정이 없는 화면도 이 값(prefers-color-scheme)으로 밝기를 고른다.</summary>
    public static CoreWebView2PreferredColorScheme Scheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
        AppTheme.Discord => CoreWebView2PreferredColorScheme.Dark,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    /// <summary>창 틀(맨 위 줄 · 미니 창 제목 줄)을 어둡게 할지. 시스템 따름은 Windows 앱 모드를 따른다.</summary>
    public static bool IsDark(AppTheme theme, bool systemDark) => theme switch
    {
        AppTheme.Light => false,
        AppTheme.Discord => true,
        _ => systemDark,
    };

    public static FrameColors Colors(bool dark) => dark ? Dark : Light;

    /// <summary>Windows 앱 모드가 어둡게인지 (HKCU Personalize AppsUseLightTheme = 0). 값을 못 읽으면 밝게.</summary>
    public static bool SystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
