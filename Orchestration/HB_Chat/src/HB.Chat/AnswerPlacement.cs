using HB.Orchestration.Shared;

namespace HB.Chat;

/// <summary>
/// 질문 카드 답 말풍선의 자리 (4차 D1 — 노션 "질문 카드에 답한 것 · 고른 것을 '나'의 말풍선으로"): 답 줄보다 뒤의 첫 말 앞, 그런 말이 없으면 맨 뒤(Trailing).
/// 세션 기록의 답(ChatTranscript.Answers)을 쓰므로 다시 열어도 남는다. 말은 참조로 찾는다 (RunPlacement와 같은 이유).
/// </summary>
public sealed record AnswerPlacement(IReadOnlyDictionary<ChatEntry, IReadOnlyList<ChatAnswer>> Before, IReadOnlyList<ChatAnswer> Trailing)
{
    public static readonly AnswerPlacement Empty = new(new Dictionary<ChatEntry, IReadOnlyList<ChatAnswer>>(ReferenceEqualityComparer.Instance), []);

    public IReadOnlyList<ChatAnswer> For(ChatEntry e) => Before.TryGetValue(e, out var list) ? list : [];

    public static AnswerPlacement Of(ChatTranscript? transcript)
    {
        if (transcript is not { FormatError: null } t || t.Answers.Count == 0) return Empty;
        var before = new Dictionary<ChatEntry, IReadOnlyList<ChatAnswer>>(ReferenceEqualityComparer.Instance);
        var trailing = new List<ChatAnswer>();
        foreach (var a in t.Answers)
        {
            var next = t.Entries.FirstOrDefault(e => e.Line > a.Line);
            if (next is null) { trailing.Add(a); continue; }
            before[next] = [.. before.TryGetValue(next, out var list) ? list : [], a];
        }
        return new AnswerPlacement(before, trailing);
    }

    /// <summary>
    /// 답하지 않은 질문의 짧은 라벨 (/self-review e8091295fe C8): 기록의 글은 앱이 지휘자에게 보낸 거절 문구
    /// ("사용자가 질문 카드에 답하지 않았습니다 (시간 초과). 필요하면 …")라 괄호 안 라벨만, 없으면 첫 문장 (80자까지).
    /// </summary>
    public static string DeclinedLabel(string note)
    {
        var first = note.Split(". ", 2)[0].TrimEnd('.');
        if (System.Text.RegularExpressions.Regex.Match(first, @"\(([^()]{1,30})\)\s*$") is { Success: true } m) return m.Groups[1].Value;
        return first.Length <= 80 ? first : first[..(char.IsHighSurrogate(first[79]) ? 79 : 80)] + "…";
    }

    /// <summary>
    /// 같은 말 앞의 외부 실행 말풍선과 답을 시각으로 섞는다 (/self-review e8091295fe C1: 답 → 그 답으로 부른 실행 차례가 거꾸로 보였다).
    /// 시각이 없는 답은 앞에. 각 목록은 이미 시각 · 줄 차례.
    /// </summary>
    public static IEnumerable<(RunBubble? Run, ChatAnswer? Answer)> Merge(IReadOnlyList<RunBubble> runs, IReadOnlyList<ChatAnswer> answers)
    {
        int i = 0, j = 0;
        while (i < runs.Count || j < answers.Count)
        {
            if (j >= answers.Count || (i < runs.Count && answers[j].Timestamp is { } at && runs[i].At <= at)) yield return (runs[i++], null);
            else yield return (null, answers[j++]);
        }
    }
}
