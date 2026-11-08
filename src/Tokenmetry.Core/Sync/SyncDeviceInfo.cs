namespace Tokenmetry.Core.Sync;

/// <summary>
/// 同步视野内的一台设备（本机或远端）：设备元数据随设备注册同步，供筛选下拉显示。
/// </summary>
public sealed record SyncDeviceInfo(string DeviceId, string Name, DateTimeOffset LastSeenAt);
