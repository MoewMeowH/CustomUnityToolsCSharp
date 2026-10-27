using HB.Orchestration.ChatUi;
using HB.Orchestration.Shared;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace HB.Chat.Pages;

/// <summary>
/// 방 하나. 말풍선은 세션 기록에서 읽는다 (앱은 말 본문을 따로 저장하지 않는다). 진행 중 턴은 /events/{id} SSE로 흘린다.
/// ?handler=Messages: 말풍선 한 쪽 — 맨 끝 50개, before=줄이면 그 앞 50개(위로 불러오기), after=줄이면 그 뒤 전부(턴이 끝난 뒤 새 말, 계획서 2-2 b).
/// ?handler=Pane: 방 전환 때 오른쪽 칸만 (2-2 a). Send · Stop · MarkRead는 POST (antiforgery + 같은 출처).
/// </summary>
public sealed class RoomModel(
    RoomStore store, RoomHub hub, RoomList list, ChatTranscripts transcripts, StickerCatalog stickers, FaceCatalog faces, IOptions<ChatOptions> options)
    : PageModel
{
    public Room Room { get; private set; } = null!;
    public ChatTranscript? Transcript { get; private set; }
    public string Title => RoomList.TitleOf(Room, Transcript);

    /// <summary>이번에 그릴 말풍선 쪽 (Load 뒤 맨 끝 50개, Messages 핸들러가 before · after로 바꾼다).</summary>
    public MessagePage Messages { get; private set; } = new([], false);

    /// <summary>after로 새 말만 붙이는 응답이면 true — 빈 방 안내 · 읽기 실패 줄을 그리지 않는다.</summary>
    public bool Fragment { get; private set; }

    /// <summary>세션 기록의 마지막 말 줄 번호 (읽음 표시 · 새 말 받기의 기준).</summary>
    public int LastLine => RoomList.LastLine(Transcript);

    public FaceCatalog Faces => faces;

    /// <summary>머리말 표정 (끝난 턴의 잠깐 표정 포함).</summary>
    public Face Face => hub.FaceOf(Room);

    /// <summary>왼쪽 방 목록 (첫 화면에서만 만든다 — 방 전환 · 말풍선 다시 받기에는 쓰지 않음).</summary>
    public SidebarView Sidebar => new(list.All(), Room.Id);
    /// <summary>대시보드 채팅방 (세션 기록이 생긴 뒤부터 보인다 — 첫 말 전에는 대시보드가 404).</summary>
    public string? DashboardLink =>
        options.Value.DashboardUrl is { Length: > 0 } url ? $"{url.TrimEnd('/')}/Room/{Room.SessionId}" : null;

    /// <summary>명령 방: 작업 폴더 · 사용자 설정이 이미 미리 허용한 명령 (카드 없이 실행됨).</summary>
    public IReadOnlyList<(string Source, string Rule)> InheritedRules =>
        Room.Level == PermissionLevel.Command ? hub.InheritedCommandRules(Room) : [];

    public IActionResult OnGet(string id)
    {
        if (!Load(id)) return NotFound();
        return Page();
    }

    public IActionResult OnGetPane(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        return Partial("_RoomPane", this);
    }

    /// <summary>
    /// 읽음 표시: 화면이 맨 아래를 보고 있을 때 마지막 말의 줄 번호를 보낸다. 세션 기록 마지막 줄로 자르고 앞으로만 간다
    /// (늦게 온 작은 값이 되돌리지 않게). 바뀌었을 때만 저장하고 목록 알림을 낸다 (<see cref="RoomList.NextReadUpTo"/>).
    /// </summary>
    public IActionResult OnPostMarkRead(string id, int line)
    {
        if (!RoomStore.IsRoomId(id) || line < 0 || store.Get(id) is not { } current) return NotFound();
        if (RoomList.NextReadUpTo(current.ReadUpTo, line, RoomList.LastLine(transcripts.Read(current.SessionId))) is not { } next)
            return new JsonResult(new { ok = true });
        try
        {
            if (store.Update(id, r => next > (r.ReadUpTo ?? int.MinValue) ? r with { ReadUpTo = next } : r) is null) return NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest($"읽음 표시를 저장하지 못했습니다: {ex.Message}");
        }
        hub.Touch(id);
        return new JsonResult(new { ok = true });
    }

    public IActionResult OnGetMessages(string id, int? before, int? after)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Load(id)) return NotFound();
        // 읽기 실패는 실패로 돌려준다: 화면이 앞 말풍선을 그대로 둔다 (대시보드 채팅방과 같은 처리).
        if (Transcript?.FormatError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        Messages = RoomList.Page(Transcript?.Entries ?? [], before, after);
        Fragment = before is not null || after is not null;
        return Partial("_Messages", this);
    }

    public IActionResult OnPostSend(string id, string? text)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        return hub.Send(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    public IActionResult OnPostStop(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.Stop(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>승인 카드 단추: choice = allow · always · deny, reason은 거절 이유 (선택).</summary>
    public IActionResult OnPostApprove(string id, string? approval, string? choice, string? reason)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(approval)) return NotFound();
        ApprovalChoice? parsed = choice switch
        {
            "allow" => ApprovalChoice.Allow,
            "always" => ApprovalChoice.AlwaysAllow,
            "deny" => ApprovalChoice.Deny,
            _ => null,
        };
        if (parsed is null) return BadRequest("알 수 없는 선택입니다.");
        if (reason is { Length: > 500 }) reason = reason[..500];
        return hub.Decide(id, approval, parsed.Value, reason) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>"이 방에서 항상 허용" 규칙 지우기.</summary>
    public IActionResult OnPostRemoveRule(string id, string? rule)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(rule)) return NotFound();
        return hub.RemoveRule(id, rule) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    private bool Load(string id)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return false;
        Room = room;
        Transcript = transcripts.Read(room.SessionId);
        Messages = RoomList.Page(Transcript?.FormatError is null ? Transcript?.Entries ?? [] : [], null, null);
        return true;
    }

    /// <summary>토큰 수 짧게 (8K · 1.2M). K로 반올림해 1000이 되면 M으로 (/self-review 83b453653b C10 — "1000K"가 나오지 않게).</summary>
    public static string Tokens(long n)
    {
        if (n < 1_000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var k = Math.Round(n / 1_000d);
        return k < 1_000
            ? k.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "K"
            : (n / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>끝난 턴 표정을 앞으로 보일 밀리초 (없으면 0) · 그 표정을 보이는 전체 시간 (화면 타이머, 83b453653b C11).</summary>
    public long EndFaceLeftMs => (long)(hub.EndFaceLeft(Room.Id)?.TotalMilliseconds ?? 0);
    public static long EndFaceMs => (long)RoomHub.EndFaceFor.TotalMilliseconds;

    public HtmlString Render(ChatEntry e) =>
        new(StickerRenderer.Replace(SafeMarkdown.ToHtml(e.Text, e.Speaker == ChatSpeaker.User ? SafeMarkdown.KeepLineBreaks : SafeMarkdown.Default), stickers));

    /// <summary>중지한 턴의 자리표시 답 — 지휘자 말풍선 대신 안내 한 줄로 (<see cref="RoomList.IsStoppedTurn"/>).</summary>
    public static bool IsStoppedTurn(ChatEntry e) => RoomList.IsStoppedTurn(e);

    public static string RoleClass(ChatSpeaker s) => s switch
    {
        ChatSpeaker.User => "user",
        ChatSpeaker.Conductor => "conductor",
        _ => "system",
    };
}
