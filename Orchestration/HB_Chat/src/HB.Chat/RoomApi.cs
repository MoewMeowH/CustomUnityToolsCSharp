using System.Text.Json;

namespace HB.Chat;

/// <summary>
/// 방 상태 한 줄 (데스크톱 앱이 읽는 JSON). Activity · Face는 이름 그대로 (Idle · Busy · Approval / FaceCatalog 상태 이름).
/// Preview는 방 목록 미리보기(80자). 끝점은 늘 싣고(펫 말풍선, 계획서 2026-10-29_바탕화면-펫 결정 "말풍선은 늘"), 토스트 본문은
/// 데스크톱이 설정 "알림에 답 앞부분 보이기"(/api/appearance notifyBody)로 가린다.
/// FaceUrl은 그 방 스티커 팩의 지금 표정 그림 주소 (상대 경로, 그림이 없는 태그면 null — 데스크톱 펫, 계획서 2026-10-29_바탕화면-펫).
/// Member는 단톡방의 구성원 이름 (승인 · 실행 · 답한 구성원, 계획서 2026-10-29_단톡방-차례-알림 — 설정과 상관없이 싣는다). 보통 방은 null.
/// </summary>
public sealed record RoomRow(string Id, string Title, string Activity, bool Failed, int Unread, string Face, DateTimeOffset LastAt, string? Preview = null,
    string? Member = null, string? FaceUrl = null);

/// <summary>
/// `GET /api/rooms` (계획서 2-3): 휴지통 밖 방 요약, 최근 활동 순. 데스크톱 앱이 서버가 HB Chat인지 확인하고
/// 알림 · 트레이 상태를 정하는 데 쓴다. 읽기만 — 다른 사이트는 CORS가 없어 응답을 읽지 못하고 AllowedHosts는 localhost뿐.
/// 미리보기 글은 늘 싣는다 — Windows 알림 센터에 남는 토스트 본문은 데스크톱이 알림 본문 설정(기본 끔)으로 가린다.
/// </summary>
public static class RoomApi
{
    /// <summary>
    /// 이 끝점 전용 형식 (camelCase — 데스크톱 RoomStates가 "activity" 등으로 읽는다). SSE 형식과 묶지 않는다:
    /// 그쪽을 바꾸면 끌 때 확인이 조용히 사라진다 (/self-review 26d478170f C11).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="faceUrl">방 줄 → 표정 그림 주소 (없으면 싣지 않는다).</param>
    public static IReadOnlyList<RoomRow> Rows(IEnumerable<RoomSummary> rooms, bool withPreview = false, Func<RoomSummary, string?>? faceUrl = null) =>
        [.. rooms.Select(r => new RoomRow(r.Id, r.Title, r.Activity.ToString(), r.Failed, r.Unread, r.Face.ToString(), r.LastAt,
            withPreview ? r.Preview : null, r.Member, faceUrl?.Invoke(r)))];
}
