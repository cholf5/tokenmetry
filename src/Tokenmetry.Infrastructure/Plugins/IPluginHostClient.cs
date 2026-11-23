using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 一次宿主扫描的结果。`HasChanges` 与宿主 `done` 消息里的值精确一致：
/// `false` 表示插件自报无变化，调用方应复用上一轮成功快照（last-known-good）。
/// </summary>
public sealed record PluginScanOutcome(
    bool HasChanges,
    IReadOnlyList<PluginUsageRecord> Records,
    PluginDiagnostics Diagnostics);

/// <summary>
/// 主进程侧的插件宿主门面：按 plugin id 复用宿主进程，向调用方屏蔽进程生命周期。
/// </summary>
public interface IPluginHostClient : IAsyncDisposable
{
    /// <summary>
    /// 扫描一个插件。进程是新建的时候，无论 <paramref name="forceFull"/> 为何，实际都按全量下发。
    /// 超时会杀掉整个进程树并抛 <see cref="TimeoutException"/>；进程崩溃/协议错误会丢弃进程并在下次重建。
    /// </summary>
    Task<PluginScanOutcome> ScanAsync(
        PluginInstallation installation,
        bool forceFull,
        CancellationToken cancellationToken);

    /// <summary>停用/卸载时释放并遗忘该插件的宿主进程；未知 id 幂等。</summary>
    void Remove(string pluginId);

    /// <summary>
    /// 停掉并遗忘所有常驻宿主进程，但不把客户端标记为已释放：下一次 <see cref="ScanAsync"/>
    /// 会重建进程。更新替换文件前调用（Windows 上运行中的宿主会锁住 <c>plugin-host/</c> 目录，
    /// 让 rename 失败）；与永久关停的 <see cref="IAsyncDisposable.DisposeAsync"/> 不同。
    /// </summary>
    void StopAll();
}
