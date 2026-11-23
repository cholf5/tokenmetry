namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>单个插件宿主进程：一个进程只加载一个插件，一条 NDJSON 扫描往返。</summary>
public interface IPluginHostProcess : IAsyncDisposable
{
    /// <summary>
    /// 进程是否已不可用（已退出、无法通信或已释放）。客户端据此在扫描前/失败后重建进程。
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// 强制杀掉整个进程树。用于超时等无法靠协作取消中断的场景；不等待退出、可重复调用。
    /// </summary>
    void Kill();

    Task<PluginScanOutcome> ScanAsync(bool forceFull, CancellationToken cancellationToken);
}

/// <summary>启动新的宿主进程；客户端按 plugin id 缓存其结果。</summary>
public interface IPluginHostProcessFactory
{
    IPluginHostProcess Create(PluginInstallation installation);
}
