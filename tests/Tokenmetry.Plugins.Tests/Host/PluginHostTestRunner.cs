using System.Diagnostics;
using System.Text;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Host;

/// <summary>
/// 把构建期复制到测试输出目录的 fixture 插件包复制到临时目录——每个用例拿到独立的插件副本，
/// 避免用例之间互相污染。复制内容来自测试项目 `CopyPluginArtifactsForTests` 目标。
/// </summary>
internal static class TestPluginDirectory
{
    private const string SourceFolderName = "test-plugin";

    public static string CopyToTemp()
    {
        var source = Path.Combine(AppContext.BaseDirectory, SourceFolderName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"fixture plugin output not found at '{source}'; the test project copies it during build.");
        }

        var target = Directory.CreateTempSubdirectory("tokenmetry-plugin-").FullName;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        return target;
    }
}

/// <summary>一次宿主扫描的协议汇总结果。</summary>
internal sealed record HostScanResult(
    IReadOnlyList<PluginUsageRecord> Records,
    PluginDiagnostics Diagnostics,
    bool HasChanges,
    string? ErrorMessage,
    int BatchCount,
    int ExitCode);

/// <summary>每个用例的临时插件目录 + 数据目录，用完自动清理。</summary>
internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        PluginDirectory = TestPluginDirectory.CopyToTemp();
        DataDirectory = Directory.CreateTempSubdirectory("tokenmetry-data-").FullName;
    }

    public string PluginDirectory { get; }

    public string DataDirectory { get; }

    public Task WriteUsageAsync(string content) =>
        File.WriteAllTextAsync(Path.Combine(DataDirectory, "usage.jsonl"), content);

    public void Dispose()
    {
        TryDelete(PluginDirectory);
        TryDelete(DataDirectory);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论（宿主进程退出后文件句柄已释放）。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 起真实宿主进程（`dotnet Tokenmetry.PluginHost.dll ...`），按 NDJSON 协议发送一条 `scan`，
/// 聚合 `records` 直到 `done`/`error`，再发 `shutdown` 收尾。
/// </summary>
internal static class PluginHostTestRunner
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    public static async Task<HostScanResult> ScanAsync(
        string pluginDirectory,
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "plugin-host", "Tokenmetry.PluginHost.dll");
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException(
                $"plugin host output not found at '{hostPath}'; the test project copies it during build.",
                hostPath);
        }

        using var timeout = new CancellationTokenSource(ScanTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.ArgumentList.Add("--plugin-dir");
        startInfo.ArgumentList.Add(pluginDirectory);
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--host-version");
        startInfo.ArgumentList.Add("test");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start the plugin host process.");

        // 持续排空 stderr，避免宿主/插件写日志写满管道导致死锁。
        var stderrDrain = process.StandardError.ReadToEndAsync(token);

        try
        {
            var records = new List<PluginUsageRecord>();
            PluginDiagnostics? diagnostics = null;
            string? errorMessage = null;
            var hasChanges = false;
            var batchCount = 0;

            try
            {
                await process.StandardInput.WriteLineAsync(
                    PluginJson.SerializeHostRequest(new PluginHostRequest { Type = "scan", ForceFull = true }));
                await process.StandardInput.FlushAsync(token);
            }
            catch (IOException)
            {
                // 宿主在读到请求前就退出（例如加载失败）：继续按“未收到 done”处理，让断言暴露问题。
            }

            var finished = false;
            while (!finished)
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var message = PluginJson.DeserializeHostMessage(line)
                    ?? throw new InvalidOperationException($"plugin host produced an unreadable line: {line}");

                switch (message.Type)
                {
                    case "records":
                        batchCount++;
                        if (message.Batch is not null)
                        {
                            records.AddRange(message.Batch);
                        }

                        break;

                    case "done":
                        diagnostics = message.Diagnostics ?? new PluginDiagnostics();
                        hasChanges = message.HasChanges ?? false;
                        finished = true;
                        break;

                    case "error":
                        errorMessage = message.Message;
                        diagnostics = new PluginDiagnostics
                        {
                            Status = PluginScanStatus.Error,
                            Message = message.Message,
                        };
                        finished = true;
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"plugin host produced an unexpected message type: {message.Type}");
                }
            }

            await ShutdownAsync(process, token);
            await DrainStderrAsync(stderrDrain);

            return new HostScanResult(
                records,
                diagnostics ?? new PluginDiagnostics(),
                hasChanges,
                errorMessage,
                batchCount,
                process.HasExited ? process.ExitCode : -1);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task ShutdownAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(
                PluginJson.SerializeHostRequest(new PluginHostRequest { Type = "shutdown" }));
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // 宿主已退出或管道已关闭：无需再发 shutdown。
        }

        using var waitTimeout = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            await process.WaitForExitAsync(waitTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static async Task DrainStderrAsync(Task<string> stderrDrain)
    {
        try
        {
            await stderrDrain;
        }
        catch (OperationCanceledException)
        {
            // 超时取消时无需等待 stderr 读完。
        }
    }
}
