namespace Tokenmetry.Core.Models;

/// <summary>
/// 本机设置同步账面（settings_sync_state）：value = 上次与服务端对齐的值，
/// SyncedAt = 该值对应的服务端时间戳。变更检测 = 当前值 vs 这里的 value；
/// 远端是否更新 = 行 updatedAt vs 这里的 SyncedAt（settings-sync 设计 §5.1）。
/// </summary>
public sealed record SyncedSettingState(string Key, string Value, DateTimeOffset SyncedAt);
