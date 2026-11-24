using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.PluginValidator;

/// <summary>
/// 本地插件包校验器的命令入口。`validate` 只检查包结构；`scan` 解包到临时目录、
/// 用指定数据目录跑一轮真实宿主扫描。只读本地输入，不访问网络。
/// </summary>
public static class ValidatorCli
{
    /// <summary>通过。</summary>
    public const int ExitPass = 0;

    /// <summary>数据缺失（manifest / 入口程序集 / 图标缺失）。</summary>
    public const int ExitDataMissing = 2;

    /// <summary>输出不合规（结构、manifest 或记录校验失败）。</summary>
    public const int ExitInvalidOutput = 3;

    /// <summary>运行失败（无法读取包、无法启动宿主、宿主崩溃或插件抛异常）。</summary>
    public const int ExitRuntimeFailure = 4;

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, Console.Out, Console.Error);

    /// <summary>可注入输出流的重载，便于测试在不劫持进程级 <see cref="Console"/> 的情况下断言摘要。</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            PrintUsage(error);
            return ExitRuntimeFailure;
        }

        switch (args[0])
        {
            case "validate":
                return RunValidate(args, output, error);

            case "scan":
                return await RunScanAsync(args, output, error).ConfigureAwait(false);

            case "verify-contract":
                return RunVerifyContract(args, output, error);

            default:
                error.WriteLine($"unknown command '{args[0]}'");
                PrintUsage(error);
                return ExitRuntimeFailure;
        }
    }

    private static int RunValidate(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2)
        {
            PrintUsage(error);
            return ExitRuntimeFailure;
        }

        var packagePath = args[1];
        if (!File.Exists(packagePath))
        {
            error.WriteLine($"package file was not found: {packagePath}");
            return ExitRuntimeFailure;
        }

        var validation = PluginPackageValidator.Validate(packagePath);
        return ReportValidation(validation, output);
    }

    /// <summary>
    /// 维护者自检：<c>plugin-sdk/contract</c> 里的契约包必须与当前源码的公开 API 一致。
    /// 插件作者离线 restore 用的就是包内 DLL；改完契约忘了重新打包时这条会红。
    /// </summary>
    private static int RunVerifyContract(string[] args, TextWriter output, TextWriter error)
    {
        string? packagePath = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--package", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    error.WriteLine("--package requires a path");
                    PrintUsage(error);
                    return ExitRuntimeFailure;
                }

                packagePath = args[++i];
                continue;
            }

            error.WriteLine($"unknown argument '{args[i]}'");
            PrintUsage(error);
            return ExitRuntimeFailure;
        }

        packagePath ??= ResolveDefaultContractPackage(error);
        if (packagePath is null)
        {
            return ExitRuntimeFailure;
        }

        if (!File.Exists(packagePath))
        {
            error.WriteLine($"package file was not found: {packagePath}");
            return ExitRuntimeFailure;
        }

        output.WriteLine($"contract package: {packagePath}");
        ContractSyncReport report;
        try
        {
            report = ContractSyncVerifier.Verify(packagePath, typeof(IUsageSourcePlugin).Assembly);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"package could not be read: {exception.Message}");
            return ExitRuntimeFailure;
        }

        if (report.InSync)
        {
            output.WriteLine("contract: PASS (public API matches the current source)");
            return ExitPass;
        }

        output.WriteLine($"contract: FAIL ({report.Differences.Count} difference(s))");
        foreach (var difference in report.Differences)
        {
            output.WriteLine($"  {difference}");
        }

        output.WriteLine("run scripts/pack-plugin-contract.sh and commit the regenerated package");
        return ExitInvalidOutput;
    }

    /// <summary>
    /// 没传 <c>--package</c> 时，从当前目录或校验器所在目录向上找
    /// <c>plugin-sdk/contract</c>；目录里必须恰好一个 nupkg。
    /// </summary>
    private static string? ResolveDefaultContractPackage(TextWriter error)
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "plugin-sdk", "contract");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var packages = Directory.GetFiles(directory, "*.nupkg");
            if (packages.Length == 1)
            {
                return packages[0];
            }

            if (packages.Length > 1)
            {
                error.WriteLine("multiple contract packages found; pass --package <path>:");
                foreach (var package in packages.OrderBy(p => p, StringComparer.Ordinal))
                {
                    error.WriteLine($"  {package}");
                }

                return null;
            }
        }

        error.WriteLine("plugin-sdk/contract/*.nupkg was not found; pass --package <path>");
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
            }
        }
    }

    private static async Task<int> RunScanAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 4)
        {
            PrintUsage(error);
            return ExitRuntimeFailure;
        }

        var packagePath = args[1];
        string? dataDirectory = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--data", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    error.WriteLine("--data requires a directory");
                    PrintUsage(error);
                    return ExitRuntimeFailure;
                }

                dataDirectory = args[++i];
                continue;
            }

            error.WriteLine($"unknown argument '{args[i]}'");
            PrintUsage(error);
            return ExitRuntimeFailure;
        }

        if (dataDirectory is null)
        {
            error.WriteLine("--data <directory> is required for scan");
            PrintUsage(error);
            return ExitRuntimeFailure;
        }

        if (!File.Exists(packagePath))
        {
            error.WriteLine($"package file was not found: {packagePath}");
            return ExitRuntimeFailure;
        }

        if (!Directory.Exists(dataDirectory))
        {
            error.WriteLine($"data directory was not found: {dataDirectory}");
            return ExitDataMissing;
        }

        var validation = PluginPackageValidator.Validate(packagePath);
        var validationExit = ReportValidation(validation, output);
        if (validationExit != ExitPass)
        {
            return validationExit;
        }

        var tempRoot = Directory.CreateTempSubdirectory("tokenmetry-validator-").FullName;
        try
        {
            var pluginDirectory = Path.Combine(tempRoot, "plugin");
            var dataCopy = Path.Combine(tempRoot, "data");
            Directory.CreateDirectory(pluginDirectory);
            Directory.CreateDirectory(dataCopy);

            try
            {
                ZipFile.ExtractToDirectory(packagePath, pluginDirectory);
                CopyDirectory(dataDirectory, dataCopy);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error.WriteLine($"package or data could not be prepared: {exception.Message}");
                return ExitRuntimeFailure;
            }

            var hostPath = LocateHost(error);
            if (hostPath is null)
            {
                return ExitRuntimeFailure;
            }

            var scan = await RunHostScanAsync(hostPath, pluginDirectory, dataCopy, error).ConfigureAwait(false);
            if (scan is null)
            {
                return ExitRuntimeFailure;
            }

            PrintScan(scan, output);
            return MapScanExit(scan);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static int ReportValidation(PluginPackageValidation validation, TextWriter output)
    {
        if (validation.Manifest is { } manifest)
        {
            output.WriteLine(
                $"manifest: {manifest.Id} {manifest.Version} (displayName: {manifest.DisplayName})");
        }

        foreach (var warning in validation.Warnings)
        {
            output.WriteLine($"warning: {warning}");
        }

        if (validation.Errors.Count == 0)
        {
            output.WriteLine("validation: PASS");
            return ExitPass;
        }

        foreach (var validationError in validation.Errors)
        {
            output.WriteLine($"error: {validationError}");
        }

        // 退出码 2 只表示「文件缺失」这一档；其余结构/manifest 问题归为输出不合规。
        if (validation.Errors.All(IsMissingDataError))
        {
            output.WriteLine("validation: FAIL (data missing)");
            return ExitDataMissing;
        }

        output.WriteLine("validation: FAIL (invalid output)");
        return ExitInvalidOutput;
    }

    private static bool IsMissingDataError(string validationError) =>
        validationError.Contains("was not found at the package root", StringComparison.OrdinalIgnoreCase)
        || validationError.Contains("was not found in the package", StringComparison.OrdinalIgnoreCase);

    private static void PrintScan(HostScanOutcome scan, TextWriter output)
    {
        output.WriteLine($"status: {scan.Diagnostics.Status}");
        output.WriteLine($"records: {scan.Diagnostics.Records}");
        output.WriteLine($"invalid: {scan.Diagnostics.InvalidRecords}");
        output.WriteLine($"duplicates: {scan.Diagnostics.DuplicateRecords}");
        output.WriteLine($"missing-cost: {scan.Diagnostics.MissingCostRecords}");
        output.WriteLine($"unknown-model: {scan.Diagnostics.UnknownModelRecords}");
        output.WriteLine($"files-discovered: {scan.Diagnostics.FilesDiscovered}");
        output.WriteLine($"files-read: {scan.Diagnostics.FilesRead}");
        output.WriteLine($"has-changes: {scan.HasChanges}");
        output.WriteLine($"batches: {scan.BatchCount}");

        if (!string.IsNullOrWhiteSpace(scan.Diagnostics.Message))
        {
            output.WriteLine($"message: {scan.Diagnostics.Message}");
        }

        if (scan.ErrorMessage is not null)
        {
            output.WriteLine($"error: {scan.ErrorMessage}");
        }

        output.WriteLine("samples:");
        if (scan.Records.Count == 0)
        {
            output.WriteLine("(no records)");
            return;
        }

        foreach (var record in scan.Records.Take(3))
        {
            var cost = record.CostUsd is null
                ? "unknown"
                : record.CostUsd.Value.ToString(CultureInfo.InvariantCulture);
            output.WriteLine(
                $"sample: recordId={record.RecordId} sessionId={record.SessionId} model={record.Model} "
                + $"input={record.InputTokens} output={record.OutputTokens} costUsd={cost}");
        }
    }

    private static int MapScanExit(HostScanOutcome scan)
    {
        if (scan.ErrorMessage is not null)
        {
            return ExitRuntimeFailure;
        }

        if (scan.Diagnostics.InvalidRecords > 0 || scan.Diagnostics.DuplicateRecords > 0)
        {
            return ExitInvalidOutput;
        }

        return scan.Diagnostics.Status == PluginScanStatus.Error ? ExitRuntimeFailure : ExitPass;
    }

    private static async Task<HostScanOutcome?> RunHostScanAsync(
        string hostPath,
        string pluginDirectory,
        string dataDirectory,
        TextWriter error)
    {
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
        startInfo.ArgumentList.Add("validator");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"plugin host could not be started: {exception.Message}");
            return null;
        }

        if (process is null)
        {
            error.WriteLine("plugin host could not be started");
            return null;
        }

        using (process)
        {
            using var timeout = new CancellationTokenSource(ScanTimeout);
            var stderrDrain = process.StandardError.ReadToEndAsync(timeout.Token);

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
                    await process.StandardInput.FlushAsync(timeout.Token);
                }
                catch (IOException)
                {
                    // 宿主在读到请求前就退出：按“未收到结果”处理，下面的读循环会暴露。
                }

                var finished = false;
                while (!finished)
                {
                    string? line;
                    try
                    {
                        line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        error.WriteLine("plugin host scan timed out");
                        Kill(process);
                        return null;
                    }

                    if (line is null)
                    {
                        error.WriteLine("plugin host exited before reporting a result");
                        Kill(process);
                        return null;
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
                    catch (JsonException)
                    {
                        error.WriteLine("plugin host produced an unreadable message");
                        Kill(process);
                        return null;
                    }

                    if (message is null)
                    {
                        error.WriteLine("plugin host produced an unreadable message");
                        Kill(process);
                        return null;
                    }

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
                            errorMessage = message.Message ?? "plugin host reported an error";
                            diagnostics = new PluginDiagnostics
                            {
                                Status = PluginScanStatus.Error,
                                Message = errorMessage,
                            };
                            finished = true;
                            break;

                        default:
                            error.WriteLine($"plugin host produced unexpected message type '{message.Type}'");
                            Kill(process);
                            return null;
                    }
                }

                await ShutdownAsync(process, timeout.Token).ConfigureAwait(false);
                await DrainStderrAsync(stderrDrain).ConfigureAwait(false);

                return new HostScanOutcome(
                    records,
                    diagnostics ?? new PluginDiagnostics(),
                    hasChanges,
                    errorMessage,
                    batchCount);
            }
            finally
            {
                if (!process.HasExited)
                {
                    Kill(process);
                }
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waitTimeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
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
        }
        catch (IOException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static string? LocateHost(TextWriter error)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "plugin-host", "Tokenmetry.PluginHost.dll"),
                     Path.Combine(AppContext.BaseDirectory, "Tokenmetry.PluginHost.dll"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        error.WriteLine("plugin host was not found next to the validator; build the solution first");
        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PrintUsage(TextWriter error)
    {
        error.WriteLine("usage:");
        error.WriteLine("  Tokenmetry.PluginValidator validate <package.tkmplugin>");
        error.WriteLine("  Tokenmetry.PluginValidator scan <package.tkmplugin> --data <directory>");
        error.WriteLine("  Tokenmetry.PluginValidator verify-contract [--package <contract.nupkg>]");
        error.WriteLine("exit codes: 0 pass, 2 data missing, 3 invalid output, 4 runtime failure");
    }

    private sealed record HostScanOutcome(
        IReadOnlyList<PluginUsageRecord> Records,
        PluginDiagnostics Diagnostics,
        bool HasChanges,
        string? ErrorMessage,
        int BatchCount);
}
