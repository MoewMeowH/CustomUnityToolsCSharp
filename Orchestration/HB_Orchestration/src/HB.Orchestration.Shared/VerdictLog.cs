using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HB.Orchestration.Shared;

/// <summary>
/// 지적 하나에 대한 지휘자의 판정 1회 (record_verdicts). 한 줄 = 판정 1회.
/// 같은 RunId·FindingKey로 다시 쓰면 앞 판정을 대체한다 (보류 → 결론, 사용자가 뒤집은 판정).
/// 에이전트·모델·역할은 저장하지 않고 RunId + Steps로 calls.jsonl에서 찾는다.
/// </summary>
public sealed record VerdictRecord
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string RunId { get; init; }

    /// <summary>실행 안에서 지적을 구별하는 짧은 키 (지휘자가 정함).</summary>
    public required string FindingKey { get; init; }

    /// <summary>이 지적을 낸 단계 번호들. 여럿이면 공통 지적, 비어 있으면 Claude만 지적 (에이전트가 놓친 것).</summary>
    public IReadOnlyList<int> Steps { get; init; } = [];

    public required string Summary { get; init; }

    /// <summary><see cref="Verdicts"/>의 값 중 하나.</summary>
    public required string Verdict { get; init; }

    /// <summary>판정 근거. 채택(adopted)이 아니면 필수. 소급 기록은 "소급: 파일" 표시.</summary>
    public string? Reason { get; init; }

    /// <summary>high · medium · low (선택).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; init; }

    /// <summary>지휘자의 ownReview에도 있던 지적인지. Claude만 지적(Steps 비어 있음)이면 항상 true.</summary>
    public bool ClaudeAlsoFound { get; init; }

    /// <summary>관련 파일 "path" 또는 "path:line" (선택).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; init; }

    public bool IsClaudeOnly => Steps.Count == 0;
}

/// <summary>판정 값. 채택률 = (채택 + 부분 채택 × 0.5) / (채택 + 부분 채택 + 기각). 보류·중복은 분모에서 뺀다.</summary>
public static class Verdicts
{
    public const string Adopted = "adopted";
    public const string Partial = "partial";
    public const string Rejected = "rejected";
    public const string Deferred = "deferred";
    public const string Duplicate = "duplicate";

    public static readonly IReadOnlyList<string> All = [Adopted, Partial, Rejected, Deferred, Duplicate];

    /// <summary>부분 채택의 채택률 가중치.</summary>
    public const double PartialWeight = 0.5;

    public static string Label(string verdict) => verdict switch
    {
        Adopted => "채택",
        Partial => "부분 채택",
        Rejected => "기각",
        Deferred => "보류",
        Duplicate => "중복",
        _ => verdict,
    };
}

/// <summary>
/// verdicts.jsonl. calls.jsonl과 같은 방식(append 전용, 깨진 줄 무시)이고, 한 번의 기록은 한 번의 쓰기로 붙인다.
/// 쓰는 동안은 다른 쓰기를 막는다(FileShare.Read): .NET의 FileMode.Append는 열 때 끝으로 옮긴 위치에 쓰므로
/// 다른 프로세스와 동시에 쓰면 같은 위치를 덮을 수 있다. 막힌 쪽은 공유 위반(IOException)으로 짧게 재시도한다 (review 7c780ad3cd).
/// </summary>
public static class VerdictLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lock Gate = new();

    /// <summary>HB_ORCH_VERDICTS_PATH 환경 변수, 없으면 호출 기록 파일(callLogPath, 기본 CallLog.DefaultPath)과 같은 폴더의 verdicts.jsonl.</summary>
    /// 상대 경로는 작업 디렉터리 기준 전체 경로로 바꾼다 (파일 이름만 주면 폴더 부분이 비어 만들 수 없으므로).
    public static string PathFor(string? callLogPath = null) => Path.GetFullPath(
        Environment.GetEnvironmentVariable("HB_ORCH_VERDICTS_PATH") is { Length: > 0 } p
            ? p
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(callLogPath ?? CallLog.DefaultPath))!, "verdicts.jsonl"));

    public static void Append(IReadOnlyList<VerdictRecord> records, string path)
    {
        if (records.Count == 0) return;
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        foreach (var r in records) sb.Append(JsonSerializer.Serialize(r, Json)).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        lock (Gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    // 앞 쓰기가 줄 중간에서 끊겼으면 줄을 먼저 끊는다: 안 그러면 이번 첫 줄이 깨진 줄에 붙어 함께 버려진다.
                    if (fs.Length > 0)
                    {
                        fs.Position = fs.Length - 1;
                        if (fs.ReadByte() != '\n') fs.WriteByte((byte)'\n');
                    }
                    fs.Position = fs.Length;
                    fs.Write(bytes);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
    }

    /// <summary>파일 순서대로 전체 판정 (대체된 판정 포함). 깨진 줄은 건너뛴다.</summary>
    public static List<VerdictRecord> ReadAll(string path)
    {
        var result = new List<VerdictRecord>();
        if (!System.IO.File.Exists(path)) return result;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<VerdictRecord>(line, Json) is { } r) result.Add(r);
            }
            catch (JsonException)
            {
                // 쓰는 도중인 마지막 줄 등은 무시
            }
        }
        return result;
    }

    /// <summary>RunId·FindingKey마다 마지막 판정만 (파일 순서 기준). 순서는 처음 나온 순서.</summary>
    public static List<VerdictRecord> Latest(IEnumerable<VerdictRecord> all)
    {
        var order = new List<(string, string)>();
        var last = new Dictionary<(string, string), VerdictRecord>();
        foreach (var r in all)
        {
            var key = (r.RunId, r.FindingKey);
            if (!last.ContainsKey(key)) order.Add(key);
            last[key] = r;
        }
        return order.Select(k => last[k]).ToList();
    }
}
