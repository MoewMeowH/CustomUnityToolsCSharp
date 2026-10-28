using System.Text.Json;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>질문 카드의 선택지 하나 (AskUserQuestion options[]).</summary>
public sealed record QuestionOption(string Label, string? Description);

/// <summary>질문 하나 (AskUserQuestion questions[] — question · header · multiSelect · options, 3차 0번 실측 A).</summary>
public sealed record QuestionItem(string Question, string? Header, bool MultiSelect, IReadOnlyList<QuestionOption> Options);

/// <summary>
/// 지휘자의 객관식 질문 (AskUserQuestion, 3차 a). 입력 원문을 들고 있다가 답을 더해 그대로 돌려준다 —
/// 허용 답의 updatedInput = 입력 + <c>answers</c>(질문 글 → 고른 label, 여러 개면 ", "로 잇기, 0번 실측 A에서 지휘자가 받음).
/// </summary>
public sealed record QuestionRequest(IReadOnlyList<QuestionItem> Items, string InputJson)
{
    public const int MaxQuestions = 8, MaxOptions = 12, MaxTextChars = 2_000;

    /// <summary>
    /// 질문 하나의 답 상한 (글자, 크기 보호용): 선택지를 다 고르고 기타 글까지 붙여도 넘지 않게 label 상한 × (선택지 수 + 1).
    /// 직접 쓰는 글의 1,000자 상한은 화면 입력칸이 건다 (/self-review 7d839f4aa5 C4).
    /// </summary>
    public const int MaxAnswerChars = MaxTextChars * (MaxOptions + 1);

    /// <summary>도구 입력(JSON)을 읽는다. 모양이 아니거나 상한을 넘으면 null — 그때는 카드 없이 거절하고 채팅으로 물으라고 돌려준다.</summary>
    public static QuestionRequest? Parse(string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("questions", out var qs)
                || qs.ValueKind != JsonValueKind.Array || qs.GetArrayLength() is 0 or > MaxQuestions) return null;
            var items = new List<QuestionItem>();
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object || Text(q, "question") is not { } question) return null;
                if (!q.TryGetProperty("options", out var os) || os.ValueKind != JsonValueKind.Array || os.GetArrayLength() > MaxOptions) return null;
                var options = new List<QuestionOption>();
                foreach (var o in os.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object || Text(o, "label") is not { } label) return null;
                    options.Add(new QuestionOption(label, Text(o, "description")));
                }
                if (items.Any(i => i.Question == question)) return null; // 답이 질문 글을 키로 쓰므로 같은 글이 둘이면 받지 않는다
                items.Add(new QuestionItem(question, Text(q, "header"),
                    q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True, options));
            }
            return new QuestionRequest(items, inputJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 화면에서 온 답 검사: 질문마다 답 하나, 비어 있지 않고 상한 안. 선택지 밖의 글은 "기타"로 받는다. 오류 문구(null이면 받음).
    /// </summary>
    public string? Validate(IReadOnlyDictionary<string, string> answers)
    {
        foreach (var item in Items)
        {
            if (!answers.TryGetValue(item.Question, out var answer) || string.IsNullOrWhiteSpace(answer))
                return $"답하지 않은 질문이 있습니다: {item.Question}";
            if (answer.Length > MaxAnswerChars) return $"답이 너무 깁니다 ({answer.Length:N0}자, 상한 {MaxAnswerChars:N0}자).";
        }
        return answers.Keys.All(k => Items.Any(i => i.Question == k)) ? null : "없는 질문에 대한 답이 있습니다.";
    }

    /// <summary>허용 답에 넣을 입력: 원래 입력 + answers.</summary>
    public string WithAnswers(IReadOnlyDictionary<string, string> answers)
    {
        var input = JsonNode.Parse(InputJson)!.AsObject();
        var map = new JsonObject();
        foreach (var (question, answer) in answers) map[question] = answer.Trim();
        input["answers"] = map;
        return input.ToJsonString();
    }

    /// <summary>기록 · 끝난 카드에 남길 한 줄: "질문 → 답" (질문마다).</summary>
    public static string Summary(IReadOnlyList<QuestionItem> items, IReadOnlyDictionary<string, string>? answers) =>
        string.Join(" / ", items.Select(i => answers is not null && answers.TryGetValue(i.Question, out var a) ? $"{i.Question} → {a.Trim()}" : i.Question));

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= MaxTextChars } s ? s : null;
}

/// <summary>화면의 질문 카드 하나.</summary>
public sealed record QuestionView(string Id, IReadOnlyList<QuestionItem> Questions, DateTimeOffset ExpiresAt);
