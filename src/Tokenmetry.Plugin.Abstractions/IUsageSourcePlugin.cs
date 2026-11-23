namespace Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 自定义 Agent 数据源插件的入口契约。manifest `entryType` 指向的类型必须实现此接口；
/// 插件在独立宿主进程中运行，只产出契约 DTO，由主进程完成校验与 UsageRecord 映射。
/// </summary>
public interface IUsageSourcePlugin
{
    /// <summary>
    /// 扫描并返回当前快照。插件应尊重取消令牌，且返回一致快照（不能是半轮中间态）。
    /// </summary>
    Task<PluginScanResult> ScanAsync(PluginScanContext context, CancellationToken cancellationToken);
}
