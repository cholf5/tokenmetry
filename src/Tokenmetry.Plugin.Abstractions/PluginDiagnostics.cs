namespace Tokenmetry.Plugin.Abstractions;

/// <summary>扫描状态，语义与现有数据源行一致。</summary>
public enum PluginScanStatus
{
    /// <summary>正常产出记录。</summary>
    Ok,

    /// <summary>没有找到数据（源未安装/未使用），不是错误。</summary>
    NoData,

    /// <summary>有记录但存在坏行/无效记录。</summary>
    Partial,

    /// <summary>扫描失败或输出全部不合规。</summary>
    Error,
}

/// <summary>
/// 扫描诊断。`Message` 是给用户看的短消息，不得包含敏感内容（不记录记录内容）。
/// </summary>
public sealed record PluginDiagnostics
{
    public PluginScanStatus Status { get; init; }

    public int FilesDiscovered { get; init; }

    public int FilesRead { get; init; }

    public int Records { get; init; }

    public int InvalidRecords { get; init; }

    public int DuplicateRecords { get; init; }

    public int MissingCostRecords { get; init; }

    public int UnknownModelRecords { get; init; }

    /// <summary>面向用户的短消息，不含敏感内容。</summary>
    public string? Message { get; init; }
}
