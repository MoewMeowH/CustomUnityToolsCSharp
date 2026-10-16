namespace HB.Orchestration.Agents;

/// <summary>
/// 마크다운 펜스 코드 블록 찾기. 응답 검사기(코드 제외)와 save_result(N번째 블록 저장)가 같은 규칙을 쓴다.
/// - 여는 펜스: 앞 공백을 뺀 줄이 ``` 또는 ~~~ 3개 이상으로 시작 (뒤에 언어 표시 가능).
/// - 닫는 펜스: 같은 문자로만 이루어진 줄, 여는 펜스 이상의 길이 (```` 안의 ```는 닫지 않음).
/// - 닫히지 않은 펜스는 응답 끝까지 블록으로 본다.
/// </summary>
public static class CodeFences
{
    /// <param name="FenceStart">여는 펜스 줄의 시작 위치.</param>
    /// <param name="ContentStart">내용 시작 (여는 펜스 줄 다음).</param>
    /// <param name="ContentEnd">내용 끝 (닫는 펜스 줄 시작, 닫히지 않았으면 텍스트 끝). 내용은 원문 그대로 [ContentStart, ContentEnd).</param>
    /// <param name="FenceEnd">블록 끝 (닫는 펜스 줄 다음).</param>
    public readonly record struct Block(int FenceStart, int ContentStart, int ContentEnd, int FenceEnd);

    public static List<Block> Find(string text)
    {
        var blocks = new List<Block>();
        var pos = 0;
        char fenceChar = default;
        var fenceLength = 0;
        int fenceStart = 0, contentStart = 0;

        while (pos < text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var next = lineEnd < 0 ? text.Length : lineEnd + 1;
            var line = text.AsSpan(pos, (lineEnd < 0 ? text.Length : lineEnd) - pos).TrimEnd('\r').TrimStart();

            if (fenceLength == 0)
            {
                if (line.Length >= 3 && line[0] is '`' or '~')
                {
                    var c = line[0];
                    var run = CountRun(line, c);
                    if (run >= 3)
                    {
                        fenceChar = c;
                        fenceLength = run;
                        fenceStart = pos;
                        contentStart = next;
                    }
                }
            }
            else
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length >= fenceLength && CountRun(trimmed, fenceChar) == trimmed.Length)
                {
                    blocks.Add(new Block(fenceStart, contentStart, pos, next));
                    fenceLength = 0;
                }
            }
            pos = next;
        }

        if (fenceLength > 0) blocks.Add(new Block(fenceStart, contentStart, text.Length, text.Length));
        return blocks;
    }

    /// <summary>N번째(1부터) 블록의 내용을 원문 그대로. 없으면 null.</summary>
    public static string? Content(string text, int number)
    {
        var blocks = Find(text);
        if (number < 1 || number > blocks.Count) return null;
        var b = blocks[number - 1];
        return text[b.ContentStart..b.ContentEnd];
    }

    /// <summary>코드 블록을 뺀 본문.</summary>
    public static string WithoutBlocks(string text)
    {
        var blocks = Find(text);
        if (blocks.Count == 0) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        var last = 0;
        foreach (var b in blocks)
        {
            sb.Append(text, last, b.FenceStart - last);
            last = b.FenceEnd;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    private static int CountRun(ReadOnlySpan<char> s, char c)
    {
        var n = 0;
        while (n < s.Length && s[n] == c) n++;
        return n;
    }
}
