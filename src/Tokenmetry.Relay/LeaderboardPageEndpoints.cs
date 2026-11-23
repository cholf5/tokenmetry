namespace Tokenmetry.Relay;

/// <summary>
/// 自建中继自带的公开排行榜网页（docs/selfhost.md「排行榜」）：
/// 纯静态 HTML + 页面内脚本，读同源匿名 <c>GET /api/leaderboard</c>。
/// 页面本体在 <c>LeaderboardPage.html</c>（嵌入资源），服务端只负责发字节与安全头——
/// 不改公开 API 形状，也不引入任何管理面。
/// </summary>
internal static class LeaderboardPageEndpoints
{
    private const string ResourceName = "Tokenmetry.Relay.LeaderboardPage.html";

    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
        "img-src https: data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Lazy<string> PageHtml = new(LoadHtml);

    public static void MapLeaderboardPage(this WebApplication app)
    {
        // 路由匹配对尾部斜杠等价，单个 pattern 即可同时命中 /leaderboard 与 /leaderboard/；
        // 再注册一条 "/leaderboard/" 会撞 AmbiguousMatchException（500）。
        app.MapGet("/leaderboard", ServeAsync);
    }

    private static async Task ServeAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await context.Response.WriteAsync(PageHtml.Value, context.RequestAborted);
    }

    private static string LoadHtml()
    {
        using var stream = typeof(LeaderboardPageEndpoints).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
