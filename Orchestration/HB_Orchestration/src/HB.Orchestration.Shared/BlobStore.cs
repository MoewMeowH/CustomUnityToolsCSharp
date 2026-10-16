using System.Security.Cryptography;

namespace HB.Orchestration.Shared;

/// <summary>
/// 해시 이름으로 원문을 한 번만 저장하는 폴더 (blobs/&lt;sha256&gt;.txt). MCP 서버가 쓰고 대시보드가 읽는다.
/// 같은 내용이면 이름이 같으므로 여러 서버 프로세스가 동시에 써도 결과가 같다.
/// </summary>
public static class BlobStore
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>호출 기록 파일과 같은 폴더 아래 blobs/.</summary>
    public static string DefaultFolderFor(string callLogPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(callLogPath))!, "blobs");

    /// <summary>설정값이 비면 기본 폴더, 상대 경로면 호출 기록 폴더 기준.</summary>
    public static string ResolveFolder(string? configured, string callLogPath)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultFolderFor(callLogPath);
        var logDir = Path.GetDirectoryName(Path.GetFullPath(callLogPath))!;
        return Path.GetFullPath(configured, logDir);
    }

    /// <summary>64자리 소문자 hex만 유효 (대시보드에서 경로 조작을 막는 검사).</summary>
    public static bool IsValidSha(string? sha) =>
        sha is { Length: 64 } && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string PathFor(string folder, string sha) => Path.Combine(folder, sha + ".txt");

    /// <summary>
    /// 없으면 저장한다. 성공(이미 있음 포함)이면 null, 실패면 "예외 이름: 메시지". 예외를 던지지 않는다.
    /// 임시 파일에 쓴 뒤 덮어쓰지 않는 이동으로 옮겨, 쓰다 만 파일이 해시 이름으로 남지 않게 한다.
    /// </summary>
    public static string? TryStore(string folder, string sha, byte[] bytes)
    {
        string? tmp = null;
        try
        {
            if (!IsValidSha(sha)) return $"잘못된 해시 '{sha}'";
            var path = PathFor(folder, sha);
            if (File.Exists(path)) return null;

            Directory.CreateDirectory(folder);
            tmp = Path.Combine(folder, $"{sha}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                File.Move(tmp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // 다른 서버 프로세스가 같은 내용을 먼저 저장했다.
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (tmp is not null)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception) { /* 임시 파일 정리 실패는 무시 */ }
            }
        }
    }
}
