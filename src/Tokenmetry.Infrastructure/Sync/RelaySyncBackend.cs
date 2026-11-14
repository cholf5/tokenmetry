using System.Net.Http.Headers;
using System.Net.Http.Json;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Infrastructure.Sync;

/// <summary>
/// 官方/自建中继的 ISyncBackend HTTP 实现（relay 设计 §4.6）：Bearer token 认证 +
/// X-Tokenmetry-Device 标识 caller 设备；协议编解码全走 SyncJsonContext（source-gen）——
/// 反射序列化在 AOT 发布下是 IL2026/IL3050 编译错误（v0.6.0 首次 AOT 打包即被这条挡下）。
/// 注册/换 token 是中继专属流程（不在抽象接口，A5）。
/// </summary>
public sealed class RelaySyncBackend : ISyncBackend
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _deviceId;

    /// <param name="httpClient">注入的 HttpClient（App 侧装配 BaseAddress/超时；此处只用相对拼接的绝对 URL）。</param>
    /// <param name="baseUrl">中继根地址（如 http://192.168.1.10:8080 或官方实例）。</param>
    /// <param name="token">账号 token（明文，随请求 Authorization: Bearer 过线）。</param>
    /// <param name="deviceId">本机设备 id（push/pull 的 caller 标识；构造时由 store 提供）。</param>
    public RelaySyncBackend(HttpClient httpClient, string baseUrl, string token, string deviceId)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = baseUrl.TrimEnd('/');
        _deviceId = deviceId;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task PushAsync(SyncPushRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/sync/push");
        message.Headers.Add("X-Tokenmetry-Device", _deviceId);
        message.Content = JsonContent.Create(request, SyncJsonContext.Default.SyncPushRequest);
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response);
    }

    public async Task<SyncPullResponse> PullAsync(long sinceSeq, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/sync/pull?since={sinceSeq}");
        message.Headers.Add("X-Tokenmetry-Device", _deviceId);
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.SyncPullResponse, ct)
            ?? throw new InvalidOperationException("Relay returned an empty pull response.");
    }

    public async Task<SyncDeviceListResponse> GetDevicesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/api/devices", ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.SyncDeviceListResponse, ct)
            ?? throw new InvalidOperationException("Relay returned an empty device list.");
    }

    public async Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}/api/devices/{Uri.EscapeDataString(deviceId)}")
        {
            Content = JsonContent.Create(new RenameDeviceRequest(name), SyncJsonContext.Default.RenameDeviceRequest),
        };
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response);
    }

    public async Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"{_baseUrl}/api/devices/{Uri.EscapeDataString(deviceId)}", ct);
        await EnsureSuccessAsync(response);
    }

    public async Task DeleteAccountAsync(CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"{_baseUrl}/api/me", ct);
        await EnsureSuccessAsync(response);
    }

    public async Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_baseUrl}/api/me/leaderboard", ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.LeaderboardPreferenceResponse, ct)
            ?? throw new InvalidOperationException("Relay returned an empty leaderboard preference.");
    }

    public async Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/api/me/leaderboard")
        {
            Content = JsonContent.Create(request, SyncJsonContext.Default.LeaderboardPreferenceRequest),
        };
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.LeaderboardPreferenceResponse, ct)
            ?? throw new InvalidOperationException("Relay returned an empty leaderboard preference.");
    }

    /// <summary>官方模式登录（中继专属）：GitHub OAuth code 换账号 token。匿名端点。</summary>
    public async Task<SyncTokenResponse> ExchangeGitHubCodeAsync(string code, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/auth/github")
        {
            Content = JsonContent.Create(new GitHubCodeRequest(code), SyncJsonContext.Default.GitHubCodeRequest),
        };
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.SyncTokenResponse, ct)
            ?? throw new InvalidOperationException("Relay returned an empty token response.");
    }

    /// <summary>注册本机设备（中继专属）：服务端建立设备行并绑定名字。返回 SyncTokenResponse：无新 token（Token 为空串），DeviceId 回显服务端登记结果。</summary>
    public async Task<SyncTokenResponse> RegisterDeviceAsync(string deviceId, string name, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/devices")
        {
            Content = JsonContent.Create(
                new RegisterDeviceRequest(deviceId, name), SyncJsonContext.Default.RegisterDeviceRequest),
        };
        message.Headers.Add("X-Tokenmetry-Device", _deviceId);
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response);
        var registered = await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.RegisterDeviceResponse, ct);
        return new SyncTokenResponse(Token: string.Empty, DeviceId: registered?.DeviceId ?? deviceId);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // 401（token 失效/删号）与其余错误同形上抛；App 层按状态码决定回退未登录态或提示。
        throw new HttpRequestException(
            $"Relay request failed: {(int)response.StatusCode} {response.StatusCode} {response.RequestMessage?.RequestUri}");
    }
}
