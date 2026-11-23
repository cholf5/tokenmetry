namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 一次扫描的宿主上下文。`DataDirectory` 是宿主分配给该插件的可丢弃缓存目录；
/// `ForceFull` 为 true 时插件必须返回全量快照（应用启动/安装/更新/崩溃重启后的首次扫描）。
/// </summary>
public sealed record PluginScanContext
{
    /// <summary>插件自己的缓存目录。</summary>
    public string DataDirectory { get; init; } = string.Empty;

    /// <summary>要求返回全量快照。</summary>
    public bool ForceFull { get; init; }

    /// <summary>宿主版本，供插件做兼容性提示。</summary>
    public string HostVersion { get; init; } = string.Empty;

    /// <summary>宿主使用的契约 API 版本。</summary>
    public string PluginApiVersion { get; init; } = string.Empty;
}
