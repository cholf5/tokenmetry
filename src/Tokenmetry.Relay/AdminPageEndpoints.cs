namespace Tokenmetry.Relay;

/// <summary>
/// 自建中继的运营者管理页（docs/selfhost.md「Web 管理页面」）：
/// 页面本体是公开空壳，不含任何数据；登录后页面用 Authorization 头调用
/// <see cref="AdminEndpoints"/> 的管理 API。页面代码在 <c>AdminPage.html</c>（嵌入资源）。
/// </summary>
internal static class AdminPageEndpoints
{
    private const string ResourceName = "Tokenmetry.Relay.AdminPage.html";

    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
        "img-src 'none'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Lazy<string> PageHtml = new(LoadHtml);

    public static void MapAdminPage(this WebApplication app)
    {
        app.MapGet("/admin", ServeAsync);
    }

    private static async Task ServeAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        await context.Response.WriteAsync(PageHtml.Value, context.RequestAborted);
    }

    private static string LoadHtml()
    {
        using var stream = typeof(AdminPageEndpoints).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
