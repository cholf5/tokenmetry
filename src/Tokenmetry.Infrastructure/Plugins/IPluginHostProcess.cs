namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>单个插件宿主进程：一个进程只加载一个插件，一条 NDJSON 扫描往返。</summary>
public interface IPluginHostProcess : IAsyncDisposable
{
    Task<PluginScanOutcome> ScanAsync(bool forceFull, CancellationToken cancellationToken);
}

/// <summary>启动新的宿主进程；客户端按 plugin id 缓存其结果。</summary>
public interface IPluginHostProcessFactory
{
    IPluginHostProcess Create(PluginInstallation installation);
}
