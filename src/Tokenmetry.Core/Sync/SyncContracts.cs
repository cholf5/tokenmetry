namespace Tokenmetry.Core.Sync;

/// <summary>
/// 同步协议契约（relay 设计 §4.4）：客户端与中继共享的线形状。
/// 纪律：载荷只有两张事实表行 + 设备元数据——不含 prompt / 代码 / 会话内容（隐私红线）；
/// cost_usd 由归属设备采集时一次性计算、原样传输、接收端永不重算（A3）。
/// </summary>

/// <summary>push 载荷：归属设备自身的行，必带 updated_at（LWW 版本）。</summary>
public sealed record SyncPushRequest(
    IReadOnlyList<Models.UsageFact> UsageFacts,
    IReadOnlyList<Models.SessionFact> SessionFacts);

/// <summary>
/// pull 响应：账号内其他设备的行增量（seq 之后）、全设备清单与当前 seq。
/// hasMore = 服务器还有超出本批（行数上限）的增量，客户端须以响应 Seq 续拉。
/// </summary>
public sealed record SyncPullResponse(
    long Seq,
    IReadOnlyList<Models.UsageFact> UsageFacts,
    IReadOnlyList<Models.SessionFact> SessionFacts,
    IReadOnlyList<SyncDeviceInfo> Devices,
    bool HasMore = false);

/// <summary>设备清单响应（GET /api/devices）。</summary>
public sealed record SyncDeviceListResponse(IReadOnlyList<SyncDeviceInfo> Devices);

/// <summary>登录/注册换发的账号 token 与本机设备 id。</summary>
public sealed record SyncTokenResponse(string Token, string DeviceId);

/// <summary>改设备名请求体（PATCH /api/devices/{id}）。</summary>
public sealed record RenameDeviceRequest(string Name);

/// <summary>官方模式登录的 code 交换请求体（POST /api/auth/github，G3：客户端不持 secret）。</summary>
public sealed record GitHubCodeRequest(string Code);

/// <summary>注册本机设备请求体（POST /api/devices）。</summary>
public sealed record RegisterDeviceRequest(string DeviceId, string Name);

/// <summary>注册本机设备响应：回显服务端登记后的设备 id 与名字。</summary>
public sealed record RegisterDeviceResponse(string DeviceId, string Name);
