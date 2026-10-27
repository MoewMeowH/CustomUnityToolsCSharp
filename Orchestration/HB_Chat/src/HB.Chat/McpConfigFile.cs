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
        if (!ClaudeArgs.IsSafeName(server)) throw new InvalidOperationException($"MCP 서버 이름 형식이 올바르지 않습니다: {server}");
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
