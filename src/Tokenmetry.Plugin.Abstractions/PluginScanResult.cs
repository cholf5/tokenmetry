namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 一次扫描的结果。`HasChanges = true` 时 `Records` 必须是插件当前知道的完整记录集
/// （全量快照，不是增量）；`HasChanges = false` 时宿主复用上一轮成功快照。
/// </summary>
public sealed record PluginScanResult
{
    /// <summary>是否产生了新快照。</summary>
    public bool HasChanges { get; init; }

    /// <summary>全量记录集；无变化时可为空。</summary>
    public IReadOnlyList<PluginUsageRecord> Records { get; init; } = [];

    /// <summary>扫描诊断，供数据源行展示。</summary>
    public PluginDiagnostics Diagnostics { get; init; } = new();
}
