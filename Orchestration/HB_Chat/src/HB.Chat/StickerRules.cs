using HB.Orchestration.ChatUi;

namespace HB.Chat;

/// <summary>
/// 스티커 방 규칙: Claude 방 규칙 뒤에 붙인다 (Codex 방은 넣지 않음 — 사용자 결정). 태그 목록 · 쓸 때는 스티커 설정(Aliases · Hints)에서 읽는다 —
/// 규칙 글과 설정 태그가 어긋나지 않게. 고정 글에는 태그 이름을 쓰지 않는다 (태그별 조건은 Hints로).
/// </summary>
public static class StickerRules
{
    /// <summary>방 규칙에 붙일 글. 그림으로 이어지는 의미 태그가 없으면 빈 글 (안내 없음).</summary>
    public static string For(StickerOptions? o)
    {
        if (o is null) return "";
        var tags = o.Aliases.Where(a => o.Files.ContainsKey(a.Value)).Select(a => a.Key).ToList();
        if (tags.Count == 0) return "";
        var hints = tags
            .Where(t => o.Hints.TryGetValue(t, out var h) && !string.IsNullOrWhiteSpace(h))
            .Select(t => $"{t} = {o.Hints[t].Trim()}")
            .ToList();
        return
            " 노가리나 작업 보고의 맨 끝에 [[icon:태그]]를 따로 한 줄로 0~1개 쓰면 화면이 스티커 그림으로 바꾼다. 문장마다 붙이지 않는다. " +
            $"태그: {string.Join(" · ", tags)}. 목록에 없는 태그는 만들지 않는다." +
            (hints.Count > 0 ? $" 쓸 때: {string.Join(" · ", hints)}." : "") +
            " 같은 태그를 연달아 쓰지 않는다 — 앞 답과 비슷한 상황이면 가까운 다른 태그를 고르거나 생략한다. 노가리에는 감정 쪽 태그를 먼저 본다." +
            " 사용자가 화났거나 심각한 문제면 쓰지 않는다. 계획서 · 작업 로그 · 커밋 메시지 · run_mode input · record_verdicts · Slack 글 · 파일 내용에는 쓰지 않는다.";
    }
}
