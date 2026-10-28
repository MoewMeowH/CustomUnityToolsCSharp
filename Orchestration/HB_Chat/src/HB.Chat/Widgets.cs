namespace HB.Chat;

/// <summary>
/// 위젯 (계획서 2-2 f): 지휘자가 ```widget 코드 블록에 쓴 HTML 한 장을 말풍선 안 격리 iframe에 그린다.
/// 그리기는 화면(chat.js widgets — 처음엔 스크립트 없이, "실행"을 누르면 sandbox="allow-scripts" + srcdoc 문서 CSP),
/// 서버는 말풍선 HTML을 그대로 두고 아래만 정한다.
/// ```chart(차트 시안, 2026-10-29)는 Chart.js 설정 JSON — 화면(chat.js charts)이 앱에 넣어 둔 wwwroot/lib/chart.umd.min.js와 같은 격리 틀에 그린다.
/// </summary>
public static class Widgets
{
    /// <summary>
    /// 앱 페이지 CSP: iframe이 주소를 불러오지 못하게 한다 — 위젯이 location을 바꿔 글을 밖으로 보내는 길을 막는다 (srcdoc은 가져오기가 아니라 그대로 그려진다).
    /// 다른 지시어는 넣지 않는다 (지금 화면의 인라인 스크립트 · 스타일을 그대로 두기 위해).
    /// 2-3 WebView2 껍데기도 이 헤더가 붙은 HTTP 응답으로만 화면을 띄운다 (NavigateToString · 파일로 띄우면 빠진다 — abc6cec3a6 C4).
    /// </summary>
    public const string PageCsp = "frame-src 'none'";

    /// <summary>모든 응답에 <see cref="PageCsp"/> (그림 · 첨부 · SSE에도 붙지만 해가 없다 — 빠뜨리는 쪽이 위험).</summary>
    public static IApplicationBuilder UseWidgetCsp(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers.ContentSecurityPolicy = PageCsp;
        await next();
    });

    /// <summary>방 규칙에 넣는 형식 안내 (대화 단계 포함 모든 방).</summary>
    public const string Rule =
        " 도표 · 그림 · 작은 계산기처럼 상호작용이나 그림이 필요한 것은 ```widget 코드 블록에 HTML 한 장으로 쓴다 — 말풍선 안 격리된 틀에 그려진다 " +
        "(인라인 CSS · JS와 data: 그림만, 외부 파일 · 네트워크 · 폼 전송 · 새 창 · 저장소는 막힌다. 스크립트는 사용자가 '실행'을 눌러야 돈다). " +
        "수치 차트(막대 · 선 · 원 · 도넛 · 레이더 · 극좌표 · 산점도 · 버블)는 위젯 대신 ```chart 코드 블록에 Chart.js 설정 JSON 하나(type · data · options)만 쓴다 — " +
        "함수 · 스크립트 없이 순수 JSON, 앱이 Chart.js로 바로 그린다. " +
        "Markdown 표 · 목록으로 되는 것은 Markdown으로 쓰고, 코드로 보여 줄 HTML은 ```html로 쓴다. 위젯으로 승인 카드 · 입력칸 · 이 앱 화면을 흉내 내지 않는다.";
}
