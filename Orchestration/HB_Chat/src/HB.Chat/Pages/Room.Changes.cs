using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;

namespace HB.Chat.Pages;

/// <summary>곁칸 변경 탭 한 줄: git 변경 · 출처(이 방 · 다른 방 · 모름) · 다른 방이면 가장 최근 방 이름과 방 수.</summary>
public sealed record ChangeRow(GitEntry Entry, ChangeOrigin Origin, string? OtherName = null, int OtherCount = 0);

public enum ChangeOrigin { Mine, Other, Unknown }

/// <summary>
/// 변경 탭 내용. Rows는 변경 전부 (개수 · 인덱스 경고는 전부로 — 화면 목록만 <see cref="RoomModel.MaxChanges"/>개, /self-review sub C6).
/// Error가 있으면 git 목록 대신 이 방 기록(MyEdits)만 보인다.
/// </summary>
public sealed record ChangesView(string? Root, string? Error, IReadOnlyList<ChangeRow> Rows, IReadOnlyList<RoomEdit> MyEdits, IReadOnlyList<RepoPeer> Working)
{
    public int Count(ChangeOrigin origin) => Rows.Count(r => r.Origin == origin);

    public IReadOnlyList<ChangeRow> Shown => [.. Rows.Take(RoomModel.MaxChanges)];

    public bool Truncated => Rows.Count > RoomModel.MaxChanges;

    /// <summary>인덱스(staged)에 올라간 줄 중 이 방이 고치지 않은 것 — 커밋에 섞일 수 있음 (다른 세션의 git rm · mv가 섞인 일).</summary>
    public IReadOnlyList<ChangeRow> StagedNotMine => [.. Rows.Where(r => r.Entry.Staged && r.Origin != ChangeOrigin.Mine)];
}

/// <summary>
/// 곁칸 "변경" 탭 (결정 "보기만" · "저장소 전체"): 작업 폴더가 든 git 저장소의 변경 목록 · 파일별 diff · 출처 배지, 같은 저장소 다른 방 띠.
/// 출처는 방마다 쓰기 도구로 부른 파일 기록(<see cref="RoomEdits"/>) — Bash로 고친 파일은 출처 모름.
/// </summary>
public sealed partial class RoomModel
{
    /// <summary>변경 목록 상한 (넘으면 개수만).</summary>
    public const int MaxChanges = 300;

    public ChangesView? Changes { get; private set; }

    public async Task<IActionResult> OnGetChanges(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        Room = room;
        var ct = HttpContext.RequestAborted;
        var (_, peers) = await RepoPeers.FindAsync(room, store.All(), git, options.Value.WorkspacePath, hub.PeerActivity, store.Get, ct);
        var working = peers.Where(p => p.Working).ToList();
        if (WorkspacePath is not { } folder)
        {
            Changes = new(null, "작업 폴더를 찾지 못했습니다.", [], edits.For(id).Take(50).ToList(), working);
            return Partial("_RoomChanges", this);
        }
        var status = await git.StatusAsync(folder, ct);
        if (status.Error is not null || status.Root is null)
        {
            Changes = new(status.Root, status.Error ?? "git 저장소가 아닙니다.", [], edits.For(id).Take(50).ToList(), working);
            return Partial("_RoomChanges", this);
        }
        var names = peers.ToDictionary(p => p.RoomId, p => p.Name, StringComparer.Ordinal);
        var byPath = edits.ByPath(names.Keys.Append(id));
        var rows = status.Entries.Select(e => Origin(id, status.Root, status.HeadAt, e, byPath, names))
            .OrderBy(r => r.Origin).ThenBy(r => r.Entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Changes = new(status.Root, null, rows, [], working);
        return Partial("_RoomChanges", this);
    }

    /// <summary>
    /// 한 줄의 출처: 새 경로(이름 바꿈이면 옛 경로도)의 편집 기록 중 HEAD 커밋 뒤의 것만 보고(그 전 것은 이미 커밋된 일),
    /// **가장 최근 기록의 방**으로 정한다 — 이 방이 예전에 한 번 고친 파일이라고 다른 방의 새 변경까지 "이 방"이 되지 않게 (/self-review sub C1).
    /// 기록이 없거나 지금 없는 방이면 출처 모름 (Bash · 다른 프로그램 · 앱 밖 세션).
    /// </summary>
    internal static ChangeRow Origin(string roomId, string root, DateTimeOffset? headAt, GitEntry entry,
        IReadOnlyDictionary<string, IReadOnlyList<(string RoomId, RoomEdit Edit)>> byPath, IReadOnlyDictionary<string, string> names)
    {
        var hits = new[] { entry.Path, entry.OldPath }.OfType<string>()
            .SelectMany(p => byPath.TryGetValue(Path.GetFullPath(Path.Combine(root, p)), out var list) ? list : [])
            .Where(h => headAt is not { } head || h.Edit.At > head)
            .OrderByDescending(h => h.Edit.At).ToList();
        if (hits.Count == 0) return new(entry, ChangeOrigin.Unknown);
        if (hits[0].RoomId == roomId) return new(entry, ChangeOrigin.Mine);
        var others = hits.Where(h => h.RoomId != roomId && names.ContainsKey(h.RoomId)).Select(h => h.RoomId).Distinct(StringComparer.Ordinal).ToList();
        return others.Count > 0 ? new(entry, ChangeOrigin.Other, names[others[0]], others.Count) : new(entry, ChangeOrigin.Unknown);
    }

    /// <summary>
    /// 파일 하나 diff를 HTML 조각으로 (줄마다 + · − · @@ 색). 경로는 지금 git 변경 목록에 있는 것만 받는다 — 임의 파일을 읽지 않게.
    /// </summary>
    public async Task<IActionResult> OnGetChangeDiff(string id, string? path)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room || string.IsNullOrEmpty(path)) return NotFound();
        Room = room;
        if (WorkspacePath is not { } folder) return NotFound();
        var ct = HttpContext.RequestAborted;
        var status = await git.StatusAsync(folder, ct);
        if (status.Root is not { } root || status.Entries.FirstOrDefault(e => e.Path == path) is not { } entry) return NotFound();
        var (text, note) = await git.DiffAsync(root, entry, ct);
        var html = new StringBuilder();
        var enc = HtmlEncoder.Default;
        if (note is not null) html.Append("<p class=\"muted diff-note\">").Append(enc.Encode(note)).Append("</p>");
        if (text is not null)
        {
            html.Append("<pre class=\"diff\">");
            foreach (var line in text.Split('\n'))
            {
                var cls = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ? "meta"
                    : line.StartsWith('+') ? "add" : line.StartsWith('-') ? "del" : line.StartsWith("@@", StringComparison.Ordinal) ? "hunk"
                    : line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal) ? "meta" : null;
                html.Append(cls is null ? "<span>" : $"<span class=\"{cls}\">").Append(enc.Encode(line.TrimEnd('\r'))).Append("</span>\n");
            }
            html.Append("</pre>");
        }
        return Content(html.ToString(), "text/html; charset=utf-8");
    }

    /// <summary>입력칸 위 띠: 같은 저장소에서 작업 중인 다른 방 이름들 (목록 신호 때 화면이 받는다).</summary>
    public async Task<IActionResult> OnGetRepoPeers(string id)
    {
        Response.Headers.CacheControl = "no-store";
        if (!RoomStore.IsRoomId(id) || store.Get(id) is not { } room) return NotFound();
        var (_, peers) = await RepoPeers.FindAsync(room, store.All(), git, options.Value.WorkspacePath, hub.PeerActivity, store.Get, HttpContext.RequestAborted);
        return new JsonResult(new { working = peers.Where(p => p.Working).Select(p => p.Name).ToList() });
    }
}
