using System.Text.Json;

namespace HB.Chat;

/// <summary>지휘자 할 일 하나 (TaskCreate · TaskUpdate가 쓰는 파일 — 3차 0번 실측 B). Status: pending · in_progress · completed.</summary>
public sealed record StepItem(string Id, string Subject, string Status);

/// <summary>
/// 지휘자의 진행 단계 (3차 b): CLI가 세션마다 <c>~/.claude/tasks/&lt;세션 ID&gt;/&lt;번호&gt;.json</c>에 쓰는 할 일 목록을 읽는다
/// (<c>{id, subject, description, status, blocks, blockedBy}</c>, 0번 실측 B). 읽지 못한 파일은 건너뛴다 — 단계 표시만 빠지고 실행은 그대로.
/// </summary>
public static class Steps
{
    /// <summary>읽을 파일 수 · 파일 하나 크기 · 제목 글자 상한.</summary>
    public const int MaxFiles = 100, MaxFileBytes = 64 * 1024, MaxSubjectChars = 200;

    /// <summary>훑어볼 파일 이름 상한 — 번호 순으로 정렬한 뒤 앞 <see cref="MaxFiles"/>개만 연다.</summary>
    public const int MaxListed = 2_000;

    /// <summary>
    /// 번호 순 할 일 목록. 폴더 위치를 모르거나(null) 폴더가 없거나 세션 ID가 uuid 모양이 아니면 빈 목록. 던지지 않는다 — 단계 표시는 보조 기능이라
    /// 읽기 실패가 턴 결과를 바꾸지 않게 (/self-review ea28ba9185 C4).
    /// </summary>
    public static IReadOnlyList<StepItem> Read(string? tasksRoot, string sessionId)
    {
        if (tasksRoot is null || !Guid.TryParseExact(sessionId, "D", out _)) return []; // 경로에 들어가므로 모양부터
        var items = new List<StepItem>();
        try
        {
            var dir = Path.Combine(tasksRoot, sessionId);
            if (!Directory.Exists(dir)) return [];
            // 이름(번호) 순으로 정렬한 뒤 자른다 — 자른 뒤 정렬하면 100개가 넘을 때 앞 번호가 빠졌다 (ea28ba9185 C5)
            var files = Directory.EnumerateFiles(dir, "*.json").Take(MaxListed)
                .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : int.MaxValue).ThenBy(f => f, StringComparer.Ordinal)
                .Take(MaxFiles);
            foreach (var file in files)
            {
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes) continue;
                    // 쓰기 · 지우기를 막지 않게 연다 — CLI가 같은 파일을 고치는 순간 읽으면 그 쓰기(바꾸기)가 실패할 수 있다 (Windows 공유 모드, 시험에서 가짜 claude의 쓰기가 실패해 발견)
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(stream);
                    var e = doc.RootElement;
                    if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { } id || Str(e, "subject") is not { } subject) continue;
                    var status = Str(e, "status") ?? "pending";
                    if (status == "deleted") continue;
                    items.Add(new StepItem(id, subject.Length > MaxSubjectChars ? subject[..MaxSubjectChars] + "…" : subject, status));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        return [.. items.OrderBy(i => int.TryParse(i.Id, out var n) ? n : int.MaxValue).ThenBy(i => i.Id, StringComparer.Ordinal)];
    }

    /// <summary>짧은 진행 글: "2/5" (끝난 수 / 전체). 목록이 비면 null.</summary>
    public static string? Progress(IReadOnlyList<StepItem> steps) =>
        steps.Count == 0 ? null : $"{steps.Count(s => s.Status == "completed")}/{steps.Count}";

    /// <summary>지금 하는 단계 (in_progress인 첫 것). 없으면 null.</summary>
    public static StepItem? Current(IReadOnlyList<StepItem> steps) => steps.FirstOrDefault(s => s.Status == "in_progress");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;
}
