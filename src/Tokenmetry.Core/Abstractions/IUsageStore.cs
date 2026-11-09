using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// usage facts 存储抽象（I-002）：所有页面查询的唯一数据面。
/// 实现需保证 upsert 幂等（同一维度组合重复写入更新数值而非插新行），
/// 并通过 schema_migrations 顺序迁移自动建表 / 升级。
/// </summary>
public interface IUsageStore
{
    /// <summary>
    /// 本机设备 id（迁移 3 自举的稳定 GUID）。跨端同步的行归属维度；
    /// 设备名见实现侧的设备注册表读写方法。
    /// </summary>
    Task<string> GetLocalDeviceIdAsync(CancellationToken ct = default);

    Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default);

    /// <summary>
    /// 以"整日替换"语义写入 facts：批次涉及的每个日期，先删除该日的分解粒度行
    /// （agent / model / project 等非 full 粒度）再写入本批行，full 粒度行不受影响。
    /// 用于 CLI 数据源的分解写入，日内消失的分解条目不残留。
    /// </summary>
    Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default);

    Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default);

    Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default);

    Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default);

    /// <summary>
    /// 删除全部分解粒度行（grain ≠ full 的 CLI 源行）；full 粒度行与 session_facts 不受影响。
    /// 用于数据源从 CLI 切到本地采集后的清理（I-005）：本地采集写入的只有 full 粒度行，
    /// 旧 CLI 分解行残留会在混源日干扰读取与诊断。
    /// </summary>
    Task DeleteDecompositionFactsAsync(CancellationToken ct = default);

    // ---- 跨端同步（relay 设计 §4.2/§4.3）：以下方法为 SyncEngine 的存储面 ----

    /// <summary>本机设备名（迁移时抓 MachineName 当初值，之后不随 hostname 漂移，A7）。</summary>
    Task<string> GetLocalDeviceNameAsync(CancellationToken ct = default);

    Task SetLocalDeviceNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// 远端行原样落地：device 与 updated_at 按载荷保留、不盖本机戳、不重算值（A3），
    /// LWW 守卫（仅当载荷 updated_at 更新时覆盖）。
    /// </summary>
    Task UpsertRemoteUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default);

    /// <inheritdoc cref="UpsertRemoteUsageFactsAsync"/>
    Task UpsertRemoteSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default);

    /// <summary>本机设备且 updated_at 严格大于 since 的行（增量推送的读取面）。</summary>
    Task<IReadOnlyList<UsageFact>> GetLocalUsageFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <inheritdoc cref="GetLocalUsageFactsSinceAsync"/>
    Task<IReadOnlyList<SessionFact>> GetLocalSessionFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>同步游标：上次推送的最大 updated_at 与上次拉取见到的服务端 seq。</summary>
    Task<(DateTimeOffset? LastPushedAt, long LastPullSeq)> GetSyncCursorAsync(CancellationToken ct = default);

    Task SetSyncCursorAsync(DateTimeOffset? lastPushedAt, long? lastPullSeq, CancellationToken ct = default);

    /// <summary>远端设备清单（本机除外；设备清单对账的本地侧账本）。</summary>
    Task<IReadOnlyList<SyncDeviceInfo>> GetRemoteDevicesAsync(CancellationToken ct = default);

    Task UpsertRemoteDevicesAsync(IReadOnlyList<SyncDeviceInfo> devices, CancellationToken ct = default);

    /// <summary>
    /// 设备退场（A13 设备清单对账 / 解绑）：删除其 remote_devices 行与该设备的全部 usage/session 行。
    /// </summary>
    Task DeleteRemoteDeviceDataAsync(string deviceId, CancellationToken ct = default);
}
