using System.Text;
using System.Text.Json;

namespace HB.Orchestration.Shared;

/// <summary>
/// JSONL 파일 하나에 호출 기록을 한 줄씩 append한다.
/// DB 없이 MCP 서버(쓰기)와 대시보드(읽기)가 파일만 공유하면 되도록 단순하게 둔다.
/// </summary>
public static class CallLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
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
}
