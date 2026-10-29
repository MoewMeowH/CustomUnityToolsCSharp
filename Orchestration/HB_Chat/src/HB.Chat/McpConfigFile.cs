using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HB.Chat;

/// <summary>
/// 읽기 · 편집 단계의 --mcp-config 파일: 작업 폴더 .mcp.json에서 서버 하나(hb-orchestration)만 골라 앱 데이터 폴더에 쓴다.
/// .mcp.json을 그대로 넘기면 거기 더해진 다른 서버도 붙는다. --strict-mcp-config와 같이 써서 claude.ai 커넥터 · 사용자 MCP도 막는다 (0번 실측 B · C).
/// 항목은 고치지 않고 옮긴다 (env의 ${VAR:-} 자리표시는 claude가 푼다 — 0번 실측 C에서 연결 확인). 실행마다 다시 써서 .mcp.json 변경을 따라간다.
/// </summary>
public static class McpConfigFile
{
    public static string Write(string workspaceDir, string server, string dataDir)
    {
        // 이름이 파일 이름에 들어가므로 쓰기 전에 검사한다 (설정 값에 ..\ 가 있으면 데이터 폴더 밖에 쓰게 된다 — b19234c862 C4).
        if (!ClaudeArgs.IsServerName(server)) throw new InvalidOperationException($"MCP 서버 이름 형식이 올바르지 않습니다: {server}");
        var source = Path.Combine(workspaceDir, ".mcp.json");
        JsonNode? entry;
        try
        {
            entry = JsonNode.Parse(File.ReadAllText(source))?["mcpServers"]?[server];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException($"{source}를 읽지 못했습니다: {ex.Message}", ex);
        }
        if (entry is not JsonObject)
            throw new InvalidOperationException($"{source}에 mcpServers.{server} 항목이 없습니다.");

        var config = new JsonObject { ["mcpServers"] = new JsonObject { [server] = entry.DeepClone() } };
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspaceDir).ToLowerInvariant())))[..12];
        var path = Path.Combine(dataDir, $"mcp-{server}-{hash}.json");
        var json = config.ToJsonString(new() { WriteIndented = true });
        Directory.CreateDirectory(dataDir);
        // 같은 작업 폴더의 방들이 같은 파일을 쓴다 (0a3b1d5155 C5): 내용이 같으면 쓰지 않고, 쓸 때는 임시 파일 이름을 실행마다 다르게.
        if (SameContent(path, json)) return path;
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        // Windows에서는 같은 대상으로 동시에 바꾸면(File.Move overwrite) 한쪽이 실패한다 (테스트에서 확인).
        // 다른 방이 같은 내용을 이미 넣었으면 그대로 쓰고, 아니면 잠깐 뒤 다시 한다.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (SameContent(path, json))
                {
                    TryDelete(tmp);
                    return path;
                }
                if (attempt >= 4)
                {
                    TryDelete(tmp);
                    throw;
                }
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Unity 연결 항목 이름 — 도구 이름이 <c>mcp__unity__&lt;명령&gt;</c> (0단계 실측).</summary>
    public const string UnityServer = "unity";

    /// <summary>
    /// 방마다 고른 연결로 만든 --mcp-config 파일: 작업 폴더 .mcp.json에서 servers 이름들을 옮기고,
    /// unity가 있으면 <c>unity.exe mcp --project-path 경로</c> 항목을 더한다. 방 ID 이름의 파일이라 실행이 끝나면 <see cref="DeleteRoom"/>로 지운다.
    /// .mcp.json에 없는 이름은 appServer(앱에서 더한 서버)에서 찾고 — 작업 폴더 것이 먼저 —
    /// 둘 다 없으면 빼고 missing으로 돌려준다 (방 칸이 알린다). servers가 비고 unity도 없으면 빈 mcpServers. .mcp.json이 없으면 앱 서버만.
    /// </summary>
    public static (string Path, IReadOnlyList<string> Missing) WriteRoom(string workspaceDir, IEnumerable<string> servers,
        (string Cli, string Project)? unity, string dataDir, string roomId, Func<string, JsonObject?>? appServer = null)
    {
        if (!RoomStore.IsRoomId(roomId)) throw new InvalidOperationException($"방 ID 모양이 아닙니다: {roomId}");
        var source = Path.Combine(workspaceDir, ".mcp.json");
        JsonObject? all = null;
        var names = servers.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > 0)
        {
            foreach (var name in names)
                if (!ClaudeArgs.IsServerName(name) || name == UnityServer || name == TerminalMcp.Server)
                    throw new InvalidOperationException($"MCP 서버 이름 형식이 올바르지 않습니다: {name}");
            try
            {
                // 앱 서버만 고른 방은 작업 폴더에 .mcp.json이 없어도 된다 — 있는데 못 읽으면 실패 (예전과 같음)
                if (appServer is null || File.Exists(source)) all = JsonNode.Parse(File.ReadAllText(source))?["mcpServers"] as JsonObject;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                throw new InvalidOperationException($"{source}를 읽지 못했습니다: {ex.Message}", ex);
            }
        }
        var picked = new JsonObject();
        var missing = new List<string>();
        foreach (var name in names)
            if (all?[name] is JsonObject entry) picked[name] = entry.DeepClone();
            else if (appServer?.Invoke(name) is { } app) picked[name] = app;
            else missing.Add(name);
        if (unity is { } u) picked[UnityServer] = UnityEntry(u.Cli, u.Project);
        Directory.CreateDirectory(dataDir);
        var path = RoomPath(dataDir, roomId);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, new JsonObject { ["mcpServers"] = picked }.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true); // 한 방은 한 번에 한 실행만 돈다
        return (path, missing);
    }

    /// <summary>Unity 연결 항목: <c>unity.exe mcp --project-path 경로</c> (stdio, 0단계 실측).</summary>
    public static JsonObject UnityEntry(string cli, string project) => new()
    {
        ["command"] = cli,
        ["args"] = new JsonArray("mcp", "--project-path", project),
    };

    public static string RoomPath(string dataDir, string roomId) => Path.Combine(dataDir, $"mcp-room-{roomId}.json");

    public static void DeleteRoom(string path) => TryDelete(path);

    /// <summary>그 폴더 .mcp.json의 서버 이름들 (연결 칸 목록). 없거나 읽지 못하면 빈 목록.</summary>
    public static IReadOnlyList<string> ServerNames(string workspaceDir)
    {
        try
        {
            var file = Path.Combine(workspaceDir, ".mcp.json");
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file))?["mcpServers"] is not JsonObject all) return [];
            return [.. all.Select(p => p.Key).Where(ClaudeArgs.IsServerName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return []; }
    }

    /// <summary>그 폴더 .mcp.json의 서버 항목 (앱 쪽 "확인"이 띄운다). 없으면 null.</summary>
    public static JsonObject? Entry(string workspaceDir, string server)
    {
        try
        {
            var file = Path.Combine(workspaceDir, ".mcp.json");
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?["mcpServers"]?[server] as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { return null; }
    }

    private static bool SameContent(string path, string json)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path) == json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
