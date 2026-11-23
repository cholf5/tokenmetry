namespace Tokenmetry.Relay;

using System.Security.Cryptography;

/// <summary>
/// 中继自建网页（/admin、/leaderboard）的同源脚本资源。
/// 两页 CSP 已从 <c>script-src 'unsafe-inline'</c> 收紧到 <c>script-src 'self'</c>，
/// 全部脚本经这里提供：公共工具在 <c>relay-common.js</c>，页面各自逻辑在 <c>admin.js</c> /
/// <c>leaderboard.js</c>（嵌入资源）。
/// 同一份字节挂三个路径：<c>/assets</c>（页面 URL 不带尾斜杠时的相对解析）与
/// <c>/{page}/assets</c>（页面 URL 带尾斜杠时相对解析会多一层）——
/// 反代子路径只有浏览器知道，服务端不猜外部前缀。
/// </summary>
internal static class RelayAssetEndpoints
{
    private const string ContentType = "text/javascript; charset=utf-8";

    private static readonly string[] FileNames = ["relay-common.js", "admin.js", "leaderboard.js"];

    private static readonly Lazy<Dictionary<string, RelayAsset>> Assets = new(LoadAssets);

    public static void MapRelayAssets(this WebApplication app)
    {
        // 三个挂载点共用同一处理器：文件白名单在处理器内校验，未知名字一律 404。
        app.MapGet("/assets/{file}", ServeAsync);
        app.MapGet("/admin/assets/{file}", ServeAsync);
        app.MapGet("/leaderboard/assets/{file}", ServeAsync);
    }

    private static async Task ServeAsync(HttpContext context)
    {
        var file = context.Request.RouteValues["file"] as string ?? string.Empty;
        if (!Assets.Value.TryGetValue(file, out var asset))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 页面是 no-store，脚本按 no-cache + ETag 走条件请求：升级后不会吃到旧字节。
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = asset.ETag;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (NotModified(context.Request, asset.ETag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentType = ContentType;
        context.Response.ContentLength = asset.Content.Length;
        await context.Response.Body.WriteAsync(asset.Content, context.RequestAborted);
    }

    private static bool NotModified(HttpRequest request, string etag)
    {
        foreach (var value in request.Headers.IfNoneMatch)
        {
            if (!string.IsNullOrEmpty(value)
                && (value.Trim() == "*" || value.Contains(etag, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, RelayAsset> LoadAssets()
    {
        var assembly = typeof(RelayAssetEndpoints).Assembly;
        var assets = new Dictionary<string, RelayAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in FileNames)
        {
            using var stream = assembly.GetManifestResourceStream($"Tokenmetry.Relay.Assets.{name}")
                ?? throw new InvalidOperationException($"Missing embedded resource: Assets/{name}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var content = buffer.ToArray();
            assets.Add(name, new RelayAsset(content, ComputeETag(content)));
        }

        return assets;
    }

    private static string ComputeETag(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return $"\"{Convert.ToHexStringLower(hash.AsSpan(0, 8))}\"";
    }

    private sealed record RelayAsset(byte[] Content, string ETag);
}
