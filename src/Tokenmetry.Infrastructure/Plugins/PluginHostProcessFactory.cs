using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 启动真实宿主进程（<c>dotnet Tokenmetry.PluginHost.dll</c> 或直接执行 apphost）。
/// stdout 是 NDJSON 协议；stderr 逐行交给 <c>log</c> 回调（应用日志），持续排空避免管道死锁。
/// 每个插件的 <c>--data-dir</c> 固定为 <c>&lt;pluginDataRoot&gt;/&lt;pluginId&gt;</c>，与安装目录分离，
/// 升级/卸载插件文件不会误删该插件的解析缓存。
/// </summary>
public sealed class PluginHostProcessFactory : IPluginHostProcessFactory
{
    private readonly string _hostPath;
    private readonly string _pluginDataRoot;
    private readonly Action<string>? _log;

    public PluginHostProcessFactory(string hostPath, string pluginDataRoot, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            throw new ArgumentException("host path must be provided", nameof(hostPath));
        }

        if (string.IsNullOrWhiteSpace(pluginDataRoot))
        {
            throw new ArgumentException("plugin data root must be provided", nameof(pluginDataRoot));
        }

        _hostPath = hostPath;
        _pluginDataRoot = Path.GetFullPath(pluginDataRoot);
        _log = log;
    }

    public IPluginHostProcess Create(PluginInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        // 插件 id 会拼进数据目录：必须是单段安全目录名，不能借 ../ 或绝对路径逃出数据根。
        var dataDirectory = ResolveDataDirectory(installation.Manifest.Id);

        Process process;
        try
        {
            process = Process.Start(BuildStartInfo(installation, dataDirectory))
                ?? throw new PluginHostException($"failed to start plugin host '{_hostPath}'");
        }
        catch (PluginHostException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            throw new PluginHostException(
                $"failed to start plugin host '{_hostPath}': {exception.Message}", exception);
        }

        DrainStandardError(process, installation.Manifest.Id);
        return new PluginHostProcess(process, installation.Manifest.Id);
    }

    private string ResolveDataDirectory(string pluginId)
    {
        var unsafeId = string.IsNullOrWhiteSpace(pluginId)
            || pluginId is "." or ".."
            || Path.IsPathRooted(pluginId)
            || pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || pluginId.Contains('/')
            || pluginId.Contains('\\');

        if (unsafeId)
        {
            throw new PluginHostException(
                $"plugin id '{pluginId}' is not a safe directory name");
        }

        var dataDirectory = Path.Combine(_pluginDataRoot, pluginId);

        // 纵深防御：解析后仍必须落在数据根之内。
        var rootWithSeparator = Path.EndsInDirectorySeparator(_pluginDataRoot)
            ? _pluginDataRoot
            : _pluginDataRoot + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(dataDirectory).StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new PluginHostException(
                $"plugin id '{pluginId}' resolves outside the plugin data root");
        }

        return dataDirectory;
    }

    private ProcessStartInfo BuildStartInfo(PluginInstallation installation, string dataDirectory)
    {
        var runsAsAssembly = _hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(runsAsAssembly ? "dotnet" : _hostPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (runsAsAssembly)
        {
            startInfo.ArgumentList.Add(_hostPath);
        }

        Directory.CreateDirectory(dataDirectory);

        startInfo.ArgumentList.Add("--plugin-dir");
        startInfo.ArgumentList.Add(installation.Directory);
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--host-version");
        startInfo.ArgumentList.Add(
            typeof(PluginHostProcessFactory).Assembly.GetName().Version?.ToString() ?? "0.0.0");

        return startInfo;
    }

    private void DrainStandardError(Process process, string pluginId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (_log is null)
                    {
                        continue;
                    }

                    try
                    {
                        _log($"[plugin-host:{pluginId}] {line}");
                    }
                    catch
                    {
                        // 日志回调自身异常不影响宿主进程。
                    }
                }
            }
            catch
            {
                // 进程退出/释放时读取结束；stderr 只用于日志，不影响协议。
            }
        });
    }
}

/// <summary>
/// 单个真实宿主进程。首次 <see cref="ScanAsync"/> 才写入请求；读到 <c>done</c> 返回，
/// <c>error</c> 或协议异常抛 <see cref="PluginHostException"/>。<see cref="DisposeAsync"/> 杀掉整个进程树。
/// </summary>
internal sealed class PluginHostProcess : IPluginHostProcess
{
    private readonly Process _process;
    private readonly string _pluginId;
    private bool _disposed;
    private volatile bool _communicationFailed;

    public PluginHostProcess(Process process, string pluginId)
    {
        _process = process;
        _pluginId = pluginId;
    }

    /// <summary>
    /// 已释放、通信已失败（EOF / IO 异常）或操作系统报告进程已退出。通信失败时立即返回 true，
    /// 不依赖 <see cref="Process.HasExited"/> 的时序，保证客户端能确定性地在本次调用内重建。
    /// </summary>
    public bool HasExited
    {
        get
        {
            if (_disposed || _communicationFailed)
            {
                return true;
            }

            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Kill()
    {
        if (_disposed)
        {
            return;
        }

        _communicationFailed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException
                or Win32Exception)
        {
            // 进程已退出或无法枚举进程树：已达成目标。
        }
    }

    public async Task<PluginScanOutcome> ScanAsync(bool forceFull, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = PluginJson.SerializeHostRequest(new PluginHostRequest
        {
            Type = "scan",
            ForceFull = forceFull,
        });

        try
        {
            await _process.StandardInput
                .WriteLineAsync(request.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _communicationFailed = true;
            throw new PluginHostException(
                $"failed to send scan request to plugin host for '{_pluginId}': {exception.Message}",
                exception);
        }

        var records = new List<PluginUsageRecord>();

        while (true)
        {
            string? line;
            try
            {
                line = await _process.StandardOutput
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _communicationFailed = true;
                throw new PluginHostException(
                    $"failed to read from plugin host for '{_pluginId}': {exception.Message}",
                    exception);
            }

            if (line is null)
            {
                _communicationFailed = true;
                throw new PluginHostException(
                    $"plugin host for '{_pluginId}' exited before completing the scan");
            }

            if (line.Length == 0)
            {
                continue;
            }

            PluginHostMessage? message;
            try
            {
                message = PluginJson.DeserializeHostMessage(line);
            }
            catch (JsonException exception)
            {
                throw new PluginHostException(
                    $"plugin host for '{_pluginId}' produced an unreadable message", exception);
            }

            if (message is null)
            {
                throw new PluginHostException(
                    $"plugin host for '{_pluginId}' produced an unreadable message");
            }

            switch (message.Type)
            {
                case "records":
                    if (message.Batch is not null)
                    {
                        records.AddRange(message.Batch);
                    }

                    break;

                case "done":
                    return new PluginScanOutcome(
                        HasChanges: message.HasChanges ?? false,
                        Records: records,
                        Diagnostics: message.Diagnostics ?? new PluginDiagnostics());

                case "error":
                    throw new PluginHostException(
                        string.IsNullOrWhiteSpace(message.Message)
                            ? $"plugin host for '{_pluginId}' reported an error"
                            : message.Message);

                default:
                    throw new PluginHostException(
                        $"plugin host for '{_pluginId}' produced an unexpected message type '{message.Type}'");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException
                or Win32Exception)
        {
            // 进程已退出或无法枚举进程树：继续释放句柄。
        }

        try
        {
            _process.Dispose();
        }
        catch
        {
            // 释放句柄尽力而为。
        }

        return ValueTask.CompletedTask;
    }
}
