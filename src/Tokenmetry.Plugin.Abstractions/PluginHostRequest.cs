namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 主进程经 stdin 发给宿主的一行 NDJSON 请求。v1 只定义 `scan` 与 `shutdown` 两种类型，
/// 协议为内部协议（不承诺跨版本稳定）。
/// </summary>
public sealed record PluginHostRequest
{
    /// <summary>请求类型：`scan` 或 `shutdown`。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>`scan` 请求是否要求全量快照。</summary>
    public bool ForceFull { get; init; }
}
