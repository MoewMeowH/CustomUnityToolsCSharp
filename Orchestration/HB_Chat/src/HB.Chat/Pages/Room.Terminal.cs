using Microsoft.AspNetCore.Mvc;

namespace HB.Chat.Pages;

/// <summary>방 화면의 터미널 · 지휘자 브라우저 곁칸 처리기.</summary>
public sealed partial class RoomModel
{
    // ---- 터미널 곁칸 ----

    // ---- 지휘자 브라우저 ----

    /// <summary>방 기록의 스크린샷 그림: 그 방 폴더 · 이름 모양이 맞을 때만, nosniff · 같은 출처.</summary>
    public IActionResult OnGetShot(string id, string? name)
    {
        if (RoomShots.PathOf(options.Value.DataFolder, id, name) is not { } path) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        Response.Headers.CacheControl = "private, max-age=86400"; // 이름이 시각이라 바뀌지 않는다
        return PhysicalFile(path, RoomShots.ContentType(path)); // 브라우저 png · Unity png/jpg
    }

    /// <summary>곁칸 브라우저 탭을 열 때: 마지막 실시간 화면(jpeg). 없으면 204.</summary>
    public IActionResult OnGetBrowserFrame(string id)
    {
        if (!RoomStore.IsRoomId(id)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff"; // 스크린샷 그림과 같게
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        return browser.LastFrame(id) is { } jpeg ? File(jpeg, "image/jpeg") : new NoContentResult();
    }

    /// <summary>터미널 탭을 열 때: 출력 버퍼 전체(base64)와 끝 오프셋 — 화면은 그보다 앞선 실시간 조각을 버린다.</summary>
    public IActionResult OnGetTerminal(string id, string? term)
    {
        if (!RoomStore.IsRoomId(id) || terminals.Get(id, term) is not { } s) return NotFound();
        var (data, end) = s.Snapshot();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { data = Convert.ToBase64String(data), end, view = s.View });
    }

    /// <summary>내 셸 열기 (결정 3: "+"는 PowerShell, ▾로 PowerShell · 명령 프롬프트). 사용자 본인 동작이라 카드 없음, 방 작업 폴더에서.</summary>
    public IActionResult OnPostTerminalOpen(string id, string? shell)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (options.Value.WorkspacePath(room.Workspace) is not { } workspace) return BadRequest("작업 폴더를 찾지 못했습니다.");
        return terminals.OpenShell(id, shell ?? "powershell", workspace, out var s) is { } why
            ? BadRequest(why)
            : new JsonResult(new { ok = true, id = s!.Id });
    }

    /// <summary>곁칸에서 친 키 — 내 셸만 (사용자 본인 입력, 결정 "입력도 (ConPTY)"). b64 = xterm onData 글의 UTF-8 바이트(base64), 한 번에 4KB까지 — 화면이 나눠 보낸다.</summary>
    public IActionResult OnPostTerminalInput(string id, string? term, string? b64)
    {
        // base64로 받는다 — 폼 바인딩이 공백만 있는 글(Enter "\r" · 스페이스 · 탭 한 번)을 null로 바꿔 키가 사라졌다 (5291 실제 확인)
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term) || string.IsNullOrEmpty(b64)) return NotFound();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return BadRequest("입력 모양이 올바르지 않습니다."); }
        if (bytes.Length is 0 or > 4096) return BadRequest("한 번에 보내는 입력이 너무 깁니다.");
        return terminals.Write(id, term, bytes) ? new JsonResult(new { ok = true }) : BadRequest("끝난 터미널입니다.");
    }

    public IActionResult OnPostTerminalResize(string id, string? term, int cols, int rows)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term)) return NotFound();
        terminals.Resize(id, term, cols, rows); // 끝났거나 범위 밖이면 무시
        return new JsonResult(new { ok = true });
    }

    public IActionResult OnPostTerminalStop(string id, string? term)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term)) return NotFound();
        return terminals.Stop(id, term, "사용자") is { } why ? BadRequest(why) : new JsonResult(new { ok = true });
    }

    /// <summary>끝난 터미널을 탭에서 닫는다 (목록에서 뺌).</summary>
    public IActionResult OnPostTerminalClose(string id, string? term)
    {
        if (!RoomStore.IsRoomId(id) || string.IsNullOrEmpty(term)) return NotFound();
        return terminals.Remove(id, term) ? new JsonResult(new { ok = true }) : BadRequest("돌고 있는 터미널은 먼저 중지하세요.");
    }
}
