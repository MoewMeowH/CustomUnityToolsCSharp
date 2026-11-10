using Microsoft.AspNetCore.Mvc;

namespace HB.Chat.Pages;

/// <summary>방 화면의 카드 · 기다리는 말 처리기: 승인 · 질문 · 지금 보내기 · 끼어들기.</summary>
public sealed partial class RoomModel
{
    /// <summary>"이 턴 동안 묻지 않기" 끄기.</summary>
    public IActionResult OnPostTrustOff(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        hub.EndTurnTrust(id);
        return new JsonResult(new { ok = true });
    }

    /// <summary>승인 카드 단추: choice = allow · turn · folder(이 폴더 늘 허용) · deny, reason은 거절 이유 (선택), picks는 모델 선택 카드에서 고른 값 (JSON, 선택).</summary>
    public IActionResult OnPostApprove(string id, string? approval, string? choice, string? reason, string? picks, string? room)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(approval)) return NotFound();
        if (ModeCard.ParsePicks(picks) is not { } chosen) return BadRequest("고른 값을 읽지 못했습니다.");
        // 새 방 열기 카드에서 고친 값 (허용 때) — 모양이 틀리면 null로 넘겨 허브가 "카드 값을 읽지 못했습니다"로 거절한다
        var roomPick = RoomOpen.Parse(room);
        ApprovalChoice? parsed = choice switch
        {
            "allow" => ApprovalChoice.Allow,
            "turn" => ApprovalChoice.AllowTurn,
            "folder" => ApprovalChoice.AllowFolder,
            "deny" => ApprovalChoice.Deny,
            _ => null,
        };
        if (parsed is null) return BadRequest("알 수 없는 선택입니다.");
        if (reason is { Length: > 500 }) reason = reason[..500];
        return hub.Decide(id, approval, parsed.Value, reason, chosen.Count == 0 ? null : chosen, roomPick) is { } error ? CardError(error) : new JsonResult(new { ok = true });
    }

    /// <summary>
    /// 카드 답 실패: 이미 끝난 카드면 409 — 단톡방 화면과 과정 보기가 같은 카드를 보여 한쪽에서 답한 뒤 다른 쪽을 누른 경우라 화면이 오류 창 없이 카드만 닫는다
    /// 그 밖의 실패는 지금처럼 400(오류 창).
    /// </summary>
    private IActionResult CardError(string error) =>
        error is RoomHub.QuestionDone or RoomHub.ApprovalDone
            ? new ContentResult { StatusCode = StatusCodes.Status409Conflict, Content = error, ContentType = "text/plain; charset=utf-8" }
            : BadRequest(error);

    /// <summary>기다리는 말 하나를 지금 보내기 (3차 b): 큐 맨 앞으로 옮기고 지금 턴을 끊는다.</summary>
    public IActionResult OnPostSendNow(string id, string? queued)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(queued)) return NotFound();
        return hub.SendNow(id, queued) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>질문 카드의 답 (3차 a). answers는 JSON 객체(질문 글 → 답). skip이면 답하지 않기.</summary>
    public IActionResult OnPostAnswer(string id, string? question, string? answers, bool skip)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(question)) return NotFound();
        Dictionary<string, string>? parsed = null;
        if (!skip)
        {
            if (answers is null || answers.Length > 64 * 1024) return BadRequest("답을 읽지 못했습니다.");
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(answers);
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest("답을 읽지 못했습니다.");
            }
            if (parsed is null) return BadRequest("답을 읽지 못했습니다.");
        }
        return hub.Answer(id, question, parsed) is { } error ? CardError(error) : new JsonResult(new { ok = true });
    }

    /// <summary>끼어들기: 지금 턴을 멈추고 이 말을 바로 보낸다. 큐의 다른 말은 남는다.</summary>
    public IActionResult OnPostInterrupt(string id, string? text)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        if (TooManyAttachments(text)) return BadRequest($"첨부는 한 말에 {Attachments.MaxPerMessage}개까지입니다.");
        if (SlashRefusal(id, text) is { } slash) return BadRequest(slash);
        if (store.Get(id) is { IsGroup: true }) return BadRequest("단톡방에는 끼어들기가 없습니다 — 중지한 뒤 보내세요.");
        if (ChildRefusal(id) is { } refusal) return BadRequest(refusal);
        return hub.Interrupt(id, text ?? "") is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }

    /// <summary>기다리는 말 하나 취소.</summary>
    public IActionResult OnPostCancelQueued(string id, string? queued)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(queued)) return NotFound();
        return hub.CancelQueued(id, queued) is { } error ? BadRequest(error) : new JsonResult(new { ok = true });
    }
}
