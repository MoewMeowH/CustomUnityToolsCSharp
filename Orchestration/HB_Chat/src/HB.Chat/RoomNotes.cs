using System.Text;
using System.Text.Json;
using HB.Orchestration.Shared;
using Microsoft.Extensions.Options;

namespace HB.Chat;

/// <summary>방 기록 줄 하나 — 승인 결과 · 백그라운드 정리 표시. Denied면 거절 · 시간 초과처럼 흐린 색.</summary>
public sealed record RoomNote(DateTimeOffset At, string Text, bool Denied)
{
    /// <summary>지휘자 브라우저 스크린샷 파일 이름 (그 방 browser-shots 폴더) — 기록 화면이 작은 그림으로 그린다.</summary>
    public string? Image { get; init; }
}

/// <summary>방 기록 줄의 자리: 말 → 그 말 앞에 보일 줄들, 마지막 말 뒤의 줄(Trailing). 외부 실행 말풍선(RunPlacement)과 같은 규칙.</summary>
public sealed record NotePlacement(IReadOnlyDictionary<ChatEntry, IReadOnlyList<RoomNote>> Before, IReadOnlyList<RoomNote> Trailing)
{
    public static readonly NotePlacement Empty = new(new Dictionary<ChatEntry, IReadOnlyList<RoomNote>>(ReferenceEqualityComparer.Instance), []);

    public IReadOnlyList<RoomNote> For(ChatEntry e) => Before.TryGetValue(e, out var list) ? list : [];

    /// <summary>시각으로 끼운다 — 실행 말풍선과 같은 규칙 (<see cref="TimePlacement"/>).</summary>
    public static NotePlacement Place(IReadOnlyList<ChatEntry> entries, IEnumerable<RoomNote> notes)
    {
        var (before, trailing) = TimePlacement.Place(entries, notes, n => n.At);
        return new NotePlacement(before, trailing);
    }
}

/// <summary>
/// 방 기록 줄 읽기: 승인 기록(데이터 폴더 approvals.jsonl — 모든 방이 한 파일)에서 그 방 줄만. 진행 칸에만 있던 승인 결과 · 정리 표시를
/// 기록 속 제자리에 그려 새로고침 뒤에도 남긴다 (사용자 데스크톱 확인 2026-10-29: 두 번째 답 아래로 몰림 · "끝남" 3초뿐).
/// 덧붙은 줄만 읽는다 (파일이 줄면 처음부터). 질문 카드 답은 이미 '나' 말풍선이라 뺀다. 읽기 실패는 줄 없이 그린다.
/// </summary>
public sealed class RoomNotes(Func<string> dataFolder)
{
    public RoomNotes(IOptions<ChatOptions> options) : this(() => options.Value.DataFolder) { }

    /// <summary>정리 표시를 쓸 때의 도구 이름 (승인 기록 tool 칸).</summary>
    public const string BackgroundTool = "백그라운드";

    /// <summary>터미널 시작 · 끝 줄의 도구 이름. 시작 카드 결과는 "터미널"로 따로 남는다.</summary>
    public const string TerminalTool = "터미널 상태";

    /// <summary>지휘자 브라우저 스크린샷 줄 (결정 "기록에 그림으로 남김") — 요약 칸 = 그림 파일 이름.</summary>
    public const string BrowserShotTool = "브라우저 스크린샷";

    /// <summary>Unity 캡처 그림 줄 — 브라우저 스크린샷과 같은 모양.</summary>
    public const string UnityShotTool = "Unity 스크린샷";

    private readonly Lock _gate = new();
    private string? _path;
    private long _end;
    private readonly Dictionary<string, List<RoomNote>> _byRoom = new(StringComparer.Ordinal);

    public NotePlacement For(string roomId, ChatTranscript? transcript)
    {
        if (transcript is not { FormatError: null } t || t.Entries.Count == 0) return NotePlacement.Empty;
        try
        {
            var notes = Notes(roomId);
            return notes.Count == 0 ? NotePlacement.Empty : NotePlacement.Place(t.Entries, notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NotePlacement.Empty;
        }
    }

    /// <summary>그 방 기록 줄 (시각 차례). 시험용으로도 쓴다.</summary>
    public IReadOnlyList<RoomNote> Notes(string roomId)
    {
        lock (_gate)
        {
            Refresh();
            return _byRoom.TryGetValue(roomId, out var list) ? [.. list] : [];
        }
    }

    private void Refresh()
    {
        var path = Path.Combine(dataFolder(), "approvals.jsonl");
        var info = new FileInfo(path);
        if (path != _path || !info.Exists || info.Length < _end)
        {
            _path = path;
            _end = 0;
            _byRoom.Clear();
        }
        if (!info.Exists || info.Length == _end) return;
        byte[] tail;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // 길이를 본 뒤 여는 사이에 파일이 줄었으면 처음부터 (/self-review e464ec29ba C7)
            if (stream.Length < _end)
            {
                _end = 0;
                _byRoom.Clear();
            }
            stream.Seek(_end, SeekOrigin.Begin);
            tail = new byte[stream.Length - _end];
            stream.ReadExactly(tail);
        }
        // 마지막 줄바꿈까지만 — 끝에 줄바꿈이 아직 없는 줄(쓰는 중)은 다음에 다시 읽는다
        var last = Array.LastIndexOf(tail, (byte)'\n');
        if (last < 0) return;
        foreach (var line in Encoding.UTF8.GetString(tail, 0, last).Split('\n'))
        {
            if (Parse(line.TrimEnd('\r')) is { } parsed)
            {
                if (!_byRoom.TryGetValue(parsed.Room, out var list)) _byRoom[parsed.Room] = list = [];
                list.Add(parsed.Note);
            }
        }
        _end += last + 1;
    }

    /// <summary>승인 기록 한 줄 → (방, 기록 줄). 모양이 아니거나 질문 카드 답이면 null.</summary>
    public static (string Room, RoomNote Note)? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (Str(e, "room") is not { Length: > 0 } room || Str(e, "tool") is not { Length: > 0 } tool) return null;
            if (!e.TryGetProperty("at", out var atEl) || atEl.ValueKind != JsonValueKind.String || !atEl.TryGetDateTimeOffset(out var at)) return null;
            if (tool == ClaudeArgs.AskTool) return null; // 질문 카드 답은 '나' 말풍선으로 이미 있다
            var decision = Str(e, "decision") ?? "";
            if (decision == RoomHub.AppRanLabel) return null; // 앱 단계 호출은 설정 › 승인 기록에만
            var summary = FirstLine(Str(e, "summary") ?? "", 120);
            if (tool is BrowserShotTool or UnityShotTool) return (room, new RoomNote(at, decision, false) { Image = Str(e, "summary") });
            var text = tool switch
            {
                BackgroundTool or TerminalTool => decision,
                ApprovalRequest.PlanTool => "계획 " + (decision == "허용" ? "승인" : decision),
                _ => summary.Length > 0 ? $"승인 {decision} — {tool}: {summary}" : $"승인 {decision} — {tool}",
            };
            var denied = tool is not (BackgroundTool or TerminalTool) && decision is not ("허용" or "항상 허용" or RoomHub.TurnAllowLabel); // "항상 허용"은 없앤 단추의 옛 기록
            return (room, new RoomNote(at, text, denied));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstLine(string s, int max)
    {
        var line = s.Split('\n')[0].TrimEnd('\r');
        return line.Length > max ? line[..max] + "…" : line + (s.Contains('\n') ? " …" : "");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
