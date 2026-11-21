using System.Text.Json;

namespace HBLint.App.Desktop;

/// <summary>페이지가 창에 보내는 말.</summary>
internal abstract record HostMessage;

/// <summary>칸 옆 "찾아보기" — 고르기 창을 띄워 그 칸 값을 돌려준다.</summary>
internal sealed record PickMessage(string Field, string? Current) : HostMessage;

/// <summary>저장 안 한 변경이 있는지 — 창을 닫을 때 묻는 데 쓴다.</summary>
internal sealed record DirtyMessage(bool Dirty, string? File) : HostMessage;

/// <summary>
/// WebView2 메시지 해석. 앱 출처가 아닌 곳(다른 사이트 · 다른 포트)에서 온 말과 모르는 kind · field는 버린다 (null).
/// 창 → 페이지 답: <c>{"kind":"picked","field":…,"path":… 또는 null(취소)}</c>.
/// </summary>
internal static class HostMessages
{
    public static readonly string[] PickFields = ["rules", "out", "project"];

    public static HostMessage? Parse(Uri app, string source, string json)
    {
        if (!NavigationPolicy.IsAppOrigin(app, source))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                return null;
            switch (kind.GetString())
            {
                case "pick":
                    var field = String(root, "field");
                    return field != null && PickFields.Contains(field) ? new PickMessage(field, String(root, "current")) : null;
                case "dirty":
                    return root.TryGetProperty("dirty", out var dirty) && (dirty.ValueKind == JsonValueKind.True || dirty.ValueKind == JsonValueKind.False)
                        ? new DirtyMessage(dirty.GetBoolean(), String(root, "file"))
                        : null;
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Picked(string field, string? path) =>
        JsonSerializer.Serialize(new { kind = "picked", field, path });

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
