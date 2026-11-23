namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 宿主经 stdout 发给主进程的一行 NDJSON 消息。`Type` 取 `records` / `done` / `error`：
/// `records` 每批最多 500 条；`done` 携带诊断与是否产生新快照；`error` 携带面向用户的短消息。
/// 协议为内部协议（不承诺跨版本稳定）。
/// </summary>
public sealed record PluginHostMessage
{
    /// <summary>消息类型：`records` / `done` / `error`。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>`records` 消息携带的一批用量记录。</summary>
    public IReadOnlyList<PluginUsageRecord>? Batch { get; init; }

    /// <summary>`done` 消息携带的扫描诊断。</summary>
    public PluginDiagnostics? Diagnostics { get; init; }

    /// <summary>`done` 消息：本轮是否产生新快照。</summary>
    public bool? HasChanges { get; init; }

    /// <summary>`error` 消息面向用户的短消息，不含敏感内容。</summary>
    public string? Message { get; init; }
}
