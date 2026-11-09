using System.Text.Json.Serialization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Sync;

/// <summary>
/// 同步协议的 JSON source-gen 上下文（camelCase wire）：客户端与中继共用同一形状，
/// 反射序列化禁用路径（AOT/单文件发布）也能工作。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncPushRequest))]
[JsonSerializable(typeof(SyncPullResponse))]
[JsonSerializable(typeof(SyncDeviceListResponse))]
[JsonSerializable(typeof(SyncTokenResponse))]
[JsonSerializable(typeof(UsageFact))]
[JsonSerializable(typeof(SessionFact))]
[JsonSerializable(typeof(SyncDeviceInfo))]
[JsonSerializable(typeof(LeaderboardPreferenceRequest))]
[JsonSerializable(typeof(LeaderboardPreferenceResponse))]
[JsonSerializable(typeof(LeaderboardEntry))]
[JsonSerializable(typeof(LeaderboardResponse))]
public sealed partial class SyncJsonContext : JsonSerializerContext;
