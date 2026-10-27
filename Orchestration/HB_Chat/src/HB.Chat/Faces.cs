using System.Text.Json;
using HB.Orchestration.ChatUi;
using Microsoft.Extensions.Options;

namespace HB.Chat;

/// <summary>지휘자 표정 상태 (계획서 2-2 b, 시안 B에서 가져옴). 실행 중 단계 · 끝난 턴 · 쉼.</summary>
public enum Face
{
    Idle,
    Thinking,
    Writing,
    Tool,
    Approval,
    Done,
    Failed,
    Stopped,
    Sleep,
}

/// <summary>
/// 표정 → 스티커 태그 → 그림 주소. 태그는 설정 Chat:Expressions(상태 이름 → 태그)가 먼저이고, 없으면 기본값
/// (사용자 결정 2026-10-28 "추천대로": 쉼 대기 · 생각 중 생각 · 답 쓰는 중 설명 · 도구 검색 · 승인 요청 · 끝남 긍정 · 실패 당황 · 중지 미안 · 오래 쉼 잠).
/// 그림이 없는 태그면 Url이 null — 화면은 글자 아바타 + 상태 글로 보인다.
/// </summary>
public sealed class FaceCatalog(StickerCatalog stickers, IOptions<ChatOptions> options)
{
    private static readonly Dictionary<Face, string> DefaultTags = new()
    {
        [Face.Idle] = "대기", [Face.Thinking] = "생각", [Face.Writing] = "설명", [Face.Tool] = "검색", [Face.Approval] = "요청",
        [Face.Done] = "긍정", [Face.Failed] = "당황", [Face.Stopped] = "미안", [Face.Sleep] = "잠",
    };

    public static string Label(Face face) => face switch
    {
        Face.Idle => "쉬는 중",
        Face.Thinking => "생각 중…",
        Face.Writing => "답 쓰는 중…",
        Face.Tool => "도구 실행 중",
        Face.Approval => "승인 기다림",
        Face.Done => "끝남",
        Face.Failed => "실패",
        Face.Stopped => "중지됨",
        Face.Sleep => "오래 쉼",
        _ => face.ToString(),
    };

    public string Tag(Face face) =>
        options.Value.Expressions.TryGetValue(face.ToString(), out var tag) && !string.IsNullOrWhiteSpace(tag) ? tag.Trim() : DefaultTags[face];

    /// <summary>그림 주소 (/sticker/{태그}). 그림 파일이 없으면 null.</summary>
    public string? Url(Face face) =>
        stickers.FileFor(Tag(face)) is null ? null : "/sticker/" + Uri.EscapeDataString(Tag(face));

    /// <summary>화면 스크립트용: 상태 이름 → { url, label }.</summary>
    public string Json() => JsonSerializer.Serialize(
        Enum.GetValues<Face>().ToDictionary(f => f.ToString(), f => new { url = Url(f), label = Label(f) }));
}
