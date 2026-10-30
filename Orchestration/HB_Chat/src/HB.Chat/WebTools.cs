using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace HB.Chat;

/// <summary>
/// 방의 웹 가져오기(WebFetch) 사이트 허용 — 사이트마다 한 번 카드, 앱을 다시 켤 때까지 그 방에서 기억 (사용자 결정, 지휘자 브라우저 카드와 같은 결).
/// 기억 단위는 사이트 origin(스킴 · 퓨니코드 이름 · 포트) — 같은 이름의 다른 포트 · http는 다시 묻는다 (/cross-review 909a5c575e C5 · C7).
/// 로컬 · 사설망 주소는 카드로 묻지 않고 거절한다 — 이 앱(5290) · 대시보드 · 사내 서버를 부르지 않게.
/// 앱이 보지 못하는 것: claude에 내장된 공식 문서 사이트와, 사용자 · 작업 폴더 설정의 WebFetch 허용 규칙에 든 사이트는 CLI가 묻지 않고 가져와
/// 여기 · 카드 · 승인 기록을 거치지 않는다 (ClaudeArgs.WebTools 주석, C6). 이름이 사설 주소로 풀리는 것(DNS) · 리다이렉트도 보지 않는다.
/// </summary>
public sealed class WebFetchSites
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _approved = new(StringComparer.Ordinal);

    /// <summary>그 방에서 카드로 허용한 사이트인지.</summary>
    public bool IsApproved(string roomId, Uri url)
    {
        if (!_approved.TryGetValue(roomId, out var sites)) return false;
        lock (sites) return sites.Contains(Origin(url));
    }

    public void Approve(string roomId, Uri url)
    {
        var sites = _approved.GetOrAdd(roomId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (sites) sites.Add(Origin(url));
    }

    /// <summary>그 방에서 허용한 사이트를 잊는다 (방을 지울 때 · 단계를 바꿀 때 — RoomHub.ForgetCommands).</summary>
    public void Forget(string roomId) => _approved.TryRemove(roomId, out _);

    private static string Origin(Uri url) => $"{url.Scheme}://{url.IdnHost.TrimEnd('.').ToLowerInvariant()}:{url.Port}";

    /// <summary>
    /// 가져와도 되는 주소인지 — 안 되면 까닭. http · https만, 로컬 이름(localhost · *.localhost · 이 PC 이름 · 점 없는 사내 이름) · 루프백 · 사설망 ·
    /// 링크 로컬 · CGNAT · 고유 로컬(IPv6) 주소, 숫자만으로 된 이름(IPv4 다른 표기일 수 있음 — 2130706433 · 0x7f.1), 일반 도메인도 IP도 아닌 이름(밑줄 등)은 거절.
    /// </summary>
    public static string? WhyNot(string? text, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u)) return "주소를 읽지 못했습니다.";
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return "http · https 주소만 가져옵니다.";
        var host = u.Host.TrimEnd('.');
        if (host.Length == 0) return "주소를 읽지 못했습니다.";
        switch (u.HostNameType)
        {
            case UriHostNameType.Dns:
                if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                    || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    return "이 PC 주소는 가져오지 않습니다 (이 앱 · 대시보드 · 로컬 서버).";
                if (!host.Contains('.')) return "점 없는 이름(사내 · 로컬 이름)은 가져오지 않습니다.";
                // 마지막 조각이 숫자 · 16진 꼴이면 IPv4 다른 표기일 수 있다 (0x7f.1 · 127.0.1) — 이름으로 보내면 OS가 IP로 읽는다 (지휘자 K1)
                if (NumericLabel(host[(host.LastIndexOf('.') + 1)..])) return "숫자로 된 주소(IPv4 다른 표기일 수 있음)는 가져오지 않습니다.";
                break;
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                if (!IPAddress.TryParse(u.DnsSafeHost, out var ip) || Private(ip)) return "로컬 · 사설망 주소는 가져오지 않습니다.";
                break;
            default:
                // 밑줄 든 이름 등 일반 도메인도 IP도 아닌 모양 — 사내 이름일 수 있다 (/cross-review 909a5c575e C4)
                return "일반 도메인 이름이 아닌 주소는 가져오지 않습니다.";
        }
        url = u;
        return null;
    }

    private static bool NumericLabel(string label) =>
        label.Length > 0 && (label.All(char.IsAsciiDigit) || (label.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && label.Length > 2 && label[2..].All(char.IsAsciiHexDigit)));

    private static bool Private(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT(Tailscale 등)
    }
}
