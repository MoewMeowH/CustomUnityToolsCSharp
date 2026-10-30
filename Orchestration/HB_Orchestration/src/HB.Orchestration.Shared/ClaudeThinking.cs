namespace HB.Orchestration.Shared;

/// <summary>
/// 생각 블록 판별. <c>thinking.display: updates</c>에서는 서버가 도구 호출 사이 글을 요약해 생각 블록으로 돌려준다 — 글 블록이 없고,
/// 서명에 block_kind "narration"이 든다 (claude CLI 2.1.312 실측 · CLI 코드와 같은 경로: protobuf 필드 2 → 1 → 8). 진짜 생각은 글이 비어 온다.
/// 서명 형식은 공개되지 않았다 — 읽지 못하면 false (보이지 않을 뿐 다른 문제는 없다).
/// </summary>
public static class ClaudeThinking
{
    private const int MaxSignatureChars = 64 * 1024;
    private static readonly byte[] Narration = "narration"u8.ToArray();

    public static bool IsNarration(string? signature)
    {
        if (signature is not { Length: > 0 and <= MaxSignatureChars }) return false;
        var padded = signature.PadRight(signature.Length + (4 - signature.Length % 4) % 4, '=');
        var bytes = new byte[padded.Length * 3 / 4];
        if (!Convert.TryFromBase64String(padded, bytes, out var length)) return false;
        return Field(bytes.AsSpan(0, length), 2, out var outer)
            && Field(outer, 1, out var inner)
            && Field(inner, 8, out var kind)
            && kind.SequenceEqual(Narration);
    }

    /// <summary>protobuf 메시지에서 그 번호의 길이 구분 필드 (여럿이면 마지막). 모양이 깨졌으면 false.</summary>
    private static bool Field(ReadOnlySpan<byte> data, int number, out ReadOnlySpan<byte> value)
    {
        value = default;
        var found = false;
        var i = 0;
        while (i < data.Length)
        {
            if (!Varint(data, ref i, out var key)) return false;
            switch (key & 7)
            {
                case 0:
                    if (!Varint(data, ref i, out _)) return false;
                    break;
                case 1:
                    i += 8;
                    break;
                case 2:
                    if (!Varint(data, ref i, out var size) || size > (ulong)(data.Length - i)) return false;
                    if ((key >> 3) == (ulong)number)
                    {
                        value = data.Slice(i, (int)size);
                        found = true;
                    }
                    i += (int)size;
                    break;
                case 5:
                    i += 4;
                    break;
                default:
                    return false;
            }
            if (i > data.Length) return false;
        }
        return found;
    }

    private static bool Varint(ReadOnlySpan<byte> data, ref int i, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && i < data.Length; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}
