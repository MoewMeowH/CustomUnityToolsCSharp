using System.Text;
using System.Text.Json;

namespace HB.Orchestration.Shared;

/// <summary>MCP 서버 프로세스 하나(= Claude Code 세션 하나)의 상태. 서버가 쓰고 대시보드가 읽는다.</summary>
public sealed class SessionState
{
    public int Pid { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Claude Code를 실행한 폴더 (= Unity 프로젝트 루트).</summary>
    public string? ProjectDir { get; set; }

    /// <summary>서버 dll의 수정 시각. publish 후 새 서버가 떴는지 확인할 때 쓴다.</summary>
    public DateTimeOffset? ServerBuild { get; set; }

    /// <summary>모드별 이번 세션 실행 횟수.</summary>
    public Dictionary<string, int> Runs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 세션 상태 파일 sessions/{pid}.json — 서버가 실행할 때마다 갱신하고 대시보드가 읽는다.
/// (예전의 초기화 요청 파일 {pid}.reset은 2026-10-20 세션 한도 제거로 없어졌다. 남은 파일은 Delete가 같이 지운다.)
/// </summary>
public static class SessionFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>호출 기록 파일과 같은 폴더 아래 sessions/.</summary>
    public static string FolderFor(string callLogPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(callLogPath))!, "sessions");

    public static string DefaultFolder => FolderFor(CallLog.DefaultPath);

    public static string StatePath(string folder, int pid) => Path.Combine(folder, $"{pid}.json");
    private static string LegacyResetPath(string folder, int pid) => Path.Combine(folder, $"{pid}.reset");

    public static void Write(string folder, SessionState state)
    {
        Directory.CreateDirectory(folder);
        var path = StatePath(folder, state.Pid);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static List<SessionState> ReadAll(string folder)
    {
        var list = new List<SessionState>();
        if (!Directory.Exists(folder)) return list;

        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<SessionState>(File.ReadAllText(file), Json) is { } s) list.Add(s);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // 쓰는 중이거나 깨진 파일은 건너뛴다.
            }
        }
        return list;
    }

    public static void Delete(string folder, int pid)
    {
        TryDelete(StatePath(folder, pid));
        TryDelete(LegacyResetPath(folder, pid));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
