using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.Infrastructure.Sync;

namespace Tokenmetry.App.Services;

/// <summary>
/// 官方实例的编译期占位常量（relay 设计 §7 上线项：构建期占位 + 发布配置覆盖）。
/// 空串 = 官方通道未就绪：登录按钮给出错误态、不发起任何网络动作。
/// </summary>
public static class TokenmetryOfficial
{
    /// <summary>GitHub OAuth App 的 Client Id（scope 只用 read:user，U3）。</summary>
    public const string GitHubClientId = "";

    /// <summary>官方中继根地址（如 https://relay.tokenmetry.example）。</summary>
    public const string OfficialRelayUrl = "";
}

/// <summary>
/// 官方模式的 GitHub loopback 登录（relay 设计 §4.6）：本机随机端口起 HttpListener →
/// 打开系统浏览器到 github.com/login/oauth/authorize（scope=read:user + state 防伪）→
/// 等 GitHub 重定向回 127.0.0.1 取 code → 经中继交换账号 token。
/// 客户端不持 secret（交换在中继完成）；PKCE 由官方 OAuth App 形态决定（plain OAuth App 无 PKCE）。
/// </summary>
public static class GitHubSyncLogin
{
    /// <summary>等浏览器完成授权的最长时间。</summary>
    private static readonly TimeSpan WaitForCallback = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 走完 loopback 流程并换得账号 token。返回 null = 用户放弃 / 超时 / 官方配置未就绪；
    /// 其余失败（网络 / 交换被拒）向上抛，由调用方呈错误态。
    /// </summary>
    public static async Task<string?> AcquireTokenAsync(RelaySyncBackend backend, CancellationToken ct = default)
    {
        var clientId = TokenmetryOfficial.GitHubClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        var port = ReserveLoopbackPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            var state = Guid.NewGuid().ToString("N");
            var redirectUri = $"http://127.0.0.1:{port}/";
            var authorizeUrl = "https://github.com/login/oauth/authorize"
                + $"?client_id={Uri.EscapeDataString(clientId)}"
                + "&scope=read:user"
                + $"&state={state}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
            OpenBrowser(authorizeUrl);

            // 只收第一个回调；state 不匹配视为伪造请求，放弃本次登录。
            var context = await listener.GetContextAsync().WaitAsync(WaitForCallback, ct);
            var code = context.Request.QueryString["code"];
            var returnedState = context.Request.QueryString["state"];
            await WriteCallbackResponseAsync(context, ct);
            if (string.IsNullOrEmpty(code) || !string.Equals(returnedState, state, StringComparison.Ordinal))
            {
                return null;
            }

            var token = await backend.ExchangeGitHubCodeAsync(code, ct);
            return string.IsNullOrEmpty(token.Token) ? null : token.Token;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>随机回环端口（TcpListener :0 探测，随后 HttpListener 复用——极小竞窗，v1 接受）。</summary>
    private static int ReserveLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static void OpenBrowser(string url)
    {
        // 系统默认浏览器；失败不吞——调用方呈错误态（无法打开浏览器 = 无法完成官方登录）。
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static async Task WriteCallbackResponseAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        var html = "<html><body style=\"font-family:sans-serif\"><p>Tokenmetry sign-in complete — you can close this window.</p></body></html>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        await response.OutputStream.WriteAsync(buffer, ct);
        await response.OutputStream.FlushAsync(ct);
        response.Close();
    }
}
