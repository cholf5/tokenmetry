namespace Tokenmetry.Core.Sync;

/// <summary>
/// 应用设置跨设备同步的线协议契约（settings-sync 设计 §3/§4）：逐 key 的纯偏好。
/// 值统一用 TEXT 过线（字符串原样、页边距 invariant 数字、预设清单 JSON 数组），
/// updatedAt 由服务端在写入时赋值（S3：不用设备时钟）。
/// </summary>

/// <summary>一条已同步设置（GET/写入回显）：updatedAt = 服务端落库时间。</summary>
public sealed record SyncedSetting(string Key, string Value, DateTimeOffset UpdatedAt);

/// <summary>PUT 单项：只带 key 与值（时间戳由服务端赋值，客户端不得伪造）。</summary>
public sealed record SyncedSettingUpdate(string Key, string Value);

/// <summary>PUT /api/me/settings 载荷（≤64 项，key ≤64 字符，value ≤16KB）。</summary>
public sealed record SettingsSyncPutRequest(IReadOnlyList<SyncedSettingUpdate> Settings);

/// <summary>GET / PUT /api/me/settings 的响应（全部已存 key）。</summary>
public sealed record SettingsSyncResponse(IReadOnlyList<SyncedSetting> Settings);
