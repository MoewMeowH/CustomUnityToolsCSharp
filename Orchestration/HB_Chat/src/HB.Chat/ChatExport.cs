using System.Globalization;
using System.Text;
using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>
/// 방 대화를 Markdown 파일로 (계획서 2-2 c). 화자 · 시각 · 본문(세션 기록 글 그대로, 스티커 태그도 글자 그대로) · 도구 이름,
/// 그리고 이 방에서 부른 run_mode 실행 말풍선 (2-2 e, 화면과 같은 자리).
/// </summary>
public static class ChatExport
{
    public static string Markdown(Room room, ChatTranscript? transcript, DateTimeOffset now, RunPlacement? runs = null, AnswerPlacement? answers = null,
        NotePlacement? notes = null)
    {
        runs ??= RunPlacement.Empty;
        answers ??= AnswerPlacement.Empty;
        notes ??= NotePlacement.Empty;
        var sb = new StringBuilder();
        sb.Append("# ").Append(RoomList.TitleOf(room, transcript)).Append("\n\n");
        sb.Append("- 내보낸 시각: ").Append(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("- 작업 폴더: ").Append(room.Workspace).Append(" · 권한: ").Append(RoomRules.LevelName(room.Level))
          .Append(" · 모델: ").Append(room.Model).Append('\n');
        sb.Append("- 세션: `").Append(room.SessionId).Append("`\n");
        if (transcript?.FormatError is { } error) sb.Append("- 세션 기록을 다 읽지 못함: ").Append(error).Append('\n');
        sb.Append('\n');

        foreach (var e in transcript?.FormatError is null ? transcript?.Entries ?? [] : [])
        {
            var when = e.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
            if (e.Tools.Count > 0)
                sb.Append("_도구: ").Append(string.Join(", ", RoomList.ToolChips(e.Tools).Select(c => c.Count > 1 ? $"{c.Name} ×{c.Count}" : c.Name)))
                  .Append("_\n\n");
            AppendPlaced(sb, runs.For(e), answers.For(e), notes.For(e));
            if (RoomList.IsStoppedTurn(e))
            {
                sb.Append("> 중지된 턴 · ").Append(when).Append("\n\n");
                continue;
            }
            if (e.Speaker == ChatSpeaker.System)
            {
                sb.Append("> 안내 · ").Append(when).Append(": ").Append(e.Text.Replace("\n", " ")).Append("\n\n");
                continue;
            }
            sb.Append("### ").Append(e.Speaker == ChatSpeaker.User ? "나" : "지휘자").Append(" · ").Append(when).Append("\n\n");
            sb.Append(e.Text.TrimEnd()).Append("\n\n");
        }
        AppendPlaced(sb, runs.Trailing, answers.Trailing, notes.Trailing);
        return sb.ToString();
    }

    /// <summary>방 기록 줄 (승인 결과 · 정리 표시).</summary>
    private static void AppendNote(StringBuilder sb, RoomNote n) =>
        sb.Append("> ").Append(n.Text.Replace("\n", " ")).Append(" · ")
          .Append(n.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\n\n");

    /// <summary>
    /// 실행 말풍선 · 질문 답 · 방 기록 줄을 화면(_Messages Placed)처럼 시각 차례로 (4차 D1 — 보류 K1: 내보낸 대화에서 고른 답이 빠졌다,
    /// /self-review e464ec29ba C4: 방 기록 줄이 늘 먼저 나왔다).
    /// </summary>
    private static void AppendPlaced(StringBuilder sb, IReadOnlyList<RunBubble> runs, IReadOnlyList<ChatAnswer> answers, IReadOnlyList<RoomNote> notes)
    {
        var waiting = new Queue<RoomNote>(notes);
        foreach (var (run, answer) in AnswerPlacement.Merge(runs, answers))
        {
            var at = run?.At ?? answer!.Timestamp;
            while (waiting.Count > 0 && at is { } t && waiting.Peek().At <= t) AppendNote(sb, waiting.Dequeue());
            if (run is not null) { AppendRuns(sb, [run]); continue; }
            var when = answer!.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
            if (answer.Declined is { } declined)
            {
                sb.Append("> 질문에 답하지 않음 · ").Append(when).Append(": ").Append(AnswerPlacement.DeclinedLabel(declined)).Append("\n\n");
                continue;
            }
            sb.Append("### 나 (질문에 답함) · ").Append(when).Append("\n\n");
            foreach (var item in answer.Items)
                sb.Append("> ").Append(item.Question.Replace("\n", " ")).Append("\n\n").Append(item.Answer?.Trim() ?? "(답 글을 읽지 못함)").Append("\n\n");
        }
        while (waiting.Count > 0) AppendNote(sb, waiting.Dequeue());
    }

    /// <summary>
    /// 실행 말풍선: 시작은 인용 한 줄, 단계 응답은 "### 에이전트 (모드 · 단계 · 모델) · 시각" + 인용으로 감싼 본문
    /// (응답의 "## 지적" 같은 제목이 문서 목차와 섞이지 않게 — /self-review a9e6066519 C7).
    /// </summary>
    private static void AppendRuns(StringBuilder sb, IReadOnlyList<RunBubble> bubbles)
    {
        foreach (var b in bubbles)
        {
            var when = b.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (b.Kind == RunBubbleKind.Start)
            {
                sb.Append("> ").Append(b.Markdown.Replace("\n", " ")).Append(" · ").Append(when).Append("\n\n");
                continue;
            }
            sb.Append("### ").Append(b.Name).Append(" (").Append(b.Detail).Append(") · ").Append(when).Append("\n\n");
            foreach (var line in b.Markdown.TrimEnd().ReplaceLineEndings("\n").Split('\n'))
                sb.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
            sb.Append('\n');
        }
    }

    /// <summary>내려받을 파일 이름: 제목(파일 이름에 못 쓰는 글자는 _ , 60자) + 날짜.</summary>
    public static string FileName(string title, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().Trim('.');
        safe = RoomList.Cut(safe, 60);
        if (safe.Length == 0) safe = "대화";
        return $"{safe}_{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.md";
    }
}
