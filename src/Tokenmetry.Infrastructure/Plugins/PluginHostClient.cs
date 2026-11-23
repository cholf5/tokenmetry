using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 主进程宿主客户端：一个 plugin id 常驻一个宿主进程。
/// 新建进程的第一次扫描强制全量（插件宿主内存态已丢）；成功一次后按调用方 <c>forceFull</c> 下发。
/// 超时杀整个进程树并抛 <see cref="TimeoutException"/>；进程已退出则就地重建并强制全量；
/// 进程扫描中崩溃/协议通信失败在同一次调用内重建并重试一次（仍强制全量）。
/// </summary>
public sealed class PluginHostClient : IPluginHostClient
{
    private readonly IPluginHostProcessFactory _factory;
    private readonly TimeSpan _scanTimeout;
    private readonly object _sync = new();
    private readonly Dictionary<string, HostEntry> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
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

        // 门闩按 plugin id 常驻，进程替换不会换门闩；同一插件的扫描串行，不同插件并行。
        var gate = GetGate(pluginId);
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            var entry = GetOrCreateEntry(installation);

            // 缓存进程已经退出：本次调用内就地重建（新进程首扫自然强制全量）。
            if (entry.Process.HasExited)
            {
                entry = await ReplaceEntryAsync(pluginId, entry, installation).ConfigureAwait(false);
            }

            var retried = false;
            while (true)
            {
                // 新建进程的第一次扫描无论如何都要全量。
                var effectiveForceFull = entry.HasCompletedScan ? forceFull : true;

                try
                {
                    var outcome = await ExecuteScanAsync(pluginId, entry, effectiveForceFull, cancellationToken)
                        .ConfigureAwait(false);
                    entry.HasCompletedScan = true;
                    return outcome;
                }
                catch (TimeoutException)
                {
                    await DiscardEntryAsync(pluginId, entry).ConfigureAwait(false);
                    throw;
                }
                catch (PluginHostException) when (
                    !retried && entry.Process.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    // 扫描途中进程死掉：重建进程并重试一次，重试一律强制全量。
                    entry = await ReplaceEntryAsync(pluginId, entry, installation).ConfigureAwait(false);
                    retried = true;
                }
                catch
                {
                    // 宿主自报错误/协议错误/调用方取消：丢弃进程交给下一轮重建。
                    await DiscardEntryAsync(pluginId, entry).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
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

    private HostEntry GetOrCreateEntry(PluginInstallation installation)
    {
        var pluginId = installation.Manifest.Id;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_processes.TryGetValue(pluginId, out var entry))
            {
                entry = new HostEntry(_factory.Create(installation));
                _processes[pluginId] = entry;
            }

            return entry;
        }
    }

    private SemaphoreSlim GetGate(string pluginId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_gates.TryGetValue(pluginId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[pluginId] = gate;
            }

            return gate;
        }
    }

    private async Task<PluginScanOutcome> ExecuteScanAsync(
        string pluginId,
        HostEntry entry,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_scanTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            return await entry.Process.ScanAsync(forceFull, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Plugin host for '{pluginId}' did not finish scanning within {_scanTimeout}.");
        }
    }

    private async Task<HostEntry> ReplaceEntryAsync(
        string pluginId,
        HostEntry oldEntry,
        PluginInstallation installation)
    {
        HostEntry? alreadyReplaced = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_processes.TryGetValue(pluginId, out var current) && !ReferenceEquals(current, oldEntry))
            {
                alreadyReplaced = current;
            }
            else
            {
                _processes.Remove(pluginId);
            }
        }

        if (alreadyReplaced is not null)
        {
            await DisposeProcessAsync(oldEntry).ConfigureAwait(false);
            return alreadyReplaced;
        }

        await DisposeProcessAsync(oldEntry).ConfigureAwait(false);

        var replacement = new HostEntry(_factory.Create(installation));

        lock (_sync)
        {
            if (_disposed)
            {
                // 关闭并发：不发布无人收尾的新进程。
                DisposeInBackground(replacement.Process);
                throw new ObjectDisposedException(nameof(PluginHostClient));
            }

            if (_processes.TryGetValue(pluginId, out var raced))
            {
                DisposeInBackground(replacement.Process);
                return raced;
            }

            _processes[pluginId] = replacement;
            return replacement;
        }
    }

    private async Task DiscardEntryAsync(string pluginId, HostEntry entry)
    {
        lock (_sync)
        {
            if (_processes.TryGetValue(pluginId, out var current) && ReferenceEquals(current, entry))
            {
                _processes.Remove(pluginId);
            }
        }

        await DisposeProcessAsync(entry).ConfigureAwait(false);
    }

    private static async Task DisposeProcessAsync(HostEntry entry)
    {
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

        public bool HasCompletedScan { get; set; }
    }
}
