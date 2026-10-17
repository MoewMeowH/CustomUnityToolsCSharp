using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HB.Orchestration.Shared;

/// <summary>
/// JSONL 파일 하나에 호출 기록을 한 줄씩 append한다.
/// DB 없이 MCP 서버(쓰기)와 대시보드(읽기)가 파일만 공유하면 되도록 단순하게 둔다.
/// </summary>
public static class CallLog
{
    /// <summary>
    /// 한글을 \uXXXX(6바이트)로 바꾸지 않고 UTF-8(3바이트) 그대로 쓴다. 예전처럼 이스케이프된 기록도 그대로 읽힌다.
    /// 기록은 HTML에 그대로 넣지 않으므로 HTML 문자를 이스케이프하지 않아도 된다 (대시보드는 Razor가 따로 인코딩한다).
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 기록은 호출이 끝난 뒤 시작 시각(Timestamp)으로 남으므로 파일 순서와 시각 순서가 조금 어긋날 수 있다.
    /// 끝에서부터 읽을 때 since보다 이만큼 더 이전 기록이 나와야 멈춘다.
    /// </summary>
    private static readonly TimeSpan AppendSlack = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>HB_ORCH_LOG_PATH 환경 변수, 없으면 %LOCALAPPDATA%/HB_Orchestration/calls.jsonl</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("HB_ORCH_LOG_PATH") is { Length: > 0 } p
            ? p
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HB_Orchestration", "calls.jsonl");

    public static async Task AppendAsync(CallRecord record, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");

        await Gate.WaitAsync();
        try
        {
            // Claude Code 세션이 여러 개면 MCP 서버 프로세스도 여러 개라 잠깐 충돌할 수 있다 → 짧게 재시도
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    await fs.WriteAsync(bytes);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(50 * (attempt + 1));
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>최신순으로 전체 기록을 읽는다. 깨진 줄은 건너뛴다.</summary>
    public static List<CallRecord> ReadAll(string? path = null)
    {
        path ??= DefaultPath;
        var result = new List<CallRecord>();
        if (!File.Exists(path)) return result;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<CallRecord>(line, Json) is { } r) result.Add(r);
            }
            catch (JsonException)
            {
                // 쓰는 도중인 마지막 줄 등은 무시
            }
        }

        result.Reverse();
        return result;
    }

    /// <summary>since 이후 기록만, 최신순. 파일 끝에서부터 읽다가 충분히 오래된 기록이 나오면 멈춘다 (오늘 사용량 같은 집계용).</summary>
    public static List<CallRecord> ReadSince(DateTimeOffset since, string? path = null)
    {
        path ??= DefaultPath;
        var result = new List<CallRecord>();
        if (!File.Exists(path)) return result;

        var stopBefore = since.UtcTicks > AppendSlack.Ticks ? since - AppendSlack : DateTimeOffset.MinValue;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var line in LinesFromEnd(fs))
        {
            CallRecord? r;
            try
            {
                r = JsonSerializer.Deserialize<CallRecord>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (r is null) continue;
            if (r.Timestamp < stopBefore) break;
            if (r.Timestamp >= since) result.Add(r);
        }
        return result;
    }

    /// <summary>파일의 줄을 끝에서부터. '\n'(0x0A)은 UTF-8 다른 글자의 일부가 될 수 없으므로 바이트 단위로 잘라도 안전하다.</summary>
    private static IEnumerable<string> LinesFromEnd(FileStream fs)
    {
        const int chunkSize = 64 * 1024;
        var pos = fs.Length;
        var carry = Array.Empty<byte>();   // 아직 줄의 시작을 못 찾은 앞부분

        while (pos > 0)
        {
            var n = (int)Math.Min(chunkSize, pos);
            pos -= n;
            var buffer = new byte[n + carry.Length];
            fs.Position = pos;
            fs.ReadExactly(buffer, 0, n);
            carry.CopyTo(buffer, n);

            var end = buffer.Length;
            for (var i = buffer.Length - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (Decode(buffer, i + 1, end) is { } line) yield return line;
                end = i;
            }
            carry = buffer[..end];
        }

        if (Decode(carry, 0, carry.Length) is { } first) yield return first;

        static string? Decode(byte[] bytes, int start, int end)
        {
            var text = Encoding.UTF8.GetString(bytes, start, end - start);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
