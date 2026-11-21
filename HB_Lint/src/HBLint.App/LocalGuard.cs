using System.Security.Cryptography;
using System.Text;

namespace HBLint.App;

/// <summary>
/// 로컬 서버 문지기. 127.0.0.1에만 묶어도 같은 PC의 브라우저 페이지는 닿을 수 있어서:
/// Host가 localhost · 127.0.0.1이 아니면 거절(DNS 리바인딩), Origin이 있으면 자기 출처(또는 --dev-origin)만,
/// /api는 X-HBLint-Token 헤더가 맞아야 한다 — 사용자 정의 헤더라 다른 사이트의 폼 · 단순 요청으로는 못 보내고 CORS는 켜지 않는다.
/// </summary>
internal static class LocalGuard
{
    public const string TokenHeader = "X-HBLint-Token";

    public static bool IsLocalHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";

    public static bool TokenMatches(string expected, string? actual) =>
        actual != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    public static IApplicationBuilder UseLocalGuard(this IApplicationBuilder app, AppOptions options) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (!IsLocalHost(request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0
                && !string.Equals(origin, $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(origin, options.DevOrigin, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (request.Path.StartsWithSegments("/api") && !TokenMatches(options.Token, request.Headers[TokenHeader].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            await next();
        });
}
