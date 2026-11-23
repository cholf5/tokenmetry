using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// 设置同步的本机账面存储（独立于 IUsageStore：只被 SettingsSyncRunner 消费，
/// 不把十余个测试 fake 拖进新成员）。实现方 = SqliteUsageStore（迁移 4 建表）。
/// </summary>
public interface ISettingsSyncStateStore
{
    Task<IReadOnlyList<SyncedSettingState>> GetSettingsSyncStateAsync(CancellationToken ct = default);

    /// <summary>逐 key upsert（覆盖 value 与 synced_at）。</summary>
    Task UpsertSettingsSyncStateAsync(IReadOnlyList<SyncedSettingState> rows, CancellationToken ct = default);

    /// <summary>清空账面（启用同步/换账号时重置，S8）。</summary>
    Task ClearSettingsSyncStateAsync(CancellationToken ct = default);
}
