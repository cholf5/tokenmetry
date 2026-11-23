namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 插件包 manifest（plugin.json）的契约形状。字段语义见
/// docs/plans/2026-11-23-custom-agent-plugin-design.md §5.2：
/// `id` 是包身份，`agentId` 是 Agent 稳定身份，`displayName` 写入 facts 作为 Agent 键。
/// </summary>
public sealed record PluginManifest
{
    /// <summary>包格式版本；v1 固定为 1。</summary>
    public int SchemaVersion { get; init; }

    /// <summary>插件包身份，安装/升级/卸载按它定位。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Agent 稳定身份，用于冲突检查。</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>写入 facts 的 Agent 键，同时是 UI 显示名。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>插件版本（SemVer 字符串）。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>面向的契约 API 版本；v1 为 "1"。</summary>
    public string ApiVersion { get; init; } = string.Empty;

    /// <summary>入口程序集文件名。</summary>
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>实现 IUsageSourcePlugin 的类型全名。</summary>
    public string EntryType { get; init; } = string.Empty;

    /// <summary>相对路径的 PNG 图标。</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>可选、纯展示的作者信息，宿主不做验证。</summary>
    public string? Author { get; init; }

    /// <summary>可选、纯展示的描述，宿主不做验证。</summary>
    public string? Description { get; init; }
}
