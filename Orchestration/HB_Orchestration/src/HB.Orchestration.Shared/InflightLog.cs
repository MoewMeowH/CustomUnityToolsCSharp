using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HB.Orchestration.Shared;

/// <summary>
/// 호출 중 표시. 요청을 보내기 직전에 호출 기록 옆 inflight/&lt;기록 Id&gt;.json을 쓰고, 시도 기록을 남긴 뒤 지운다.
/// 프로세스가 강제 종료되면(작업 중지 · 세션 종료) 표시가 남는다 → 다음 정리(<see cref="SweepAsync"/>)가 Lost 기록으로 옮긴다.
/// 표시 쓰기 · 지우기 · 정리 실패는 호출을 막지 않는다.
/// </summary>
public static class InflightLog
{
    public const string LostKind = "Lost";
    public const string LostError = "[끊김] 프로세스가 응답 전에 종료됨 (호출 중 표시만 남음, 응답 · 사용량 없음)";

    /// <summary>표시 파일 내용. 프로세스 시작 시각은 PID 재사용과 구분하는 데 쓴다.</summary>
    public sealed record Marker(int Pid, DateTimeOffset ProcessStart, CallRecord Record);

    private static readonly Lazy<DateTimeOffset?> CurrentStart = new(() => StartOf(Process.GetCurrentProcess()));

    public static string FolderFor(string logPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logPath))!, "inflight");

    /// <summary>표시를 쓴다. 성공하면 표시 파일 경로, 실패하면 null과 이유.</summary>
    public static string? Begin(CallRecord record, string logPath, out string? error)
    {
        error = null;
        string? tmp = null;
        try
        {
            if (CurrentStart.Value is not { } start) { error = "프로세스 시작 시각을 읽지 못함"; return null; }
            var folder = FolderFor(logPath);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, record.Id + ".json");
            // 쓰다 만 표시를 정리가 읽지 않도록 임시 이름에 쓴 뒤 옮긴다.
            tmp = path + $".tmp-{OwnerSuffix(start)}";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(new Marker(Environment.ProcessId, start, record), CallLog.Json));
            File.Move(tmp, path, overwrite: true);
            tmp = null;
            return path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // AgentRunner는 이 호출을 요청 try 안에서 부른다 — 여기서 던지면 보내지도 않은 요청이 실패로 분류된다.
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            if (tmp is not null) TryDelete(tmp);
        }
    }

    /// <summary>표시를 지운다. 시도 기록을 남긴 뒤에 부른다 (그 사이에 죽으면 같은 Id의 Lost가 생기고, 읽을 때 <see cref="UsageEstimator.Apply"/>가 뺀다).</summary>
    public static void End(string? path)
    {
        if (path is not null) TryDelete(path);
    }

    /// <summary>
    /// 남긴 프로세스가 끝난 표시를 Lost 기록으로 옮긴다. 옮긴 건수를 돌려준다.
    /// 여러 프로세스(MCP 서버 여러 개 · 대시보드)가 동시에 정리해도 표시 파일 이름을 먼저 바꾼 쪽만 옮긴다.
    /// </summary>
    public static async Task<int> SweepAsync(string logPath, Func<int, DateTimeOffset, bool>? isAlive = null)
    {
        isAlive ??= IsAlive;
        string[] files;
        try
        {
            var folder = FolderFor(logPath);
            if (!Directory.Exists(folder)) return 0;
            RecoverLeftovers(folder, isAlive);
            files = Directory.GetFiles(folder, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var moved = 0;
        foreach (var file in files)
        {
            Marker? marker;
            try { marker = JsonSerializer.Deserialize<Marker>(ReadShared(file), CallLog.Json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (marker?.Record is null || isAlive(marker.Pid, marker.ProcessStart)) continue;

            if (CurrentStart.Value is not { } myStart) return moved; // 주인 표시 없이 차지하면 이 프로세스가 죽었을 때 되살릴 수 없다
            var claimed = file + $".claimed-{OwnerSuffix(myStart)}-{Guid.NewGuid():N}";
            try { File.Move(file, claimed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // 다른 프로세스가 먼저 가져갔다

            try
            {
                await CallLog.AppendAsync(marker.Record with { Success = false, Error = LostError, ErrorKind = LostKind }, logPath);
                moved++;
                TryDelete(claimed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 기록하지 못했으면 표시를 되돌려 다음 정리에 맡긴다.
                try { File.Move(claimed, file); } catch (Exception) { /* 되돌리기 실패는 무시 */ }
            }
        }
        return moved;
    }

    /// <summary>임시 · 차지 파일 이름에 붙이는 주인 표시 "PID-시작 시각 Ticks". 주인이 죽었는지 정리 때 알 수 있게.</summary>
    private static string OwnerSuffix(DateTimeOffset start) => $"{Environment.ProcessId}-{start.UtcTicks}";

    private static readonly Regex LeftoverName =
        new(@"^(?<orig>.+\.json)\.(?<kind>tmp|claimed)-(?<pid>\d+)-(?<ticks>\d+)(?:-[0-9a-f]+)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 주인이 죽은 임시 · 차지 파일을 회수한다 (/cross-review 39552cf537 1-F1). 쓰다 만 임시 파일은 지운다 (표시를 다 쓰기 전이라 요청은 아직 나가지 않았다).
    /// 차지한 채 죽은 파일은 원래 이름으로 되돌려 이번 정리에서 다시 옮긴다 — 기록을 남긴 뒤 죽었으면 같은 Id의 Lost가 하나 더 생기고, 읽을 때 <see cref="UsageEstimator.Apply"/>가 뺀다.
    /// </summary>
    private static void RecoverLeftovers(string folder, Func<int, DateTimeOffset, bool> isAlive)
    {
        foreach (var leftover in Directory.GetFiles(folder, "*.json.*"))
        {
            var m = LeftoverName.Match(leftover);
            if (!m.Success || !int.TryParse(m.Groups["pid"].Value, out var pid) || !long.TryParse(m.Groups["ticks"].Value, out var ticks)) continue;
            if (ticks > DateTimeOffset.MaxValue.UtcTicks) continue;
            if (isAlive(pid, new DateTimeOffset(ticks, TimeSpan.Zero))) continue;
            if (m.Groups["kind"].Value == "tmp") TryDelete(leftover);
            else
            {
                try { File.Move(leftover, m.Groups["orig"].Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 다른 프로세스가 먼저 되돌렸다 */ }
            }
        }
    }

    /// <summary>주인이 같은 순간 지울 수 있게(FileShare.Delete) 연다. ReadAllBytes는 지우기를 막아 주인의 End가 실패한다.</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>PID가 살아 있고 시작 시각이 같은지. 시작 시각을 읽지 못하면 살아 있는 것으로 본다 (잘못 옮기지 않도록).</summary>
    private static bool IsAlive(int pid, DateTimeOffset start)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return StartOf(p) is not { } actual || Math.Abs((actual - start).TotalSeconds) < 1;
        }
        catch (ArgumentException)
        {
            return false; // 그 PID의 프로세스 없음
        }
        catch (InvalidOperationException)
        {
            return false; // 조회 중 끝남
        }
    }

    private static DateTimeOffset? StartOf(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* 정리 실패는 무시 */ }
    }
}
