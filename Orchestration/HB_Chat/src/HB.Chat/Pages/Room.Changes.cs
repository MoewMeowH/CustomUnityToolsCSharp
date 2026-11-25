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

    /// <summary><see cref="GitChanges.IgnoreFile"/>로 숨긴 미추적 줄 수.</summary>
    public int Hidden { get; init; }

    /// <summary>"이 방 파일만 add" 명령 (<see cref="AddCommand"/>). 이 방 줄이 없으면 null.</summary>
    public (string Command, string Prompt)? Add => Root is { } root ? AddCommand(root, Rows, StagedNotMine.Count) : null;

    /// <summary>
    /// 셸마다 다르게 풀리는 글자 — 큰따옴표 안에서도 PowerShell은 ` $와 둥근 따옴표(“ ” „ ‘ ’)를, bash는 $ ` 와 대화형이면 !를 푼다 (sub_self C3).
    /// </summary>
    private static bool Risky(string s) => s.IndexOfAny(['"', '`', '$', '!', '“', '”', '„', '‘', '’']) >= 0;

    /// <summary>명령 길이 상한 — Windows 명령줄(32K)보다 넉넉히 작게. 넘는 경로는 빼고 알린다 (sub_self C4).</summary>
    public const int MaxCommandChars = 8000;

    /// <summary>
    /// 이 방 출처 줄만 올리는 명령과 지휘자에게 보낼 글 (결정 "입력칸 + 복사 둘 다"). 앱은 실행하지 않는다 (결정 "보기만").
    /// 넣는 줄: 작업 트리 쪽 변경이 있는 줄(Work ≠ ' ')과 미추적 — 이미 인덱스에 다 오른 줄(staged 이름 바꿈 · git rm 삭제)은 빼고,
    /// 옛 경로도 넣지 않는다 (인덱스 · 작업 트리 어디에도 없어 git add가 명령 전체를 실패시킨다 — sub_self C2 · 지휘자 K1).
    /// git이 경로를 패턴(* ? [ ])으로 풀지 않게 --literal-pathspecs (sub_self C1). 위험 글자 경로 · 길이를 넘는 경로는 빼고 글에 알린다.
    /// 넣을 경로가 없거나 루트에 위험 글자가 있으면 null.
    /// </summary>
    public static (string Command, string Prompt)? AddCommand(string root, IReadOnlyList<ChangeRow> rows, int stagedNotMine)
    {
        var paths = rows.Where(r => r.Origin == ChangeOrigin.Mine && (r.Entry.Untracked || r.Entry.Work != ' ')).Select(r => r.Entry.Path)
            .Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0 || Risky(root)) return null;
        var head = $"git -C \"{root}\" --literal-pathspecs add --";
        var safe = new List<string>();
        var (risky, tooLong, length) = (0, 0, head.Length);
        foreach (var p in paths)
        {
            if (Risky(p)) { risky++; continue; }
            if (length + p.Length + 3 > MaxCommandChars) { tooLong++; continue; }
            safe.Add(p);
            length += p.Length + 3;
        }
        if (safe.Count == 0) return null;
        var command = head + " " + string.Join(' ', safe.Select(p => $"\"{p}\""));
        var prompt = new StringBuilder("이 방에서 고친 파일만 올려 줘 (다른 방 · 출처 모름 파일은 올리지 말 것):\n").Append(command);
        if (stagedNotMine > 0) prompt.Append($"\n인덱스에 이 방이 고치지 않은 파일 {stagedNotMine}개가 이미 있음 — 커밋 전에 git restore --staged로 뺄지 물어볼 것.");
        if (risky > 0) prompt.Append($"\n따옴표 · $ · ` · ! 가 든 경로 {risky}개는 명령에서 뺐음 — 따로 확인할 것.");
        if (tooLong > 0) prompt.Append($"\n명령이 너무 길어 경로 {tooLong}개를 뺐음 — 나눠서 올릴 것.");
        return (command, prompt.ToString());
    }
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
        Changes = new(status.Root, null, rows, [], working) { Hidden = status.Hidden };
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
