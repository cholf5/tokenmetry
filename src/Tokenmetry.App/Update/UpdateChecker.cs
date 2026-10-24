using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tokenmetry.App.Update;

/// <summary>一次更新检查的结论。</summary>
public enum UpdateCheckOutcome
{
    /// <summary>本地版本不落后于最新 Release。</summary>
    UpToDate,

    /// <summary>存在更新（LatestVersion / ReleaseUrl 已置位）。</summary>
    UpdateAvailable,

    /// <summary>网络或解析失败（原因在 Error：离线、被墙、响应不可识别）。</summary>
    Failed,
}

/// <summary>Failed 的细分：HTTP 状态给短诊断，其余给原始报文。</summary>
public enum UpdateCheckErrorKind
{
    None,

    /// <summary>服务端返回非成功状态（Error = "HTTP 403" 式短诊断）。</summary>
    HttpStatus,

    /// <summary>网络层失败（离线 / DNS / 超时，Error = 异常原文）。</summary>
    Network,

    /// <summary>连上了但响应里没有可识别的版本（代理劫持页、端点变更）。</summary>
    InvalidResponse,
}

/// <summary>检查结果：UpdateAvailable 时置 LatestVersion/ReleaseUrl，Failed 时置 Error/ErrorKind。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? Error = null,
    UpdateCheckErrorKind ErrorKind = UpdateCheckErrorKind.None);

/// <summary>
/// 更新检查：请求 GitHub 网页侧 latest-release 重定向并与本地程序集版本比较（去 v 前缀、
/// 三段数字）。刻意不走 api.github.com——未认证 API 按每 IP 60 次/小时限流，共享代理/CGNAT
/// 出口极易 403；网页端 releases/latest 回 302，Location 头里就带 Release URL，读它即可拿到
/// tag，无需跟随重定向下载整页。不建自定义代理管线：HttpClientHandler 自动吃 https_proxy 等
/// 环境变量，与更新包下载器同一代理行为。
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>latest-release 重定向端点（GitHub 网页，无 API 限流）。</summary>
    public const string LatestReleaseUrl = $"https://github.com/{UpdateAssets.Repository}/releases/latest";

    /// <summary>Release 列表页——检查失败时的逃生口。</summary>
    public const string ReleasesPageUrl = $"https://github.com/{UpdateAssets.Repository}/releases";

    /// <summary>当前应用版本（v 前缀 + 三段数字），与设置页「关于」同源：程序集版本。</summary>
    public static string CurrentVersion { get; } =
        "v" + (typeof(UpdateChecker).Assembly.GetName().Version is { } version
            ? version.ToString(3)
            : "0.0.0");

    /// <summary>CheckAsync 的请求基址（相对 Location 头按它补全）。</summary>
    private static readonly Uri LatestReleaseBaseUri = new(LatestReleaseUrl, UriKind.Absolute);

    private readonly HttpMessageHandler? _handler;
    private readonly string _currentVersion;

    /// <summary>handler 供测试注入假响应；currentVersion 覆盖比较基线，保证注入基线下 User-Agent 与 UpToDate 状态行自洽。</summary>
    public UpdateChecker(HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        _handler = handler;
        _currentVersion = currentVersion ?? CurrentVersion;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(LatestReleaseUrl, ct);
            var location = response.Headers.Location is { IsAbsoluteUri: false } relative
                ? new Uri(LatestReleaseBaseUri, relative) // 相对 Location 按请求地址补全
                : response.Headers.Location;
            if (location is null)
            {
                response.EnsureSuccessStatusCode(); // 错误状态码归入 HttpStatus 类失败
                return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                    Error: "no release redirect", ErrorKind: UpdateCheckErrorKind.InvalidResponse);
            }
            return Evaluate(location.ToString(), _currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // 一律转结果不抛异常：离线/超时/HTTP 错误都进 Failed 带消息，UI 不接异常。
            return ToFailed(e);
        }
    }

    /// <summary>HTTP 状态失败折叠为短诊断（"HTTP 403"）；网络失败保留异常原文。</summary>
    private static UpdateCheckResult ToFailed(Exception e) =>
        e is HttpRequestException { StatusCode: { } status }
            ? new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: $"HTTP {(int)status}", ErrorKind: UpdateCheckErrorKind.HttpStatus)
            : new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: e.Message, ErrorKind: UpdateCheckErrorKind.Network);

    private HttpClient CreateClient()
    {
        // 不自动重定向：我们要的就是 302 的 Location；跟下去等于白下载整页。
        // 测试注入的 handler 本身也不跟随。
        var client = _handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"tokenmetry-desktop/{_currentVersion.TrimStart('v')}");
        return client;
    }

    /// <summary>本实例的比较基线（v 前缀、三段数字）；UpToDate 状态行显示它而非静态程序集版本，注入基线在测试里保持自洽。</summary>
    public string Version => _currentVersion;

    /// <summary>从 Release URL 提取 tag 并与本地版本比较。internal 供测试。</summary>
    internal static UpdateCheckResult Evaluate(string? releaseUrl, string currentVersion)
    {
        var tag = ExtractTag(releaseUrl);
        if (!TryParseVersion(tag, out var latest))
            return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: tag ?? "no release redirect", ErrorKind: UpdateCheckErrorKind.InvalidResponse);

        TryParseVersion(currentVersion, out var current); // 本地版本解析不出按 0.0.0：任何 Release 都算新
        var outcome = Compare(latest, current) > 0 ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate;
        return new UpdateCheckResult(outcome, LatestVersion: tag, ReleaseUrl: releaseUrl);
    }

    /// <summary>取 URL 最后一段路径（…/releases/tag/&lt;tag&gt; → &lt;tag&gt;）；非 URL 原样返回供诊断。</summary>
    internal static string? ExtractTag(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl)) return null;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri)) return releaseUrl;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments.LastOrDefault(s => s.Length > 0);
        return last is null ? null : Uri.UnescapeDataString(last);
    }

    /// <summary>"v1.2.3" 解析为三段数字：v/V 前缀可选，缺段按 0，非数字后缀（如 -beta）忽略。</summary>
    internal static bool TryParseVersion(string? tag, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var parts = tag.TrimStart('v', 'V').Split('.');
        if (parts.Length == 0 || !StartsWithDigits(parts[0], out var major)) return false;
        var minor = parts.Length > 1 && StartsWithDigits(parts[1], out var m) ? m : 0;
        var patch = parts.Length > 2 && StartsWithDigits(parts[2], out var p) ? p : 0;
        version = (major, minor, patch);
        return true;

        static bool StartsWithDigits(string segment, out int value)
        {
            value = 0;
            var digits = segment.TakeWhile(char.IsDigit).ToArray();
            // 过长数字串按不可识别处理，int.Parse 不会溢出。
            if (digits.Length == 0 || digits.Length > 9) return false;
            value = int.Parse(digits);
            return true;
        }
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b) =>
        (a.Major, a.Minor, a.Patch).CompareTo((b.Major, b.Minor, b.Patch));
}
