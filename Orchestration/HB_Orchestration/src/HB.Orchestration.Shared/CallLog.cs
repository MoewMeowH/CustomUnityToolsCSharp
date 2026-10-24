using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
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

    // ── 프롬프트 원문을 blobs로 (계획서 2026-10-23_호출기록-blobs-분리와-첫화면-쪽나누기) ──
    // 측정: 기록의 66%가 프롬프트인데 서로 다른 것은 4분의 1뿐 (골든 반복). 해시 이름이라 같은 원문은 한 번만 저장된다.

    /// <summary>이 크기(UTF-8 바이트) 이상인 프롬프트만 blobs에 둔다. 작은 것은 파일 수만 늘린다.</summary>
    public const int PromptBlobMinBytes = 4096;

    /// <summary>blobs에 둔 프롬프트 대신 기록에 남기는 앞부분 글자 수.</summary>
    public const int PromptPreviewChars = 1000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 프롬프트가 크면 blobs에 저장하고 앞부분 · 해시만 남긴 기록. 작거나 이미 나눴거나 폴더가 없으면 그대로.
    /// 저장에 실패하면 원문을 그대로 두고 error에 이유를 준다 (기록을 잃지 않는다).
    /// </summary>
    public static CallRecord WithPromptBlob(CallRecord record, string? blobFolder, out string? error)
    {
        error = null;
        if (record.PromptSha256 is not null || string.IsNullOrEmpty(blobFolder)) return record;
        var bytes = Utf8NoBom.GetBytes(record.Prompt);
        if (bytes.Length < PromptBlobMinBytes) return record;
        var sha = BlobStore.Sha256Hex(bytes);
        if (StorePrompt(blobFolder, sha, bytes) is { } e)
        {
            error = $"prompt: {e}";
            return record;
        }
        return record with { Prompt = PromptPreview(record.Prompt, bytes.Length), PromptSha256 = sha, PromptBytes = bytes.Length };
    }

    /// <summary>
    /// BlobStore.TryStore는 해시 이름 파일이 있기만 하면 성공으로 본다. 프롬프트는 blob이 유일한 원문 보관처라, 이미 있는 파일은
    /// 내용 해시까지 확인하고 다르면(손상 · 수동 편집) 실패로 돌려 원문을 기록에 남긴다 (/cross-review 255e65a12d self C3).
    /// </summary>
    private static string? StorePrompt(string folder, string sha, byte[] bytes)
    {
        try
        {
            var path = BlobStore.PathFor(folder, sha);
            if (File.Exists(path) && BlobStore.Sha256Hex(File.ReadAllBytes(path)) != sha)
                return $"기존 원문 파일 내용이 해시와 다름 ({path})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        return BlobStore.TryStore(folder, sha, bytes);
    }

    /// <summary>앞 PromptPreviewChars자 + 안내. 서로게이트 쌍 가운데서 자르지 않는다 (이모지 등).</summary>
    private static string PromptPreview(string prompt, long bytes)
    {
        var cut = Math.Min(PromptPreviewChars, prompt.Length);
        if (cut > 0 && cut < prompt.Length && char.IsHighSurrogate(prompt[cut - 1])) cut--;
        return prompt[..cut] + $"\n… (프롬프트 원문 {bytes:N0}바이트는 blob 폴더에 따로 — 대시보드 호출 화면 · CallLog.FullPrompt로 읽는다)";
    }

    /// <summary>기록의 프롬프트 원문 폴더: Provenance.BlobFolder, 없거나 비었으면 호출 기록 옆 blobs/ (저장 · 읽기 · 압축이 같은 규칙).</summary>
    public static string PromptFolder(CallRecord record, string logPath) =>
        record.Provenance?.BlobFolder is { Length: > 0 } folder ? folder : BlobStore.DefaultFolderFor(logPath);

    /// <summary>
    /// 프롬프트 원문. 해시가 없으면(작은 프롬프트 · 예전 기록) 기록 그대로. 해시가 있는데 원문 파일이 없거나 내용이 해시와 다르면
    /// Missing = true이고 Text는 기록의 앞부분이다.
    /// </summary>
    public static (string Text, bool Missing) FullPrompt(CallRecord record, string? logPath = null)
    {
        if (record.PromptSha256 is not { } sha) return (record.Prompt, false);
        if (!BlobStore.IsValidSha(sha)) return (record.Prompt, true);
        try
        {
            var bytes = File.ReadAllBytes(BlobStore.PathFor(PromptFolder(record, logPath ?? DefaultPath), sha));
            if (BlobStore.Sha256Hex(bytes) == sha) return (Utf8NoBom.GetString(bytes), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 아래: 원문 없음
        }
        return (record.Prompt, true);
    }

    public sealed record CompactResult(int Lines, int Moved, int Kept, long BytesBefore, long BytesAfter, string? Backup, IReadOnlyList<string> Errors);

    /// <summary>
    /// 예전 기록의 큰 프롬프트를 blobs로 옮긴다. 줄은 JSON 그대로 다루어(알 수 없는 필드 · 순서 유지) prompt만 바꾸고 promptSha256 ·
    /// promptBytes를 더한다. 깨진 줄 · 이미 나눈 줄은 그대로. 원문 폴더는 기록의 것(PromptFolder) — FullPrompt와 같은 규칙.
    /// 다른 서버 프로세스가 같은 파일에 덧붙이므로 (/cross-review 255e65a12d 1-F1 · self C1 · C2):
    /// 1) 스냅샷을 마지막 줄바꿈까지만 압축한다 (쓰는 중이던 줄은 꼬리로). blob 저장 · JSON 처리는 파일을 잡지 않고 한다.
    /// 2) 스냅샷 부분의 백업을 먼저 디스크까지 쓴다.
    /// 3) 파일을 짧게 독점(FileShare.None)한 채, 그사이 덧붙은 꼬리를 읽어 백업에 더하고, 같은 파일을 압축본 + 꼬리로 덮어쓴다.
    ///    다른 서버의 AppendAsync는 공유 위반 재시도로 기다린다. 파일 바꾸기(Replace)를 쓰지 않아 원본이 사라지는 중간 상태가 없다.
    /// </summary>
    public static CompactResult CompactPrompts(string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        var errors = new List<string>();
        byte[] snapshot;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // 서버가 쓰는 중에도 읽는다
        {
            snapshot = new byte[fs.Length];
            fs.ReadExactly(snapshot);
        }
        var cut = Array.LastIndexOf(snapshot, (byte)'\n') + 1; // 0이면 끝난 줄이 없다
        if (cut == 0) return new CompactResult(0, 0, 0, snapshot.Length, snapshot.Length, null, errors);

        var output = new MemoryStream();
        int lines = 0, moved = 0, kept = 0;
        for (var start = 0; start < cut;)
        {
            var nl = Array.IndexOf(snapshot, (byte)'\n', start);
            var line = snapshot.AsSpan(start, nl - start);
            lines++;
            if (TryCompactLine(line, path, errors) is { } replaced)
            {
                output.Write(replaced);
                moved++;
            }
            else
            {
                output.Write(line);
                kept++;
            }
            output.WriteByte((byte)'\n');
            start = nl + 1;
        }
        var compacted = output.ToArray();

        var backup = UniqueBackupPath(path);
        using (var b = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            b.Write(snapshot, 0, cut);
            b.Flush(flushToDisk: true);
        }

        long before, after;
        using (var fs = OpenExclusive(path))
        {
            before = fs.Length;
            // 앞부분은 덧붙이기만 하므로 그대로여야 한다 — 다른 압축 등으로 바뀌었으면 손대지 않는다 (마지막 64KB로 확인)
            var check = (int)Math.Min(cut, 64 * 1024);
            var probe = new byte[check];
            fs.Position = cut - check;
            fs.ReadExactly(probe);
            if (before < cut || !probe.AsSpan().SequenceEqual(snapshot.AsSpan(cut - check, check)))
            {
                errors.Add("압축하는 사이 파일 앞부분이 바뀌었다 — 아무것도 바꾸지 않았다. 다시 실행하라.");
                return new CompactResult(lines, 0, lines, before, before, backup, errors);
            }
            var tail = new byte[before - cut];
            fs.Position = cut;
            fs.ReadExactly(tail);
            using (var b = new FileStream(backup, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                b.Write(tail);
                b.Flush(flushToDisk: true);
            }
            fs.Position = 0;
            fs.Write(compacted);
            fs.Write(tail);
            fs.SetLength(compacted.Length + tail.Length);
            fs.Flush(flushToDisk: true);
            after = fs.Length;
        }
        return new CompactResult(lines, moved, kept, before, after, backup, errors);
    }

    private static string UniqueBackupPath(string path)
    {
        var stem = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        for (var n = 0; ; n++)
        {
            var candidate = n == 0 ? stem : $"{stem}-{n}";
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>다른 프로세스가 열지 못하게 연다. 서버가 쓰는 중이면 공유 위반 — 짧게 재시도.</summary>
    private static FileStream OpenExclusive(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>줄 하나의 큰 prompt를 blobs로 옮긴 새 줄 바이트, 바꿀 것이 없으면 null.</summary>
    private static byte[]? TryCompactLine(ReadOnlySpan<byte> line, string logPath, List<string> errors)
    {
        if (line.Trim(" \t\r"u8).IsEmpty) return null;
        JsonObject obj;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject parsed) return null;
            obj = parsed;
        }
        catch (JsonException)
        {
            return null;
        }
        if (obj["promptSha256"] is not null || obj["prompt"] is not JsonValue pv || !pv.TryGetValue<string>(out var prompt)) return null;
        var bytes = Utf8NoBom.GetBytes(prompt);
        if (bytes.Length < PromptBlobMinBytes) return null;

        // FullPrompt · PromptFolder와 같은 규칙: 기록의 blobFolder, 없거나 비었으면 호출 기록 옆 blobs/
        var folder = (obj["provenance"] as JsonObject)?["blobFolder"] is JsonValue fv && fv.TryGetValue<string>(out var f) && f.Length > 0
            ? f : BlobStore.DefaultFolderFor(logPath);
        var sha = BlobStore.Sha256Hex(bytes);
        if (StorePrompt(folder, sha, bytes) is { } e)
        {
            errors.Add($"{obj["id"]}: {e}");
            return null;
        }
        obj["prompt"] = PromptPreview(prompt, bytes.Length);
        obj["promptSha256"] = sha;
        obj["promptBytes"] = bytes.Length;
        return Encoding.UTF8.GetBytes(obj.ToJsonString(Json));
    }

    public static async Task AppendAsync(CallRecord record, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");

        await Gate.WaitAsync();
        try
        {
            // Claude Code 세션이 여러 개면 MCP 서버 프로세스도 여러 개다. FileMode.Append는 열 때 끝으로 옮긴 위치에 쓰므로
            // 동시에 쓰면 같은 위치를 덮을 수 있다 → 쓰는 동안 다른 쓰기를 막고(FileShare.Read), 막힌 쪽은 공유 위반으로 짧게 재시도 (review 7c780ad3cd).
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    // 앞 쓰기가 줄 중간에서 끊겼으면 줄을 먼저 끊는다: 안 그러면 이번 기록이 깨진 줄에 붙어 함께 버려진다.
                    if (fs.Length > 0)
                    {
                        fs.Position = fs.Length - 1;
                        if (fs.ReadByte() != '\n') fs.WriteByte((byte)'\n');
                    }
                    fs.Position = fs.Length;
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
                if (JsonSerializer.Deserialize<CallRecord>(line, Json) is { } r) result.Add(ModeNames.Normalize(r));
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
            // Lost는 원래 시작 시각을 단 채 정리 때(몇 시간 · 며칠 뒤) 끝에 붙으므로 멈출 기준으로 쓰지 않는다 (/cross-review 39552cf537 1-F3).
            if (r.ErrorKind != InflightLog.LostKind && r.Timestamp < stopBefore) break;
            if (r.Timestamp >= since) result.Add(ModeNames.Normalize(r));
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
