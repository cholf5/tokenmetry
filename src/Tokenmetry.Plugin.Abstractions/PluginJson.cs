using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 契约 DTO 的 JSON 门面。源生成上下文（camelCase wire）保证反射序列化禁用路径
/// （AOT/单文件发布）也能工作。宿主协议消息的序列化方法在后续任务加入同一上下文。
/// </summary>
public static class PluginJson
{
    public static string SerializeManifest(PluginManifest manifest) =>
        JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest);

    public static PluginManifest? DeserializeManifest(string json) =>
        JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);

    public static string SerializeRecord(PluginUsageRecord record) =>
        JsonSerializer.Serialize(record, PluginJsonContext.Default.PluginUsageRecord);

    public static PluginUsageRecord? DeserializeRecord(string json) =>
        JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginUsageRecord);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginUsageRecord))]
internal sealed partial class PluginJsonContext : JsonSerializerContext;
