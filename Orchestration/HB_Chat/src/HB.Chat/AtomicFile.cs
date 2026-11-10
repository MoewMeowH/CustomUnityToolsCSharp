using System.Text;

namespace HB.Chat;

/// <summary>앱 설정 · 색인 파일 쓰기: 임시 파일(.tmp)에 쓰고 덮어 옮긴다 — 쓰다 끊겨도 원래 파일이 남는다.</summary>
internal static class AtomicFile
{
    /// <summary>UTF-8(BOM 없음)로 쓴다. 폴더가 없으면 만든다. 실패하면 예외 그대로 (IOException · UnauthorizedAccessException).</summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
