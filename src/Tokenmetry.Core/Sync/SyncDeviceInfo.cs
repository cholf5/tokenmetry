namespace Tokenmetry.Core.Sync;

/// <summary>
/// 同步视野内的一台设备（本机或远端）：设备元数据随设备注册同步，供筛选下拉显示。
/// <see cref="DataEpoch"/> 是该设备行身份代的单调计数：服务端做整份快照替换（例如宿主维度迁移后
/// 重建行身份）时 +1；其它设备发现 epoch 变化时先清掉该设备的本地副本，再应用新批次
/// （同步协议没有行级 tombstone）。旧载荷没有该字段 → 0。
/// </summary>
public sealed record SyncDeviceInfo(string DeviceId, string Name, DateTimeOffset LastSeenAt, int DataEpoch = 0);
