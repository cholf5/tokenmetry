using Tokenmetry.Core.Sync;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// 同步后端抽象（relay 设计 A5）：官方中继、自建中继、社区实现（如 Cloudflare Worker）可插拔。
/// 实现负责 Bearer 认证与线协议编解码；编排（游标推进、设备对账、退避）在 SyncEngine。
/// </summary>
public interface ISyncBackend
{
    /// <summary>推送归属设备的行增量（服务端按 (device, PK) LWW 落库并发号 seq）。</summary>
    Task PushAsync(SyncPushRequest request, CancellationToken ct = default);

    /// <summary>拉取账号内其他设备的行增量（seq 之后）与全设备清单。</summary>
    Task<SyncPullResponse> PullAsync(long sinceSeq, CancellationToken ct = default);

    Task<SyncDeviceListResponse> GetDevicesAsync(CancellationToken ct = default);

    Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default);

    /// <summary>解绑设备（服务端清其行；其余设备经设备清单对账自然清除本地副本，A13）。</summary>
    Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>删除账号（服务端全清，A11；后续请求 401 即本地回未登录态）。</summary>
    Task DeleteAccountAsync(CancellationToken ct = default);
}
