using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 主进程宿主客户端：一个 plugin id 常驻一个宿主进程。
/// 新建进程的第一次扫描强制全量（插件宿主内存态已丢）；成功一次后按调用方 <c>forceFull</c> 下发。
/// 超时杀整个进程树并抛 <see cref="TimeoutException"/>；进程崩溃/协议错误/取消则丢弃进程，
/// 下次扫描重建并再次强制全量。
/// </summary>
public sealed class PluginHostClient : IPluginHostClient
{
    private readonly IPluginHostProcessFactory _factory;
    private readonly TimeSpan _scanTimeout;
    private readonly object _sync = new();
    private readonly Dictionary<string, HostEntry> _processes = new(StringComparer.Ordinal);
    private bool _disposed;

    public PluginHostClient(IPluginHostProcessFactory factory, TimeSpan scanTimeout)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _scanTimeout = scanTimeout;
    }

    public async Task<PluginScanOutcome> ScanAsync(
        PluginInstallation installation,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var pluginId = installation.Manifest.Id;

        HostEntry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_processes.TryGetValue(pluginId, out entry!))
            {
                entry = new HostEntry(_factory.Create(installation));
                _processes[pluginId] = entry;
            }
        }

        var acquired = false;
        try
        {
            // 同一插件的扫描串行执行；不同插件可并行。
            await entry.ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            // 新建进程的第一次扫描无论如何都要全量。
            var effectiveForceFull = entry.HasCompletedScan ? forceFull : true;

            using var timeout = new CancellationTokenSource(_scanTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);

            PluginScanOutcome outcome;
            try
            {
                outcome = await entry.Process
                    .ScanAsync(effectiveForceFull, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await DiscardAsync(pluginId, entry).ConfigureAwait(false);
                throw new TimeoutException(
                    $"Plugin host for '{pluginId}' did not finish scanning within {_scanTimeout}.");
            }
            catch
            {
                // 进程退出/崩溃/协议错误/调用方取消：丢弃进程，下次扫描重建并强制全量。
                await DiscardAsync(pluginId, entry).ConfigureAwait(false);
                throw;
            }

            entry.HasCompletedScan = true;
            return outcome;
        }
        finally
        {
            if (acquired)
            {
                entry.ScanGate.Release();
            }
        }
    }

    public void Remove(string pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);

        HostEntry? entry;
        lock (_sync)
        {
            if (!_processes.Remove(pluginId, out entry))
            {
                return;
            }
        }

        DisposeInBackground(entry.Process);
    }

    public async ValueTask DisposeAsync()
    {
        HostEntry[] entries;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _processes.Values.ToArray();
            _processes.Clear();
        }

        foreach (var entry in entries)
        {
            try
            {
                await entry.Process.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 关闭路径上忽略单个进程的清理异常；继续释放其余进程。
            }
        }
    }

    private async Task DiscardAsync(string pluginId, HostEntry entry)
    {
        lock (_sync)
        {
            if (_processes.TryGetValue(pluginId, out var current) && ReferenceEquals(current, entry))
            {
                _processes.Remove(pluginId);
            }
        }

        try
        {
            await entry.Process.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 进程已死或已被移除：清理尽力而为。
        }
    }

    private static void DisposeInBackground(IAsyncDisposable disposable)
    {
        ValueTask completion;
        try
        {
            // 同步调用：真实进程在这一步就 Kill(entireProcessTree)，随后异步等待退出。
            completion = disposable.DisposeAsync();
        }
        catch
        {
            return;
        }

        if (!completion.IsCompletedSuccessfully)
        {
            _ = ObserveAsync(completion);
        }
    }

    private static async Task ObserveAsync(ValueTask completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // 后台清理异常不传播。
        }
    }

    private sealed class HostEntry(IPluginHostProcess process)
    {
        public IPluginHostProcess Process { get; } = process;

        public SemaphoreSlim ScanGate { get; } = new(1, 1);

        public bool HasCompletedScan { get; set; }
    }
}
