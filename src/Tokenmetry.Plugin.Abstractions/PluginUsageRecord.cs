namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 插件一次扫描返回的单条用量记录。字段语义见
/// docs/plans/2026-11-23-custom-agent-plugin-design.md §5.3 / 附录 B：
/// `RecordId` 在快照内稳定唯一用于去重，`CostUsd = null` 表示未知成本（交给定价引擎），
/// 缓存写入细分 5m + 1h 必须等于总数。
/// </summary>
public sealed record PluginUsageRecord
{
    /// <summary>非空、稳定、在本次快照内唯一。</summary>
    public string RecordId { get; init; } = string.Empty;

    /// <summary>有效事件时间；未知时区按 UTC。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>插件内唯一的会话 id；宿主会加 plugin 命名空间前缀。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>项目身份（cwd 或等价物）。</summary>
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>可选展示名；缺省由 ProjectPath 末段推导。</summary>
    public string? ProjectDisplayName { get; init; }

    /// <summary>模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>可选 provider；空表示未指定。</summary>
    public string? Provider { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheCreationTokens { get; init; }

    public long CacheCreationEphemeral5mTokens { get; init; }

    public long CacheCreationEphemeral1hTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>null = 未知；0 = 免费；正数 = 日志自带成本。</summary>
    public decimal? CostUsd { get; init; }

    public PluginSpeedTier Speed { get; init; }
}

/// <summary>速度档位，与 Core 的四档镜像对齐（含 Ultrafast）。</summary>
public enum PluginSpeedTier
{
    Standard,
    Fast,
    Flex,
    Ultrafast,
}
