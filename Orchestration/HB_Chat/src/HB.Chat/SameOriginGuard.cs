namespace HB.Chat;

/// <summary>
/// GET · HEAD · OPTIONS 밖의 요청은 같은 출처에서 온 것만 받는다 (계획서 5번 보안).
/// 이 앱은 편집 권한으로 claude를 띄우므로, 다른 웹 페이지가 localhost로 POST해 지휘자를 움직이는 것을 막는다.
/// Razor Pages의 antiforgery 검사와 겹쳐 둔다. Origin이 없으면 Referer로, 둘 다 없으면 거절한다.
/// </summary>
public static class SameOriginGuard
{
    /// <summary>
    /// 승인 훅(claude → 앱)은 브라우저가 아니라 Origin · Referer가 없다. 이 경로만 빼고, 실행마다 바뀌는 토큰으로 지킨다 (ApprovalEndpoint).
    /// </summary>
    public const string ApprovalPath = "/approve";

    public static bool IsAllowed(string method, string? origin, string? referer, string scheme, string host, string? path = null)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return true;
        if (HttpMethods.IsPost(method) && string.Equals(path, ApprovalPath, StringComparison.OrdinalIgnoreCase)) return true;
        var self = $"{scheme}://{host}";
        if (!string.IsNullOrEmpty(origin)) return string.Equals(origin, self, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
            return string.Equals(r.GetLeftPart(UriPartial.Authority), self, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public static IApplicationBuilder UseSameOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            if (!IsAllowed(req.Method, req.Headers.Origin, req.Headers.Referer, req.Scheme, req.Host.Value ?? "", req.Path.Value))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("같은 출처의 요청만 받습니다.");
                return;
            }
            await next();
        });
}
