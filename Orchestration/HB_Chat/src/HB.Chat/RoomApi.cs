using System.Text.Json;

namespace HB.Chat;

/// <summary>
/// 방 상태 한 줄 (데스크톱 앱이 읽는 JSON). Activity · Face는 이름 그대로 (Idle · Busy · Approval / FaceCatalog 상태 이름).
/// Preview는 방 목록 미리보기(80자)로, 설정 "알림에 답 앞부분 보이기"를 켰을 때만 싣는다 — 꺼져 있으면 null.
/// </summary>
public sealed record RoomRow(string Id, string Title, string Activity, bool Failed, int Unread, string Face, DateTimeOffset LastAt, string? Preview = null);

/// <summary>
/// `GET /api/rooms` (계획서 2-3): 휴지통 밖 방 요약, 최근 활동 순. 데스크톱 앱이 서버가 HB Chat인지 확인하고
/// 알림 · 트레이 상태를 정하는 데 쓴다. 읽기만 — 다른 사이트는 CORS가 없어 응답을 읽지 못하고 AllowedHosts는 localhost뿐.
/// 미리보기 글은 알림 본문 설정을 켰을 때만 싣는다 (기본 끔 — Windows 알림 센터에 남으므로).
/// </summary>
public static class RoomApi
{
    /// <summary>
    /// 이 끝점 전용 형식 (camelCase — 데스크톱 RoomStates가 "activity" 등으로 읽는다). SSE 형식과 묶지 않는다:
    /// 그쪽을 바꾸면 끌 때 확인이 조용히 사라진다 (/self-review 26d478170f C11).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<RoomRow> Rows(IEnumerable<RoomSummary> rooms, bool withPreview = false) =>
        [.. rooms.Select(r => new RoomRow(r.Id, r.Title, r.Activity.ToString(), r.Failed, r.Unread, r.Face.ToString(), r.LastAt,
            withPreview ? r.Preview : null))];
}
