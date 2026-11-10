using Microsoft.AspNetCore.Mvc;

namespace HB.Chat.Pages;

/// <summary>방 화면의 첨부: 목록 · 올리기 · 보기.</summary>
public sealed partial class RoomModel
{
    /// <summary>곁칸 "첨부": 그 방 첨부 파일 (최근 것부터).</summary>
    public IReadOnlyList<(string Name, long Bytes, DateTimeOffset At)> AttachmentFiles =>
        Attachments.List(Attachments.Folder(options.Value.DataFolder, Room.Id));

    /// <summary>
    /// 파일 첨부: 하나씩 받아 그 방 첨부 폴더에 저장하고 절대 경로를 돌려준다 — 화면이 보낼 때 [첨부] 줄로 붙인다.
    /// 휴지통 방은 받지 않는다. 20MB 넘으면 거절.
    /// </summary>
    public async Task<IActionResult> OnPostAttach(string id, IFormFile? file)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        if (room.DeletedAt is not null) return BadRequest("휴지통에 있는 방입니다.");
        if (Request.ContentLength is > Attachments.MaxBytes + 64 * 1024 || file is { Length: > Attachments.MaxBytes })
            return StatusCode(StatusCodes.Status413PayloadTooLarge, $"파일 하나는 {Attachments.MaxBytes / 1024 / 1024}MB까지입니다.");
        if (file is not { Length: > 0 }) return BadRequest("빈 파일입니다.");
        try
        {
            await using var stream = file.OpenReadStream();
            var path = await Attachments.SaveAsync(Attachments.Folder(options.Value.DataFolder, id), file.FileName, stream, DateTimeOffset.Now,
                HttpContext.RequestAborted);
            var name = Path.GetFileName(path);
            return new JsonResult(new
            {
                ok = true, name, path, image = Attachments.IsImage(name),
                url = $"/Room/{id}?handler=Attachment&name={Uri.EscapeDataString(name)}",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BadRequest($"첨부를 저장하지 못했습니다: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult(); // 화면이 요청을 끊었다 — 임시 파일은 SaveAsync가 지웠다
        }
    }

    /// <summary>
    /// 첨부 보기: 그 방 폴더 안의 파일만. 그림(png · jpg · gif · webp)은 화면에, 나머지는 내려받기. nosniff.
    /// </summary>
    public IActionResult OnGetAttachment(string id, string? name)
    {
        if (!RoomStore.IsRoomId(id) || store.Get(id) is null) return NotFound();
        if (Attachments.Resolve(Attachments.Folder(options.Value.DataFolder, id), name) is not { } path) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin"; // 다른 사이트의 <img>로 있는지 떠보지 못하게
        Response.Headers.CacheControl = "private, max-age=3600";
        return Attachments.Images.TryGetValue(Path.GetExtension(path), out var type)
            ? PhysicalFile(path, type)
            : PhysicalFile(path, "application/octet-stream", Path.GetFileName(path));
    }

    private static bool TooManyAttachments(string? text) => text is not null && Attachments.Split(text).Paths.Count > Attachments.MaxPerMessage;
}
