using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HB.Chat;

/// <summary>
/// 데이터 폴더 쓰기가 다른 곳으로 옮겨지는지 (4차 A): Claude 데스크톱 앱(MSIX) 안의 셸에서 띄우면 %LOCALAPPDATA% 쓰기가
/// %LOCALAPPDATA%\Packages\&lt;패키지&gt;\LocalCache\Local\… 로 옮겨져, 밖에서 띄운 앱과 데이터가 두 벌로 갈린다 (2026-10-28 실사용에서 발견).
/// 그 프로세스는 패키지 ID가 없어(GetCurrentPackageFullName = 패키지 없음) 알 수 없으므로, 시험 파일을 만들어 실제로 쓰인 곳(최종 경로)을 본다.
/// 서버(HB.Chat)와 데스크톱 앱(HB.Chat.Desktop — 이 파일을 링크로 함께 컴파일)이 같이 쓴다. 두 어셈블리에 같은 이름이 생기므로
/// internal (/self-review dc27a724bf C7).
/// </summary>
internal static class RedirectedFolder
{
    /// <summary>
    /// 폴더에 시험 파일을 만들어(닫으면 지워짐) 실제로 쓰인 폴더를 본다. 패키지 캐시로 옮겨지면 그 폴더, 아니면 null.
    /// Windows가 아니거나 확인하지 못하면 null — 데이터가 갈리는 것을 막는 장치라 확인 실패로 켜지 못하게 하지는 않는다.
    /// </summary>
    public static string? Probe(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var full = Path.GetFullPath(folder);
            Directory.CreateDirectory(full);
            var file = Path.Combine(full, $".probe-{Guid.NewGuid():N}");
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
            var final = Strip(buffer.ToString());
            return IsRedirected(file, final) ? Path.GetDirectoryName(final) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>쓴 경로는 패키지 캐시가 아닌데 실제로는 패키지 캐시에 쓰였는지.</summary>
    public static bool IsRedirected(string expected, string final) =>
        IsPackageCache(Strip(final)) && !IsPackageCache(Path.GetFullPath(expected));

    /// <summary>켜지 않을 때 보일 글.</summary>
    public static string Message(string folder, string redirected) =>
        $"데이터 폴더 쓰기가 다른 곳으로 옮겨집니다: {folder} → {redirected}. " +
        "Claude 데스크톱 앱 안의 셸에서 띄운 것 같습니다 — 그대로 쓰면 탐색기 · 시작 메뉴에서 띄운 앱과 방 목록 · 기록이 두 벌로 갈립니다. " +
        "탐색기나 시작 메뉴에서 띄우세요 (시험용이면 데이터 폴더를 직접 주면 됩니다).";

    private static bool IsPackageCache(string path) =>
        path.Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);

    private static string Strip(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
