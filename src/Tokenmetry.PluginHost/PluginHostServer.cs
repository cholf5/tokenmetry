using System.Globalization;
using System.Text;
using System.Text.Json;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.PluginHost;

/// <summary>宿主启动参数。</summary>
public sealed record PluginHostOptions
{
    /// <summary>插件包目录（含 plugin.json 与入口程序集）。</summary>
    public string PluginDirectory { get; init; } = string.Empty;

    /// <summary>宿主分配给该插件的可丢弃缓存目录。</summary>
    public string DataDirectory { get; init; } = string.Empty;

    /// <summary>宿主版本，透传给插件做兼容性提示。</summary>
    public string HostVersion { get; init; } = string.Empty;
}

/// <summary>
/// 插件宿主主循环。每个宿主进程只加载一个插件；stdin 收 NDJSON 请求，stdout 写 NDJSON 响应，
/// stderr 写日志。插件异常被就地转成 `error` 消息，不传播给主进程。
/// </summary>
public static class PluginHostServer
{
    private const string ManifestFileName = "plugin.json";
    private const int MaxBatchSize = 500;

    public static async Task<int> RunAsync(
        PluginHostOptions options,
        Stream stdin,
        Stream stdout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);

        // 先把协议独占 stdout：协议写入流在这里锁定；随后把 Console.Out 指向 stderr，
        // 这样插件里任何 Console.WriteLine 调试输出都进 stderr，不会污染 NDJSON。
        await using var protocol = new StreamWriter(
            stdout,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        Console.SetOut(Console.Error);

        using var reader = new StreamReader(
            stdin,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        IUsageSourcePlugin plugin;
        try
        {
            plugin = LoadPlugin(options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception);
            await WriteAsync(
                protocol,
                new PluginHostMessage { Type = "error", Message = ShortMessage(exception) },
                cancellationToken);
            return 1;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (line is null)
            {
                // stdin 关闭：主进程不再需要宿主，正常退出。
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PluginHostRequest? request;
            try
            {
                request = PluginJson.DeserializeHostRequest(line);
            }
            catch (JsonException exception)
            {
                await WriteAsync(
                    protocol,
                    new PluginHostMessage { Type = "error", Message = ShortMessage(exception) },
                    cancellationToken);
                continue;
            }

            if (request is null)
            {
                await WriteAsync(
                    protocol,
                    new PluginHostMessage { Type = "error", Message = "request was not valid JSON" },
                    cancellationToken);
                continue;
            }

            switch (request.Type)
            {
                case "scan":
                    await HandleScanAsync(plugin, options, request, protocol, cancellationToken);
                    break;

                case "shutdown":
                    return 0;

                default:
                    await WriteAsync(
                        protocol,
                        new PluginHostMessage
                        {
                            Type = "error",
                            Message = $"unknown request type '{request.Type}'",
                        },
                        cancellationToken);
                    break;
            }
        }

        return 0;
    }

    private static IUsageSourcePlugin LoadPlugin(PluginHostOptions options)
    {
        var manifestPath = Path.GetFullPath(Path.Combine(options.PluginDirectory, ManifestFileName));
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"plugin manifest not found at '{manifestPath}'", manifestPath);
        }

        var manifest = PluginJson.DeserializeManifest(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("plugin.json was not valid JSON");

        var manifestErrors = PluginManifestValidator.Validate(manifest);
        if (manifestErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "plugin.json failed validation: " + string.Join("; ", manifestErrors));
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(options.PluginDirectory, manifest.EntryAssembly));
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"plugin entry assembly not found at '{assemblyPath}'", assemblyPath);
        }

        // 每次调用走独立加载上下文；契约程序集强制共用宿主默认上下文（见 PluginLoadContext）。
        var loadContext = new PluginLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        var entryType = assembly.GetType(manifest.EntryType, throwOnError: true)
            ?? throw new InvalidOperationException($"entryType '{manifest.EntryType}' was not found");

        if (!typeof(IUsageSourcePlugin).IsAssignableFrom(entryType))
        {
            throw new InvalidOperationException(
                $"entryType '{manifest.EntryType}' does not implement {nameof(IUsageSourcePlugin)}");
        }

        return Activator.CreateInstance(entryType) as IUsageSourcePlugin
            ?? throw new InvalidOperationException($"entryType '{manifest.EntryType}' could not be instantiated");
    }

    private static async Task HandleScanAsync(
        IUsageSourcePlugin plugin,
        PluginHostOptions options,
        PluginHostRequest request,
        StreamWriter protocol,
        CancellationToken cancellationToken)
    {
        var context = new PluginScanContext
        {
            DataDirectory = options.DataDirectory,
            ForceFull = request.ForceFull,
            HostVersion = options.HostVersion,
            PluginApiVersion = PluginApiVersion.Current.ToString(CultureInfo.InvariantCulture),
        };

        PluginScanResult result;
        try
        {
            result = await plugin.ScanAsync(context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 堆栈进 stderr（app.log 可查），协议里只回面向用户的短消息，不含记录内容。
            Console.Error.WriteLine(exception);
            await WriteAsync(
                protocol,
                new PluginHostMessage { Type = "error", Message = ShortMessage(exception) },
                cancellationToken);
            return;
        }

        var emitted = new List<PluginUsageRecord>();
        var invalidRecords = 0;
        var duplicateRecords = 0;
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in result.Records)
        {
            if (PluginRecordValidator.Validate(record).Count > 0)
            {
                invalidRecords++;
                continue;
            }

            if (!seenRecordIds.Add(record.RecordId))
            {
                duplicateRecords++;
                continue;
            }

            emitted.Add(record);
        }

        var diagnostics = BuildDiagnostics(
            result.Diagnostics,
            result.HasChanges,
            emitted.Count,
            invalidRecords,
            duplicateRecords);

        for (var offset = 0; offset < emitted.Count; offset += MaxBatchSize)
        {
            var batch = emitted.Skip(offset).Take(MaxBatchSize).ToArray();
            await WriteAsync(
                protocol,
                new PluginHostMessage { Type = "records", Batch = batch },
                cancellationToken);
        }

        await WriteAsync(
            protocol,
            new PluginHostMessage
            {
                Type = "done",
                Diagnostics = diagnostics,
                HasChanges = result.HasChanges,
            },
            cancellationToken);
    }

    private static PluginDiagnostics BuildDiagnostics(
        PluginDiagnostics pluginDiagnostics,
        bool hasChanges,
        int emittedRecords,
        int invalidRecords,
        int duplicateRecords)
    {
        var status = pluginDiagnostics.Status;

        // 插件自报 Ok/Partial 时，宿主按校验结果收口状态；NoData/Error 直接沿用。
        if (status is PluginScanStatus.Ok or PluginScanStatus.Partial)
        {
            if (invalidRecords > 0 || duplicateRecords > 0)
            {
                status = PluginScanStatus.Partial;
            }
            else if (hasChanges && emittedRecords == 0)
            {
                status = PluginScanStatus.NoData;
            }
        }

        return pluginDiagnostics with
        {
            Status = status,
            Records = emittedRecords,
            InvalidRecords = pluginDiagnostics.InvalidRecords + invalidRecords,
            DuplicateRecords = pluginDiagnostics.DuplicateRecords + duplicateRecords,
        };
    }

    private static Task WriteAsync(
        StreamWriter writer,
        PluginHostMessage message,
        CancellationToken cancellationToken) =>
        writer.WriteLineAsync(PluginJson.SerializeHostMessage(message).AsMemory(), cancellationToken);

    private static string ShortMessage(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}
