using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Infrastructure.Sync;

/// <summary>
/// 同步引擎（relay 设计 §4.6）：编排一轮同步——增量 push（上次推送游标之后的本机行）→
/// pull 循环（hasMore 续拉）→ 远端行原样落地 → 设备清单对账（A13：已知远端设备从清单消失
/// 即本地清其行）→ 游标推进 → StateChanged 通知。异常向上抛（退避调度归 App 层）。
/// 单写者 + LWW 语义在存储层（<c>SqliteUsageStore</c> 远端落地路径）与服务端保证。
/// </summary>
/// <param name="store">本地 facts 存储（同步游标与远端行落地面）。</param>
/// <param name="backendProvider">同步后端工厂：返回 null = 未开启同步 → RunOnce 直接 null，零出站（G3）。</param>
public sealed class SyncEngine(IUsageStore store, Func<ISyncBackend?> backendProvider)
{
    /// <summary>App 层刷新设备清单/同步状态用（每轮收尾触发一次）。</summary>
    public event Action? StateChanged;

    public async Task<SyncRunResult?> RunOnceAsync(CancellationToken ct = default)
    {
        var backend = backendProvider();
        if (backend is null)
        {
            return null;
        }

        var (lastPushedAt, lastPullSeq) = await store.GetSyncCursorAsync(ct);

        // 推送游标为 null = 首次启用，或版本迁移复位（host 维度重建后必须整份快照）。
        // 即使本机此刻没有任何行，也要推一次空快照：服务端据此清掉该设备的旧行并推进 epoch，
        // 否则已升级的客户端会一直看到本设备迁移前的 host='' 幽灵行。
        var fullSnapshot = lastPushedAt is null;

        // push：只发上次推送游标之后、归属本机的行（值变化检测保证增量天然小）。
        var since = lastPushedAt ?? DateTimeOffset.MinValue;
        var localUsage = await store.GetLocalUsageFactsSinceAsync(since, ct);
        var localSessions = await store.GetLocalSessionFactsSinceAsync(since, ct);
        if (localUsage.Count > 0 || localSessions.Count > 0 || fullSnapshot)
        {
            await backend.PushAsync(new SyncPushRequest(localUsage, localSessions, fullSnapshot), ct);
        }

        var localDeviceId = await store.GetLocalDeviceIdAsync(ct);
        var knownDevices = (await store.GetRemoteDevicesAsync(ct))
            .ToDictionary(device => device.DeviceId, StringComparer.Ordinal);
        var epochPurgedDeviceIds = new List<string>();

        // pull：循环续拉直至 hasMore=false（行数上限分页），逐批落地。
        long pullSeq = lastPullSeq;
        var pulledUsage = 0;
        var pulledSessions = 0;
        List<SyncDeviceInfo> devices = new();
        var hasMore = true;
        while (hasMore)
        {
            var response = await backend.PullAsync(pullSeq, ct);

            // 行身份 epoch：服务端整份快照替换过某设备的数据时，先清掉它的本地旧副本再应用本批。
            // 必须在 upsert 之前处理：本批/后续批可能同时含该设备的新行，先清后写才不会与旧行共存。
            foreach (var device in response.Devices)
            {
                if (device.DeviceId == localDeviceId)
                {
                    continue;
                }

                if (knownDevices.TryGetValue(device.DeviceId, out var known)
                    && known.DataEpoch != device.DataEpoch)
                {
                    await store.DeleteRemoteDeviceDataAsync(device.DeviceId, ct);
                    epochPurgedDeviceIds.Add(device.DeviceId);
                }

                knownDevices[device.DeviceId] = device;
            }

            if (response.UsageFacts.Count > 0)
            {
                await store.UpsertRemoteUsageFactsAsync(response.UsageFacts, ct);
            }

            if (response.SessionFacts.Count > 0)
            {
                await store.UpsertRemoteSessionFactsAsync(response.SessionFacts, ct);
            }

            pulledUsage += response.UsageFacts.Count;
            pulledSessions += response.SessionFacts.Count;
            devices = response.Devices.ToList();
            pullSeq = Math.Max(pullSeq, response.Seq);
            hasMore = response.HasMore;
        }

        // 服务端的设备清单包含 caller 自身；本机不是"远端设备"——剔除后才落 remote_devices
        // （终审 C2/I3：否则 UI 重复出本机，且对账清退可能把本机当清退对象）。
        devices = devices.Where(device => device.DeviceId != localDeviceId).ToList();

        // A13 设备清单对账：本地已知的远端设备从账号清单消失（他机解绑/删号传播）→ 清其本地副本。
        // 本机永远豁免（终审 C2）：设备被远端解绑/中继重建后响应清单缺 caller，若不清退豁免，
        // 空闲设备的本地历史会被 DeleteRemoteDeviceDataAsync(本机) 整表删除——不可恢复。
        //
        // 只清退本轮还没在 pull 循环里做过 epoch 清库的设备：后者已经"先清后写"，这里再删一次
        // 会把刚落地的新快照行一起删掉（epoch 变化恰好也是本测试暴露的时序）。
        var currentDevices = devices.Select(device => device.DeviceId).ToHashSet(StringComparer.Ordinal);
        var purgedDeviceIds = new List<string>(epochPurgedDeviceIds);
        var a13PurgedDeviceIds = (await store.GetRemoteDevicesAsync(ct))
            .Where(device => device.DeviceId != localDeviceId && !currentDevices.Contains(device.DeviceId))
            .Select(device => device.DeviceId)
            .Where(deviceId => !epochPurgedDeviceIds.Contains(deviceId, StringComparer.Ordinal))
            .ToList();
        foreach (var purgedDeviceId in a13PurgedDeviceIds)
        {
            await store.DeleteRemoteDeviceDataAsync(purgedDeviceId, ct);
        }

        purgedDeviceIds.AddRange(a13PurgedDeviceIds);

        if (devices.Count > 0)
        {
            await store.UpsertRemoteDevicesAsync(devices, ct);
        }

        // 游标推进：推送游标 = 批次内最大 updated_at（严格大于过滤下，批内同戳行下轮重发是安全的：
        // 服务端 LWW 幂等）；无行可推时保持旧值——凭空推进会跳过期间写入的本地行。
        DateTimeOffset? newPushedAt = lastPushedAt;
        if (localUsage.Count + localSessions.Count > 0)
        {
            var batchMax = localUsage.Select(row => row.UpdatedAt ?? DateTimeOffset.MinValue)
                .Concat(localSessions.Select(row => row.UpdatedAt ?? DateTimeOffset.MinValue))
                .Concat(lastPushedAt is { } previous ? new[] { previous } : Array.Empty<DateTimeOffset>())
                .Max();
            newPushedAt = batchMax;
        }
        else if (fullSnapshot)
        {
            // 空快照也要推进推送游标，否则下一轮会重复推空快照并反复推进 epoch。
            newPushedAt = DateTimeOffset.UtcNow;
        }

        await store.SetSyncCursorAsync(newPushedAt, pullSeq, ct);
        StateChanged?.Invoke();

        return new SyncRunResult(PushedUsage: localUsage.Count, PushedSessions: localSessions.Count,
            PulledUsage: pulledUsage, PulledSessions: pulledSessions, PurgedDeviceIds: purgedDeviceIds);
    }
}

/// <summary>一轮同步的计数结果（设置页"上次同步"摘要用）。</summary>
public sealed record SyncRunResult(
    int PushedUsage,
    int PushedSessions,
    int PulledUsage,
    int PulledSessions,
    IReadOnlyList<string> PurgedDeviceIds);
