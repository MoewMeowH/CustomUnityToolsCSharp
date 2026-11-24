using System.Text.Json;

namespace HB.Chat.Desktop;

/// <summary>`/api/limits/alerts`의 한도 문턱 알림 한 줄 (데스크톱이 쓰는 칸만 — 글은 서버가 만든다). At은 서버가 알림을 만든 시각.</summary>
public sealed record LimitAlertRow(string Id, string Title, string Text, DateTimeOffset At = default);

/// <summary>
/// 한도 문턱 알림 읽기 · 판정: 처음 받은 목록 중 앱을 켜기 전에 생긴 것은 알리지 않고(켰을 때 쏟아지지 않게 — 방 알림과 같은 규칙), 그 뒤 처음 보는 ID만.
/// 켠 직후 첫 화면 그리기가 만든 새 알림은 처음 목록에 들어 있어도 알린다 (/self-review sub C3).
/// </summary>
public sealed class LimitNotices(DateTimeOffset started)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private HashSet<string>? _seen;

    /// <summary>알림 목록. 읽지 못하면 null (판정을 바꾸지 않는다).</summary>
    public static IReadOnlyList<LimitAlertRow>? Parse(string json)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<List<LimitAlertRow?>>(json, Json);
            return rows is null ? null : [.. rows.Where(r => r is { Id.Length: > 0, Title: not null }).Select(r => r! with { Text = r.Text ?? "" })];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>받은 목록 중 새로 알릴 것 (받은 차례대로).</summary>
    public IReadOnlyList<LimitAlertRow> Update(IReadOnlyList<LimitAlertRow> rows)
    {
        if (_seen is null)
        {
            _seen = [.. rows.Select(r => r.Id)];
            return [.. rows.Where(r => r.At >= started)];
        }
        return [.. rows.Where(r => _seen.Add(r.Id))];
    }

    /// <summary>알림 한 장의 제목 · 글: 하나면 그대로(글이 비면 제목 — 풍선은 빈 글을 받지 않음), 여럿이면 개수와 제목들.</summary>
    public static (string Title, string Text) Balloon(IReadOnlyList<LimitAlertRow> fresh) =>
        fresh.Count == 1 ? (fresh[0].Title, fresh[0].Text.Length > 0 ? fresh[0].Text : fresh[0].Title)
            : ($"한도 알림 {fresh.Count}개", string.Join("\n", fresh.Select(a => a.Title)));
}
